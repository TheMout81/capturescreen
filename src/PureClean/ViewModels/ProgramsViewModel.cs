using System.Collections.ObjectModel;
using System.ComponentModel;
using System.Diagnostics;
using System.Globalization;
using System.Windows;
using System.Windows.Input;
using PureClean.Core.Programs;
using PureClean.Infrastructure;

namespace PureClean.ViewModels;

public enum ProgramSort
{
    Name,
    Size,
    Date,
}

public sealed class ProgramItemViewModel(InstalledProgram program)
{
    public InstalledProgram Program { get; } = program;
    public string Name => Program.Name;
    public string SizeText => Program.SizeBytes is { } size ? ByteFormatter.Format(size) : "";

    public string Details => string.Join(" · ", new[]
    {
        Program.Publisher,
        Program.Version is null ? null : "version " + Program.Version,
        Program.InstallDate is { } date ? "installé le " + date.ToString("d MMM yyyy", CultureInfo.CurrentCulture) : null,
    }.Where(s => !string.IsNullOrEmpty(s)));

    public bool CanOpenLocation => Program.InstallLocation is { } location && Directory.Exists(location);
}

public sealed class ProgramsViewModel : ObservableObject
{
    private List<ProgramItemViewModel> _all = [];
    private bool _isBusy;
    private string _searchText = "";
    private ProgramSort _sort = ProgramSort.Name;
    private string _statusText = "";

    public ProgramsViewModel()
    {
        RefreshCommand = new AsyncRelayCommand(RefreshAsync, () => !IsBusy);
        UninstallCommand = new RelayCommand<ProgramItemViewModel>(p => _ = UninstallAsync(p), _ => !IsBusy);
        OpenLocationCommand = new RelayCommand<ProgramItemViewModel>(OpenLocation, p => p.CanOpenLocation);
    }

    public ObservableCollection<ProgramItemViewModel> Programs { get; } = [];

    public ICommand RefreshCommand { get; }
    public ICommand UninstallCommand { get; }
    public ICommand OpenLocationCommand { get; }

    public bool IsLoaded { get; private set; }

    public bool IsBusy
    {
        get => _isBusy;
        private set => SetProperty(ref _isBusy, value);
    }

    public string SearchText
    {
        get => _searchText;
        set
        {
            if (SetProperty(ref _searchText, value))
                ApplyFilter();
        }
    }

    public ProgramSort Sort
    {
        get => _sort;
        set
        {
            if (SetProperty(ref _sort, value))
                ApplyFilter();
        }
    }

    public string StatusText
    {
        get => _statusText;
        private set => SetProperty(ref _statusText, value);
    }

    public async Task RefreshAsync()
    {
        IsBusy = true;
        StatusText = "Chargement de la liste des programmes…";
        try
        {
            var programs = await Task.Run(ProgramInventory.GetAll);
            _all = programs.Select(p => new ProgramItemViewModel(p)).ToList();
            IsLoaded = true;
            ApplyFilter();
        }
        finally
        {
            IsBusy = false;
            CommandManager.InvalidateRequerySuggested();
        }
    }

    private void ApplyFilter()
    {
        IEnumerable<ProgramItemViewModel> query = _all;
        if (!string.IsNullOrWhiteSpace(SearchText))
        {
            var search = SearchText.Trim();
            query = query.Where(p => p.Name.Contains(search, StringComparison.CurrentCultureIgnoreCase)
                                     || (p.Program.Publisher?.Contains(search, StringComparison.CurrentCultureIgnoreCase) ?? false));
        }

        query = Sort switch
        {
            ProgramSort.Size => query.OrderByDescending(p => p.Program.SizeBytes ?? -1),
            ProgramSort.Date => query.OrderByDescending(p => p.Program.InstallDate ?? DateTime.MinValue),
            _ => query.OrderBy(p => p.Name, StringComparer.CurrentCultureIgnoreCase),
        };

        Programs.Clear();
        foreach (var program in query)
            Programs.Add(program);

        var total = _all.Sum(p => p.Program.SizeBytes ?? 0);
        StatusText = $"{_all.Count} programme(s) installé(s), {ByteFormatter.Format(total)} au total (taille déclarée par les éditeurs)." +
                     (Programs.Count != _all.Count ? $" {Programs.Count} affiché(s)." : "");
    }

    private async Task UninstallAsync(ProgramItemViewModel item)
    {
        var answer = MessageBox.Show(
            $"Désinstaller « {item.Name} » ?\n\nLe programme de désinstallation de l'éditeur va s'ouvrir : suivez ses instructions.",
            AppInfo.Title, MessageBoxButton.YesNo, MessageBoxImage.Question, MessageBoxResult.No);
        if (answer != MessageBoxResult.Yes)
            return;

        IsBusy = true;
        StatusText = $"Désinstallation de {item.Name} en cours… (la liste sera actualisée à la fin)";
        try
        {
            await ProgramInventory.UninstallAsync(item.Program);
        }
        catch (Exception ex) when (ex is InvalidOperationException or Win32Exception)
        {
            MessageBox.Show($"Impossible de lancer la désinstallation de « {item.Name} » :\n{ex.Message}",
                AppInfo.Title, MessageBoxButton.OK, MessageBoxImage.Error);
        }
        finally
        {
            IsBusy = false;
        }

        // Certains désinstalleurs rendent la main avant d'avoir fini : un petit délai évite une liste périmée.
        await Task.Delay(TimeSpan.FromSeconds(2));
        await RefreshAsync();
    }

    private static void OpenLocation(ProgramItemViewModel item)
        => Process.Start(new ProcessStartInfo("explorer.exe", $"\"{item.Program.InstallLocation}\"") { UseShellExecute = true });
}
