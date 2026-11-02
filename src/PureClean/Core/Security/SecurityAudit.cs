using System.Diagnostics;
using System.Globalization;
using Microsoft.Win32;
using PureClean.Core.Programs;
using PureClean.Core.Startup;
using PureClean.Infrastructure;
using PureClean.Models;

namespace PureClean.Core.Security;

/// <param name="Fix">Correction proposée, appliquée seulement après confirmation de l'utilisateur.</param>
public sealed record SecurityFinding(
    FindingSeverity Severity,
    string Title,
    string Detail,
    string? FixLabel = null,
    string? FixConfirmation = null,
    Func<Task>? Fix = null);

/// <summary>
/// Bilan de sécurité : contrôle les réglages que les logiciels malveillants et publicitaires
/// modifient le plus souvent. Les règles sont fixes ; aucune base de signatures n'est nécessaire.
/// </summary>
public static class SecurityAudit
{
    private const string PoliciesSystem = @"SOFTWARE\Microsoft\Windows\CurrentVersion\Policies\System";
    private const string InternetSettings = @"Software\Microsoft\Windows\CurrentVersion\Internet Settings";

    private static readonly string[] BrowserPolicyKeys =
    [
        @"SOFTWARE\Policies\Google\Chrome",
        @"SOFTWARE\Policies\Microsoft\Edge",
        @"SOFTWARE\Policies\BraveSoftware\Brave",
    ];

    private static readonly string[] HijackPolicyValues =
    [
        "HomepageLocation", "NewTabPageLocation", "DefaultSearchProviderSearchURL", "DefaultSearchProviderName",
        "DefaultSearchProviderEnabled", "RestoreOnStartup",
    ];

    private const string DefaultHosts = """
        # Copyright (c) 1993-2009 Microsoft Corp.
        #
        # This is a sample HOSTS file used by Microsoft TCP/IP for Windows.
        #
        # localhost name resolution is handled within DNS itself.
        #	127.0.0.1       localhost
        #	::1             localhost

        """;

    private static string WindowsFolder => Environment.GetFolderPath(Environment.SpecialFolder.Windows);
    private static string HostsPath => Path.Combine(WindowsFolder, "System32", "drivers", "etc", "hosts");

    public static async Task<IReadOnlyList<SecurityFinding>> RunAsync(SecurityProbe? probe)
    {
        var findings = new List<SecurityFinding>();
        if (probe is not null)
        {
            findings.AddRange(DefenderFindings(probe));
            findings.AddRange(DnsFindings(probe.Dns));
        }

        findings.AddRange(await Task.Run(() =>
        {
            var local = new List<SecurityFinding>();
            Try(local, "Contrôle de compte (UAC)", () => local.AddRange(Uac()));
            Try(local, "Windows Update", () => local.AddRange(WindowsUpdate()));
            Try(local, "Fichier hosts", () => local.AddRange(Hosts()));
            Try(local, "Proxy", () => local.AddRange(Proxy()));
            Try(local, "Stratégies des navigateurs", () => local.AddRange(BrowserPolicies()));
            Try(local, "Programmes au démarrage", () => local.AddRange(Startup()));
            Try(local, "Bureau à distance", () => local.AddRange(RemoteDesktop()));
            return local;
        }));

        return findings.OrderByDescending(f => f.Severity).ToList();
    }

    private static void Try(List<SecurityFinding> findings, string area, Action check)
    {
        try
        {
            check();
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or System.Security.SecurityException or InvalidOperationException)
        {
            findings.Add(new SecurityFinding(FindingSeverity.Info, area, "Contrôle impossible : " + ex.Message));
        }
    }

    // --- Microsoft Defender, pare-feu, DNS (données de la sonde PowerShell) ---

