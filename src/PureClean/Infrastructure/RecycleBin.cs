using Microsoft.VisualBasic.FileIO;

namespace PureClean.Infrastructure;

public static class RecycleBin
{
    /// <summary>
    /// Envoie un fichier ou un dossier à la corbeille. Les dialogues de Windows restent actifs :
    /// si l'élément est trop gros pour la corbeille, Windows demande avant de le supprimer définitivement.
    /// Renvoie false si l'utilisateur a annulé.
    /// </summary>
    public static bool Send(string path)
    {
        try
        {
            if (Directory.Exists(path))
                FileSystem.DeleteDirectory(path, UIOption.AllDialogs, RecycleOption.SendToRecycleBin, UICancelOption.ThrowException);
            else
                FileSystem.DeleteFile(path, UIOption.AllDialogs, RecycleOption.SendToRecycleBin, UICancelOption.ThrowException);
            return true;
        }
        catch (OperationCanceledException)
        {
            return false;
        }
    }
}
