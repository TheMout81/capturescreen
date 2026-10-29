namespace PureClean.Infrastructure;

/// <summary>
/// Parcours et suppression de fichiers « défensifs » :
/// - ne suit jamais les liens symboliques / jonctions (évite de sortir du dossier ciblé) ;
/// - ignore silencieusement les dossiers inaccessibles et les fichiers verrouillés.
/// </summary>
public static class SafeFileSystem
{
    private static readonly EnumerationOptions FlatOptions = new()
    {
        RecurseSubdirectories = false,
        IgnoreInaccessible = true,
        AttributesToSkip = FileAttributes.ReparsePoint,
        ReturnSpecialDirectories = false,
    };

    public static IEnumerable<FileInfo> EnumerateFiles(string root, string pattern, bool recursive)
    {
        if (!Directory.Exists(root) || IsReparsePoint(root))
            yield break;

        var pending = new Stack<string>();
        pending.Push(root);

        while (pending.Count > 0)
        {
            var dir = pending.Pop();
            DirectoryInfo info;
            FileInfo[] files;
            try
            {
                info = new DirectoryInfo(dir);
                files = info.GetFiles(pattern, FlatOptions);
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or System.Security.SecurityException)
            {
                continue;
            }

            foreach (var file in files)
                yield return file;

            if (!recursive)
                continue;

            DirectoryInfo[] subDirs;
            try
            {
                subDirs = info.GetDirectories("*", FlatOptions);
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or System.Security.SecurityException)
            {
                continue;
            }

            foreach (var sub in subDirs)
                pending.Push(sub.FullName);
        }
    }

    /// <summary>Supprime un fichier ; renvoie false s'il est verrouillé ou protégé.</summary>
    public static bool TryDelete(FileInfo file)
    {
        try
        {
            if (file.IsReadOnly)
                file.IsReadOnly = false;
            file.Delete();
            return true;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or System.Security.SecurityException)
        {
            return false;
        }
    }

    /// <summary>Supprime les sous-dossiers vides (jamais la racine elle-même).</summary>
    public static void DeleteEmptySubdirectories(string root)
    {
        if (!Directory.Exists(root) || IsReparsePoint(root))
            return;

        DirectoryInfo[] subDirs;
        try
        {
            subDirs = new DirectoryInfo(root).GetDirectories("*", FlatOptions);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or System.Security.SecurityException)
        {
            return;
        }

        foreach (var sub in subDirs)
        {
            DeleteEmptySubdirectories(sub.FullName);
            try
            {
                if (!sub.EnumerateFileSystemInfos().Any())
                    sub.Delete(recursive: false);
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or System.Security.SecurityException)
            {
                // Dossier utilisé ou protégé : on le laisse.
            }
        }
    }

    private static bool IsReparsePoint(string path)
    {
        try
        {
            return new DirectoryInfo(path).Attributes.HasFlag(FileAttributes.ReparsePoint);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            return true;
        }
    }
}
