using System.Text.Json;
using PureClean.Infrastructure;

namespace PureClean.Core.Security;

public sealed record DefenderStatus(
    bool Available,
    string? Error,
    string Mode,
    bool RealTime,
    bool TamperProtected,
    int SignatureAgeDays,
    string SignatureVersion,
    long QuickScanAgeDays,
    long FullScanAgeDays);

public sealed record ThreatRecord(string Name, int Severity, DateTime? Date, int Status, string Resources)
{
    public string SeverityLabel => Severity switch
    {
        1 => "Faible",
        2 => "Modérée",
        4 => "Élevée",
        5 => "Grave",
        _ => "Inconnue",
    };

    public string StatusLabel => Status switch
    {
        1 => "Détectée",
        2 => "Nettoyée",
        3 => "En quarantaine",
        4 => "Supprimée",
        5 => "Autorisée",
        6 => "Bloquée",
        >= 100 => "Échec du traitement",
        _ => "Inconnu",
    };
}

public sealed record FirewallProfile(string Name, bool Enabled);

public sealed record DnsSetting(string Interface, int Index, IReadOnlyList<string> Servers);

public sealed record DefenderExclusions(IReadOnlyList<string> Paths, IReadOnlyList<string> Processes, IReadOnlyList<string> Extensions)
{
    public bool Any => Paths.Count + Processes.Count + Extensions.Count > 0;
    public IEnumerable<string> All => Paths.Concat(Processes).Concat(Extensions.Select(e => "*." + e.TrimStart('.', '*')));
}

/// <summary>État de Defender, du pare-feu et du DNS, lu en une seule commande PowerShell.</summary>
public sealed record SecurityProbe(
    DefenderStatus Defender,
    DefenderExclusions Exclusions,
    IReadOnlyList<ThreatRecord> Threats,
    IReadOnlyList<FirewallProfile> Firewall,
    IReadOnlyList<DnsSetting> Dns);

public enum ScanKind
{
    Quick,
    Full,
    Folder,
}

/// <summary>
/// Pilote Microsoft Defender via ses commandes PowerShell officielles. Les signatures et le moteur
/// sont maintenus par Microsoft : PureClean ne fait que les déclencher et afficher le résultat.
/// </summary>
public static class DefenderClient
{
    private const string ProbeScript = """
        $ErrorActionPreference = 'Stop'
        [Console]::OutputEncoding = [Text.Encoding]::UTF8
        $r = [ordered]@{}
        try {
          $s = Get-MpComputerStatus
          $r.Defender = [ordered]@{
            Available = $true; Mode = [string]$s.AMRunningMode
            RealTime = [bool]$s.RealTimeProtectionEnabled; TamperProtected = [bool]$s.IsTamperProtected
            SignatureAgeDays = [int]$s.AntivirusSignatureAge; SignatureVersion = [string]$s.AntivirusSignatureVersion
            QuickScanAgeDays = [long]$s.QuickScanAge; FullScanAgeDays = [long]$s.FullScanAge }
        } catch { $r.Defender = [ordered]@{ Available = $false; Error = $_.Exception.Message } }
        try {
          $p = Get-MpPreference
          $r.Exclusions = [ordered]@{
            Paths = @($p.ExclusionPath | Where-Object { $_ } | ForEach-Object { [string]$_ })
            Processes = @($p.ExclusionProcess | Where-Object { $_ } | ForEach-Object { [string]$_ })
            Extensions = @($p.ExclusionExtension | Where-Object { $_ } | ForEach-Object { [string]$_ }) }
        } catch { $r.Exclusions = [ordered]@{ Paths = @(); Processes = @(); Extensions = @() } }
        try {
          $names = @{}
          Get-MpThreat | ForEach-Object { $names[[string]$_.ThreatID] = $_ }
          $r.Threats = @(Get-MpThreatDetection | Sort-Object InitialDetectionTime -Descending | Select-Object -First 30 | ForEach-Object {
            $t = $names[[string]$_.ThreatID]
            [ordered]@{ Name = [string]$t.ThreatName; Severity = [int]$t.SeverityID
              Date = ([datetime]$_.InitialDetectionTime).ToString('o'); Status = [int]$_.ThreatStatusID
              Resources = (@($_.Resources) -join ' ; ') } })
        } catch { $r.Threats = @() }
        try {
          $r.Firewall = @(Get-NetFirewallProfile | ForEach-Object { [ordered]@{ Name = [string]$_.Name; Enabled = ([string]$_.Enabled -eq 'True') } })
        } catch { $r.Firewall = @() }
        try {
          $r.Dns = @(Get-DnsClientServerAddress -AddressFamily IPv4 | Where-Object { $_.ServerAddresses } | ForEach-Object {
            [ordered]@{ Interface = [string]$_.InterfaceAlias; Index = [int]$_.InterfaceIndex; Servers = @($_.ServerAddresses | ForEach-Object { [string]$_ }) } })
        } catch { $r.Dns = @() }
        ConvertTo-Json -InputObject $r -Depth 5 -Compress
        """;

    public static Task<SecurityProbe> ProbeAsync() => Task.Run(() =>
    {
        var output = ProcessHelper.RunPowerShell(ProbeScript);
        var json = output.StandardOutput.Trim();
        if (json.Length == 0)
            throw new InvalidOperationException("Impossible de lire l'état de sécurité : " + output.StandardError.Trim());
        return ParseProbe(json);
    });

    public static Task UpdateSignaturesAsync() => RunAsync("Update-MpSignature");