    private static IEnumerable<SecurityFinding> DefenderFindings(SecurityProbe probe)
    {
        var d = probe.Defender;
        if (!d.Available)
        {
            yield return new SecurityFinding(FindingSeverity.Warning, "Microsoft Defender inaccessible",
                "Un autre antivirus est peut-être installé et a pris le relais. " + d.Error);
        }
        else if (d.Mode.Contains("Passive", StringComparison.OrdinalIgnoreCase))
        {
            yield return new SecurityFinding(FindingSeverity.Info, "Microsoft Defender en mode passif",
                "Un autre antivirus assure la protection en temps réel de ce PC.");
        }
        else
        {
            yield return d.RealTime
                ? new SecurityFinding(FindingSeverity.Ok, "Protection en temps réel active", "Microsoft Defender surveille les fichiers en permanence.")
                : new SecurityFinding(FindingSeverity.Danger, "Protection en temps réel désactivée",
                    "Les fichiers ne sont plus analysés à l'ouverture. Si vous ne l'avez pas désactivée vous-même, un logiciel malveillant a pu le faire.",
                    "Réactiver", "Réactiver la protection en temps réel de Microsoft Defender ?", DefenderClient.EnableRealTimeAsync);

            yield return d.SignatureAgeDays <= 3
                ? new SecurityFinding(FindingSeverity.Ok, "Signatures antivirus à jour", $"Version {d.SignatureVersion}.")
                : new SecurityFinding(FindingSeverity.Warning, "Signatures antivirus anciennes",
                    $"Dernière mise à jour il y a {d.SignatureAgeDays} jours.",
                    "Mettre à jour", null, DefenderClient.UpdateSignaturesAsync);

            if (!d.TamperProtected)
                yield return new SecurityFinding(FindingSeverity.Info, "Protection contre les falsifications désactivée",
                    "Elle empêche les logiciels malveillants de couper Defender. Elle se réactive dans Sécurité Windows.",
                    "Ouvrir Sécurité Windows", null, () => Open("windowsdefender://threatsettings"));
        }

        var unresolved = probe.Threats.Where(t => t.Status is 1 or 5 || t.Status >= 100).ToList();
        if (unresolved.Count > 0)
            yield return new SecurityFinding(FindingSeverity.Danger, $"{unresolved.Count} menace(s) non traitée(s)",
                string.Join("\n", unresolved.Take(5).Select(t => $"{t.Name} ({t.StatusLabel})")),
                "Ouvrir Sécurité Windows", null, () => Open("windowsdefender://threat"));

        var exclusions = probe.Exclusions;
        if (exclusions.Any)
            yield return new SecurityFinding(FindingSeverity.Warning, "Exclusions dans Microsoft Defender",
                "Ces éléments ne sont jamais analysés. Les logiciels malveillants s'y ajoutent souvent :\n" + string.Join("\n", exclusions.All),
                "Supprimer les exclusions",
                "Supprimer toutes les exclusions de Microsoft Defender ? Si l'une d'elles est voulue (logiciel métier, jeu…), vous devrez la recréer.",
                () => DefenderClient.RemoveExclusionsAsync(exclusions));

        var off = probe.Firewall.Where(f => !f.Enabled).Select(f => f.Name).ToList();
        if (probe.Firewall.Count > 0)
            yield return off.Count == 0
                ? new SecurityFinding(FindingSeverity.Ok, "Pare-feu actif", "Tous les profils réseau sont protégés.")
                : new SecurityFinding(FindingSeverity.Danger, "Pare-feu désactivé", "Profils sans pare-feu : " + string.Join(", ", off),
                    "Réactiver", "Réactiver le pare-feu Windows pour tous les profils réseau ?", DefenderClient.EnableFirewallAsync);
    }

    private static IEnumerable<SecurityFinding> DnsFindings(IReadOnlyList<DnsSetting> dns)
    {
        var unusual = dns.Where(d => d.Servers.Any(s => !SecurityRules.IsExpectedDns(s))).ToList();
        if (dns.Count > 0 && unusual.Count == 0)
            yield return new SecurityFinding(FindingSeverity.Ok, "Serveurs DNS habituels", "Box, réseau local ou résolveur public reconnu.");

        foreach (var setting in unusual)
            yield return new SecurityFinding(FindingSeverity.Warning, $"Serveur DNS inhabituel ({setting.Interface})",
                $"{string.Join(", ", setting.Servers)}. Un DNS détourné peut vous envoyer vers de faux sites. " +
                "Normal si vous l'avez choisi (VPN, entreprise, filtrage).",
                "DNS automatique", $"Revenir au DNS fourni automatiquement par le réseau pour « {setting.Interface} » ?",
                () => DefenderClient.ResetDnsAsync(setting.Index));
    }

    // --- Contrôles du registre et des fichiers ---

