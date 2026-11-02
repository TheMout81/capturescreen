using System.Collections.ObjectModel;
using System.Diagnostics;
using System.Net.Http;
using System.Text.Json;
using System.Windows;
using System.Windows.Input;
using Microsoft.Win32;
using PureClean.Core.Software;
using PureClean.Core.Updates;
using PureClean.Infrastructure;

namespace PureClean.ViewModels;

public sealed class SoftwareItemViewModel : ObservableObject
{
    private readonly Action _changed;
    private bool _isInstalled;
    private string _status = "";

    public SoftwareItemViewModel(SoftwareEntry entry, Action changed)
    {
        Entry = entry;
        _changed = changed;
    }

    public SoftwareEntry Entry { get; }
    public string Name => Entry.Name;
    public string Id => Entry.Id;
    public bool IsRustDesk => Entry.ToPackage().IsRustDesk;

    public string Details => Entry.Source switch
    {
        SoftwareCatalog.StoreSource => Entry.Id + " · Microsoft Store",
        SoftwareCatalog.GitHubSource => "GitHub officiel",
        _ => Entry.Id,
    };

    public bool IsSelected
    {
        get => Entry.Selected;
        set
        {
            if (Entry.Selected == value)
                return;
            Entry.Selected = value;
            OnPropertyChanged();
            _changed();
        }
    }

    public bool IsInstalled
    {
        get => _isInstalled;
        set => SetProperty(ref _isInstalled, value);
    }

    public string Status
    {
        get => _status;
        set => SetProperty(ref _status, value);
    }
}

public sealed record SoftwareCategoryViewModel(string Name, IReadOnlyList<SoftwareItemViewModel> Items);

/// <summary>Onglet « Installer » : catalogue à cocher, installation silencieuse, profils.</summary>
public sealed class InstallerViewModel : ObservableObject
{
    private const string AppInstallerStorePage = "ms-windows-store://pdp/?ProductId=9NBLGGH4NNS1";

    private readonly AppSettings _settings = AppSettings.Current;
    private List<SoftwareItemViewModel> _items = [];
    private IReadOnlyList<string> _installedIds = [];
    private CancellationTokenSource? _searchCts;
    private bool _isBusy;
    private bool _isLoaded;
    private bool _wingetMissing;
    private string _statusText = "";
    private string _searchText = "";
    private string _searchStatus = "";

    public InstallerViewModel()
    {
        InstallCommand = new AsyncRelayCommand(InstallSelectedAsync, () => !IsBusy && _items.Any(i => i.IsSelected));
        RefreshCommand = new AsyncRelayCommand(CheckInstalledAsync, () => !IsBusy);
        SelectNoneCommand = new RelayCommand(() => SetAllSelected(false), () => !IsBusy);
        SelectMissingCommand = new RelayCommand(SelectMissing, () => !IsBusy);
        RemoveCommand = new RelayCommand<SoftwareItemViewModel>(Remove, _ => !IsBusy);
        ResetCommand = new RelayCommand(ResetList, () => !IsBusy);
        SearchCommand = new AsyncRelayCommand(SearchAsync, () => !string.IsNullOrWhiteSpace(SearchText));
        AddResultCommand = new RelayCommand<SearchResult>(AddResult);
        ClearSearchCommand = new RelayCommand(ClearSearch);
        ExportCommand = new RelayCommand(ExportProfile);
        ImportCommand = new RelayCommand(ImportProfile, () => !IsBusy);
        FromPcCommand = new AsyncRelayCommand(ProfileFromPcAsync, () => !IsBusy);
        OpenStoreCommand = new RelayCommand(() => Process.Start(new ProcessStartInfo(AppInstallerStorePage) { UseShellExecute = true }));
        ApplyRustDeskCommand = new AsyncRelayCommand(ApplyRustDeskAsync, () => !IsBusy && _settings.RustDesk.IsConfigured);

        LoadItems(_settings.SoftwareList ?? DefaultEntries());
    }

    public ObservableCollection<SoftwareCategoryViewModel> Categories { get; } = [];
    public ObservableCollection<SearchResult> SearchResults { get; } = [];

