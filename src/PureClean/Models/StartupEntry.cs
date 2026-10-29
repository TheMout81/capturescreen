namespace PureClean.Models;

public enum StartupSource
{
    RegistryCurrentUser,
    RegistryLocalMachine,
    RegistryLocalMachine32,
    StartupFolderUser,
    StartupFolderCommon,
    ScheduledTask,
}

/// <param name="Key">Nom de la valeur de registre, nom du fichier raccourci ou chemin de la tâche.</param>
public sealed record StartupEntry(string Name, string Command, StartupSource Source, bool IsEnabled, string Key)
{
    public string SourceLabel => Source switch
    {
        StartupSource.RegistryCurrentUser => "Registre (utilisateur)",
        StartupSource.RegistryLocalMachine => "Registre (tous les utilisateurs)",
        StartupSource.RegistryLocalMachine32 => "Registre 32 bits (tous les utilisateurs)",
        StartupSource.StartupFolderUser => "Dossier Démarrage (utilisateur)",
        StartupSource.StartupFolderCommon => "Dossier Démarrage (tous les utilisateurs)",
        StartupSource.ScheduledTask => "Tâche planifiée",
        _ => Source.ToString(),
    };
}
