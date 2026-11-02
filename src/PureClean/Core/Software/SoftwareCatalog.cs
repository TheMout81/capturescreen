using System.Text.RegularExpressions;

namespace PureClean.Core.Software;

/// <param name="Source">« winget » (dépôt de Microsoft), « msstore » (Microsoft Store) ou « github » (page officielle, voir <see cref="SoftwareCatalog.GitHubApps"/>).</param>
public sealed record SoftwarePackage(string Id, string Name, string Category, string Source = SoftwareCatalog.WingetSource)
{
    public bool IsRustDesk => Source == SoftwareCatalog.GitHubSource && Id == SoftwareCatalog.RustDeskId;
}

/// <summary>
/// Liste de logiciels proposée par défaut. Chaque identifiant a été vérifié dans le dépôt officiel
/// microsoft/winget-pkgs. Les logiciels dont l'installateur embarque des offres publicitaires
/// (FileZilla, par exemple) en ont été retirés par Microsoft et ne sont donc pas proposés.
/// </summary>
public static class SoftwareCatalog
{
    public const string WingetSource = "winget";
    public const string StoreSource = "msstore";
    public const string GitHubSource = "github";
    public const string RustDeskId = "RustDesk.RustDesk";
    public const string ArtCraftLauncherId = "ArtCraft.Launcher";
    public const string UserCategory = "Mes ajouts";

    private const string Browsers = "Navigateurs";
    private const string Communication = "Communication";
    private const string Media = "Multimédia";
    private const string Graphics = "Image et 3D";
    private const string Office = "Bureautique et PDF";
    private const string Tools = "Utilitaires";
    private const string Security = "Mots de passe";
    private const string Backup = "Sauvegarde";
    private const string Remote = "Accès à distance et réseau";
    private const string Games = "Jeux";
    private const string Development = "Développement";

    /// <summary>Ordre d'affichage des catégories ; les catégories inconnues (profils importés) suivent.</summary>
    public static IReadOnlyList<string> CategoryOrder { get; } =
        [Browsers, Communication, Media, Graphics, Office, Tools, Security, Backup, Remote, Games, Development, UserCategory];

    public static IReadOnlyList<SoftwarePackage> Default { get; } =
    [
        new("Mozilla.Firefox.fr", "Firefox", Browsers),
        new("Google.Chrome", "Google Chrome", Browsers),
        new("Brave.Brave", "Brave", Browsers),

        new("Mozilla.Thunderbird.fr", "Thunderbird", Communication),
        new("Discord.Discord", "Discord", Communication),
        new("OpenWhisperSystems.Signal", "Signal", Communication),
        new("Telegram.TelegramDesktop", "Telegram", Communication),
        new("Zoom.Zoom", "Zoom", Communication),
        new("Microsoft.Teams", "Microsoft Teams", Communication),

        new("VideoLAN.VLC", "VLC", Media),
        new("MPC-BE.MPC-BE", "MPC-BE", Media),
        new("Spotify.Spotify", "Spotify", Media),
        new("Audacity.Audacity", "Audacity", Media),
        new("OBSProject.OBSStudio", "OBS Studio", Media),
        new("HandBrake.HandBrake", "HandBrake", Media),
        new("9P4CLT2RJ1RS", "MusicBee", Media, StoreSource),

        new("GIMP.GIMP.3", "GIMP", Graphics),
        new("dotPDN.PaintDotNet", "Paint.NET", Graphics),
        new("IrfanSkiljan.IrfanView", "IrfanView", Graphics),
        new("Inkscape.Inkscape", "Inkscape", Graphics),
        new("ShareX.ShareX", "ShareX", Graphics),
        new("Ultimaker.Cura", "UltiMaker Cura", Graphics),
        new(ArtCraftLauncherId, "ArtCraft Launcher", Graphics, GitHubSource),

        new("TheDocumentFoundation.LibreOffice", "LibreOffice", Office),
        new("Foxit.FoxitReader", "Foxit PDF Reader", Office),
        new("SumatraPDF.SumatraPDF", "SumatraPDF", Office),
        new("Notepad++.Notepad++", "Notepad++", Office),

        new("7zip.7zip", "7-Zip", Tools),
        new("Microsoft.PowerToys", "PowerToys", Tools),
        new("voidtools.Everything", "Everything", Tools),
        new("AntibodySoftware.WizTree", "WizTree", Tools),
        new("CodeSector.TeraCopy", "TeraCopy", Tools),
        new("BinaryFortress.DisplayFusion", "DisplayFusion", Tools),

        new("Bitwarden.Bitwarden", "Bitwarden", Security),
        new("KeePassXCTeam.KeePassXC", "KeePassXC", Security),

        new("Kopia.KopiaUI", "Kopia", Backup),
        new("Duplicati.Duplicati", "Duplicati", Backup),
        new("restic.restic", "restic (ligne de commande)", Backup),

        new("TeamViewer.TeamViewer", "TeamViewer", Remote),
        new(RustDeskId, "RustDesk", Remote, GitHubSource),
        new("Mobatek.MobaXterm", "MobaXterm", Remote),
        new("WireGuard.WireGuard", "WireGuard", Remote),
        new("Tailscale.Tailscale", "Tailscale", Remote),
        new("Nextcloud.NextcloudDesktop", "Nextcloud", Remote),
        new("WinSCP.WinSCP", "WinSCP", Remote),
        new("qBittorrent.qBittorrent", "qBittorrent", Remote),

        new("Valve.Steam", "Steam", Games),
        new("EpicGames.EpicGamesLauncher", "Epic Games Launcher", Games),
        new("GOG.Galaxy", "GOG Galaxy", Games),

        new("Microsoft.VisualStudioCode", "Visual Studio Code", Development),
        new("Git.Git", "Git", Development),
        new("Python.Python.3.14", "Python 3.14", Development),
        new("Docker.DockerDesktop", "Docker Desktop", Development),
    ];

    /// <summary>Logiciels absents de winget, installés depuis leur page GitHub officielle.</summary>
    public static IReadOnlyDictionary<string, GitHubApp> GitHubApps { get; } = new Dictionary<string, GitHubApp>
    {
        [RustDeskId] = new(RustDeskId, "rustdesk/rustdesk",
            new Regex(@"^rustdesk-[\d.]+-x86_64\.(msi|exe)$", RegexOptions.IgnoreCase),
            () => RustDeskInstaller.ExecutablePath),

        // Le launcher installe puis tient à jour les applications ArtCraft (FilmCraft, CadCraft…).
        [ArtCraftLauncherId] = new(ArtCraftLauncherId, "storytold/craft-launcher",
            new Regex(@"^artcraft-launcher-[\d.]+-windows-x64\.msi$", RegexOptions.IgnoreCase),
            () => Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
                "Programs", "ArtCraft", "Launcher", "artcraft-launcher.exe")),
    };

    /// <summary>
    /// Vrai si <paramref name="id"/> figure parmi les identifiants installés renvoyés par winget,
    /// qui tronque parfois les plus longs (« Microsoft.VisualStud… »).
    /// </summary>
    public static bool IsInstalled(string id, IEnumerable<string> installedIds)
        => installedIds.Any(installed =>
            string.Equals(installed, id, StringComparison.OrdinalIgnoreCase)
            || (installed.EndsWith('…') && installed.Length > 1
                && id.StartsWith(installed[..^1], StringComparison.OrdinalIgnoreCase)));
}
