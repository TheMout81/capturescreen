using System.Globalization;
using PureClean.Infrastructure;
using PureClean.Models;

namespace PureClean.Core;

/// <summary>
/// Nettoyage sans fenêtre lancé par le Planificateur de tâches (« PureClean.exe --auto-clean »).
/// Utilise la sélection de la page Nettoyage, sauf le nettoyage avancé (long et irréversible).
/// </summary>
public static class AutoClean
{
    public const string Argument = "--auto-clean";

    private const long MaxLogSize = 512 * 1024;

    public static string LogPath => Path.Combine(AppSettings.Folder, "auto-clean.log");

    public static CleanResult Run(AppSettings settings)
    {
        var log = new List<string> { $"=== {DateTime.Now.ToString("G", CultureInfo.CurrentCulture)} : nettoyage automatique" };
        var total = CleanResult.Empty;

        var cleaners = CleanerCatalog.CreateAll(settings.CustomFolders)
            .Where(c => c.Category != CleanerCategory.Advanced && c.IsAvailable)
            .Where(c => settings.SelectedCleaners is { } selected ? selected.Contains(c.Id) : c.EnabledByDefault);

        foreach (var cleaner in cleaners)
        {
            try
            {
                var result = cleaner.Clean(CancellationToken.None);
                total = total.Add(result);
                log.Add($"  {cleaner.Name} : {ByteFormatter.Format(result.BytesFreed)} libérés, {result.FilesDeleted} fichier(s)" +
                        (result.FilesSkipped > 0 ? $", {result.FilesSkipped} en cours d'utilisation" : ""));
            }
            catch (Exception ex)
            {
                // Un élément en échec (navigateur ouvert, droits…) n'empêche pas les suivants.
                log.Add($"  {cleaner.Name} : ignoré ({ex.Message})");
            }
        }

        log.Add($"  Total : {ByteFormatter.Format(total.BytesFreed)} libérés.");
        if (total.BytesFreed > 0 || total.FilesDeleted > 0)
            CleanHistory.Record(total.BytesFreed, total.FilesDeleted, automatic: true);
        WriteLog(log);
        return total;
    }

    private static void WriteLog(IEnumerable<string> lines)
    {
        try
        {
            Directory.CreateDirectory(AppSettings.Folder);
            if (File.Exists(LogPath) && new FileInfo(LogPath).Length > MaxLogSize)
                File.Delete(LogPath);
            File.AppendAllLines(LogPath, lines);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            // Journal facultatif.
        }
    }
}