    public ICommand InstallCommand { get; }
    public ICommand RefreshCommand { get; }
    public ICommand SelectNoneCommand { get; }
    public ICommand SelectMissingCommand { get; }
    public ICommand RemoveCommand { get; }
    public ICommand ResetCommand { get; }
    public ICommand SearchCommand { get; }
    public ICommand AddResultCommand { get; }
    public ICommand ClearSearchCommand { get; }
    public ICommand ExportCommand { get; }
    public ICommand ImportCommand { get; }
    public ICommand FromPcCommand { get; }
    public ICommand OpenStoreCommand { get; }
    public ICommand ApplyRustDeskCommand { get; }

    public bool IsBusy
    {
        get => _isBusy;
        private set => SetProperty(ref _isBusy, value);
    }

    public bool WingetMissing
    {
        get => _wingetMissing;
        private set => SetProperty(ref _wingetMissing, value);
    }

    public string StatusText
    {
        get => _statusText;
        private set => SetProperty(ref _statusText, value);
    }

    public string SelectionText
    {
        get
        {
            var selected = _items.Where(i => i.IsSelected).ToList();
            var toInstall = selected.Count(i => !i.IsInstalled);
            return selected.Count == 0
                ? "Aucun logiciel coché"
                : $"{selected.Count} logiciel(s) coché(s), dont {toInstall} à installer";
        }
    }

    public string SearchText
    {
        get => _searchText;
        set => SetProperty(ref _searchText, value);
    }

    public string SearchStatus
    {
        get => _searchStatus;
        private set => SetProperty(ref _searchStatus, value);
    }

    public bool HasSearchResults => SearchResults.Count > 0;

    public bool HasRustDesk => _items.Any(i => i.IsRustDesk);

    public string RustDeskConfigString
    {
        get => _settings.RustDesk.ConfigString ?? "";
        set => UpdateRustDesk(s => s.ConfigString = value);
    }

    public string RustDeskIdServer
    {
        get => _settings.RustDesk.IdServer ?? "";
        set => UpdateRustDesk(s => s.IdServer = value);
    }

    public string RustDeskRelayServer
    {
        get => _settings.RustDesk.RelayServer ?? "";
        set => UpdateRustDesk(s => s.RelayServer = value);
    }

    public string RustDeskKey
    {
        get => _settings.RustDesk.Key ?? "";
        set => UpdateRustDesk(s => s.Key = value);
    }

    /// <summary>Premier affichage de l'onglet : repère les logiciels déjà installés.</summary>
    public void EnsureLoaded()
    {
        if (_isLoaded || IsBusy)
            return;
        _isLoaded = true;
        RefreshCommand.Execute(null);
    }

    private static List<SoftwareEntry> DefaultEntries()
        => SoftwareCatalog.Default.Select(p => SoftwareEntry.From(p, selected: false)).ToList();

    private void LoadItems(IEnumerable<SoftwareEntry> entries)
    {
        _items = entries
            .GroupBy(e => e.Id, StringComparer.OrdinalIgnoreCase)
            .Select(g => new SoftwareItemViewModel(g.First(), OnListChanged))
            .ToList();
        ApplyInstalledState();
        RebuildCategories();
    }

    private void RebuildCategories()
    {
        int Order(string category)
        {
            var index = SoftwareCatalog.CategoryOrder.ToList().IndexOf(category);
            return index < 0 ? int.MaxValue : index;
        }

        Categories.Clear();
        foreach (var group in _items.GroupBy(i => i.Entry.Category).OrderBy(g => Order(g.Key)).ThenBy(g => g.Key))
            Categories.Add(new SoftwareCategoryViewModel(group.Key, group.OrderBy(i => i.Name, StringComparer.CurrentCultureIgnoreCase).ToList()));

        OnPropertyChanged(nameof(HasRustDesk));
        OnPropertyChanged(nameof(SelectionText));
    }

    private void OnListChanged()
    {
        OnPropertyChanged(nameof(SelectionText));
        Save();
    }

    private void Save()
    {
        _settings.SoftwareList = _items.Select(i => i.Entry).ToList();
        _settings.Save();
    }

