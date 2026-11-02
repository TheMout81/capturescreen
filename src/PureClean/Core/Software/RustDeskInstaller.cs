using System.Diagnostics;
using System.Net.Http;
using System.Security.Cryptography;
using System.Text.Json;
using System.Text.RegularExpressions;

namespace PureClean.Core.Software;

public sealed record ReleaseAsset(string Name, string Url, string? Sha256);

/// <summary>
/// RustDesk n'est plus dans le dépôt winget : il est téléchargé depuis sa page officielle
/// (github.com/rustdesk/rustdesk), l'empreinte SHA-256 publiée par GitHub est vérifiée,
/// puis la configuration du serveur auto-hébergé est appliquée.
/// </summary>
public static partial class RustDeskInstaller
{
    private const string LatestReleaseApi = "https://api.github.com/repos/rustdesk/rustdesk/releases/latest";

    public static string ExecutablePath =>
        Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles), "RustDesk", "rustdesk.exe");

    public static bool IsInstalled => File.Exists(ExecutablePath);

    public static async Task InstallAsync(RustDeskSettings settings, IProgress<string> progress)
    {
        using var http = new HttpClient { Timeout = TimeSpan.FromMinutes(10) };
        http.DefaultRequestHeaders.UserAgent.ParseAdd("PureClean");

        progress.Report("Recherche de la dernière version…");
        var asset = SelectAsset(await http.GetStringAsync(LatestReleaseApi))
                    ?? throw new InvalidOperationException("aucun installateur Windows 64 bits dans la dernière version");

        var file = Path.Combine(Path.GetTempPath(), asset.Name);
        try
        {
            progress.Report($"Téléchargement de {asset.Name}…");
            await using (var stream = await http.GetStreamAsync(asset.Url))
            await using (var output = File.Create(file))
                await stream.CopyToAsync(output);

            if (asset.Sha256 is { } expected)
            {
                await using var check = File.OpenRead(file);
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

        if (settings.IsConfigured)
        {
            progress.Report("Configuration du serveur…");
            // Laisse le service RustDesk démarrer avant de lui transmettre la configuration.
            await Task.Delay(TimeSpan.FromSeconds(5));
            await ApplyConfigurationAsync(settings);
        }
    }

    /// <summary>Applique l'adresse et la clé du serveur à RustDesk déjà installé (droits administrateur requis).</summary>
    public static async Task ApplyConfigurationAsync(RustDeskSettings settings)
    {
        if (!IsInstalled)
            throw new InvalidOperationException("RustDesk n'est pas installé sur ce PC");

        var commands = new List<string[]>();
        if (!string.IsNullOrWhiteSpace(settings.ConfigString))
        {
            commands.Add(["--config", settings.ConfigString.Trim()]);
        }
        else if (!string.IsNullOrWhiteSpace(settings.IdServer))
        {
            commands.Add(["--option", "custom-rendezvous-server", settings.IdServer.Trim()]);
            if (!string.IsNullOrWhiteSpace(settings.RelayServer))
                commands.Add(["--option", "relay-server", settings.RelayServer.Trim()]);
            if (!string.IsNullOrWhiteSpace(settings.Key))
                commands.Add(["--option", "key", settings.Key.Trim()]);
        }

        foreach (var command in commands)
            await RunAsync(ExecutablePath, command, TimeSpan.FromSeconds(30), timeoutIsSuccess: true);
    }

    /// <summary>Choisit l'installateur Windows x64 (MSI de préférence) dans la réponse de l'API GitHub.</summary>
    public static ReleaseAsset? SelectAsset(string releaseJson)
    {
        using var document = JsonDocument.Parse(releaseJson);
        if (!document.RootElement.TryGetProperty("assets", out var assets))
            return null;

        var candidates = new List<ReleaseAsset>();
        foreach (var asset in assets.EnumerateArray())
        {
            var name = asset.GetProperty("name").GetString() ?? "";
            var url = asset.GetProperty("browser_download_url").GetString() ?? "";
            var digest = asset.TryGetProperty("digest", out var d) ? d.GetString() : null;
            var sha256 = digest is not null && digest.StartsWith("sha256:", StringComparison.OrdinalIgnoreCase) ? digest[7..] : null;
            if (WindowsInstaller().IsMatch(name) && url.StartsWith("https://github.com/rustdesk/rustdesk/", StringComparison.Ordinal))
                candidates.Add(new ReleaseAsset(name, url, sha256));
        }
        return candidates.FirstOrDefault(a => a.Name.EndsWith(".msi", StringComparison.OrdinalIgnoreCase))
               ?? candidates.FirstOrDefault();
    }

    private static async Task<int> RunAsync(string file, string[] arguments, TimeSpan timeout, bool timeoutIsSuccess)
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
            // rustdesk.exe peut rester ouvert après avoir appliqué une option : on le ferme.
            process.Kill(entireProcessTree: true);
            return timeoutIsSuccess ? 0 : throw new InvalidOperationException("l'installation a dépassé le délai de 10 minutes");
        }
    }

    [GeneratedRegex(@"^rustdesk-[\d.]+-x86_64\.(msi|exe)$", RegexOptions.IgnoreCase)]
    private static partial Regex WindowsInstaller();
}
