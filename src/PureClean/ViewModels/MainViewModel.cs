using System.Security.Principal;
using PureClean.Infrastructure;

namespace PureClean.ViewModels;

public sealed record NavigationItem(string Title, string Glyph, object Page);

public sealed class MainViewModel : ObservableObject
{
    private NavigationItem _selectedItem;

    public MainViewModel()
    {
        Startup = new StartupViewModel();
        NavigationItems =
        [
            new("Nettoyage", "\uE74D", new CleanerViewModel()),
            new("Démarrage", "\uE7E8", Startup),
            new("Réglages Windows", "\uE713", new TweaksViewModel()),
            new("Paramètres", "\uE115", new SettingsViewModel()),
        ];
        _selectedItem = NavigationItems[0];
        IsAdministrator = CheckAdministrator();
    }

    public IReadOnlyList<NavigationItem> NavigationItems { get; }

    public StartupViewModel Startup { get; }

    public NavigationItem SelectedItem
    {
        get => _selectedItem;
        set
        {
            if (!SetProperty(ref _selectedItem, value))
                return;

            // La liste de démarrage n'est chargée qu'à la première visite.
            if (value.Page == Startup && Startup.Items.Count == 0 && Startup.RefreshCommand.CanExecute(null))
                Startup.RefreshCommand.Execute(null);
        }
    }

    public bool IsAdministrator { get; }

    public bool ShowAdminWarning => !IsAdministrator;

    public string Version => AppInfo.Version;

    public string WindowTitle => AppInfo.Title;

    private static bool CheckAdministrator()
    {
        using var identity = WindowsIdentity.GetCurrent();
        return new WindowsPrincipal(identity).IsInRole(WindowsBuiltInRole.Administrator);
    }
}
