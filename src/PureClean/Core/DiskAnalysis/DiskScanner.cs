namespace PureClean.Core.DiskAnalysis;

public sealed class FolderNode
{
    public FolderNode(string name, string fullPath, FolderNode? parent)
    {
        Name = name;
        FullPath = fullPath;
        Parent = parent;
    }

    public string Name { get; }
    public string FullPath { get; }
    public FolderNode? Parent { get; }

    /// <summary>Taille totale (fichiers du dossier + sous-dossiers).</summary>
    public long Size { get; set; }

    /// <summary>Nombre total de fichiers (sous-dossiers compris).</summary>
    public long FileCount { get; set; }

    /// <summary>Fichiers placés directement dans ce dossier.</summary>
    public long OwnFilesSize { get; set; }
    public int OwnFileCount { get; set; }

    /// <summary>Sous-dossiers, triés du plus gros au plus petit après l'analyse.</summary>
    public List<FolderNode> Children { get; } = [];

    /// <summary>Le contenu n'a pas pu être lu (droits insuffisants).</summary>
    public bool AccessDenied { get; set; }

    /// <summary>Retire une taille de ce dossier et de tous ses parents (après une suppression).</summary>
    public void Subtract(long bytes, long files)
    {
        for (var node = this; node is not null; node = node.Parent)
        {
            node.Size = Math.Max(0, node.Size - bytes);
            node.FileCount = Math.Max(0, node.FileCount - files);
        }
    }

    /// <summary>Cherche le dossier correspondant à un chemin sous cette racine.</summary>
    public FolderNode? Find(string path)
    {
        var relative = Path.GetRelativePath(FullPath, path);
        if (relative == ".")
            return this;
        if (relative.StartsWith("..", StringComparison.Ordinal) || Path.IsPathRooted(relative))
            return null;

        var node = this;
        foreach (var part in relative.Split(Path.DirectorySeparatorChar, StringSplitOptions.RemoveEmptyEntries))
        {
            node = node.Children.FirstOrDefault(c => string.Equals(c.Name, part, StringComparison.OrdinalIgnoreCase));
            if (node is null)
                return null;
        }
        return node;
    }
}

public sealed record LargeFile(string Path, long Size, DateTime Modified);

public sealed record DiskScanResult(FolderNode Root, IReadOnlyList<LargeFile> LargestFiles, long FolderCount, TimeSpan Duration);

/// <summary>
/// Calcule la taille de chaque dossier d'une arborescence (à la manière de TreeSize).
/// Les liens symboliques et jonctions ne sont pas suivis (sinon des fichiers seraient comptés deux fois),
/// et les fichiers « en ligne uniquement » (OneDrive…) ne sont pas comptés car ils n'occupent pas le disque.
/// </summary>
public sealed class DiskScanner
{
    private const FileAttributes NotOnDisk = FileAttributes.Offline | (FileAttributes)0x00040000 | (FileAttributes)0x00400000;

    private static readonly EnumerationOptions Options = new()
    {
        RecurseSubdirectories = false,
        IgnoreInaccessible = false,
        AttributesToSkip = 0,
        ReturnSpecialDirectories = false,
    };

    private readonly int _largestCount;
    private readonly PriorityQueue<LargeFile, long> _largest = new();
    private readonly object _largestGate = new();
    private long _largestThreshold;
    private long _folderCount;

    public DiskScanner(int largestCount = 200) => _largestCount = largestCount;

    /// <param name="progress">Reçoit périodiquement le nombre de dossiers analysés et le dossier en cours.</param>
    public DiskScanResult Scan(string rootPath, IProgress<(long Folders, string Current)>? progress, CancellationToken cancellationToken)
    {
        var started = DateTime.UtcNow;
        var fullRoot = Path.GetFullPath(rootPath);
        var name = Path.GetFileName(fullRoot.TrimEnd(Path.DirectorySeparatorChar)) is { Length: > 0 } n ? n : fullRoot;
        var root = new FolderNode(name, fullRoot, null);

        var subDirectories = ReadFolder(root, cancellationToken);

        // Premier niveau en parallèle : chaque sous-arbre est indépendant.
        var children = subDirectories.Select(d => new FolderNode(d.Name, d.FullName, root)).ToList();
        Parallel.ForEach(children, new ParallelOptions { CancellationToken = cancellationToken, MaxDegreeOfParallelism = 4 },
            child => ScanFolder(child, progress, cancellationToken));

        Attach(root, children);

        var largest = _largest.UnorderedItems.Select(i => i.Element).OrderByDescending(f => f.Size).ToList();
        return new DiskScanResult(root, largest, Interlocked.Read(ref _folderCount), DateTime.UtcNow - started);
    }

    private void ScanFolder(FolderNode node, IProgress<(long, string)>? progress, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();

        var count = Interlocked.Increment(ref _folderCount);
        if (count % 250 == 0)
            progress?.Report((count, node.FullPath));

        var children = ReadFolder(node, cancellationToken).Select(d => new FolderNode(d.Name, d.FullName, node)).ToList();
        foreach (var child in children)
            ScanFolder(child, progress, cancellationToken);

        Attach(node, children);
    }

    /// <summary>Compte les fichiers du dossier et renvoie les sous-dossiers à parcourir.</summary>
    private List<DirectoryInfo> ReadFolder(FolderNode node, CancellationToken cancellationToken)
    {
        var directories = new List<DirectoryInfo>();
        try
        {
            foreach (var entry in new DirectoryInfo(node.FullPath).EnumerateFileSystemInfos("*", Options))
            {
                cancellationToken.ThrowIfCancellationRequested();
                if (entry is DirectoryInfo directory)
                {
                    if (!IsLink(directory))
                        directories.Add(directory);
                }
                else if (entry is FileInfo file && (file.Attributes & NotOnDisk) == 0 && !IsLinkFile(file))
                {
                    node.OwnFilesSize += file.Length;
                    node.OwnFileCount++;
                    OfferLargeFile(file);
                }
            }
        }
        catch (Exception ex) when (ex is UnauthorizedAccessException or IOException or System.Security.SecurityException)
        {
            node.AccessDenied = true;
        }
        return directories;
    }

    private static void Attach(FolderNode node, List<FolderNode> children)
    {
        children.Sort((a, b) => b.Size.CompareTo(a.Size));
        node.Children.AddRange(children);
        node.Size = node.OwnFilesSize + children.Sum(c => c.Size);
        node.FileCount = node.OwnFileCount + children.Sum(c => c.FileCount);
    }

    private static bool IsLink(DirectoryInfo directory)
    {
        if (!directory.Attributes.HasFlag(FileAttributes.ReparsePoint))
            return false;
        try
        {
            // Jonction ou lien symbolique : on ne suit pas. Les autres points d'analyse (OneDrive…) sont parcourus.
            return directory.LinkTarget is not null;
        }
        catch (IOException)
        {
            return true;
        }
    }

    private static bool IsLinkFile(FileInfo file)
        => file.Attributes.HasFlag(FileAttributes.ReparsePoint) && file.LinkTarget is not null;

    private void OfferLargeFile(FileInfo file)
    {
        var size = file.Length;
        if (size <= Interlocked.Read(ref _largestThreshold))
            return;

        lock (_largestGate)
        {
            _largest.Enqueue(new LargeFile(file.FullName, size, file.LastWriteTime), size);
            if (_largest.Count > _largestCount)
                _largest.Dequeue();
            if (_largest.Count == _largestCount && _largest.TryPeek(out _, out var smallest))
                Interlocked.Exchange(ref _largestThreshold, smallest);
        }
    }
}