    private static IEnumerable<SecurityFinding> Uac()
    {
        using var key = Registry.LocalMachine.OpenSubKey(PoliciesSystem);
        if (key?.GetValue("EnableLUA") is int lua && lua == 0)
        {
            yield return new SecurityFinding(FindingSeverity.Danger, "Contrôle de compte (UAC) désactivé",
                "Tout programme obtient les droits administrateur sans rien demander.",
                "Réactiver", "Réactiver le contrôle de compte d'utilisateur ? Un redémarrage est nécessaire.",
                () => SetMachineDword(PoliciesSystem, "EnableLUA", 1));
            yield break;
        }

        if (key?.GetValue("ConsentPromptBehaviorAdmin") is int consent && consent == 0)
            yield return new SecurityFinding(FindingSeverity.Warning, "Élévation sans confirmation",
                "Les programmes obtiennent les droits administrateur sans afficher la demande de confirmation.",
                "Rétablir", "Rétablir la demande de confirmation (réglage par défaut de Windows) ?",
                () => SetMachineDword(PoliciesSystem, "ConsentPromptBehaviorAdmin", 5));
        else
            yield return new SecurityFinding(FindingSeverity.Ok, "Contrôle de compte (UAC) actif", "Windows demande confirmation avant toute élévation.");
    }

    private static IEnumerable<SecurityFinding> WindowsUpdate()
    {
        const string ux = @"SOFTWARE\Microsoft\WindowsUpdate\UX\Settings";
        const string policy = @"SOFTWARE\Policies\Microsoft\Windows\WindowsUpdate\AU";
        const string service = @"SYSTEM\CurrentControlSet\Services\wuauserv";
        var problem = false;

        using (var key = Registry.LocalMachine.OpenSubKey(ux))
        {
            if (key?.GetValue("PauseUpdatesExpiryTime") is string text
                && DateTime.TryParse(text, CultureInfo.InvariantCulture, DateTimeStyles.AdjustToUniversal, out var until)
                && until > DateTime.UtcNow)
            {
                problem = true;
                yield return new SecurityFinding(FindingSeverity.Warning, "Windows Update en pause",
                    $"Les mises à jour de sécurité sont suspendues jusqu'au {until.ToLocalTime():d MMMM yyyy}.",
                    "Reprendre", "Reprendre les mises à jour Windows maintenant ?", ResumeWindowsUpdate);
            }
        }

        using (var key = Registry.LocalMachine.OpenSubKey(policy))
        {
            if (key?.GetValue("NoAutoUpdate") is int noAuto && noAuto == 1)
            {
                problem = true;
                yield return new SecurityFinding(FindingSeverity.Warning, "Mises à jour automatiques désactivées",
                    "Une stratégie empêche Windows d'installer ses mises à jour de sécurité (souvent laissée par un outil « d'optimisation »).",
                    "Supprimer la stratégie", "Supprimer la stratégie qui désactive les mises à jour automatiques ?",
                    () => DeleteMachineValue(policy, "NoAutoUpdate"));
            }
        }

        using (var key = Registry.LocalMachine.OpenSubKey(service))
        {
            if (key?.GetValue("Start") is int start && start == 4)
            {
                problem = true;
                yield return new SecurityFinding(FindingSeverity.Danger, "Service Windows Update désactivé",
                    "Windows ne peut plus recevoir de mises à jour de sécurité.",
                    "Réactiver", "Remettre le service Windows Update en démarrage manuel (réglage par défaut) ?",
                    () => SetMachineDword(service, "Start", 3));
            }
        }

        if (!problem)
            yield return new SecurityFinding(FindingSeverity.Ok, "Windows Update actif", "Aucune pause ni blocage des mises à jour.");
    }

    private static IEnumerable<SecurityFinding> Hosts()
    {
        if (!File.Exists(HostsPath))
        {
            yield return new SecurityFinding(FindingSeverity.Ok, "Fichier hosts d'origine", "Aucune redirection de site.");
            yield break;
        }

        var entries = SecurityRules.AnalyzeHosts(File.ReadAllText(HostsPath));
        var dangerous = entries.Where(e => e.Severity == FindingSeverity.Danger).ToList();
        var custom = entries.Where(e => e.Severity == FindingSeverity.Info).ToList();

        if (dangerous.Count > 0)
            yield return new SecurityFinding(FindingSeverity.Danger, "Sites sensibles redirigés ou bloqués (fichier hosts)",
                string.Join("\n", dangerous.Take(8).Select(e => $"{e.Host} → {e.Address}")) +
                (dangerous.Count > 8 ? $"\n… et {dangerous.Count - 8} autre(s)" : ""),
                "Rétablir l'original",
                "Remplacer le fichier hosts par celui d'origine de Windows ? Une copie de sauvegarde est conservée à côté.",
                RestoreHosts);
        else if (custom.Count > 0)
            yield return new SecurityFinding(FindingSeverity.Info, $"{custom.Count} entrée(s) personnalisée(s) dans le fichier hosts",
                string.Join("\n", custom.Take(5).Select(e => $"{e.Host} → {e.Address}")) + "\nNormal si vous les avez ajoutées (bloqueur de publicités, développement…).",
                "Ouvrir le fichier", null, () => Open("notepad.exe", HostsPath));
        else
            yield return new SecurityFinding(FindingSeverity.Ok, "Fichier hosts d'origine", "Aucune redirection de site.");
    }