    private void ApplyInstalledState()
    {
        foreach (var item in _items)
        {
            item.IsInstalled = item.IsRustDesk
                ? RustDeskInstaller.IsInstalled
                : SoftwareCatalog.IsInstalled(item.Id, _installedIds);
        }
        OnPropertyChanged(nameof(SelectionText));
    }

    private async Task CheckInstalledAsync()
    {
        IsBusy = true;
        StatusText = "Recherche des logiciels déjà installés…";
        try
        {
            WingetMissing = !await WingetClient.IsAvailableAsync();
            if (WingetMissing)
            {
                StatusText = "winget n'est pas disponible : seul RustDesk peut être installé pour l'instant.";
                ApplyInstalledState();
                return;
            }

            _installedIds = await WingetClient.ListInstalledIdsAsync(CancellationToken.None);
            ApplyInstalledState();
            StatusText = $"{_items.Count(i => i.IsInstalled)} logiciel(s) de la liste déjà installé(s).";
        }
        catch (InvalidOperationException ex)
        {
            StatusText = ex.Message;
        }
        finally
        {
            IsBusy = false;
            CommandManager.InvalidateRequerySuggested();
        }
    }

    private async Task InstallSelectedAsync()
    {
        var targets = _items.Where(i => i.IsSelected && !i.IsInstalled).ToList();
        if (targets.Count == 0)
        {
            MessageBox.Show("Tous les logiciels cochés sont déjà installés.", AppInfo.Title, MessageBoxButton.OK, MessageBoxImage.Information);
            return;
        }
        if (WingetMissing && targets.Any(t => !t.IsRustDesk))
        {
            MessageBox.Show("winget n'est pas disponible sur ce PC. Mettez à jour « Programme d'installation d'application » depuis le Microsoft Store, puis cliquez sur « Actualiser ».",
                AppInfo.Title, MessageBoxButton.OK, MessageBoxImage.Warning);
            return;
        }

        var answer = MessageBox.Show(
            $"Installer {targets.Count} logiciel(s) ?\n\n  • {string.Join("\n  • ", targets.Select(t => t.Name))}\n\n" +
            "L'installation est automatique et silencieuse : réglages par défaut de l'éditeur, sans offre ni logiciel supplémentaire. " +
            "Cela peut prendre plusieurs minutes.",
            AppInfo.Title, MessageBoxButton.YesNo, MessageBoxImage.Question, MessageBoxResult.Yes);
        if (answer != MessageBoxResult.Yes)
            return;

        IsBusy = true;
        var succeeded = 0;
        try
        {
            foreach (var item in targets)
                item.Status = "En attente…";

            for (var i = 0; i < targets.Count; i++)
            {
                var item = targets[i];
                StatusText = $"Installation {i + 1} / {targets.Count} : {item.Name}…";
                item.Status = "Installation…";
                if (await InstallAsync(item))
                    succeeded++;
            }
        }
        finally
        {
            IsBusy = false;
        }

        await CheckInstalledAsync();
        StatusText = succeeded == targets.Count
            ? $"{succeeded} logiciel(s) installé(s)."
            : $"{succeeded} logiciel(s) installé(s), {targets.Count - succeeded} en échec (voir les lignes concernées).";
    }

    private async Task<bool> InstallAsync(SoftwareItemViewModel item)
    {
        try
        {
            if (item.IsRustDesk)
            {
                await RustDeskInstaller.InstallAsync(_settings.RustDesk, new Progress<string>(s => item.Status = s));
                item.Status = _settings.RustDesk.IsConfigured ? "Installé et configuré" : "Installé";
                return true;
            }

            var exitCode = await WingetClient.InstallAsync(item.Id, item.Entry.Source);
            item.Status = exitCode switch
            {
                0 => "Installé",
                WingetClient.AlreadyInstalled => "Déjà installé",
                _ => $"Échec (code 0x{exitCode:X8})",
            };
            return exitCode is 0 or WingetClient.AlreadyInstalled;
        }
        catch (Exception ex) when (ex is InvalidOperationException or HttpRequestException or IOException)
        {
            item.Status = "Échec : " + ex.Message;
            return false;
        }
    }

    private void SetAllSelected(bool selected)
    {
        foreach (var item in _items)
            item.IsSelected = selected;
    }

