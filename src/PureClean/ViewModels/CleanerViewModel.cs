using System.Collections.ObjectModel;
using System.Windows;
using System.Windows.Input;
using Microsoft.Win32;
using PureClean.Core;
using PureClean.Infrastructure;
using PureClean.Models;

namespace PureClean.ViewModels;

public sealed class CleanerViewModel : ObservableObject
{
    private readonly AppSettings _settings = AppSettings.Current;
    private CancellationTokenSource? _cts;
    private bool _isBusy;
    private bool _suspendSelectionSave;
    private double _progress;
    private string _statusText = "Cochez les éléments à nettoyer puis lancez une analyse.";
    private string _summaryText = "";

    public CleanerViewModel()
    {
        var items = CleanerCatalog.CreateAll(_settings.CustomFolders)
            .Where(c => c.IsAvailable)
            .Select(CreateItem)
            .ToList();

        WindowsItems = Section(items, CleanerCategory.Windows);
        ApplicationItems = Section(items, CleanerCategory.Applications);
        PrivacyItems = Section(items, CleanerCategory.Privacy);
        AdvancedItems = Section(items, CleanerCategory.Advanced);
        CustomItems = Section(items, CleanerCategory.Custom);

        ScanCommand = new AsyncRelayCommand(ScanAsync, () => !IsBusy && SelectedItems.Any());
        CleanCommand = new AsyncRelayCommand(CleanAsync, () => !IsBusy && SelectedItems.Any());
        CancelCommand = new RelayCommand(() => _cts?.Cancel(), () => IsBusy);
        SelectAllCommand = new RelayCommand(() => SetAll(true), () => !IsBusy);
        SelectNoneCommand = new RelayCommand(() => SetAll(false), () => !IsBusy);
        AddCustomFolderCommand = new RelayCommand(AddCustomFolder, () => !IsBusy);
        RemoveCustomFolderCommand = new RelayCommand<CleanerItemViewModel>(RemoveCustomFolder, _ => !IsBusy);
    }

    /// <summary>Déclenché après chaque nettoyage (le tableau de bord se met à jour).</summary>
    public event EventHandler? Cleaned;

    public ObservableCollection<CleanerItemViewModel> WindowsItems { get; }
    public ObservableCollection<CleanerItemViewModel> ApplicationItems { get; }
    public ObservableCollection<CleanerItemViewModel> PrivacyItems { get; }
    public ObservableCollection<CleanerItemViewModel> AdvancedItems { get; }
    public ObservableCollection<CleanerItemViewModel> CustomItems { get; }

    public bool HasPrivacyItems => PrivacyItems.Count > 0;

    public ICommand ScanCommand { get; }
    public ICommand CleanCommand { get; }
    public ICommand CancelCommand { get; }
    public ICommand SelectAllCommand { get; }
    public ICommand SelectNoneCommand { get; }
    public ICommand AddCustomFolderCommand { get; }
    public ICommand RemoveCustomFolderCommand { get; }

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

    public int SelectedCount => SelectedItems.Count();

    private IEnumerable<CleanerItemViewModel> AllItems =>
        WindowsItems.Concat(ApplicationItems).Concat(PrivacyItems).Concat(AdvancedItems).Concat(CustomItems);

    private IEnumerable<CleanerItemViewModel> SelectedItems => AllItems.Where(i => i.IsSelected);

    /// <summary>
    /// Nettoyage rapide depuis le tableau de bord : analyse la sélection habituelle,
    /// annonce l'espace récupérable puis nettoie après confirmation.
    /// </summary>
    public async Task QuickCleanAsync()
    {
        if (IsBusy)
            return;
        if (!SelectedItems.Any())
        {
            MessageBox.Show("Aucun élément n'est coché dans la page Nettoyage.", AppInfo.Title,
                MessageBoxButton.OK, MessageBoxImage.Information);
            return;
        }

        var found = await ScanAsync();
        if (found is null)
            return;

        await CleanAsync($"{ByteFormatter.Format(found.Bytes)} récupérables dans {SelectedCount} élément(s).\n\nNettoyer maintenant ?");
    }

    private CleanerItemViewModel CreateItem(ICleaner cleaner)
    {
        var selected = _settings.SelectedCleaners is { } saved ? saved.Contains(cleaner.Id) : cleaner.EnabledByDefault;
        return new CleanerItemViewModel(cleaner, selected, SaveSelection);
    }

    private static ObservableCollection<CleanerItemViewModel> Section(IEnumerable<CleanerItemViewModel> items, CleanerCategory category)
        => new(items.Where(i => i.Cleaner.Category == category));

