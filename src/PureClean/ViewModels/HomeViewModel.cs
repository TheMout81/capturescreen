using System.Collections.ObjectModel;
using System.Globalization;
using System.Windows.Input;
using PureClean.Core.Startup;
using PureClean.Infrastructure;

namespace PureClean.ViewModels;

public sealed record DriveViewModel(string Name, string Label, long Total, long Free)
{
    public double UsedPercent => Total == 0 ? 0 : 100.0 * (Total - Free) / Total;
    public string Summary => $"{ByteFormatter.Format(Free)} libres sur {ByteFormatter.Format(Total)}";

    /// <summary>Moins de 10 % d'espace libre : affiché en orange.</summary>
    public bool IsLow => Total > 0 && Free < Total / 10;
}

public sealed record HistoryViewModel(string Date, string Freed, string Kind);

public sealed class HomeViewModel : ObservableObject
{
    private readonly CleanerViewModel _cleaner;
    private string _memoryText = "";
    private double _memoryPercent;
    private string _startupText = "…";
    private string _totalFreed = "";
    private string _lastClean = "";
    private string _selectionText = "";

    public HomeViewModel(CleanerViewModel cleaner, Action<object> navigate, object startupPage)
    {
        _cleaner = cleaner;
        _cleaner.Cleaned += (_, _) => Refresh();
        QuickCleanCommand = new AsyncRelayCommand(_cleaner.QuickCleanAsync, () => !_cleaner.IsBusy);
        GoToCleanerCommand = new RelayCommand(() => navigate(_cleaner));
        GoToStartupCommand = new RelayCommand(() => navigate(startupPage));
        WindowsVersion = SystemInfo.WindowsVersion();
    }

    public ICommand QuickCleanCommand { get; }
    public ICommand GoToCleanerCommand { get; }
    public ICommand GoToStartupCommand { get; }

    public string WindowsVersion { get; }

    public string Uptime => "Allumé depuis " + SystemInfo.Uptime();

    public ObservableCollection<DriveViewModel> Drives { get; } = [];
    public ObservableCollection<HistoryViewModel> History { get; } = [];

    public bool HasHistory => History.Count > 0;

    public string MemoryText
    {
        get => _memoryText;
        private set => SetProperty(ref _memoryText, value);
    }

    public double MemoryPercent
    {
        get => _memoryPercent;
        private set => SetProperty(ref _memoryPercent, value);
    }

    public string StartupText
    {
        get => _startupText;
        private set => SetProperty(ref _startupText, value);
    }

    public string TotalFreed
    {
        get => _totalFreed;
        private set => SetProperty(ref _totalFreed, value);
    }

    public string LastClean
    {
        get => _lastClean;
        private set => SetProperty(ref _lastClean, value);
    }

    public string SelectionText
    {
        get => _selectionText;
        private set => SetProperty(ref _selectionText, value);
    }

    /// <summary>Relit l'état du PC ; appelé à chaque affichage de la page.</summary>
    public void Refresh()
    {
        OnPropertyChanged(nameof(Uptime));

        Drives.Clear();
        foreach (var drive in DriveInfo.GetDrives().Where(d => d.DriveType == DriveType.Fixed))
        {
            try
            {
                if (drive.IsReady)
                    Drives.Add(new DriveViewModel(drive.Name.TrimEnd('\\'), drive.VolumeLabel, drive.TotalSize, drive.AvailableFreeSpace));
            }
            catch (IOException)
            {
                // Lecteur devenu indisponible entre l'énumération et la lecture.
            }
        }

        var memory = SystemInfo.Memory();
        MemoryPercent = memory.UsedPercent;
        MemoryText = $"{ByteFormatter.Format((long)(memory.Total - memory.Available))} utilisés sur {ByteFormatter.Format((long)memory.Total)}";

        var history = CleanHistory.Load();
        TotalFreed = ByteFormatter.Format(history.TotalBytesFreed);
        LastClean = history.Entries.Count == 0
            ? "Aucun nettoyage pour l'instant"
            : "Dernier nettoyage : " + FormatDate(history.Entries[0].Date);

        History.Clear();
        foreach (var entry in history.Entries.Take(5))
            History.Add(new HistoryViewModel(FormatDate(entry.Date), ByteFormatter.Format(entry.BytesFreed),
                entry.Automatic ? "Automatique" : "Manuel"));
        OnPropertyChanged(nameof(HasHistory));

        SelectionText = $"{_cleaner.SelectedCount} élément(s) coché(s) dans Nettoyage";
        _ = LoadStartupCountAsync();
    }

    private async Task LoadStartupCountAsync()
    {
        try
        {
            var entries = await Task.Run(StartupManager.GetEntries);
            var enabled = entries.Count(e => e.IsEnabled);
            StartupText = $"{enabled} programme(s) actif(s)";
        }
        catch (Exception ex) when (ex is UnauthorizedAccessException or System.Security.SecurityException or IOException)
        {
            StartupText = "Indisponible";
        }
    }

    private static string FormatDate(DateTime date)
        => date.Date == DateTime.Today ? "aujourd'hui à " + date.ToString("HH:mm", CultureInfo.CurrentCulture)
            : date.Date == DateTime.Today.AddDays(-1) ? "hier à " + date.ToString("HH:mm", CultureInfo.CurrentCulture)
            : date.ToString("d MMMM yyyy", CultureInfo.CurrentCulture);
}
