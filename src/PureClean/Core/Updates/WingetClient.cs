using System.ComponentModel;
using System.Diagnostics;
using System.Text;
using System.Text.Json;

namespace PureClean.Core.Updates;

public sealed record PackageUpdate(string Name, string Id, string Version, string Available)
{
    /// <summary>winget tronque parfois les identifiants trop longs (« … ») : la mise à jour ciblée est alors impossible.</summary>
    public bool IdTruncated => Id.EndsWith('…');
}

public sealed record SearchResult(string Name, string Id, string Version, string Source);

/// <summary>Logiciel identifié par winget (identifiant + source), par exemple lors d'un export.</summary>
public sealed record PackageReference(string Id, string Source);

/// <summary>
/// Interroge et pilote winget (Gestionnaire de paquets Windows, fourni par Microsoft).
/// Les logiciels viennent du dépôt communautaire de Microsoft (source « winget ») ou du Microsoft Store
/// (source « msstore ») ; les installateurs sont ceux des éditeurs, vérifiés par empreinte SHA-256.
/// </summary>
public static class WingetClient
{
    /// <summary>Code de winget « déjà installé » (APPINSTALLER_CLI_ERROR_PACKAGE_ALREADY_INSTALLED).</summary>
    public const int AlreadyInstalled = unchecked((int)0x8A150061);

    private static readonly string[] Agreements = ["--accept-source-agreements", "--accept-package-agreements"];

    public static async Task<bool> IsAvailableAsync()
    {
        try
        {
            var (exitCode, _) = await RunAsync(["--version"], CancellationToken.None);
            return exitCode == 0;
        }
        catch (InvalidOperationException)
        {
            return false;
        }
    }

    public static async Task<IReadOnlyList<PackageUpdate>> ListAsync(CancellationToken cancellationToken)
    {
        var (exitCode, output) = await RunAsync(["upgrade", "--accept-source-agreements"], cancellationToken);
        var updates = ParseUpgradeTable(output);

        // Code d'erreur sans tableau : on affiche le message de winget, sauf s'il dit simplement « rien à mettre à jour ».
        if (updates.Count == 0 && exitCode != 0 && !LooksLikeNoResult(output))
            throw new InvalidOperationException(LastLine(output) ?? $"winget a échoué (code {exitCode}).");

        return updates;
    }

    /// <summary>Identifiants des logiciels installés connus de winget (certains peuvent être tronqués par « … »).</summary>
    public static async Task<IReadOnlyList<string>> ListInstalledIdsAsync(CancellationToken cancellationToken)
    {
        var (_, output) = await RunAsync(["list", "--accept-source-agreements"], cancellationToken);
        return ParseTable(output).Where(r => r.Length > 1).Select(r => r[1]).ToList();
    }

    public static async Task<IReadOnlyList<SearchResult>> SearchAsync(string query, CancellationToken cancellationToken)
    {
        var (_, output) = await RunAsync(["search", query, "--accept-source-agreements"], cancellationToken);
        return ParseSearchTable(output);
    }

    /// <summary>Installe un logiciel sans aucune fenêtre ni question. Renvoie le code de sortie de winget.</summary>
    public static async Task<int> InstallAsync(string id, string source)
    {
        var (exitCode, _) = await RunAsync(
            ["install", "--id", id, "--exact", "--source", source, "--silent", .. Agreements], CancellationToken.None);
        return exitCode;
    }

    /// <summary>Met à jour un paquet (ou tous si <paramref name="id"/> est null). Renvoie le code de sortie de winget.</summary>
    public static async Task<int> UpgradeAsync(string? id)
    {
        string[] target = id is null ? ["--all"] : ["--id", id, "--exact"];
        var (exitCode, _) = await RunAsync(["upgrade", .. target, "--silent", .. Agreements], CancellationToken.None);
        return exitCode;
    }

