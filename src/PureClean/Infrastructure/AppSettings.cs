using System.Text.Json;
using System.Text.Json.Serialization;

namespace PureClean.Infrastructure;

public enum ThemeMode
{
    System,
    Light,
    Dark,
}

public sealed class CleanHistoryEntry
{
    public DateTime Date { get; set; }
    public long BytesFreed { get; set; }
    public int FilesDeleted { get; set; }
    public bool Automatic { get; set; }
}

/// <summary>
/// Préférences de l'utilisateur, stockées uniquement sur ce PC :
/// %LOCALAPPDATA%\PureClean\settings.json.
/// </summary>
public sealed class AppSettings
{
    private const int MaxHistory = 100;
    private static readonly object Gate = new();

    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        WriteIndented = true,
        Converters = { new JsonStringEnumConverter() },
    };

    private static AppSettings? _current;

    public static string Folder { get; } =
        Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "PureClean");

    private static string FilePath => Path.Combine(Folder, "settings.json");

    public static AppSettings Current
    {
        get
        {
            lock (Gate)
                return _current ??= Load();
        }
    }

    public ThemeMode Theme { get; set; } = ThemeMode.System;

    /// <summary>Noms des éléments cochés dans Nettoyage ; null = sélection par défaut.</summary>
    public List<string>? SelectedCleaners { get; set; }

    /// <summary>Dossiers ajoutés par l'utilisateur dont le contenu est vidé au nettoyage.</summary>
    public List<string> CustomFolders { get; set; } = [];

    public long TotalBytesFreed { get; set; }

    public List<CleanHistoryEntry> History { get; set; } = [];

    public ScheduleSettings Schedule { get; set; } = new();

    public void RecordClean(long bytesFreed, int filesDeleted, bool automatic)
    {
        lock (Gate)
        {
            TotalBytesFreed += bytesFreed;
            History.Insert(0, new CleanHistoryEntry
            {
                Date = DateTime.Now,
                BytesFreed = bytesFreed,
                FilesDeleted = filesDeleted,
                Automatic = automatic,
            });
            if (History.Count > MaxHistory)
                History.RemoveRange(MaxHistory, History.Count - MaxHistory);
        }
        Save();
    }

    public void Save()
    {
        lock (Gate)
        {
            try
            {
                Directory.CreateDirectory(Folder);
                var temp = FilePath + ".tmp";
                File.WriteAllText(temp, JsonSerializer.Serialize(this, JsonOptions));
                File.Move(temp, FilePath, overwrite: true);
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                // Paramètres non enregistrés : l'application continue avec ceux en mémoire.
            }
        }
    }

    private static AppSettings Load()
    {
        try
        {
            if (File.Exists(FilePath))
                return JsonSerializer.Deserialize<AppSettings>(File.ReadAllText(FilePath), JsonOptions) ?? new AppSettings();
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or JsonException)
        {
            // Fichier illisible ou corrompu : on repart des valeurs par défaut.
        }
        return new AppSettings();
    }
}

public enum ScheduleFrequency
{
    Daily,
    Weekly,
    Monthly,
}

public sealed class ScheduleSettings
{
    public bool Enabled { get; set; }
    public ScheduleFrequency Frequency { get; set; } = ScheduleFrequency.Weekly;
    public DayOfWeek Day { get; set; } = DayOfWeek.Sunday;
    public int Hour { get; set; } = 12;
}
