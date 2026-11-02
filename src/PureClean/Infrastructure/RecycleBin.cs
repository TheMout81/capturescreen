using System.Runtime.InteropServices;

namespace PureClean.Infrastructure;

public static class RecycleBin
{
    private const uint FO_DELETE = 0x0003;
    private const ushort FOF_NOCONFIRMATION = 0x0010;
    private const ushort FOF_ALLOWUNDO = 0x0040;
    private const ushort FOF_WANTNUKEWARNING = 0x4000;

    // Alignement naturel : correct pour un processus 64 bits (x64, ARM64).
    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
    private struct SHFILEOPSTRUCT
    {
        public IntPtr hwnd;
        public uint wFunc;
        public string pFrom;
        public string? pTo;
        public ushort fFlags;
        [MarshalAs(UnmanagedType.Bool)] public bool fAnyOperationsAborted;
        public IntPtr hNameMappings;
        public string? lpszProgressTitle;
    }

    [DllImport("shell32.dll", CharSet = CharSet.Unicode)]
    private static extern int SHFileOperation(ref SHFILEOPSTRUCT lpFileOp);

    /// <summary>Envoie un fichier ou un dossier à la corbeille. Renvoie false si rien n'a été supprimé.</summary>
    public static bool Send(string path) => SendMany([path]) > 0;

    /// <summary>
    /// Envoie plusieurs éléments à la corbeille en une seule opération (une seule fenêtre de progression).
    /// La confirmation est demandée par PureClean avant l'appel ; Windows avertit quand même
    /// si un élément ne peut pas aller à la corbeille (trop gros) et devrait être supprimé définitivement.
    /// Renvoie le nombre d'éléments effectivement supprimés.
    /// </summary>
    public static int SendMany(IReadOnlyCollection<string> paths)
    {
        if (paths.Count == 0)
            return 0;

        var operation = new SHFILEOPSTRUCT
        {
            wFunc = FO_DELETE,
            // Liste de chemins séparés par \0 et terminée par un double \0.
            pFrom = string.Join('\0', paths) + "\0\0",
            fFlags = FOF_ALLOWUNDO | FOF_NOCONFIRMATION | FOF_WANTNUKEWARNING,
        };

        var result = SHFileOperation(ref operation);
        if (result != 0 && !operation.fAnyOperationsAborted)
            throw new IOException($"Windows a refusé la suppression (code 0x{result:X}).");

        return paths.Count(p => !File.Exists(p) && !Directory.Exists(p));
    }
}
