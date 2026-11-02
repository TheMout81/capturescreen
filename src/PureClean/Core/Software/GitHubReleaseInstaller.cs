using System.Diagnostics;
using System.Net.Http;
using System.Security.Cryptography;
using System.Text.Json;
using System.Text.RegularExpressions;

namespace PureClean.Core.Software;

public sealed record ReleaseAsset(string Name, string Url, string? Sha256);

/// <summary>
/// Logiciel absent de winget, installé depuis la dernière version publiée sur sa page GitHub officielle.
/// </summary>
/// <param name="Repository">Dépôt « propriétaire/nom » : seuls les fichiers publiés par ce dépôt sont acceptés.</param>
/// <param name="AssetPattern">Nom de l'installateur Windows 64 bits (MSI de préférence).</param>
/// <param name="ExecutablePath">Présence de ce fichier = logiciel installé.</param>
public sealed record GitHubApp(string Id, string Repository, Regex AssetPattern, Func<string> ExecutablePath)
{
    public bool IsInstalled => File.Exists(ExecutablePath());
}

/// <summary>
/// Télécharge l'installateur d'une version GitHub, vérifie son empreinte SHA-256 (fournie par GitHub
/// ou par le fichier SHA256SUMS.txt de la version) et l'installe en silence. Sans empreinte, rien n'est installé.
/// </summary>
public static class GitHubReleaseInstaller
{
    private const string ChecksumFile = "SHA256SUMS.txt";

    public static async Task InstallAsync(GitHubApp app, IProgress<string> progress)
    {
        using var http = new HttpClient { Timeout = TimeSpan.FromMinutes(10) };
        http.DefaultRequestHeaders.UserAgent.ParseAdd("PureClean");

        progress.Report("Recherche de la dernière version…");
        var release = await http.GetStringAsync($"https://api.github.com/repos/{app.Repository}/releases/latest");
        var asset = SelectAsset(release, app.Repository, app.AssetPattern)
                    ?? throw new InvalidOperationException("aucun installateur Windows 64 bits dans la dernière version");

        var expected = asset.Sha256;
        if (expected is null && SelectChecksumFile(release, app.Repository) is { } sums)
            expected = FindChecksum(await http.GetStringAsync(sums.Url), asset.Name);
        if (expected is null)
            throw new InvalidOperationException("aucune empreinte SHA-256 publiée pour ce fichier : installation annulée par sécurité");

        var file = Path.Combine(Path.GetTempPath(), asset.Name);
        try
        {
            progress.Report($"Téléchargement de {asset.Name}…");
            await using (var stream = await http.GetStreamAsync(asset.Url))
            await using (var output = File.Create(file))
                await stream.CopyToAsync(output);

            await using (var check = File.OpenRead(file))
            {
                var actual = Convert.ToHexString(await SHA256.HashDataAsync(check));
                if (!actual.Equals(expected, StringComparison.OrdinalIgnoreCase))
                    throw new InvalidOperationException("l'empreinte du fichier téléchargé ne correspond pas : installation annulée");
            }

            progress.Report("Installation…");
            var exitCode = asset.Name.EndsWith(".msi", StringComparison.OrdinalIgnoreCase)
                ? await RunAsync("msiexec.exe", ["/i", file, "/qn", "/norestart"], TimeSpan.FromMinutes(10), timeoutIsSuccess: false)
                : await RunAsync(file, ["--silent-install"], TimeSpan.FromMinutes(10), timeoutIsSuccess: false);
            // 3010 : installé, redémarrage demandé.
            if (exitCode is not (0 or 3010))
                throw new InvalidOperationException($"l'installateur a renvoyé le code {exitCode}");
        }
        finally
        {
            File.Delete(file);
        }
    }

    /// <summary>Choisit l'installateur (MSI de préférence) parmi les fichiers publiés par le dépôt attendu.</summary>
    public static ReleaseAsset? SelectAsset(string releaseJson, string repository, Regex pattern)
    {
        var candidates = Assets(releaseJson, repository).Where(a => pattern.IsMatch(a.Name)).ToList();
        return candidates.FirstOrDefault(a => a.Name.EndsWith(".msi", StringComparison.OrdinalIgnoreCase))
               ?? candidates.FirstOrDefault();
    }

    public static ReleaseAsset? SelectChecksumFile(string releaseJson, string repository)
        => Assets(releaseJson, repository).FirstOrDefault(a => a.Name.Equals(ChecksumFile, StringComparison.OrdinalIgnoreCase));

    /// <summary>Cherche l'empreinte d'un fichier dans un SHA256SUMS.txt (« empreinte  nom » par ligne).</summary>
    public static string? FindChecksum(string sums, string fileName)
    {
        foreach (var line in sums.Split('\n'))
        {
            var parts = line.Trim().Split(' ', 2, StringSplitOptions.RemoveEmptyEntries);
            if (parts.Length == 2 && parts[0].Length == 64 && parts[1].Trim().TrimStart('*') == fileName)
                return parts[0];
        }
        return null;
    }

    public static async Task<int> RunAsync(string file, string[] arguments, TimeSpan timeout, bool timeoutIsSuccess)
    {
        var startInfo = new ProcessStartInfo(file) { UseShellExecute = false, CreateNoWindow = true };
        foreach (var argument in arguments)
            startInfo.ArgumentList.Add(argument);

        using var process = Process.Start(startInfo) ?? throw new InvalidOperationException($"impossible de lancer {file}");
        using var cts = new CancellationTokenSource(timeout);
        try
        {
            await process.WaitForExitAsync(cts.Token);
            return process.ExitCode;
        }
        catch (OperationCanceledException)
        {
            process.Kill(entireProcessTree: true);
            return timeoutIsSuccess ? 0 : throw new InvalidOperationException("l'installation a dépassé le délai de 10 minutes");
        }
    }

    /// <summary>Fichiers de la version, en ne gardant que ceux téléchargeables depuis le dépôt attendu.</summary>
    private static IEnumerable<ReleaseAsset> Assets(string releaseJson, string repository)
    {
        using var document = JsonDocument.Parse(releaseJson);
        if (!document.RootElement.TryGetProperty("assets", out var assets))
            yield break;

        var prefix = $"https://github.com/{repository}/releases/download/";
        foreach (var asset in assets.EnumerateArray())
        {
            var name = asset.GetProperty("name").GetString() ?? "";
            var url = asset.GetProperty("browser_download_url").GetString() ?? "";
            var digest = asset.TryGetProperty("digest", out var d) ? d.GetString() : null;
            var sha256 = digest is not null && digest.StartsWith("sha256:", StringComparison.OrdinalIgnoreCase) ? digest[7..] : null;
            if (url.StartsWith(prefix, StringComparison.OrdinalIgnoreCase))
                yield return new ReleaseAsset(name, url, sha256);
        }
    }
}
