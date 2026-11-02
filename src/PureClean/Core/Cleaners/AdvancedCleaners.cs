using Microsoft.Win32;
using PureClean.Infrastructure;
using PureClean.Models;

namespace PureClean.Core.Cleaners;

/// <summary>
/// Base des nettoyages délégués aux outils de Windows : le gain réel est mesuré
/// par la différence d'espace libre sur le lecteur système.
/// </summary>
public abstract class SystemToolCleaner : ICleaner
{
    protected static string WindowsFolder => Environment.GetFolderPath(Environment.SpecialFolder.Windows);
    protected static string SystemDrive => Path.GetPathRoot(WindowsFolder) ?? @"C:\";

    public abstract string Name { get; }
    public abstract string Description { get; }
    public CleanerCategory Category => CleanerCategory.Advanced;
    public bool EnabledByDefault => false;
    public IReadOnlyList<string> BlockingProcesses => [];
    public virtual bool IsAvailable => true;

    public abstract ScanResult Scan(CancellationToken cancellationToken);

    public CleanResult Clean(CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        var before = FreeSpace();
        RunTool();
        return new CleanResult(Math.Max(0, FreeSpace() - before), 0, 0);
    }

    /// <summary>Lance l'outil et attend sa fin. Il n'est jamais interrompu en cours de route.</summary>
    protected abstract void RunTool();

    private static long FreeSpace() => new DriveInfo(SystemDrive).AvailableFreeSpace;
}

/// <summary>Ancienne installation de Windows (Windows.old) et fichiers d'installation, via le Nettoyage de disque.</summary>
public sealed class PreviousWindowsCleaner : SystemToolCleaner
{
    private const int ProfileNumber = 4242;
    private const string VolumeCaches = @"SOFTWARE\Microsoft\Windows\CurrentVersion\Explorer\VolumeCaches";
    private static readonly string[] Handlers = ["Previous Installations", "Temporary Setup Files", "Windows Upgrade Log Files"];

    public override string Name => "Anciennes installations de Windows";
    public override string Description =>
        "Windows.old et fichiers d'installation laissés après une mise à jour majeure. " +
        "Attention : il ne sera plus possible de revenir à la version précédente de Windows.";

    public override bool IsAvailable => Folders().Any(Directory.Exists);

    public override ScanResult Scan(CancellationToken cancellationToken)
    {
        long bytes = 0;
        var count = 0;
        foreach (var folder in Folders())
        {
            foreach (var file in SafeFileSystem.EnumerateFiles(folder, "*", recursive: true))
            {
                cancellationToken.ThrowIfCancellationRequested();
                try { bytes += file.Length; } catch (IOException) { }
                count++;
            }
        }
        // Une partie des dossiers est protégée : la taille réelle est souvent supérieure.
        return new ScanResult(bytes, count, count == 0 ? null : $"Au moins {ByteFormatter.Format(bytes)}");
    }

    protected override void RunTool()
    {
        // Mécanisme officiel « cleanmgr /sageset » : on coche les catégories voulues dans un profil dédié.
        string[] existing;
        using (var caches = Registry.LocalMachine.OpenSubKey(VolumeCaches))
            existing = caches?.GetSubKeyNames() ?? [];
        var handlers = Handlers.Where(h => existing.Contains(h, StringComparer.OrdinalIgnoreCase)).ToList();
        if (handlers.Count == 0)
            throw new InvalidOperationException("Nettoyage de disque indisponible sur ce PC");

        var flagName = $"StateFlags{ProfileNumber}";
        try
        {
            foreach (var handler in handlers)
            {
                using var key = Registry.LocalMachine.OpenSubKey($@"{VolumeCaches}\{handler}", writable: true);
                key?.SetValue(flagName, 2, RegistryValueKind.DWord);
            }

            ProcessHelper.Run(Path.Combine(WindowsFolder, "System32", "cleanmgr.exe"), [$"/sagerun:{ProfileNumber}"]);
        }
        finally
        {
            foreach (var handler in handlers)
            {
                using var key = Registry.LocalMachine.OpenSubKey($@"{VolumeCaches}\{handler}", writable: true);
                key?.DeleteValue(flagName, throwOnMissingValue: false);
            }
        }
    }

    private static IEnumerable<string> Folders() =>
    [
        Path.Combine(SystemDrive, "Windows.old"),
        Path.Combine(SystemDrive, "$Windows.~BT"),
        Path.Combine(SystemDrive, "$Windows.~WS"),
    ];
}

/// <summary>Composants Windows remplacés par des mises à jour (magasin WinSxS), via DISM.</summary>
public sealed class ComponentStoreCleaner : SystemToolCleaner
{
    public override string Name => "Composants Windows obsolètes";
    public override string Description =>
        "Anciennes versions de composants remplacées par les mises à jour (outil officiel DISM). " +
        "Peut durer plusieurs minutes ; les mises à jour déjà installées ne pourront plus être désinstallées.";

    public override ScanResult Scan(CancellationToken cancellationToken)
        => new(0, 0, "Gain mesuré après nettoyage");

    protected override void RunTool()
    {
        var result = ProcessHelper.Run(Path.Combine(WindowsFolder, "System32", "Dism.exe"),
            ["/Online", "/Cleanup-Image", "/StartComponentCleanup", "/NoRestart"]);
        if (result.ExitCode != 0)
            throw new InvalidOperationException($"DISM a échoué (code {result.ExitCode})");
    }
}

/// <summary>Supprime les anciens points de restauration du lecteur système en conservant le plus récent.</summary>
public sealed class RestorePointsCleaner : SystemToolCleaner
{
    public override string Name => "Anciens points de restauration";
    public override string Description =>
        "Supprime les points de restauration et clichés instantanés du lecteur système, sauf le plus récent.";

    private string VolumeFilter => $"$v = (Get-CimInstance Win32_Volume -Filter \"DriveLetter='{SystemDrive.TrimEnd('\\')}'\").DeviceID; " +
                                   "$s = @(Get-CimInstance Win32_ShadowCopy | Where-Object { $_.VolumeName -eq $v } | Sort-Object InstallDate); ";

    public override ScanResult Scan(CancellationToken cancellationToken)
    {
        var output = ProcessHelper.RunPowerShell(VolumeFilter + "$s.Count");
        if (output.ExitCode != 0 || !int.TryParse(output.StandardOutput.Trim(), out var count))
            return new ScanResult(0, 0, "Impossible de lister les points");

        return count <= 1
            ? ScanResult.Empty
            : new ScanResult(0, count - 1, $"{count - 1} point(s) à supprimer, le plus récent est conservé");
    }

    protected override void RunTool()
    {
        var output = ProcessHelper.RunPowerShell(VolumeFilter +
            "if ($s.Count -gt 1) { $s | Select-Object -First ($s.Count - 1) | Remove-CimInstance }");
        if (output.ExitCode != 0)
            throw new InvalidOperationException("suppression refusée par Windows");
    }
}
