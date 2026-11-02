namespace PureClean.Core.Software;

/// <summary>
/// RustDesk n'est plus dans le dépôt winget : il est installé depuis sa page GitHub officielle
/// (voir <see cref="GitHubReleaseInstaller"/>), puis la configuration du serveur auto-hébergé est appliquée.
/// </summary>
public static class RustDeskInstaller
{
    public static string ExecutablePath =>
        Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles), "RustDesk", "rustdesk.exe");

    public static async Task InstallAsync(GitHubApp app, RustDeskSettings settings, IProgress<string> progress)
    {
        await GitHubReleaseInstaller.InstallAsync(app, progress);

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
        if (!File.Exists(ExecutablePath))
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

        // rustdesk.exe peut rester ouvert après avoir appliqué une option : il est fermé au bout de 30 s.
        foreach (var command in commands)
            await GitHubReleaseInstaller.RunAsync(ExecutablePath, command, TimeSpan.FromSeconds(30), timeoutIsSuccess: true);
    }
}
