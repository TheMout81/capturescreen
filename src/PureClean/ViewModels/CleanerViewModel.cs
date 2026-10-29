using System.Collections.ObjectModel;
using System.Diagnostics;
using System.Windows;
using System.Windows.Input;
using PureClean.Core;
using PureClean.Infrastructure;
using PureClean.Models;

namespace PureClean.ViewModels;

public sealed class CleanerViewModel : ObservableObject
{
    private CancellationTokenSource? _cts;
    private bool _isBusy;
    private double _progress;
    private string _statusText = "Cochez les éléments à nettoyer puis lancez une analyse.";
    private string _summaryText = "";

    public CleanerViewModel()
    {
        var cleaners = CleanerCatalog.CreateAll()
            .Where(c => c.IsAvailable)
            .Select(c => new CleanerItemViewModel(c))
            .ToList();

        WindowsItems = new ObservableCollection<CleanerItemViewModel>(cleaners.Where(c => c.Cleaner.Category == CleanerCategory.Windows));
        ApplicationItems = new ObservableCollection<CleanerItemViewModel>(cleaners.Where(c => c.Cleaner.Category == CleanerCategory.Applications));

        ScanCommand = new AsyncRelayCommand(ScanAsync, () => !IsBusy && SelectedItems.Any());
        CleanCommand = new AsyncRelayCommand(CleanAsync, () => !IsBusy && SelectedItems.Any());
        CancelCommand = new RelayCommand(() => _cts?.Cancel(), () => IsBusy);
        SelectAllCommand = new RelayCommand(() => SetAll(true), () => !IsBusy);
        SelectNoneCommand = new RelayCommand(() => SetAll(false), () => !IsBusy);
    }

    public ObservableCollection<CleanerItemViewModel> WindowsItems { get; }
    public ObservableCollection<CleanerItemViewModel> ApplicationItems { get; }

    public ICommand ScanCommand { get; }
    public ICommand CleanCommand { get; }
    public ICommand CancelCommand { get; }
    public ICommand SelectAllCommand { get; }
    public ICommand SelectNoneCommand { get; }

    public bool IsBusy
    {
        get => _isBusy;
        private set => SetProperty(ref _isBusy, value);
    }

    public double Progress
    {
        get => _progress;
        private set => SetProperty(ref _progress, value);
    }

    public string StatusText
    {
        get => _statusText;
        private set => SetProperty(ref _statusText, value);
    }

    public string SummaryText
    {
        get => _summaryText;
        private set => SetProperty(ref _summaryText, value);
    }

    private IEnumerable<CleanerItemViewModel> AllItems => WindowsItems.Concat(ApplicationItems);
    private IEnumerable<CleanerItemViewModel> SelectedItems => AllItems.Where(i => i.IsSelected);

    private void SetAll(bool selected)
    {
        foreach (var item in AllItems)
            item.IsSelected = selected;
    }

    private async Task ScanAsync()
    {
        var items = SelectedItems.ToList();
        long total = 0;
        var files = 0;

        var completed = await RunAsync(items, "Analyse", async (item, token) =>
        {
            var result = await Task.Run(() => item.Cleaner.Scan(token), token);
            item.ShowScan(result);
            total += result.Bytes;
            files += result.FileCount;
        });

        SummaryText = $"{ByteFormatter.Format(total)} récupérables";
        StatusText = completed
            ? $"Analyse terminée : {files:N0} fichier(s) trouvés. Aucun fichier n'a été supprimé."
            : "Analyse annulée.";
    }

    private async Task CleanAsync()
    {
        var items = SelectedItems.ToList();

        var confirmation = MessageBox.Show(
            $"Supprimer définitivement les fichiers de {items.Count} élément(s) sélectionné(s) ?\n\n" +
            "Seuls des fichiers temporaires et des caches sont concernés.",
            "Confirmer le nettoyage", MessageBoxButton.YesNo, MessageBoxImage.Question, MessageBoxResult.No);
        if (confirmation != MessageBoxResult.Yes)
            return;

        var running = RunningBlockingProcesses(items);
        if (running.Count > 0)
        {
            var answer = MessageBox.Show(
                "Ces programmes sont ouverts et verrouillent une partie de leurs fichiers :\n\n  • " +
                string.Join("\n  • ", running) +
                "\n\nFermez-les pour un nettoyage complet. Continuer quand même ?",
                "Programmes ouverts", MessageBoxButton.YesNo, MessageBoxImage.Warning, MessageBoxResult.No);
            if (answer != MessageBoxResult.Yes)
                return;
        }

        var total = CleanResult.Empty;
        var completed = await RunAsync(items, "Nettoyage", async (item, token) =>
        {
            var result = await Task.Run(() => item.Cleaner.Clean(token), token);
            item.ShowClean(result);
            total = total.Add(result);
        });

        SummaryText = $"{ByteFormatter.Format(total.BytesFreed)} libérés";
        StatusText = (completed ? "Nettoyage terminé" : "Nettoyage interrompu") +
                     $" : {total.FilesDeleted:N0} fichier(s) supprimé(s)" +
                     (total.FilesSkipped > 0 ? $", {total.FilesSkipped:N0} ignoré(s) car en cours d'utilisation." : ".");
    }

    /// <summary>Exécute une action sur chaque élément avec progression et annulation. Renvoie false si annulé.</summary>
    private async Task<bool> RunAsync(
        IReadOnlyList<CleanerItemViewModel> items,
        string verb,
        Func<CleanerItemViewModel, CancellationToken, Task> action)
    {
        _cts = new CancellationTokenSource();
        IsBusy = true;
        Progress = 0;
        foreach (var item in items)
            item.ShowText("En attente…");

        try
        {
            for (var i = 0; i < items.Count; i++)
            {
                var item = items[i];
                StatusText = $"{verb} : {item.Name}…";
                item.ShowText("En cours…");
                try
                {
                    await action(item, _cts.Token);
                }
                catch (OperationCanceledException)
                {
                    item.ShowText("Annulé");
                    foreach (var rest in items.Skip(i + 1))
                        rest.ShowText("");
                    return false;
                }
                catch (Exception ex)
                {
                    item.ShowText("Erreur : " + ex.Message);
                }
                Progress = 100.0 * (i + 1) / items.Count;
            }
            return true;
        }
        finally
        {
            IsBusy = false;
            _cts.Dispose();
            _cts = null;
            CommandManager.InvalidateRequerySuggested();
        }
    }

    private static List<string> RunningBlockingProcesses(IEnumerable<CleanerItemViewModel> items)
    {
        var names = new List<string>();
        foreach (var item in items)
        {
            // L'Explorateur est toujours ouvert : ses fichiers verrouillés sont simplement ignorés.
            var running = item.Cleaner.BlockingProcesses
                .Where(p => !p.Equals("explorer", StringComparison.OrdinalIgnoreCase))
                .Any(IsRunning);
            if (running)
                names.Add(item.Name.Split(" — ")[0]);
        }
        return names.Distinct().ToList();
    }

    private static bool IsRunning(string processName)
    {
        var processes = Process.GetProcessesByName(processName);
        foreach (var p in processes)
            p.Dispose();
        return processes.Length > 0;
    }
}
