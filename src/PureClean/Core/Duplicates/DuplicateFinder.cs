using System.Security.Cryptography;

namespace PureClean.Core.Duplicates;

public sealed record DuplicateFile(string Path, long Size, DateTime Modified);

public sealed record DuplicateGroup(long Size, IReadOnlyList<DuplicateFile> Files)
{
    /// <summary>Espace récupérable en ne gardant qu'un exemplaire.</summary>
    public long Wasted => Size * (Files.Count - 1);
}

/// <summary>
/// Trouve les fichiers au contenu identique : regroupement par taille, puis empreinte du début
/// et de la fin du fichier, puis empreinte SHA-256 complète. Seuls les fichiers réellement
/// identiques octet par octet (au sens de SHA-256) sont regroupés.
/// </summary>
public sealed class DuplicateFinder
{
    private const int SampleSize = 16 * 1024;
    private const FileAttributes NotOnDisk = FileAttributes.Offline | (FileAttributes)0x00040000 | (FileAttributes)0x00400000;

    /// <summary>Noms de dossiers jamais parcourus : y supprimer un « doublon » casserait un programme.</summary>
    private static readonly HashSet<string> ExcludedNames = new(StringComparer.OrdinalIgnoreCase)
    {
        "AppData", "$Recycle.Bin", "System Volume Information", ".git", ".svn", "node_modules",
    };

    private static readonly EnumerationOptions Options = new()
    {
        RecurseSubdirectories = false,
        IgnoreInaccessible = true,
        AttributesToSkip = 0,
    };

    private readonly long _minimumSize;
    private readonly IReadOnlyList<string> _excludedFolders;

    public DuplicateFinder(long minimumSize, IEnumerable<string>? excludedFolders = null)
    {
        _minimumSize = Math.Max(1, minimumSize);
        _excludedFolders = (excludedFolders ?? DefaultExcludedFolders())
            .Where(f => !string.IsNullOrEmpty(f))
            .Select(f => f.TrimEnd(Path.DirectorySeparatorChar))
            .ToList();
    }

    /// <summary>Dossiers de Windows et des programmes, exclus par défaut.</summary>
    public static IEnumerable<string> DefaultExcludedFolders() =>
    [
        Environment.GetFolderPath(Environment.SpecialFolder.Windows),
        Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles),
        Environment.GetFolderPath(Environment.SpecialFolder.ProgramFilesX86),
        Environment.GetFolderPath(Environment.SpecialFolder.CommonApplicationData),
    ];

    public IReadOnlyList<DuplicateGroup> Find(IEnumerable<string> roots, IProgress<string>? progress, CancellationToken cancellationToken)
    {
        // 1. Inventaire, regroupé par taille.
        var bySize = new Dictionary<long, List<DuplicateFile>>();
        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var count = 0;
        foreach (var root in roots)
        {
            foreach (var file in Enumerate(root, cancellationToken))
            {
                if (!seen.Add(file.Path))
                    continue;
                if (!bySize.TryGetValue(file.Size, out var list))
                    bySize[file.Size] = list = [];
                list.Add(file);
                if (++count % 500 == 0)
                    progress?.Report($"Recherche des fichiers… {count:N0} trouvés");
            }
        }

        var candidates = bySize.Values.Where(l => l.Count > 1).ToList();
        var total = candidates.Sum(l => l.Count);
        var done = 0;
        var groups = new List<DuplicateGroup>();

        foreach (var sameSize in candidates)
        {
            cancellationToken.ThrowIfCancellationRequested();

            // 2. Empreinte rapide (début + fin), 3. empreinte complète si nécessaire.
            foreach (var samePartial in GroupBy(sameSize, f => Hash(f, partial: true, cancellationToken)))
            {
                var needFull = sameSize[0].Size > 2 * SampleSize;
                var final = needFull ? GroupBy(samePartial, f => Hash(f, partial: false, cancellationToken)) : [samePartial];
                foreach (var identical in final)
                    groups.Add(new DuplicateGroup(identical[0].Size, identical.OrderBy(f => f.Modified).ToList()));
            }

            done += sameSize.Count;
            progress?.Report($"Comparaison du contenu… {done:N0} / {total:N0} fichiers");
        }

        return groups.OrderByDescending(g => g.Wasted).ToList();
    }

    private IEnumerable<DuplicateFile> Enumerate(string root, CancellationToken cancellationToken)
    {
        var pending = new Stack<string>();
        pending.Push(root);
        while (pending.Count > 0)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var directory = new DirectoryInfo(pending.Pop());
            if (IsExcluded(directory.FullName))
                continue;

            FileSystemInfo[] entries;
            try
            {
                entries = directory.GetFileSystemInfos("*", Options);
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or System.Security.SecurityException)
            {
                continue;
            }

            foreach (var entry in entries)
            {
                var isLink = entry.Attributes.HasFlag(FileAttributes.ReparsePoint) && entry.LinkTarget is not null;
                if (isLink)
                    continue;
                if (entry is DirectoryInfo sub)
                {
                    if (!ExcludedNames.Contains(sub.Name))
                        pending.Push(sub.FullName);
                }
                else if (entry is FileInfo file && file.Length >= _minimumSize && (file.Attributes & NotOnDisk) == 0)
                {
                    yield return new DuplicateFile(file.FullName, file.Length, file.LastWriteTime);
                }
            }
        }
    }

    private bool IsExcluded(string path)
        => _excludedFolders.Any(f => string.Equals(path, f, StringComparison.OrdinalIgnoreCase)
                                     || path.StartsWith(f + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase));

    /// <summary>Regroupe par clé et ne garde que les groupes d'au moins deux fichiers lisibles.</summary>
    private static List<List<DuplicateFile>> GroupBy(IEnumerable<DuplicateFile> files, Func<DuplicateFile, string?> key)
        => files.Select(f => (File: f, Key: key(f)))
            .Where(x => x.Key is not null)
            .GroupBy(x => x.Key!)
            .Where(g => g.Count() > 1)
            .Select(g => g.Select(x => x.File).ToList())
            .ToList();

    /// <summary>Empreinte SHA-256 du fichier, ou de son début et de sa fin. Null si le fichier est illisible.</summary>
    private static string? Hash(DuplicateFile file, bool partial, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        try
        {
            using var stream = new FileStream(file.Path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete,
                bufferSize: 1024 * 1024, FileOptions.SequentialScan);
            if (!partial)
                return Convert.ToHexString(SHA256.HashData(stream));

            using var hash = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
            var buffer = new byte[SampleSize];
            hash.AppendData(buffer, 0, stream.ReadAtLeast(buffer, buffer.Length, throwOnEndOfStream: false));
            if (stream.Length > 2 * SampleSize)
            {
                stream.Seek(-SampleSize, SeekOrigin.End);
                hash.AppendData(buffer, 0, stream.ReadAtLeast(buffer, buffer.Length, throwOnEndOfStream: false));
            }
            return Convert.ToHexString(hash.GetHashAndReset());
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            return null;
        }
    }
}
