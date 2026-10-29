using Microsoft.Win32;

namespace PureClean.Core.Tweaks;

/// <summary>
/// Une valeur de registre : <paramref name="Applied"/> quand l'optimisation est active,
/// <paramref name="Default"/> pour revenir au comportement Windows (null = supprimer la valeur).
/// </summary>
public sealed record RegistrySetting(
    RegistryHive Hive,
    string KeyPath,
    string ValueName,
    RegistryValueKind Kind,
    object Applied,
    object? Default)
{
    public bool IsApplied()
    {
        using var baseKey = RegistryKey.OpenBaseKey(Hive, RegistryView.Registry64);
        using var key = baseKey.OpenSubKey(KeyPath);
        var current = key?.GetValue(ValueName);
        return current is not null && ValuesEqual(current, Applied);
    }

    public void Write(bool apply)
    {
        using var baseKey = RegistryKey.OpenBaseKey(Hive, RegistryView.Registry64);
        var target = apply ? Applied : Default;

        if (target is null)
        {
            using var existing = baseKey.OpenSubKey(KeyPath, writable: true);
            existing?.DeleteValue(ValueName, throwOnMissingValue: false);
            return;
        }

        using var key = baseKey.CreateSubKey(KeyPath, writable: true);
        key.SetValue(ValueName, target, Kind);
    }

    private static bool ValuesEqual(object current, object expected) => expected switch
    {
        int i => current is int c && c == i,
        string s => current is string c && string.Equals(c, s, StringComparison.OrdinalIgnoreCase),
        _ => Equals(current, expected),
    };
}

public sealed class Tweak
{
    public Tweak(string group, string name, string description, IReadOnlyList<RegistrySetting> settings, bool requiresRestart = false)
    {
        Group = group;
        Name = name;
        Description = description;
        Settings = settings;
        RequiresRestart = requiresRestart;
    }

    public string Group { get; }
    public string Name { get; }
    public string Description { get; }
    public IReadOnlyList<RegistrySetting> Settings { get; }

    /// <summary>Prise en compte après redémarrage ou reconnexion de la session.</summary>
    public bool RequiresRestart { get; }

    public bool IsApplied() => Settings.All(s => s.IsApplied());

    public void Apply() => Write(apply: true);

    public void Revert() => Write(apply: false);

    private void Write(bool apply)
    {
        foreach (var setting in Settings)
            setting.Write(apply);
    }
}
