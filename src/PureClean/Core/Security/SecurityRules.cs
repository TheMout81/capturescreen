using System.Net;
using System.Net.Sockets;

namespace PureClean.Core.Security;

public enum FindingSeverity
{
    Ok,
    Info,
    Warning,
    Danger,
}

public sealed record HostsEntry(string Address, string Host, FindingSeverity Severity);

public sealed record StartupVerdict(FindingSeverity Severity, string Reason);

/// <summary>
/// Règles du bilan de sécurité, sans accès au système (donc testables) :
/// elles décrivent les détournements les plus courants des logiciels malveillants et publicitaires.
/// </summary>
public static class SecurityRules
{
    /// <summary>Domaines que les malwares redirigent (hameçonnage) ou bloquent (empêcher les mises à jour de sécurité).</summary>
    private static readonly string[] SensitiveDomains =
    [
        "microsoft", "windowsupdate", "windows.com", "live.com", "office", "google", "gstatic", "facebook", "apple",
        "paypal", "amazon", "bank", "banque", "credit", "virustotal", "malwarebytes", "kaspersky", "eset", "avast",
        "avg.com", "bitdefender", "norton", "mcafee", "sophos", "emsisoft", "trendmicro", "f-secure", "avira", "defender",
    ];

    /// <summary>Résolveurs DNS publics reconnus (FAI non compris : ceux-là sont en général fournis par la box).</summary>
    private static readonly HashSet<string> KnownDns =
    [
        "1.1.1.1", "1.0.0.1", "1.1.1.2", "1.0.0.2", "1.1.1.3", "1.0.0.3",     // Cloudflare
        "8.8.8.8", "8.8.4.4",                                                 // Google
        "9.9.9.9", "149.112.112.112", "9.9.9.11", "149.112.112.11",           // Quad9
        "208.67.222.222", "208.67.220.220", "208.67.222.123", "208.67.220.123", // OpenDNS
        "94.140.14.14", "94.140.15.15", "94.140.14.15", "94.140.15.16",       // AdGuard
        "76.76.2.0", "76.76.10.0",                                            // Control D
        "185.228.168.9", "185.228.169.9", "185.228.168.168", "185.228.169.168", // CleanBrowsing
        "80.67.169.12", "80.67.169.40",                                       // FDN
        "193.110.81.0", "185.253.5.0",                                        // DNS0.eu
    ];

    /// <summary>Programmes souvent détournés pour lancer un script ou une DLL au démarrage.</summary>
    private static readonly HashSet<string> ScriptHosts = new(StringComparer.OrdinalIgnoreCase)
    {
        "powershell.exe", "pwsh.exe", "cmd.exe", "wscript.exe", "cscript.exe", "mshta.exe", "rundll32.exe", "regsvr32.exe",
    };

    /// <summary>Emplacements où un programme installé normalement ne se trouve jamais.</summary>
    private static readonly string[] UnusualFolders =
    [
        @"\appdata\local\temp\", @"\windows\temp\", @"\downloads\", @"\users\public\", @"\$recycle.bin\",
    ];

    public static IReadOnlyList<HostsEntry> AnalyzeHosts(string content)
    {
        var entries = new List<HostsEntry>();
        foreach (var raw in content.Split('\n'))
        {
            var line = raw.Split('#')[0].Trim();
            if (line.Length == 0)
                continue;

            var parts = line.Split((char[])[' ', '\t'], StringSplitOptions.RemoveEmptyEntries);
            if (parts.Length < 2)
                continue;

            foreach (var host in parts.Skip(1))
            {
                var lower = host.ToLowerInvariant();
                if (lower is "localhost" or "localhost.localdomain" || lower.EndsWith(".local", StringComparison.Ordinal))
                {
                    entries.Add(new HostsEntry(parts[0], host, FindingSeverity.Ok));
                    continue;
                }

                var sensitive = SensitiveDomains.Any(d => lower.Contains(d, StringComparison.Ordinal));
                // Une redirection vers une vraie adresse ou un blocage d'un site sensible est typique d'un malware.
                entries.Add(new HostsEntry(parts[0], host, sensitive ? FindingSeverity.Danger : FindingSeverity.Info));
            }
        }
        return entries;
    }

    /// <summary>Vrai pour les DNS habituels : box, réseau local, VPN (Tailscale…) ou résolveur public reconnu.</summary>
    public static bool IsExpectedDns(string server)
    {
        if (!IPAddress.TryParse(server, out var address))
            return false;
        if (KnownDns.Contains(server))
            return true;
        if (IPAddress.IsLoopback(address))
            return true;
        if (address.AddressFamily == AddressFamily.InterNetworkV6)
            return address.IsIPv6LinkLocal || address.IsIPv6SiteLocal || address.IsIPv6UniqueLocal;

        var b = address.GetAddressBytes();
        return b[0] == 10
               || (b[0] == 172 && b[1] >= 16 && b[1] <= 31)
               || (b[0] == 192 && b[1] == 168)
               || (b[0] == 100 && b[1] >= 64 && b[1] <= 127)   // CGNAT, dont Tailscale (100.100.100.100)
               || (b[0] == 169 && b[1] == 254);
    }

    /// <summary>
    /// Évalue un programme lancé au démarrage.
    /// </summary>
    /// <param name="file">Exécutable (déjà séparé de ses arguments).</param>
    /// <param name="isSigned">Signature valide ? null si impossible à vérifier.</param>
    public static StartupVerdict ClassifyStartup(string file, string arguments, bool exists, bool? isSigned, string windowsFolder)
    {
        var name = file[(file.LastIndexOfAny(['\\', '/']) + 1)..];
        var lowerPath = file.ToLowerInvariant();
        var lowerArgs = arguments.ToLowerInvariant();

        if (ScriptHosts.Contains(name))
        {
            var obfuscated = lowerArgs.Contains("-enc") || lowerArgs.Contains("frombase64string")
                             || lowerArgs.Contains("http://") || lowerArgs.Contains("https://") || lowerArgs.Contains("downloadstring");
            return obfuscated
                ? new StartupVerdict(FindingSeverity.Danger, $"lance une commande {name} masquée ou téléchargée depuis Internet")
                : new StartupVerdict(FindingSeverity.Warning, $"lance un script ou une DLL via {name}, technique fréquente des logiciels malveillants");
        }

        if (!exists)
            return new StartupVerdict(FindingSeverity.Info, "programme introuvable (reste d'une désinstallation)");

        var unusual = UnusualFolders.Any(f => lowerPath.Contains(f, StringComparison.Ordinal));
        var inWindows = lowerPath.StartsWith(windowsFolder.TrimEnd('\\').ToLowerInvariant() + "\\", StringComparison.Ordinal);

        if (unusual)
            return isSigned == true
                ? new StartupVerdict(FindingSeverity.Warning, "lancé depuis un dossier temporaire ou de téléchargement")
                : new StartupVerdict(FindingSeverity.Danger, "non signé et lancé depuis un dossier temporaire ou de téléchargement");

        if (isSigned == false && !inWindows)
            return new StartupVerdict(FindingSeverity.Info, "programme non signé (fréquent pour les petits logiciels libres)");

        return new StartupVerdict(FindingSeverity.Ok, "");
    }
}
