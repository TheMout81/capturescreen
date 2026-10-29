namespace PureClean.Models;

public enum CleanerCategory
{
    Windows,
    Applications,
}

/// <summary>Résultat d'une analyse (aucune modification sur le disque).</summary>
public sealed record ScanResult(long Bytes, int FileCount)
{
    public static readonly ScanResult Empty = new(0, 0);
}

/// <summary>Résultat d'un nettoyage effectif.</summary>
public sealed record CleanResult(long BytesFreed, int FilesDeleted, int FilesSkipped)
{
    public static readonly CleanResult Empty = new(0, 0, 0);

    public CleanResult Add(CleanResult other)
        => new(BytesFreed + other.BytesFreed, FilesDeleted + other.FilesDeleted, FilesSkipped + other.FilesSkipped);
}