    /// <summary>Liste les logiciels de ce PC que winget sait réinstaller (« winget export »).</summary>
    public static async Task<IReadOnlyList<PackageReference>> ExportAsync(CancellationToken cancellationToken)
    {
        var file = Path.Combine(Path.GetTempPath(), $"PureClean-export-{Guid.NewGuid():N}.json");
        try
        {
            await RunAsync(["export", "--output", file, "--accept-source-agreements"], cancellationToken);
            if (!File.Exists(file))
                throw new InvalidOperationException("winget n'a pas pu lister les logiciels de ce PC.");
            return ParseWingetExport(await File.ReadAllTextAsync(file, cancellationToken));
        }
        finally
        {
            File.Delete(file);
        }
    }

    /// <summary>Lit un fichier au format « winget export » / « winget import ».</summary>
    public static IReadOnlyList<PackageReference> ParseWingetExport(string json)
    {
        using var document = JsonDocument.Parse(json);
        var result = new List<PackageReference>();
        if (!document.RootElement.TryGetProperty("Sources", out var sources) || sources.ValueKind != JsonValueKind.Array)
            return result;

        foreach (var source in sources.EnumerateArray())
        {
            var name = source.TryGetProperty("SourceDetails", out var details) && details.TryGetProperty("Name", out var n)
                ? n.GetString() ?? "winget"
                : "winget";
            if (!source.TryGetProperty("Packages", out var packages) || packages.ValueKind != JsonValueKind.Array)
                continue;
            foreach (var package in packages.EnumerateArray())
            {
                if (package.TryGetProperty("PackageIdentifier", out var id) && id.GetString() is { Length: > 0 } value)
                    result.Add(new PackageReference(value, name));
            }
        }
        return result;
    }

    /// <summary>
    /// Lit le tableau de « winget upgrade ». Les colonnes sont repérées par la position des titres
    /// (au-dessus de la ligne de tirets), ce qui fonctionne quelle que soit la langue de Windows.
    /// </summary>
    public static IReadOnlyList<PackageUpdate> ParseUpgradeTable(string output)
        => ParseTable(output)
            .TakeWhile(r => r.Length >= 4 && r[3].Length > 0)
            .Select(r => new PackageUpdate(r[0], r[1], r[2], r[3]))
            .ToList();

    /// <summary>Lit le tableau de « winget search » (Nom, ID, Version, [Correspondance], Source).</summary>
    public static IReadOnlyList<SearchResult> ParseSearchTable(string output)
        => ParseTable(output)
            .Where(r => r.Length >= 4)
            .Select(r => new SearchResult(r[0], r[1], r[2], r[^1].Length > 0 ? r[^1] : "winget"))
            .Where(r => r.Source is "winget" or "msstore")
            .ToList();

    /// <summary>
    /// Découpe le premier tableau de la sortie de winget en cellules. Une ligne n'est retenue que si
    /// son identifiant et sa version sont renseignés : les phrases de résumé sont ainsi écartées.
    /// </summary>
    public static IReadOnlyList<string[]> ParseTable(string output)
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
        if (starts.Count < 3)
            return [];

        var rows = new List<string[]>();
        foreach (var line in lines.Skip(dashes + 1))
        {
            // Fin du tableau : ligne vide ou phrase de résumé.
            if (string.IsNullOrWhiteSpace(line))
                break;

            var cells = new string[starts.Count];
            for (var c = 0; c < starts.Count; c++)
            {
                var start = starts[c];
                var end = c + 1 < starts.Count ? Math.Min(starts[c + 1], line.Length) : line.Length;
                cells[c] = start < line.Length && end > start ? line[start..end].Trim() : "";
            }

            if (cells[1].Length == 0 || cells[1].Contains(' ') || cells[2].Length == 0)
                break;
            rows.Add(cells);
        }
        return rows;
    }

    private static bool LooksLikeNoResult(string output)
        => output.Contains("No installed package", StringComparison.OrdinalIgnoreCase)
           || output.Contains("No available upgrade", StringComparison.OrdinalIgnoreCase)
           || output.Contains("No package found", StringComparison.OrdinalIgnoreCase)
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
                // Seules les recherches sont annulables ; une installation en cours n'est jamais interrompue.
                process.Kill(entireProcessTree: true);
                throw;
            }
            return (process.ExitCode, await stdout + await stderr);
        }
    }
}
