using System.Diagnostics;
using System.Globalization;
using System.Text.RegularExpressions;
using Microsoft.Win32;

namespace PureClean.Core.Programs;

public sealed record InstalledProgram(
    string Name,
    string? Publisher,
    string? Version,
    DateTime? InstallDate,
    long? SizeBytes,
    string UninstallCommand,
    string? InstallLocation);

/// <summary>Programmes installés, tels qu'affichés par « Programmes et fonctionnalités ».</summary>
public static partial class ProgramInventory
{
    private const string UninstallKey = @"SOFTWARE\Microsoft\Windows\CurrentVersion\Uninstall";

    public static IReadOnlyList<InstalledProgram> GetAll()
    {
        var programs = new List<InstalledProgram>();
        programs.AddRange(Read(RegistryHive.LocalMachine, RegistryView.Registry64));
        programs.AddRange(Read(RegistryHive.LocalMachine, RegistryView.Registry32));
        programs.AddRange(Read(RegistryHive.CurrentUser, RegistryView.Registry64));

        // Un même logiciel peut apparaître dans plusieurs vues du registre.
        return programs
            .GroupBy(p => (p.Name.ToUpperInvariant(), p.Version))
            .Select(g => g.First())
            .OrderBy(p => p.Name, StringComparer.CurrentCultureIgnoreCase)
            .ToList();
    }

    /// <summary>Lance le programme de désinstallation de l'éditeur et attend qu'il se termine.</summary>
    public static async Task UninstallAsync(InstalledProgram program)
    {
        var (file, arguments) = SplitCommand(NormalizeUninstallCommand(program.UninstallCommand), File.Exists);
        using var process = Process.Start(new ProcessStartInfo(file, arguments) { UseShellExecute = true })
                            ?? throw new InvalidOperationException("Le programme de désinstallation n'a pas pu être lancé.");
        await process.WaitForExitAsync();
    }

    /// <summary>
    /// « MsiExec.exe /I{GUID} » ouvre la maintenance (modifier/réparer) : on la remplace par « /X{GUID} » (désinstaller).
    /// </summary>
    public static string NormalizeUninstallCommand(string command)
    {
        var expanded = Environment.ExpandEnvironmentVariables(command.Trim());
        return MsiInstallSwitch().Replace(expanded, "$1/X$2");
    }

    /// <summary>
    /// Sépare l'exécutable de ses arguments, y compris pour les chemins non entre guillemets contenant des espaces
    /// (ex. « C:\Program Files\App\uninstall.exe /S »).
    /// </summary>
    public static (string File, string Arguments) SplitCommand(string command, Func<string, bool> fileExists)
    {
        command = command.Trim();
        if (command.StartsWith('"'))
        {
            var end = command.IndexOf('"', 1);
            if (end > 0)
                return (command[1..end], command[(end + 1)..].Trim());
        }

        // Cherche « .exe » suivi d'un espace ou de la fin : le premier qui existe (ou sans dossier, ex. MsiExec.exe) gagne.
        var matches = ExeEnd().Matches(command);
        foreach (Match match in matches)
        {
            var candidate = command[..(match.Index + 4)];
            if (!candidate.Contains('\\') || fileExists(candidate))
                return (candidate, command[(match.Index + 4)..].Trim());
        }

        // Exécutable introuvable : on garde le chemin complet pour que l'erreur affichée soit claire.
        if (matches.Count > 0)
            return (command[..(matches[0].Index + 4)], command[(matches[0].Index + 4)..].Trim());

        var space = command.IndexOf(' ');
        return space < 0 ? (command, "") : (command[..space], command[(space + 1)..].Trim());
    }

    private static IEnumerable<InstalledProgram> Read(RegistryHive hive, RegistryView view)
    {
        using var baseKey = RegistryKey.OpenBaseKey(hive, view);
        using var uninstall = baseKey.OpenSubKey(UninstallKey);
        if (uninstall is null)
            yield break;

        foreach (var subKeyName in uninstall.GetSubKeyNames())
        {
            using var key = uninstall.OpenSubKey(subKeyName);
            if (key is null || ToProgram(key) is not { } program)
                continue;
            yield return program;
        }
    }

    private static InstalledProgram? ToProgram(RegistryKey key)
    {
        var name = (key.GetValue("DisplayName") as string)?.Trim();
        var uninstall = key.GetValue("UninstallString") as string;
        if (string.IsNullOrEmpty(name) || string.IsNullOrWhiteSpace(uninstall))
            return null;

        // Composants système, mises à jour et correctifs : masqués comme dans Windows.
        if (key.GetValue("SystemComponent") is int system && system == 1)
            return null;
        if (key.GetValue("ParentKeyName") is not null)
            return null;
        if (key.GetValue("ReleaseType") is string release &&
            (release.Contains("Update", StringComparison.OrdinalIgnoreCase) || release.Contains("Hotfix", StringComparison.OrdinalIgnoreCase)))
            return null;

        DateTime? installDate = DateTime.TryParseExact(key.GetValue("InstallDate") as string, "yyyyMMdd",
            CultureInfo.InvariantCulture, DateTimeStyles.None, out var date) ? date : null;
        long? size = key.GetValue("EstimatedSize") is int kb && kb > 0 ? kb * 1024L : null;
        var location = (key.GetValue("InstallLocation") as string)?.Trim().Trim('"');

        return new InstalledProgram(
            name,
            (key.GetValue("Publisher") as string)?.Trim(),
            (key.GetValue("DisplayVersion") as string)?.Trim(),
            installDate,
            size,
            uninstall,
            string.IsNullOrEmpty(location) ? null : location);
    }

    [GeneratedRegex(@"(msiexec(?:\.exe)?""?\s+)/I(\{[0-9A-F-]+\})", RegexOptions.IgnoreCase)]
    private static partial Regex MsiInstallSwitch();

    [GeneratedRegex(@"\.exe(?=\s|$)", RegexOptions.IgnoreCase)]
    private static partial Regex ExeEnd();
}
