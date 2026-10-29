using PureClean.Infrastructure;
using PureClean.Models;

namespace PureClean.Core.Cleaners;

/// <summary>Équivalent de « ipconfig /flushdns ». Ne libère pas d'espace disque.</summary>
public sealed class DnsCacheCleaner : ICleaner
{
    public string Name => "Cache DNS";
    public string Description => "Vide le cache de résolution DNS (utile après un changement de réseau ou de DNS).";
    public CleanerCategory Category => CleanerCategory.Windows;
    public bool EnabledByDefault => false;
    public IReadOnlyList<string> BlockingProcesses => [];
    public bool IsAvailable => true;

    public ScanResult Scan(CancellationToken cancellationToken) => ScanResult.Empty;

    public CleanResult Clean(CancellationToken cancellationToken)
    {
        return NativeMethods.DnsFlushResolverCache()
            ? CleanResult.Empty
            : new CleanResult(0, 0, 1);
    }
}
