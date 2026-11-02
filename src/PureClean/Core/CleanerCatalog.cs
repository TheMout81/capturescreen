using PureClean.Core.Cleaners;
using PureClean.Models;

namespace PureClean.Core;

/// <summary>
/// Liste de tout ce que PureClean sait nettoyer.
/// Règle : par défaut, on ne touche qu'aux caches et fichiers temporaires régénérables.
/// Historique et cookies ne sont supprimés que si l'utilisateur coche la section Confidentialité ;
/// mots de passe, favoris et sessions ne sont jamais supprimés.
/// </summary>
public static class CleanerCatalog
{
    private static readonly TimeSpan OneDay = TimeSpan.FromDays(1);
    private static readonly TimeSpan OneWeek = TimeSpan.FromDays(7);

    private static string Windows => Environment.GetFolderPath(Environment.SpecialFolder.Windows);
    private static string LocalAppData => Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData);
    private static string RoamingAppData => Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData);
    private static string ProgramData => Environment.GetFolderPath(Environment.SpecialFolder.CommonApplicationData);

    public static IReadOnlyList<ICleaner> CreateAll(IEnumerable<string> customFolders) =>
    [
        .. WindowsCleaners(),
        .. ApplicationCleaners(),
        .. PrivacyCleaners(),
        .. AdvancedCleaners(),
        .. customFolders.Select(CustomFolder),
    ];

    /// <summary>Un dossier choisi par l'utilisateur : tout son contenu est supprimé.</summary>
    public static ICleaner CustomFolder(string path) => new PathCleaner(
        Path.GetFileName(path.TrimEnd('\\')) is { Length: > 0 } name ? name : path,
        path,
        CleanerCategory.Custom,
        () => [new CleanTarget(path)],
        isAvailable: () => true,
        id: "custom:" + path);

    private static IEnumerable<ICleaner> WindowsCleaners()
    {
        yield return new PathCleaner(
            "Fichiers temporaires (utilisateur)",
            "Dossier %TEMP% de votre session. Les fichiers de moins de 24 h sont conservés.",
            CleanerCategory.Windows,
            () => [new CleanTarget(Path.GetTempPath())],
            minimumAge: OneDay);

        yield return new PathCleaner(
            "Fichiers temporaires (système)",
            "Dossier C:\\Windows\\Temp. Les fichiers de moins de 24 h sont conservés.",
            CleanerCategory.Windows,
            () => [new CleanTarget(Path.Combine(Windows, "Temp"))],
            minimumAge: OneDay);

        yield return new RecycleBinCleaner();

        yield return new PathCleaner(
            "Cache de Windows Update",
            "Mises à jour déjà téléchargées et installées (SoftwareDistribution\\Download).",
            CleanerCategory.Windows,
            () => [new CleanTarget(Path.Combine(Windows, "SoftwareDistribution", "Download"))],
            minimumAge: OneDay);

        yield return new PathCleaner(
            "Cache d'optimisation de distribution",
            "Copies de mises à jour partagées avec d'autres PC (Delivery Optimization).",
            CleanerCategory.Windows,
            () => [new CleanTarget(Path.Combine(Windows, "ServiceProfiles", "NetworkService", "AppData", "Local",
                "Microsoft", "Windows", "DeliveryOptimization", "Cache"))]);

        yield return new PathCleaner(
            "Rapports d'erreurs Windows",
            "Rapports de plantage en attente ou archivés (WER).",
            CleanerCategory.Windows,
            () =>
            [
                new CleanTarget(Path.Combine(ProgramData, "Microsoft", "Windows", "WER", "ReportArchive")),
                new CleanTarget(Path.Combine(ProgramData, "Microsoft", "Windows", "WER", "ReportQueue")),
                new CleanTarget(Path.Combine(ProgramData, "Microsoft", "Windows", "WER", "Temp")),
                new CleanTarget(Path.Combine(LocalAppData, "Microsoft", "Windows", "WER")),
            ]);

        yield return new PathCleaner(
            "Vidages mémoire",
            "Fichiers de diagnostic après un écran bleu ou un plantage d'application.",
            CleanerCategory.Windows,
            () =>
            [
                new CleanTarget(Path.Combine(Windows, "Minidump")),
                new CleanTarget(Windows, "MEMORY.DMP", Recursive: false),
                new CleanTarget(Path.Combine(LocalAppData, "CrashDumps")),
            ]);

        yield return new PathCleaner(
            "Journaux Windows",
            "Anciens journaux CBS et DISM (plus de 7 jours).",
            CleanerCategory.Windows,
            () =>
            [
                new CleanTarget(Path.Combine(Windows, "Logs", "CBS"), "*.log"),
                new CleanTarget(Path.Combine(Windows, "Logs", "CBS"), "*.cab"),
                new CleanTarget(Path.Combine(Windows, "Logs", "DISM"), "*.log"),
            ],
            minimumAge: OneWeek);

        yield return new PathCleaner(
            "Cache des miniatures",
            "Aperçus d'images de l'Explorateur. Ils seront régénérés (affichage plus lent au début).",
            CleanerCategory.Windows,
            () => [new CleanTarget(Path.Combine(LocalAppData, "Microsoft", "Windows", "Explorer"), "thumbcache_*.db", Recursive: false)],
            enabledByDefault: false,
            blockingProcesses: ["explorer"]);

        yield return new PathCleaner(
            "Documents récents",
            "Liste des fichiers ouverts récemment dans l'Explorateur (raccourcis uniquement).",
            CleanerCategory.Windows,
            () => [new CleanTarget(Path.Combine(RoamingAppData, "Microsoft", "Windows", "Recent"), "*.lnk", Recursive: false)],
            enabledByDefault: false);

        yield return new DnsCacheCleaner();
    }

    private static IEnumerable<ICleaner> ApplicationCleaners()
    {
        yield return ChromiumBrowser("Google Chrome", Path.Combine(LocalAppData, "Google", "Chrome", "User Data"), "chrome");
        yield return ChromiumBrowser("Microsoft Edge", Path.Combine(LocalAppData, "Microsoft", "Edge", "User Data"), "msedge");
        yield return ChromiumBrowser("Brave", Path.Combine(LocalAppData, "BraveSoftware", "Brave-Browser", "User Data"), "brave");
        yield return ChromiumBrowser("Vivaldi", Path.Combine(LocalAppData, "Vivaldi", "User Data"), "vivaldi");

        var operaLocal = Path.Combine(LocalAppData, "Opera Software", "Opera Stable");
        yield return new PathCleaner(
            "Opera — cache",
            "Cache web d'Opera. Cookies, historique et mots de passe sont conservés.",
            CleanerCategory.Applications,
            () => [new CleanTarget(Path.Combine(operaLocal, "Cache")), new CleanTarget(Path.Combine(operaLocal, "Code Cache"))],
            blockingProcesses: ["opera"],
            isAvailable: () => Directory.Exists(operaLocal));

        var firefoxProfiles = Path.Combine(LocalAppData, "Mozilla", "Firefox", "Profiles");
        yield return new PathCleaner(
            "Mozilla Firefox — cache",
            "Cache web de tous les profils Firefox. Cookies, historique et mots de passe sont conservés.",
            CleanerCategory.Applications,
            () => SubDirectories(firefoxProfiles).SelectMany(profile => new[]
            {
                new CleanTarget(Path.Combine(profile, "cache2")),
                new CleanTarget(Path.Combine(profile, "startupCache")),
                new CleanTarget(Path.Combine(profile, "thumbnails")),
            }),
            blockingProcesses: ["firefox"],
            isAvailable: () => Directory.Exists(firefoxProfiles));

        yield return ElectronApp("Discord", Path.Combine(RoamingAppData, "discord"), "Discord");
        yield return ElectronApp("Visual Studio Code", Path.Combine(RoamingAppData, "Code"), "Code", "CachedData", "logs");
        yield return ElectronApp("Microsoft Teams (classique)", Path.Combine(RoamingAppData, "Microsoft", "Teams"), "Teams");

        var spotify = Path.Combine(LocalAppData, "Spotify");
        yield return new PathCleaner(
            "Spotify — cache",
            "Morceaux mis en cache pour la lecture en continu (hors téléchargements hors-ligne).",
            CleanerCategory.Applications,
            () => [new CleanTarget(Path.Combine(spotify, "Data")), new CleanTarget(Path.Combine(spotify, "Browser", "Cache"))],
            blockingProcesses: ["Spotify"],
            isAvailable: () => Directory.Exists(spotify));

        var steam = Path.Combine(LocalAppData, "Steam", "htmlcache");
        yield return new PathCleaner(
            "Steam — cache web",
            "Cache du navigateur intégré de Steam (magasin, communauté).",
            CleanerCategory.Applications,
            () => [new CleanTarget(steam)],
            blockingProcesses: ["steam", "steamwebhelper"],
            isAvailable: () => Directory.Exists(steam));
    }

    private static IEnumerable<ICleaner> AdvancedCleaners()
    {
        yield return new PreviousWindowsCleaner();
        yield return new ComponentStoreCleaner();
        yield return new RestorePointsCleaner();
    }

    private static IEnumerable<ICleaner> PrivacyCleaners()
    {
        (string Name, string UserData, string Process)[] chromium =
        [
            ("Google Chrome", Path.Combine(LocalAppData, "Google", "Chrome", "User Data"), "chrome"),
            ("Microsoft Edge", Path.Combine(LocalAppData, "Microsoft", "Edge", "User Data"), "msedge"),
            ("Brave", Path.Combine(LocalAppData, "BraveSoftware", "Brave-Browser", "User Data"), "brave"),
            ("Vivaldi", Path.Combine(LocalAppData, "Vivaldi", "User Data"), "vivaldi"),
        ];

        foreach (var (name, userData, process) in chromium)
        {
            yield return ChromiumHistory(name, () => ChromiumProfiles(userData), process, () => Directory.Exists(userData));
            yield return ChromiumCookies(name, () => ChromiumProfiles(userData), process, () => Directory.Exists(userData));
        }

        var opera = Path.Combine(RoamingAppData, "Opera Software", "Opera Stable");
        yield return ChromiumHistory("Opera", () => [opera], "opera", () => Directory.Exists(opera));
        yield return ChromiumCookies("Opera", () => [opera], "opera", () => Directory.Exists(opera));

        // Firefox range historique et favoris dans le même fichier : seuls les cookies sont proposés.
        var firefoxProfiles = Path.Combine(RoamingAppData, "Mozilla", "Firefox", "Profiles");
        yield return new PathCleaner(
            "Mozilla Firefox — cookies",
            "Vous serez déconnecté de tous les sites. Firefox doit être fermé. " +
            "(L'historique de Firefox n'est pas proposé : il est stocké avec vos favoris.)",
            CleanerCategory.Privacy,
            () => SubDirectories(firefoxProfiles).Select(profile => new CleanTarget(profile, "cookies.sqlite*", Recursive: false)),
            enabledByDefault: false,
            blockingProcesses: ["firefox"],
            isAvailable: () => Directory.Exists(firefoxProfiles),
            requireClosedProcesses: true);
    }

    private static ICleaner ChromiumHistory(string name, Func<IEnumerable<string>> profiles, string process, Func<bool> isAvailable)
        => new PathCleaner(
            $"{name} — historique",
            "Pages visitées, sites les plus consultés et suggestions de la barre d'adresse. Favoris conservés. Le navigateur doit être fermé.",
            CleanerCategory.Privacy,
            () => profiles().SelectMany(profile => new[]
            {
                new CleanTarget(profile, "History*", Recursive: false),
                new CleanTarget(profile, "Visited Links", Recursive: false),
                new CleanTarget(profile, "Top Sites*", Recursive: false),
                new CleanTarget(profile, "Shortcuts*", Recursive: false),
            }),
            enabledByDefault: false,
            blockingProcesses: [process],
            isAvailable: isAvailable,
            requireClosedProcesses: true);

    private static ICleaner ChromiumCookies(string name, Func<IEnumerable<string>> profiles, string process, Func<bool> isAvailable)
        => new PathCleaner(
            $"{name} — cookies",
            "Vous serez déconnecté de tous les sites. Mots de passe enregistrés conservés. Le navigateur doit être fermé.",
            CleanerCategory.Privacy,
            () => profiles().SelectMany(profile => new[]
            {
                new CleanTarget(Path.Combine(profile, "Network"), "Cookies*", Recursive: false),
                new CleanTarget(profile, "Cookies*", Recursive: false),
            }),
            enabledByDefault: false,
            blockingProcesses: [process],
            isAvailable: isAvailable,
            requireClosedProcesses: true);

    /// <summary>Navigateurs basés sur Chromium : même structure de dossiers pour tous.</summary>
    private static ICleaner ChromiumBrowser(string name, string userDataRoot, string process)
    {
        return new PathCleaner(
            $"{name} — cache",
            "Cache web, cache de code et cache GPU de tous les profils. Cookies, historique et mots de passe sont conservés.",
            CleanerCategory.Applications,
            () => ChromiumProfiles(userDataRoot)
                .SelectMany(profile => new[]
                {
                    new CleanTarget(Path.Combine(profile, "Cache")),
                    new CleanTarget(Path.Combine(profile, "Code Cache")),
                    new CleanTarget(Path.Combine(profile, "GPUCache")),
                    new CleanTarget(Path.Combine(profile, "Service Worker", "CacheStorage")),
                    new CleanTarget(Path.Combine(profile, "Service Worker", "ScriptCache")),
                })
                .Concat(
                [
                    new CleanTarget(Path.Combine(userDataRoot, "ShaderCache")),
                    new CleanTarget(Path.Combine(userDataRoot, "GrShaderCache")),
                    new CleanTarget(Path.Combine(userDataRoot, "GraphiteDawnCache")),
                ]),
            blockingProcesses: [process],
            isAvailable: () => Directory.Exists(userDataRoot));
    }

    /// <summary>Applications Electron : caches Chromium dans leur dossier AppData.</summary>
    private static ICleaner ElectronApp(string name, string root, string process, params string[] extraFolders)
    {
        string[] folders = ["Cache", "Code Cache", "GPUCache", .. extraFolders];
        return new PathCleaner(
            $"{name} — cache",
            "Caches internes de l'application. Vos comptes et réglages sont conservés.",
            CleanerCategory.Applications,
            () => folders.Select(f => new CleanTarget(Path.Combine(root, f))),
            blockingProcesses: [process],
            isAvailable: () => Directory.Exists(root));
    }

    private static IEnumerable<string> ChromiumProfiles(string userDataRoot)
        => SubDirectories(userDataRoot).Where(dir =>
        {
            var name = Path.GetFileName(dir);
            return name == "Default" || name.StartsWith("Profile ", StringComparison.Ordinal) || name == "Guest Profile";
        });

    private static IEnumerable<string> SubDirectories(string path)
    {
        try
        {
            return Directory.Exists(path) ? Directory.GetDirectories(path) : [];
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            return [];
        }
    }
}
