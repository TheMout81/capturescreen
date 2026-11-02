namespace PureClean.Infrastructure;

/// <summary>Empêche d'ajouter comme « dossier à vider » un emplacement dont la perte serait grave.</summary>
public static class PathSafety
{
    /// <summary>Renvoie la raison du refus, ou null si le dossier peut être vidé.</summary>
    public static string? WhyProtected(string path)
    {
        string full;
        try
        {
            full = Normalize(Path.GetFullPath(path));
        }
        catch (Exception ex) when (ex is ArgumentException or NotSupportedException or PathTooLongException)
        {
            return "Chemin invalide.";
        }

        var root = Path.GetPathRoot(full);
        if (root is not null && string.Equals(full, Normalize(root), StringComparison.OrdinalIgnoreCase))
            return "Impossible de vider la racine d'un lecteur.";

        // Dossiers système : ni eux, ni leurs sous-dossiers, ni leurs dossiers parents.
        foreach (var system in Folders(
                     Environment.SpecialFolder.Windows,
                     Environment.SpecialFolder.ProgramFiles,
                     Environment.SpecialFolder.ProgramFilesX86,
                     Environment.SpecialFolder.CommonApplicationData))
        {
            if (IsSameOrInside(full, system) || IsSameOrInside(system, full))
                return "Ce dossier contient des fichiers de Windows ou de vos programmes.";
        }

        // Dossiers personnels : ni eux, ni leurs parents (leurs sous-dossiers restent autorisés).
        var profile = Normalize(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile));
        var personal = Folders(
                Environment.SpecialFolder.UserProfile,
                Environment.SpecialFolder.Desktop,
                Environment.SpecialFolder.MyDocuments,
                Environment.SpecialFolder.MyPictures,
                Environment.SpecialFolder.MyMusic,
                Environment.SpecialFolder.MyVideos,
                Environment.SpecialFolder.ApplicationData,
                Environment.SpecialFolder.LocalApplicationData)
            .Append(Normalize(Path.GetDirectoryName(profile) ?? profile)); // C:\Users

        foreach (var folder in personal)
        {
            if (IsSameOrInside(folder, full))
                return "Ce dossier contient vos documents ou les données de vos applications.";
        }

        return null;
    }

    private static IEnumerable<string> Folders(params Environment.SpecialFolder[] folders)
        => folders.Select(Environment.GetFolderPath).Where(p => !string.IsNullOrEmpty(p)).Select(Normalize);

    private static string Normalize(string path) => path.TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);

    /// <summary>Vrai si <paramref name="path"/> est <paramref name="folder"/> ou se trouve dedans.</summary>
    private static bool IsSameOrInside(string path, string folder)
        => string.Equals(path, folder, StringComparison.OrdinalIgnoreCase)
           || path.StartsWith(folder + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase);
}
