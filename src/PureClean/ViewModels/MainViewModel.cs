using System.Security.Principal;
using PureClean.Infrastructure;

namespace PureClean.ViewModels;

public sealed record NavigationItem(string Title, string Glyph, object Page);

public sealed class MainViewModel : ObservableObject
{
    private NavigationItem _selectedItem;

    public MainViewModel()
    {
        var cleaner = new CleanerViewModel();
        Startup = new StartupViewModel();
        Home = new HomeViewModel(cleaner, Navigate, Startup);
        Programs = new ProgramsViewModel();

        NavigationItems =
        [
            new("Accueil", "\uE80F", Home),
            new("Nettoyage", "\uE74D", cleaner),
            new("Analyse disque", "\uEDA2", new DiskAnalyzerViewModel()),
            new("Programmes", "\uE71D", Programs),
            new("Démarrage", "\uE7E8", Startup),
            new("Réglages Windows", "\uE90F", new TweaksViewModel()),
            new("Paramètres", "\uE713", new SettingsViewModel()),
        ];
        _selectedItem = NavigationItems[0];
        Home.Refresh();
        IsAdministrator = CheckAdministrator();
    }

    public IReadOnlyList<NavigationItem> NavigationItems { get; }

    public HomeViewModel Home { get; }

    public StartupViewModel Startup { get; }

    public ProgramsViewModel Programs { get; }

    public NavigationItem SelectedItem
    {
        get => _selectedItem;
        set
        {
            if (!SetProperty(ref _selectedItem, value))
                return;

            if (value.Page == Home)
                Home.Refresh();

            // Ces listes ne sont chargées qu'à la première visite.
            if (value.Page == Startup && Startup.Items.Count == 0 && Startup.RefreshCommand.CanExecute(null))
                Startup.RefreshCommand.Execute(null);
            if (value.Page == Programs && !Programs.IsLoaded && Programs.RefreshCommand.CanExecute(null))
                Programs.RefreshCommand.Execute(null);
        }
    }

    public bool IsAdministrator { get; }

    public bool ShowAdminWarning => !IsAdministrator;

    public string Version => AppInfo.Version;

    public string WindowTitle => AppInfo.Title;

    private void Navigate(object page)
    {
        var item = NavigationItems.FirstOrDefault(i => i.Page == page);
        if (item is not null)
            SelectedItem = item;
    }

    private static bool CheckAdministrator()
    {
        using var identity = WindowsIdentity.GetCurrent();
        return new WindowsPrincipal(identity).IsInRole(WindowsBuiltInRole.Administrator);
    }
}
