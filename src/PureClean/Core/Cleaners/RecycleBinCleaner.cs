using System.Runtime.InteropServices;
using PureClean.Infrastructure;
using PureClean.Models;

namespace PureClean.Core.Cleaners;

/// <summary>Vide la corbeille de tous les lecteurs via l'API Shell.</summary>
public sealed class RecycleBinCleaner : ICleaner
{
    public string Name => "Corbeille";
    public string Description => "Supprime définitivement le contenu de la corbeille de tous les lecteurs.";
    public CleanerCategory Category => CleanerCategory.Windows;
    public bool EnabledByDefault => true;
    public IReadOnlyList<string> BlockingProcesses => [];
    public bool IsAvailable => true;

    public ScanResult Scan(CancellationToken cancellationToken)
    {
        var info = new NativeMethods.SHQUERYRBINFO { cbSize = Marshal.SizeOf<NativeMethods.SHQUERYRBINFO>() };
        var hr = NativeMethods.SHQueryRecycleBin(null, ref info);
        return hr == 0 ? new ScanResult(info.i64Size, (int)Math.Min(info.i64NumItems, int.MaxValue)) : ScanResult.Empty;
    }

    public CleanResult Clean(CancellationToken cancellationToken)
    {
        var before = Scan(cancellationToken);
        if (before.FileCount == 0)
            return CleanResult.Empty;

        const uint flags = NativeMethods.SHERB_NOCONFIRMATION | NativeMethods.SHERB_NOPROGRESSUI | NativeMethods.SHERB_NOSOUND;
        var hr = NativeMethods.SHEmptyRecycleBin(IntPtr.Zero, null, flags);
        return hr == 0
            ? new CleanResult(before.Bytes, before.FileCount, 0)
            : new CleanResult(0, 0, before.FileCount);
    }
}
