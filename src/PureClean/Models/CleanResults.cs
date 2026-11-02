namespace PureClean.Models;

public enum CleanerCategory
{
    Windows,
    Applications,
    /// <summary>Historique et cookies des navigateurs : jamais cochés par défaut.</summary>
    Privacy,
    /// <summary>Opérations lourdes de Windows (DISM, anciennes installations…) : jamais cochées par défaut.</summary>
    Advanced,
    /// <summary>Dossiers ajoutés par l'utilisateur.</summary>
    Custom,
}

/// <summary>Résultat d'une analyse (aucune modification sur le disque).</summary>
/// <param name="Note">Texte affiché à la place de la taille quand elle ne peut pas être mesurée à l'avance.</param>
public sealed record ScanResult(long Bytes, int FileCount, string? Note = null)
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
