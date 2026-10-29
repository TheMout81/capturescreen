using Microsoft.Win32;

namespace PureClean.Core.Tweaks;

/// <summary>
/// Optimisations proposées. Uniquement des réglages documentés et réversibles :
/// pas de suppression d'applications, pas de désactivation de Defender ni de Windows Update.
/// </summary>
public static class TweakCatalog
{
    private const string Privacy = "Confidentialité";
    private const string Performance = "Performances";
    private const string Explorer = "Explorateur de fichiers";

    private const RegistryHive HKCU = RegistryHive.CurrentUser;
    private const RegistryHive HKLM = RegistryHive.LocalMachine;
    private const RegistryValueKind DWord = RegistryValueKind.DWord;
    private const RegistryValueKind Text = RegistryValueKind.String;

    private const string ContentDelivery = @"Software\Microsoft\Windows\CurrentVersion\ContentDeliveryManager";
    private const string ExplorerAdvanced = @"Software\Microsoft\Windows\CurrentVersion\Explorer\Advanced";

    public static IReadOnlyList<Tweak> CreateAll() =>
    [
        // --- Confidentialité ---
        new(Privacy, "Réduire la télémétrie Windows",
            "Limite les données de diagnostic envoyées à Microsoft au minimum autorisé par votre édition " +
            "et désactive le service « Expériences des utilisateurs connectés et télémétrie » (DiagTrack).",
            [
                new(HKLM, @"SOFTWARE\Policies\Microsoft\Windows\DataCollection", "AllowTelemetry", DWord, 0, null),
                new(HKLM, @"SYSTEM\CurrentControlSet\Services\DiagTrack", "Start", DWord, 4, 2),
            ],
            requiresRestart: true),

        new(Privacy, "Désactiver l'identifiant de publicité",
            "Empêche les applications d'utiliser un identifiant publicitaire pour vous cibler.",
            [new(HKCU, @"Software\Microsoft\Windows\CurrentVersion\AdvertisingInfo", "Enabled", DWord, 0, 1)]),

        new(Privacy, "Désactiver les expériences personnalisées",
            "Microsoft n'utilise plus vos données de diagnostic pour des conseils et offres personnalisés.",
            [new(HKCU, @"Software\Microsoft\Windows\CurrentVersion\Privacy", "TailoredExperiencesWithDiagnosticDataEnabled", DWord, 0, 1)]),

        new(Privacy, "Supprimer suggestions et applications promues",
            "Retire les « suggestions » du menu Démarrer, les astuces et l'installation silencieuse d'applications sponsorisées.",
            [
                new(HKCU, ContentDelivery, "SystemPaneSuggestionsEnabled", DWord, 0, 1),
                new(HKCU, ContentDelivery, "SubscribedContent-338388Enabled", DWord, 0, 1),
                new(HKCU, ContentDelivery, "SubscribedContent-338389Enabled", DWord, 0, 1),
                new(HKCU, ContentDelivery, "SubscribedContent-353694Enabled", DWord, 0, 1),
                new(HKCU, ContentDelivery, "SilentInstalledAppsEnabled", DWord, 0, 1),
                new(HKCU, ContentDelivery, "SoftLandingEnabled", DWord, 0, 1),
            ]),

        new(Privacy, "Supprimer les publicités de l'écran de verrouillage",
            "Désactive les « anecdotes, astuces et autres » affichées sur l'écran de verrouillage.",
            [
                new(HKCU, ContentDelivery, "RotatingLockScreenOverlayEnabled", DWord, 0, 1),
                new(HKCU, ContentDelivery, "SubscribedContent-338387Enabled", DWord, 0, 1),
            ]),

        new(Privacy, "Désactiver la recherche Bing dans le menu Démarrer",
            "La recherche du menu Démarrer reste locale : vos frappes ne sont plus envoyées à Bing.",
            [
                new(HKCU, @"Software\Policies\Microsoft\Windows\Explorer", "DisableSearchBoxSuggestions", DWord, 1, null),
                new(HKCU, @"Software\Microsoft\Windows\CurrentVersion\Search", "BingSearchEnabled", DWord, 0, null),
            ],
            requiresRestart: true),

        new(Privacy, "Désactiver l'historique des activités",
            "Windows n'enregistre plus la chronologie des applications et fichiers utilisés.",
            [
                new(HKLM, @"SOFTWARE\Policies\Microsoft\Windows\System", "PublishUserActivities", DWord, 0, null),
                new(HKLM, @"SOFTWARE\Policies\Microsoft\Windows\System", "UploadUserActivities", DWord, 0, null),
            ]),

        // --- Performances ---
        new(Performance, "Supprimer le délai des programmes au démarrage",
            "Windows retarde volontairement le lancement des programmes de démarrage ; ce réglage supprime ce délai.",
            [new(HKCU, @"Software\Microsoft\Windows\CurrentVersion\Explorer\Serialize", "StartupDelayInMSec", DWord, 0, null)]),

        new(Performance, "Menus plus réactifs",
            "Réduit le délai d'ouverture des sous-menus de 400 ms à 100 ms.",
            [new(HKCU, @"Control Panel\Desktop", "MenuShowDelay", Text, "100", "400")],
            requiresRestart: true),

        new(Performance, "Désactiver les animations de fenêtres",
            "Supprime les animations de réduction/agrandissement et de la barre des tâches.",
            [
                new(HKCU, @"Control Panel\Desktop\WindowMetrics", "MinAnimate", Text, "0", "1"),
                new(HKCU, ExplorerAdvanced, "TaskbarAnimations", DWord, 0, 1),
            ],
            requiresRestart: true),

        new(Performance, "Désactiver les effets de transparence",
            "Allège le rendu de la barre des tâches et du menu Démarrer sur les petites configurations.",
            [new(HKCU, @"Software\Microsoft\Windows\CurrentVersion\Themes\Personalize", "EnableTransparency", DWord, 0, 1)]),

        new(Performance, "Désactiver l'enregistrement en arrière-plan (Game DVR)",
            "Coupe l'enregistrement vidéo permanent de la Xbox Game Bar, gourmand en ressources dans les jeux.",
            [
                new(HKCU, @"System\GameConfigStore", "GameDVR_Enabled", DWord, 0, 1),
                new(HKCU, @"Software\Microsoft\Windows\CurrentVersion\GameDVR", "AppCaptureEnabled", DWord, 0, 1),
            ]),

        new(Performance, "Empêcher les applications de s'exécuter en arrière-plan",
            "Les applications du Microsoft Store ne tournent plus en arrière-plan quand elles sont fermées.",
            [new(HKCU, @"Software\Microsoft\Windows\CurrentVersion\BackgroundAccessApplications", "GlobalUserDisabled", DWord, 1, 0)]),

        // --- Explorateur ---
        new(Explorer, "Afficher les extensions de fichiers",
            "Affiche « .exe », « .pdf », etc. Recommandé pour repérer les fichiers malveillants déguisés.",
            [new(HKCU, ExplorerAdvanced, "HideFileExt", DWord, 0, 1)]),

        new(Explorer, "Afficher les fichiers cachés",
            "Affiche les fichiers et dossiers masqués (les fichiers système protégés restent cachés).",
            [new(HKCU, ExplorerAdvanced, "Hidden", DWord, 1, 2)]),

        new(Explorer, "Ouvrir l'Explorateur sur « Ce PC »",
            "L'Explorateur s'ouvre sur vos lecteurs plutôt que sur l'Accueil / Accès rapide.",
            [new(HKCU, ExplorerAdvanced, "LaunchTo", DWord, 1, null)]),
    ];
}
