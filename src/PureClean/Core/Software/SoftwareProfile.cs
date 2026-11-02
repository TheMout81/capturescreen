using System.Text.Json;
using PureClean.Core.Updates;

namespace PureClean.Core.Software;

public sealed class SoftwareEntry
{
    public string Id { get; set; } = "";
    public string Name { get; set; } = "";
    public string Category { get; set; } = SoftwareCatalog.UserCategory;
    public string Source { get; set; } = SoftwareCatalog.WingetSource;
    public bool Selected { get; set; }

    public SoftwarePackage ToPackage() => new(Id, Name, Category, Source);

    public static SoftwareEntry From(SoftwarePackage package, bool selected) => new()
    {
        Id = package.Id,
        Name = package.Name,
        Category = package.Category,
        Source = package.Source,
        Selected = selected,
    };
}

/// <summary>Paramètres du serveur RustDesk auto-hébergé appliqués après l'installation.</summary>
public sealed class RustDeskSettings
{
    /// <summary>Chaîne exportée depuis RustDesk (Paramètres → Réseau → Exporter la configuration du serveur).</summary>
    public string? ConfigString { get; set; }

    public string? IdServer { get; set; }
    public string? RelayServer { get; set; }
    public string? Key { get; set; }

    public bool IsConfigured => !string.IsNullOrWhiteSpace(ConfigString) || !string.IsNullOrWhiteSpace(IdServer);
}

/// <summary>
/// Liste de logiciels enregistrée dans un fichier, pour la réutiliser sur un autre PC.
/// L'import accepte aussi les fichiers produits par « winget export ».
/// </summary>
public sealed class SoftwareProfile
{
    public const string FormatName = "PureClean.SoftwareProfile";

    private static readonly JsonSerializerOptions JsonOptions = new() { WriteIndented = true };

    public string Format { get; set; } = FormatName;
    public int Version { get; set; } = 1;
    public List<SoftwareEntry> Packages { get; set; } = [];
    public RustDeskSettings? RustDesk { get; set; }

    /// <summary>Vrai si le fichier venait de « winget export » (les noms et catégories sont alors inconnus).</summary>
    public bool FromWinget { get; private set; }

    public void Save(string path) => File.WriteAllText(path, JsonSerializer.Serialize(this, JsonOptions));

    public static SoftwareProfile Parse(string json)
    {
        using var document = JsonDocument.Parse(json);
        var root = document.RootElement;

        if (root.TryGetProperty("Format", out var format) && format.GetString() == FormatName)
        {
            var profile = JsonSerializer.Deserialize<SoftwareProfile>(json, JsonOptions) ?? new SoftwareProfile();
            profile.Packages.RemoveAll(p => string.IsNullOrWhiteSpace(p.Id));
            foreach (var package in profile.Packages.Where(p => string.IsNullOrWhiteSpace(p.Name)))
                package.Name = package.Id;
            return profile;
        }

        if (root.TryGetProperty("Sources", out _))
        {
            return new SoftwareProfile
            {
                FromWinget = true,
                Packages = WingetClient.ParseWingetExport(json)
                    .Select(p => FromReference(p, "Importés"))
                    .ToList(),
            };
        }

        throw new InvalidDataException("Ce fichier n'est ni un profil PureClean ni un export winget.");
    }

    public static SoftwareEntry FromReference(PackageReference reference, string category) => new()
    {
        Id = reference.Id,
        Name = reference.Id,
        Category = category,
        Source = reference.Source,
        Selected = true,
    };
}