    private static IEnumerable<SecurityFinding> Proxy()
    {
        using var key = Registry.CurrentUser.OpenSubKey(InternetSettings);
        var enabled = key?.GetValue("ProxyEnable") is int on && on == 1;
        var server = key?.GetValue("ProxyServer") as string;
        var script = key?.GetValue("AutoConfigURL") as string;

        if (!enabled && string.IsNullOrEmpty(script))
        {
            yield return new SecurityFinding(FindingSeverity.Ok, "Aucun proxy", "La connexion Internet n'est pas détournée par un proxy.");
            yield break;
        }

        var detail = (enabled ? $"Proxy : {server}. " : "") + (string.IsNullOrEmpty(script) ? "" : $"Script de configuration : {script}. ") +
                     "Un proxy inconnu peut intercepter votre navigation ; normal sur un réseau d'entreprise.";
        yield return new SecurityFinding(FindingSeverity.Warning, "Proxy configuré", detail,
            "Désactiver", "Désactiver le proxy et le script de configuration automatique ?", DisableProxy);
    }

    private static IEnumerable<SecurityFinding> BrowserPolicies()
    {
        var found = false;
        foreach (var hive in new[] { Registry.LocalMachine, Registry.CurrentUser })
        {
            foreach (var path in BrowserPolicyKeys)
            {
                using var key = hive.OpenSubKey(path);
                if (key is null)
                    continue;

                var browser = path.Split('\\')[^1];
                using (var forced = key.OpenSubKey("ExtensionInstallForcelist"))
                {
                    if (forced is not null && forced.ValueCount > 0)
                    {
                        found = true;
                        var ids = forced.GetValueNames().Select(n => forced.GetValue(n)?.ToString()?.Split(';')[0]).Where(v => v is not null);
                        yield return new SecurityFinding(FindingSeverity.Danger, $"Extensions imposées à {browser}",
                            "Ces extensions sont installées de force et ne peuvent pas être retirées depuis le navigateur " +
                            "(technique fréquente des logiciels publicitaires ; normal sur un PC d'entreprise) :\n" + string.Join("\n", ids),
                            "Supprimer la stratégie", $"Supprimer la stratégie qui impose ces extensions à {browser} ?",
                            () => DeleteSubKey(hive, path + @"\ExtensionInstallForcelist"));
                    }
                }

                var hijacked = HijackPolicyValues.Where(v => key.GetValue(v) is not null).ToList();
                var startupUrls = key.OpenSubKey("RestoreOnStartupURLs");
                if (hijacked.Count > 0 || startupUrls is not null)
                {
                    found = true;
                    var values = hijacked.Select(v => $"{v} = {key.GetValue(v)}").ToList();
                    if (startupUrls is not null)
                        values.AddRange(startupUrls.GetValueNames().Select(n => $"Page au démarrage = {startupUrls.GetValue(n)}"));
                    startupUrls?.Dispose();

                    var capturedHive = hive;
                    yield return new SecurityFinding(FindingSeverity.Warning, $"Page d'accueil ou moteur de recherche imposé à {browser}",
                        "Le navigateur affiche « géré par votre organisation » et ces réglages ne peuvent pas être changés :\n" + string.Join("\n", values),
                        "Supprimer ces réglages", $"Supprimer ces réglages imposés à {browser} ?",
                        () => DeleteBrowserHijack(capturedHive, path, hijacked));
                }
            }
        }

        if (!found)
            yield return new SecurityFinding(FindingSeverity.Ok, "Aucun réglage imposé aux navigateurs", "Ni extension forcée, ni page d'accueil ou moteur de recherche verrouillé.");
    }