    /// <summary>Lance une analyse et attend sa fin (de quelques minutes à plus d'une heure pour une analyse complète).</summary>
    public static Task ScanAsync(ScanKind kind, string? folder = null) => RunAsync(kind switch
    {
        ScanKind.Quick => "Start-MpScan -ScanType QuickScan",
        ScanKind.Full => "Start-MpScan -ScanType FullScan",
        _ => $"Start-MpScan -ScanType CustomScan -ScanPath {Quote(folder ?? throw new ArgumentNullException(nameof(folder)))}",
    });

    /// <summary>Analyse hors ligne : Windows redémarre aussitôt et analyse avant le démarrage du système.</summary>
    public static Task OfflineScanAsync() => RunAsync("Start-MpWDOScan");

    public static Task EnableRealTimeAsync() => RunAsync("Set-MpPreference -DisableRealtimeMonitoring $false");

    public static Task EnableFirewallAsync() => RunAsync("Set-NetFirewallProfile -Profile Domain,Private,Public -Enabled True");

    public static Task RemoveExclusionsAsync(DefenderExclusions exclusions)
    {
        var commands = new List<string>();
        if (exclusions.Paths.Count > 0)
            commands.Add("Remove-MpPreference -ExclusionPath " + QuoteList(exclusions.Paths));
        if (exclusions.Processes.Count > 0)
            commands.Add("Remove-MpPreference -ExclusionProcess " + QuoteList(exclusions.Processes));
        if (exclusions.Extensions.Count > 0)
            commands.Add("Remove-MpPreference -ExclusionExtension " + QuoteList(exclusions.Extensions));
        return commands.Count == 0 ? Task.CompletedTask : RunAsync(string.Join("; ", commands));
    }

    public static Task ResetDnsAsync(int interfaceIndex)
        => RunAsync($"Set-DnsClientServerAddress -InterfaceIndex {interfaceIndex} -ResetServerAddresses");

    public static SecurityProbe ParseProbe(string json)
    {
        using var document = JsonDocument.Parse(json);
        var root = document.RootElement;

        var d = root.GetProperty("Defender");
        var defender = Bool(d, "Available")
            ? new DefenderStatus(true, null, Str(d, "Mode"), Bool(d, "RealTime"), Bool(d, "TamperProtected"),
                (int)Num(d, "SignatureAgeDays"), Str(d, "SignatureVersion"), Num(d, "QuickScanAgeDays"), Num(d, "FullScanAgeDays"))
            : new DefenderStatus(false, Str(d, "Error"), "", false, false, 0, "", 0, 0);

        var exclusions = root.TryGetProperty("Exclusions", out var e)
            ? new DefenderExclusions(Strings(e, "Paths"), Strings(e, "Processes"), Strings(e, "Extensions"))
            : new DefenderExclusions([], [], []);

        var threats = Items(root, "Threats").Select(t => new ThreatRecord(
            Str(t, "Name"), (int)Num(t, "Severity"),
            DateTime.TryParse(Str(t, "Date"), out var date) ? date : null,
            (int)Num(t, "Status"), Str(t, "Resources"))).ToList();

        var firewall = Items(root, "Firewall").Select(f => new FirewallProfile(Str(f, "Name"), Bool(f, "Enabled"))).ToList();
        var dns = Items(root, "Dns").Select(x => new DnsSetting(Str(x, "Interface"), (int)Num(x, "Index"), Strings(x, "Servers"))).ToList();

        return new SecurityProbe(defender, exclusions, threats, firewall, dns);
    }

    private static async Task RunAsync(string command)
    {
        var output = await Task.Run(() => ProcessHelper.RunPowerShell("$ErrorActionPreference = 'Stop'; " + command));
        if (output.ExitCode != 0)
        {
            var message = output.StandardError.Split('\n', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries).FirstOrDefault();
            throw new InvalidOperationException(string.IsNullOrEmpty(message) ? $"commande refusée (code {output.ExitCode})" : message);
        }
    }

    private static string Quote(string value) => "'" + value.Replace("'", "''") + "'";

    private static string QuoteList(IEnumerable<string> values) => "@(" + string.Join(",", values.Select(Quote)) + ")";

    // PowerShell 5 transforme parfois un tableau d'un seul élément en objet : les deux formes sont acceptées.
    private static IEnumerable<JsonElement> Items(JsonElement parent, string name)
    {
        if (!parent.TryGetProperty(name, out var value))
            return [];
        return value.ValueKind switch
        {
            JsonValueKind.Array => value.EnumerateArray().ToList(),
            JsonValueKind.Object => [value],
            _ => [],
        };
    }

    private static IReadOnlyList<string> Strings(JsonElement parent, string name)
    {
        if (!parent.TryGetProperty(name, out var value))
            return [];
        return value.ValueKind switch
        {
            JsonValueKind.Array => value.EnumerateArray().Select(v => v.ToString()).Where(s => s.Length > 0).ToList(),
            JsonValueKind.String => value.GetString() is { Length: > 0 } s ? [s] : [],
            _ => [],
        };
    }

    private static string Str(JsonElement parent, string name)
        => parent.TryGetProperty(name, out var v) && v.ValueKind == JsonValueKind.String ? v.GetString() ?? "" : "";

    private static bool Bool(JsonElement parent, string name)
        => parent.TryGetProperty(name, out var v) && v.ValueKind == JsonValueKind.True;

    private static long Num(JsonElement parent, string name)
        => parent.TryGetProperty(name, out var v) && v.ValueKind == JsonValueKind.Number && v.TryGetInt64(out var n) ? n : 0;
}
