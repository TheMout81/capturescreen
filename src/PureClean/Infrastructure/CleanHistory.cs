using System.Text.Json;

namespace PureClean.Infrastructure;

public sealed class CleanHistoryEntry
{
    public DateTime Date { get; set; }
    public long BytesFreed { get; set; }
    public int FilesDeleted { get; set; }
    public bool Automatic { get; set; }
}

public sealed class CleanHistoryData
{
    public long TotalBytesFreed { get; set; }
    public List<CleanHistoryEntry> Entries { get; set; } = [];
}

/// <summary>
/// Historique des nettoyages (%LOCALAPPDATA%\PureClean\history.json), séparé des paramètres :
/// le nettoyage automatique peut l'enrichir pendant que l'application est ouverte sans rien écraser,
/// car chaque ajout relit le fichier juste avant de l'écrire.
/// </summary>
public static class CleanHistory
{
    private const int MaxEntries = 100;
    private static readonly JsonSerializerOptions JsonOptions = new() { WriteIndented = true };

    private static string FilePath => Path.Combine(AppSettings.Folder, "history.json");

    public static CleanHistoryData Load()
    {
        try
        {
            if (File.Exists(FilePath))
                return JsonSerializer.Deserialize<CleanHistoryData>(File.ReadAllText(FilePath), JsonOptions) ?? new CleanHistoryData();
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or JsonException)
        {
            // Historique illisible : on repart de zéro.
        }
        return new CleanHistoryData();
    }

    public static void Record(long bytesFreed, int filesDeleted, bool automatic)
    {
        Update(data =>
        {
            data.TotalBytesFreed += bytesFreed;
            data.Entries.Insert(0, new CleanHistoryEntry
            {
                Date = DateTime.Now,
                BytesFreed = bytesFreed,
                FilesDeleted = filesDeleted,
                Automatic = automatic,
            });
            if (data.Entries.Count > MaxEntries)
                data.Entries.RemoveRange(MaxEntries, data.Entries.Count - MaxEntries);
        });
    }

    public static void Clear() => Update(data =>
    {
        data.TotalBytesFreed = 0;
        data.Entries.Clear();
    });

    private static void Update(Action<CleanHistoryData> change)
    {
        // Verrou inter-processus : l'application et le nettoyage automatique peuvent écrire en même temps.
        using var mutex = new Mutex(false, @"Local\PureClean.History");
        var acquired = false;
        try
        {
            try
            {
                acquired = mutex.WaitOne(TimeSpan.FromSeconds(10));
            }
            catch (AbandonedMutexException)
            {
                acquired = true;
            }

            var data = Load();
            change(data);
            Directory.CreateDirectory(AppSettings.Folder);
            var temp = FilePath + ".tmp";
            File.WriteAllText(temp, JsonSerializer.Serialize(data, JsonOptions));
            File.Move(temp, FilePath, overwrite: true);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            // Historique non enregistré : sans conséquence sur le nettoyage.
        }
        finally
        {
            if (acquired)
                mutex.ReleaseMutex();
        }
    }
}