    private void SaveSelection()
    {
        OnPropertyChanged(nameof(SelectedCount));
        if (_suspendSelectionSave)
            return;
        _settings.SelectedCleaners = SelectedItems.Select(i => i.Cleaner.Id).ToList();
        _settings.Save();
    }

    private void SetAll(bool selected)
    {
        _suspendSelectionSave = true;
        try
        {
            // « Tout cocher » ne concerne pas les sections à risque (confidentialité, avancé).
            foreach (var item in WindowsItems.Concat(ApplicationItems).Concat(CustomItems))
                item.IsSelected = selected;
            if (!selected)
            {
                foreach (var item in PrivacyItems.Concat(AdvancedItems))
                    item.IsSelected = false;
            }
        }
        finally
        {
            _suspendSelectionSave = false;
        }
        SaveSelection();
    }

    private void AddCustomFolder()
    {
        var dialog = new OpenFolderDialog { Title = "Dossier dont le contenu sera vidé à chaque nettoyage" };
        if (dialog.ShowDialog() != true)
            return;

        var path = dialog.FolderName;
        if (PathSafety.WhyProtected(path) is { } reason)
        {
            MessageBox.Show($"Ce dossier ne peut pas être ajouté :\n{reason}", AppInfo.Title,
                MessageBoxButton.OK, MessageBoxImage.Warning);
            return;
        }
        if (_settings.CustomFolders.Contains(path, StringComparer.OrdinalIgnoreCase))
            return;

        var answer = MessageBox.Show(
            $"Tout le contenu de ce dossier sera supprimé définitivement à chaque nettoyage :\n\n{path}\n\nContinuer ?",
            AppInfo.Title, MessageBoxButton.YesNo, MessageBoxImage.Warning, MessageBoxResult.No);
        if (answer != MessageBoxResult.Yes)
            return;

        _settings.CustomFolders.Add(path);
        var item = new CleanerItemViewModel(CleanerCatalog.CustomFolder(path), true, SaveSelection);
        CustomItems.Add(item);
        SaveSelection();
    }

    private void RemoveCustomFolder(CleanerItemViewModel item)
    {
        _settings.CustomFolders.RemoveAll(p => string.Equals(p, item.Description, StringComparison.OrdinalIgnoreCase));
        CustomItems.Remove(item);
        SaveSelection();
    }

    /// <summary>Analyse la sélection. Renvoie le total trouvé, ou null si l'analyse a été annulée.</summary>
    private async Task<ScanResult?> ScanAsync()
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
        return completed ? new ScanResult(total, files) : null;
    }

    private Task CleanAsync() => CleanAsync(null);

    private async Task CleanAsync(string? confirmationText)
    {
        var items = SelectedItems.ToList();

        var confirmation = MessageBox.Show(
            confirmationText ??
            $"Supprimer définitivement les fichiers de {items.Count} élément(s) sélectionné(s) ?\n\n" +
            "Seuls des fichiers temporaires, des caches et les éléments que vous avez cochés sont concernés.",
            "Confirmer le nettoyage", MessageBoxButton.YesNo, MessageBoxImage.Question, MessageBoxResult.No);
        if (confirmation != MessageBoxResult.Yes)
            return;

        var running = RunningBlockingProcesses(items);
        if (running.Count > 0)
        {
            var answer = MessageBox.Show(
                "Ces programmes sont ouverts et verrouillent une partie de leurs fichiers :\n\n  • " +
                string.Join("\n  • ", running) +
                "\n\nFermez-les pour un nettoyage complet (historique et cookies ne sont pas touchés tant que le navigateur est ouvert). " +
                "Continuer quand même ?",
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

        if (total.BytesFreed > 0 || total.FilesDeleted > 0)
            _settings.RecordClean(total.BytesFreed, total.FilesDeleted, automatic: false);

        SummaryText = $"{ByteFormatter.Format(total.BytesFreed)} libérés";
        StatusText = (completed ? "Nettoyage terminé" : "Nettoyage interrompu") +
                     $" : {total.FilesDeleted:N0} fichier(s) supprimé(s)" +
                     (total.FilesSkipped > 0 ? $", {total.FilesSkipped:N0} ignoré(s) car en cours d'utilisation." : ".");
        Cleaned?.Invoke(this, EventArgs.Empty);
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
                .Any(ProcessHelper.IsRunning);
            if (running)
                names.Add(item.Name.Split(" — ")[0]);
        }
        return names.Distinct().ToList();
    }
}
