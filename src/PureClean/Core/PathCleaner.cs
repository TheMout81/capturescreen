using PureClean.Infrastructure;
using PureClean.Models;

namespace PureClean.Core;

/// <summary>Un dossier à nettoyer, avec un filtre de nom de fichier.</summary>
public sealed record CleanTarget(string Path, string Pattern = "*", bool Recursive = true);

/// <summary>
/// Nettoyeur générique basé sur une liste de dossiers.
/// Les cibles sont calculées à chaque appel (les profils de navigateurs peuvent changer).
/// </summary>
public class PathCleaner : ICleaner
{
    private readonly Func<IEnumerable<CleanTarget>> _targets;
    private readonly Func<bool>? _isAvailable;

    public PathCleaner(
        string name,
        string description,
        CleanerCategory category,
        Func<IEnumerable<CleanTarget>> targets,
        bool enabledByDefault = true,
        TimeSpan? minimumAge = null,
        IReadOnlyList<string>? blockingProcesses = null,
        Func<bool>? isAvailable = null)
    {
        Name = name;
        Description = description;
        Category = category;
        EnabledByDefault = enabledByDefault;
        MinimumAge = minimumAge ?? TimeSpan.Zero;
        BlockingProcesses = blockingProcesses ?? [];
        _targets = targets;
        _isAvailable = isAvailable;
    }

    public string Name { get; }
    public string Description { get; }
    public CleanerCategory Category { get; }
    public bool EnabledByDefault { get; }
    public IReadOnlyList<string> BlockingProcesses { get; }

    /// <summary>Les fichiers plus récents que cet âge sont conservés (ex. installations en cours).</summary>
    public TimeSpan MinimumAge { get; }

    public bool IsAvailable => _isAvailable?.Invoke() ?? Targets().Any(t => Directory.Exists(t.Path));

    public ScanResult Scan(CancellationToken cancellationToken)
    {
        long bytes = 0;
        var count = 0;
        foreach (var file in EligibleFiles(cancellationToken))
        {
            bytes += SafeLength(file);
            count++;
        }
        return new ScanResult(bytes, count);
    }

    public CleanResult Clean(CancellationToken cancellationToken)
    {
        long freed = 0;
        int deleted = 0, skipped = 0;

        // Matérialise la liste avant suppression pour ne pas modifier un dossier en cours d'énumération.
        foreach (var file in EligibleFiles(cancellationToken).ToList())
        {
            cancellationToken.ThrowIfCancellationRequested();
            var length = SafeLength(file);
            if (SafeFileSystem.TryDelete(file))
            {
                freed += length;
                deleted++;
            }
            else
            {
                skipped++;
            }
        }

        foreach (var target in Targets().Where(t => t.Recursive))
            SafeFileSystem.DeleteEmptySubdirectories(target.Path);

        return new CleanResult(freed, deleted, skipped);
    }

    private IEnumerable<CleanTarget> Targets()
    {
        try
        {
            return _targets().Where(t => !string.IsNullOrWhiteSpace(t.Path)).ToList();
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            return [];
        }
    }

    private IEnumerable<FileInfo> EligibleFiles(CancellationToken cancellationToken)
    {
        var threshold = DateTime.UtcNow - MinimumAge;
        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

        foreach (var target in Targets())
        {
            foreach (var file in SafeFileSystem.EnumerateFiles(target.Path, target.Pattern, target.Recursive))
            {
                cancellationToken.ThrowIfCancellationRequested();
                if (MinimumAge > TimeSpan.Zero && SafeLastWrite(file) > threshold)
                    continue;
                if (seen.Add(file.FullName))
                    yield return file;
            }
        }
    }

    private static long SafeLength(FileInfo file)
    {
        try { return file.Length; }
        catch (IOException) { return 0; }
    }

    private static DateTime SafeLastWrite(FileInfo file)
    {
        try { return file.LastWriteTimeUtc; }
        catch (IOException) { return DateTime.UtcNow; }
    }
}