    /// <summary>Coche tout ce qui n'est pas encore installé (pratique sur un PC neuf).</summary>
    private void SelectMissing()
    {
        foreach (var item in _items)
            item.IsSelected = !item.IsInstalled;
    }

    private void Remove(SoftwareItemViewModel item)
    {
        _items.Remove(item);
        Save();
        RebuildCategories();
    }

    private void ResetList()
    {
        var answer = MessageBox.Show("Revenir à la liste de logiciels proposée par défaut ? Vos ajouts seront retirés.",
            AppInfo.Title, MessageBoxButton.YesNo, MessageBoxImage.Question, MessageBoxResult.No);
        if (answer != MessageBoxResult.Yes)
            return;

        LoadItems(DefaultEntries());
        _settings.SoftwareList = null;
        _settings.Save();
    }

    private async Task SearchAsync()
    {
        _searchCts?.Cancel();
        _searchCts = new CancellationTokenSource();
        var token = _searchCts.Token;
        SearchStatus = "Recherche…";
        SearchResults.Clear();
        OnPropertyChanged(nameof(HasSearchResults));
        try
        {
            var results = await WingetClient.SearchAsync(SearchText.Trim(), token);
            foreach (var result in results.Take(40))
                SearchResults.Add(result);
            SearchStatus = results.Count == 0 ? "Aucun logiciel trouvé." : $"{results.Count} résultat(s).";
        }
        catch (OperationCanceledException)
        {
            // Nouvelle recherche lancée entre-temps.
        }
        catch (InvalidOperationException ex)
        {
            SearchStatus = ex.Message;
        }
        OnPropertyChanged(nameof(HasSearchResults));
    }

    private void AddResult(SearchResult result)
    {
        var existing = _items.FirstOrDefault(i => string.Equals(i.Id, result.Id, StringComparison.OrdinalIgnoreCase));
        if (existing is not null)
        {
            existing.IsSelected = true;
        }
        else
        {
            var entry = new SoftwareEntry
            {
                Id = result.Id,
                Name = result.Name,
                Category = SoftwareCatalog.UserCategory,
                Source = result.Source,
                Selected = true,
            };
            _items.Add(new SoftwareItemViewModel(entry, OnListChanged));
            ApplyInstalledState();
            Save();
            RebuildCategories();
        }

        SearchResults.Remove(result);
        OnPropertyChanged(nameof(HasSearchResults));
        SearchStatus = $"« {result.Name} » ajouté à « {SoftwareCatalog.UserCategory} » et coché.";
    }

    private void ClearSearch()
    {
        _searchCts?.Cancel();
        SearchText = "";
        SearchStatus = "";
        SearchResults.Clear();
        OnPropertyChanged(nameof(HasSearchResults));
    }

    private void ExportProfile()
    {
        var dialog = new SaveFileDialog
        {
            Title = "Enregistrer la liste de logiciels",
            Filter = "Profil PureClean (*.json)|*.json",
            FileName = "PureClean-logiciels.json",
        };
        if (dialog.ShowDialog() != true)
            return;

        var profile = new SoftwareProfile
        {
            Packages = _items.Select(i => i.Entry).ToList(),
            RustDesk = _settings.RustDesk.IsConfigured ? _settings.RustDesk : null,
        };
        try
        {
            profile.Save(dialog.FileName);
            StatusText = $"Profil enregistré ({_items.Count(i => i.IsSelected)} logiciel(s) coché(s)) : {dialog.FileName}";
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            MessageBox.Show(ex.Message, AppInfo.Title, MessageBoxButton.OK, MessageBoxImage.Error);
        }
    }