    private static IEnumerable<SecurityFinding> Startup()
    {
        var checkedCount = 0;
        var findings = new List<SecurityFinding>();
        foreach (var entry in StartupManager.GetEntries().Where(e => e.IsEnabled))
        {
            checkedCount++;
            var command = Environment.ExpandEnvironmentVariables(entry.Command);
            var (file, arguments) = entry.Source is StartupSource.StartupFolderUser or StartupSource.StartupFolderCommon
                ? (command, "")
                : ProgramInventory.SplitCommand(command, File.Exists);

            var exists = File.Exists(file) || !Path.IsPathRooted(file);
            bool? signed = exists && Path.IsPathRooted(file) && file.EndsWith(".exe", StringComparison.OrdinalIgnoreCase)
                ? Authenticode.Verify(file).IsValid
                : null;

            var verdict = SecurityRules.ClassifyStartup(file, arguments, exists, signed, WindowsFolder);
            if (verdict.Severity == FindingSeverity.Ok)
                continue;

            var captured = entry;
            findings.Add(new SecurityFinding(verdict.Severity, $"Démarrage : {entry.Name}",
                $"{char.ToUpper(verdict.Reason[0])}{verdict.Reason[1..]}.\n{entry.Command}",
                "Désactiver au démarrage", $"Ne plus lancer « {entry.Name} » au démarrage ? (réversible dans la page Démarrage)",
                () => Task.Run(() => StartupManager.SetEnabled(captured, false))));
        }

        if (!findings.Any(f => f.Severity >= FindingSeverity.Warning))
            findings.Insert(0, new SecurityFinding(FindingSeverity.Ok, "Programmes au démarrage", $"{checkedCount} programme(s) vérifié(s) : rien de suspect."));
        return findings;
    }

    private static IEnumerable<SecurityFinding> RemoteDesktop()
    {
        using var key = Registry.LocalMachine.OpenSubKey(@"SYSTEM\CurrentControlSet\Control\Terminal Server");
        if (key?.GetValue("fDenyTSConnections") is int deny && deny == 0)
            yield return new SecurityFinding(FindingSeverity.Info, "Bureau à distance Windows activé",
                "Ce PC accepte les connexions Bureau à distance (RDP). Désactivez-le dans Paramètres → Système → Bureau à distance si vous ne l'utilisez pas.");
    }

    // --- Corrections ---

    private static Task SetMachineDword(string path, string name, int value) => Task.Run(() =>
    {
        using var key = Registry.LocalMachine.CreateSubKey(path, writable: true);
        key.SetValue(name, value, RegistryValueKind.DWord);
    });

    private static Task DeleteMachineValue(string path, string name) => Task.Run(() =>
    {
        using var key = Registry.LocalMachine.OpenSubKey(path, writable: true);
        key?.DeleteValue(name, throwOnMissingValue: false);
    });

    private static Task DeleteSubKey(RegistryKey hive, string path) => Task.Run(() => hive.DeleteSubKeyTree(path, throwOnMissingSubKey: false));

    private static Task DeleteBrowserHijack(RegistryKey hive, string path, IReadOnlyList<string> values) => Task.Run(() =>
    {
        using var key = hive.OpenSubKey(path, writable: true);
        if (key is null)
            return;
        foreach (var value in values)
            key.DeleteValue(value, throwOnMissingValue: false);
        key.DeleteSubKeyTree("RestoreOnStartupURLs", throwOnMissingSubKey: false);
    });

    private static Task ResumeWindowsUpdate() => Task.Run(() =>
    {
        using var key = Registry.LocalMachine.OpenSubKey(@"SOFTWARE\Microsoft\WindowsUpdate\UX\Settings", writable: true);
        if (key is null)
            return;
        foreach (var name in key.GetValueNames().Where(n => n.StartsWith("Pause", StringComparison.OrdinalIgnoreCase)))
            key.DeleteValue(name, throwOnMissingValue: false);
    });

    private static Task DisableProxy() => Task.Run(() =>
    {
        using var key = Registry.CurrentUser.OpenSubKey(InternetSettings, writable: true);
        if (key is null)
            return;
        key.SetValue("ProxyEnable", 0, RegistryValueKind.DWord);
        key.DeleteValue("AutoConfigURL", throwOnMissingValue: false);
    });

    private static Task RestoreHosts() => Task.Run(() =>
    {
        var backup = HostsPath + $".pureclean-{DateTime.Now:yyyyMMdd-HHmmss}.bak";
        File.Copy(HostsPath, backup, overwrite: false);
        File.SetAttributes(HostsPath, FileAttributes.Normal);
        File.WriteAllText(HostsPath, DefaultHosts.Replace("\n", "\r\n"));
    });

    private static Task Open(string target, string? argument = null)
    {
        Process.Start(new ProcessStartInfo(target, argument is null ? "" : $"\"{argument}\"") { UseShellExecute = true });
        return Task.CompletedTask;
    }
}
