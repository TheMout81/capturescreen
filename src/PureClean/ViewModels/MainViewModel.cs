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
            new("Nettoyage", "", new CleanerViewModel()),
            new("Démarrage", "", Startup),
            new("Réglages Windows", "", new TweaksViewModel()),
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

    public string Version => "v" + (typeof(MainViewModel).Assembly.GetName().Version?.ToString(3) ?? "1.0.0");

    private static bool CheckAdministrator()
    {
        using var identity = WindowsIdentity.GetCurrent();
        return new WindowsPrincipal(identity).IsInRole(WindowsBuiltInRole.Administrator);
    }
}
