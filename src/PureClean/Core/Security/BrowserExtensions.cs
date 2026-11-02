using System.Text.Json;

namespace PureClean.Core.Security;

public sealed record BrowserExtension(string Browser, string Name, string Id, bool Enabled);

/// <summary>Liste les extensions installées dans les navigateurs (lecture seule).</summary>
public static class BrowserExtensions
{
    public static IReadOnlyList<BrowserExtension> ReadAll()
    {
        var local = Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData);
        var roaming = Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData);
        var result = new List<BrowserExtension>();

        (string Browser, string UserData)[] chromium =
        [
            ("Chrome", Path.Combine(local, "Google", "Chrome", "User Data")),
            ("Edge", Path.Combine(local, "Microsoft", "Edge", "User Data")),
            ("Brave", Path.Combine(local, "BraveSoftware", "Brave-Browser", "User Data")),
            ("Vivaldi", Path.Combine(local, "Vivaldi", "User Data")),
            ("Opera", Path.Combine(roaming, "Opera Software", "Opera Stable")),
        ];
        foreach (var (browser, userData) in chromium)
        {
            foreach (var profile in ChromiumProfiles(userData))
                result.AddRange(ReadChromiumProfile(browser, profile));
        }

        var firefox = Path.Combine(roaming, "Mozilla", "Firefox", "Profiles");
        if (Directory.Exists(firefox))
        {
            foreach (var profile in Directory.GetDirectories(firefox))
            {
                var file = Path.Combine(profile, "extensions.json");
                if (File.Exists(file))
                    result.AddRange(ParseFirefox(SafeRead(file)));
            }
        }

        return result
            .GroupBy(e => (e.Browser, e.Id))
            .Select(g => g.First())
            .OrderBy(e => e.Browser).ThenBy(e => e.Name, StringComparer.CurrentCultureIgnoreCase)
            .ToList();
    }

    public static IEnumerable<BrowserExtension> ReadChromiumProfile(string browser, string profile)
    {
        var extensions = Path.Combine(profile, "Extensions");
        if (!Directory.Exists(extensions))
            yield break;

        foreach (var extension in Directory.GetDirectories(extensions))
        {
            var version = Directory.GetDirectories(extension).OrderByDescending(v => v, StringComparer.Ordinal).FirstOrDefault();
            if (version is null)
                continue;
            var name = ChromiumName(version);
            if (name is not null)
                yield return new BrowserExtension(browser, name, Path.GetFileName(extension), true);
        }
    }

    /// <summary>Nom d'une extension Chromium, y compris quand il est traduit (« __MSG_appName__ »).</summary>
    public static string? ChromiumName(string versionFolder)
    {
        try
        {
            using var manifest = JsonDocument.Parse(SafeRead(Path.Combine(versionFolder, "manifest.json")));
            var name = manifest.RootElement.TryGetProperty("name", out var n) ? n.GetString() : null;
            if (name is null || !name.StartsWith("__MSG_", StringComparison.Ordinal))
                return name;

            var key = name[6..].TrimEnd('_');
            var locale = manifest.RootElement.TryGetProperty("default_locale", out var l) ? l.GetString() : "en";
            using var messages = JsonDocument.Parse(SafeRead(Path.Combine(versionFolder, "_locales", locale ?? "en", "messages.json")));
            foreach (var property in messages.RootElement.EnumerateObject())
            {
                if (property.Name.Equals(key, StringComparison.OrdinalIgnoreCase) && property.Value.TryGetProperty("message", out var message))
                    return message.GetString();
            }
            return key;
        }
        catch (Exception ex) when (ex is JsonException or IOException or UnauthorizedAccessException)
        {
            return null;
        }
    }

    /// <summary>Extensions installées par l'utilisateur dans un profil Firefox (fichier extensions.json).</summary>
    public static IEnumerable<BrowserExtension> ParseFirefox(string json)
    {
        if (string.IsNullOrWhiteSpace(json))
            return [];
        try
        {
            using var document = JsonDocument.Parse(json);
            if (!document.RootElement.TryGetProperty("addons", out var addons))
                return [];

            var result = new List<BrowserExtension>();
            foreach (var addon in addons.EnumerateArray())
            {
                if (Str(addon, "type") != "extension" || Str(addon, "location") != "app-profile")
                    continue;
                var name = addon.TryGetProperty("defaultLocale", out var locale) ? Str(locale, "name") : "";
                var active = addon.TryGetProperty("active", out var a) && a.ValueKind == JsonValueKind.True;
                result.Add(new BrowserExtension("Firefox", name.Length > 0 ? name : Str(addon, "id"), Str(addon, "id"), active));
            }
            return result;
        }
        catch (JsonException)
        {
            return [];
        }
    }

    private static IEnumerable<string> ChromiumProfiles(string userData)
    {
        if (!Directory.Exists(userData))
            return [];
        // Opera n'a qu'un profil, à la racine.
        if (Directory.Exists(Path.Combine(userData, "Extensions")))
            return [userData];
        return Directory.GetDirectories(userData).Where(d =>
        {
            var name = Path.GetFileName(d);
            return name == "Default" || name.StartsWith("Profile ", StringComparison.Ordinal);
        });
    }

    private static string Str(JsonElement element, string name)
        => element.TryGetProperty(name, out var v) && v.ValueKind == JsonValueKind.String ? v.GetString() ?? "" : "";

    private static string SafeRead(string path)
    {
        try
        {
            using var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete);
            using var reader = new StreamReader(stream);
            return reader.ReadToEnd();
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            return "";
        }
    }
}
