using System.ComponentModel;
using System.Diagnostics;
using System.Text;

namespace PureClean.Core.Updates;

public sealed record PackageUpdate(string Name, string Id, string Version, string Available)
{
    /// <summary>winget tronque parfois les identifiants trop longs (« … ») : la mise à jour ciblée est alors impossible.</summary>
    public bool IdTruncated => Id.EndsWith('…');
}

/// <summary>
/// Interroge et pilote winget (Gestionnaire de paquets Windows, fourni par Microsoft).
/// C'est la seule partie de PureClean qui passe par Internet, et uniquement à la demande.
/// </summary>
public static class WingetClient
{
    private static readonly string[] Agreements = ["--accept-source-agreements", "--accept-package-agreements"];

    public static async Task<IReadOnlyList<PackageUpdate>> ListAsync(CancellationToken cancellationToken)
    {
        var (exitCode, output) = await RunAsync(["upgrade", "--accept-source-agreements"], cancellationToken);
        var updates = ParseUpgradeTable(output);

        // Code d'erreur sans tableau : on affiche le message de winget, sauf s'il dit simplement « rien à mettre à jour ».
        if (updates.Count == 0 && exitCode != 0 && !LooksLikeNoUpdates(output))
            throw new InvalidOperationException(LastLine(output) ?? $"winget a échoué (code {exitCode}).");

        return updates;
    }

    /// <summary>Met à jour un paquet (ou tous si <paramref name="id"/> est null). Renvoie le code de sortie de winget.</summary>
    public static async Task<int> UpgradeAsync(string? id)
    {
        string[] target = id is null ? ["--all"] : ["--id", id, "--exact"];
        var (exitCode, _) = await RunAsync(["upgrade", .. target, "--silent", .. Agreements], CancellationToken.None);
        return exitCode;
    }

    /// <summary>
    /// Lit le tableau de « winget upgrade ». Les colonnes sont repérées par la position des titres
    /// (au-dessus de la ligne de tirets), ce qui fonctionne quelle que soit la langue de Windows.
    /// </summary>
    public static IReadOnlyList<PackageUpdate> ParseUpgradeTable(string output)
    {
        // winget anime une roue de progression avec des retours chariot : seule la fin de chaque ligne compte.
        var lines = output.Replace("\r\n", "\n").Split('\n').Select(l => l[(l.LastIndexOf('\r') + 1)..]).ToList();

        var dashes = lines.FindIndex(l => l.Length > 10 && l.Trim().All(c => c == '-'));
        if (dashes <= 0)
            return [];

        var header = lines[dashes - 1];
        var starts = new List<int>();
        for (var i = 0; i < header.Length; i++)
        {
            if (header[i] != ' ' && (i == 0 || header[i - 1] == ' '))
                starts.Add(i);
        }
        if (starts.Count < 4)
            return [];

        var result = new List<PackageUpdate>();
        foreach (var line in lines.Skip(dashes + 1))
        {
            // Fin du tableau : ligne vide ou phrase de résumé (« 2 mises à niveau disponibles. »).
            if (string.IsNullOrWhiteSpace(line) || line.Length <= starts[3])
                break;

            string Column(int index)
            {
                var start = starts[index];
                if (start >= line.Length)
                    return "";
                var end = index + 1 < starts.Count ? Math.Min(starts[index + 1], line.Length) : line.Length;
                return line[start..end].Trim();
            }

            var name = Column(0);
            var id = Column(1);
            var version = Column(2);
            var available = Column(3);
            if (id.Length == 0 || id.Contains(' ') || available.Length == 0)
                break;

            result.Add(new PackageUpdate(name, id, version, available));
        }
        return result;
    }

    private static bool LooksLikeNoUpdates(string output)
        => output.Contains("No installed package", StringComparison.OrdinalIgnoreCase)
           || output.Contains("No available upgrade", StringComparison.OrdinalIgnoreCase)
           || output.Contains("Aucun", StringComparison.OrdinalIgnoreCase);

    private static string? LastLine(string output)
        => output.Replace("\r\n", "\n").Split('\n').Select(l => l[(l.LastIndexOf('\r') + 1)..].Trim())
            .LastOrDefault(l => l.Length > 0);

    private static async Task<(int ExitCode, string Output)> RunAsync(IEnumerable<string> arguments, CancellationToken cancellationToken)
    {
        var startInfo = new ProcessStartInfo("winget")
        {
            UseShellExecute = false,
            CreateNoWindow = true,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            StandardOutputEncoding = Encoding.UTF8,
            StandardErrorEncoding = Encoding.UTF8,
        };
        foreach (var argument in arguments)
            startInfo.ArgumentList.Add(argument);

        Process process;
        try
        {
            process = Process.Start(startInfo) ?? throw new InvalidOperationException("winget n'a pas pu être lancé.");
        }
        catch (Win32Exception)
        {
            throw new InvalidOperationException(
                "winget est introuvable. Installez « Programme d'installation d'application » (App Installer) depuis le Microsoft Store.");
        }

        using (process)
        {
            var stdout = process.StandardOutput.ReadToEndAsync(cancellationToken);
            var stderr = process.StandardError.ReadToEndAsync(cancellationToken);
            try
            {
                await process.WaitForExitAsync(cancellationToken);
            }
            catch (OperationCanceledException)
            {
                // Seule la recherche est annulable ; une installation en cours n'est jamais interrompue.
                process.Kill(entireProcessTree: true);
                throw;
            }
            return (process.ExitCode, await stdout + await stderr);
        }
    }
}
