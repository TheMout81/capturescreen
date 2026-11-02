using PureClean.Infrastructure;

namespace PureClean.ViewModels;

public enum SoftwareTab
{
    Install,
    Updates,
}

/// <summary>Page « Logiciels » : onglets Installer et Mises à jour.</summary>
public sealed class SoftwareViewModel : ObservableObject
{
    private SoftwareTab _tab = SoftwareTab.Install;

    public InstallerViewModel Installer { get; } = new();
    public UpdatesViewModel Updates { get; } = new();

    public SoftwareTab Tab
    {
        get => _tab;
        set
        {
            if (!SetProperty(ref _tab, value))
                return;
            OnPropertyChanged(nameof(CurrentTab));
            if (value == SoftwareTab.Install)
                Installer.EnsureLoaded();
        }
    }

    public object CurrentTab => Tab == SoftwareTab.Install ? Installer : Updates;

    public void OnShown()
    {
        if (Tab == SoftwareTab.Install)
            Installer.EnsureLoaded();
    }
}
