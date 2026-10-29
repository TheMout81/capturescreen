using PureClean.Models;

namespace PureClean.Core;

public interface ICleaner
{
    string Name { get; }
    string Description { get; }
    CleanerCategory Category { get; }

    /// <summary>Coché par défaut dans l'interface (uniquement les éléments sans risque).</summary>
    bool EnabledByDefault { get; }

    /// <summary>Processus qui verrouillent ces fichiers (ex. « chrome ») : à fermer pour un nettoyage complet.</summary>
    IReadOnlyList<string> BlockingProcesses { get; }

    /// <summary>Indique si l'élément concerne cette machine (ex. navigateur installé).</summary>
    bool IsAvailable { get; }

    ScanResult Scan(CancellationToken cancellationToken);

    CleanResult Clean(CancellationToken cancellationToken);
}