    private void ImportProfile()
    {
        var dialog = new OpenFileDialog
        {
            Title = "Ouvrir un profil de logiciels",
            Filter = "Profil PureClean ou export winget (*.json)|*.json|Tous les fichiers (*.*)|*.*",
        };
        if (dialog.ShowDialog() != true)
            return;

        SoftwareProfile profile;
        try
        {
            profile = SoftwareProfile.Parse(File.ReadAllText(dialog.FileName));
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or JsonException or InvalidDataException)
        {
            MessageBox.Show($"Ce fichier ne peut pas être importé :\n{ex.Message}", AppInfo.Title, MessageBoxButton.OK, MessageBoxImage.Error);
            return;
        }

        var replace = false;
        if (!profile.FromWinget)
        {
            var answer = MessageBox.Show(
                $"Ce profil contient {profile.Packages.Count} logiciel(s).\n\n" +
                "Oui : remplacer votre liste par celle du profil.\nNon : ajouter ses logiciels à votre liste.",
                AppInfo.Title, MessageBoxButton.YesNoCancel, MessageBoxImage.Question, MessageBoxResult.Yes);
            if (answer == MessageBoxResult.Cancel)
                return;
            replace = answer == MessageBoxResult.Yes;
        }

        if (replace)
            LoadItems(profile.Packages);
        else
            Merge(profile.Packages.Select(WithKnownName));

        if (profile.RustDesk is { IsConfigured: true } rustDesk)
        {
            _settings.RustDesk = rustDesk;
            RefreshRustDeskFields();
        }

        Save();
        StatusText = $"Profil importé : {_items.Count(i => i.IsSelected)} logiciel(s) coché(s). Cliquez sur « Installer la sélection ».";
    }

    private async Task ProfileFromPcAsync()
    {
        IsBusy = true;
        StatusText = "Recherche des logiciels de ce PC que winget sait réinstaller…";
        try
        {
            var references = await WingetClient.ExportAsync(CancellationToken.None);
            var before = _items.Count;
            Merge(references.Select(r => WithKnownName(SoftwareProfile.FromReference(r, "Importés de ce PC"))));
            Save();
            StatusText = $"{references.Count} logiciel(s) de ce PC cochés ({_items.Count - before} ajouté(s) à la liste). " +
                         "Cliquez sur « Exporter la sélection » pour les réinstaller ailleurs.";
        }
        catch (InvalidOperationException ex)
        {
            StatusText = ex.Message;
        }
        finally
        {
            IsBusy = false;
            CommandManager.InvalidateRequerySuggested();
        }
    }

    /// <summary>Ajoute les logiciels absents de la liste et coche ceux du profil.</summary>
    private void Merge(IEnumerable<SoftwareEntry> entries)
    {
        foreach (var entry in entries)
        {
            var existing = _items.FirstOrDefault(i => string.Equals(i.Id, entry.Id, StringComparison.OrdinalIgnoreCase));
            if (existing is not null)
            {
                if (entry.Selected)
                    existing.IsSelected = true;
            }
            else
                _items.Add(new SoftwareItemViewModel(entry, OnListChanged));
        }
        ApplyInstalledState();
        RebuildCategories();
    }

    /// <summary>Reprend le nom et la catégorie du catalogue quand l'identifiant y figure.</summary>
    private static SoftwareEntry WithKnownName(SoftwareEntry entry)
    {
        var known = SoftwareCatalog.Default.FirstOrDefault(p => string.Equals(p.Id, entry.Id, StringComparison.OrdinalIgnoreCase));
        if (known is null)
            return entry;
        entry.Name = known.Name;
        entry.Category = known.Category;
        return entry;
    }

    private void UpdateRustDesk(Action<RustDeskSettings> change)
    {
        change(_settings.RustDesk);
        _settings.Save();
        CommandManager.InvalidateRequerySuggested();
    }

    private void RefreshRustDeskFields()
    {
        OnPropertyChanged(nameof(RustDeskConfigString));
        OnPropertyChanged(nameof(RustDeskIdServer));
        OnPropertyChanged(nameof(RustDeskRelayServer));
        OnPropertyChanged(nameof(RustDeskKey));
    }

    private async Task ApplyRustDeskAsync()
    {
        IsBusy = true;
        try
        {
            await RustDeskInstaller.ApplyConfigurationAsync(_settings.RustDesk);
            StatusText = "Configuration du serveur appliquée à RustDesk.";
        }
        catch (InvalidOperationException ex)
        {
            MessageBox.Show(ex.Message, AppInfo.Title, MessageBoxButton.OK, MessageBoxImage.Warning);
        }
        finally
        {
            IsBusy = false;
            CommandManager.InvalidateRequerySuggested();
        }
    }
}
