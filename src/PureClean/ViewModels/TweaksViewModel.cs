using System.Collections.ObjectModel;
using System.Windows;
using System.Windows.Input;
using PureClean.Core.Tweaks;
using PureClean.Infrastructure;

namespace PureClean.ViewModels;

public sealed class TweakItemViewModel : ObservableObject
{
    private bool _isApplied;
    private bool _isChecked;

    public TweakItemViewModel(Tweak tweak) => Tweak = tweak;

    public Tweak Tweak { get; }
    public string Name => Tweak.Name;
    public string Description => Tweak.Description;
    public bool RequiresRestart => Tweak.RequiresRestart;

    /// <summary>État réel actuellement dans le registre.</summary>
    public bool IsApplied
    {
        get => _isApplied;
        private set
        {
            if (SetProperty(ref _isApplied, value))
                OnPropertyChanged(nameof(IsModified));
        }
    }

    /// <summary>État souhaité par l'utilisateur (case cochée).</summary>
    public bool IsChecked
    {
        get => _isChecked;
        set
        {
            if (SetProperty(ref _isChecked, value))
                OnPropertyChanged(nameof(IsModified));
        }
    }

    public bool IsModified => IsChecked != IsApplied;

    public void Reload()
    {
        IsApplied = Tweak.IsApplied();
        IsChecked = IsApplied;
    }
}

public sealed record TweakGroupViewModel(string Name, IReadOnlyList<TweakItemViewModel> Items);

public sealed class TweaksViewModel : ObservableObject
{
    private bool _createRestorePoint = true;
    private bool _isBusy;
    private string _statusText = "";

    public TweaksViewModel()
    {
        var items = TweakCatalog.CreateAll().Select(t => new TweakItemViewModel(t)).ToList();
        Groups = new ObservableCollection<TweakGroupViewModel>(
            items.GroupBy(i => i.Tweak.Group).Select(g => new TweakGroupViewModel(g.Key, g.ToList())));

        ApplyCommand = new AsyncRelayCommand(ApplyAsync, () => !IsBusy && AllItems.Any(i => i.IsModified));
        ReloadCommand = new RelayCommand(Reload, () => !IsBusy);
        RestorePointCommand = new AsyncRelayCommand(CreateRestorePointAsync, () => !IsBusy);
        Reload();
    }

    public ObservableCollection<TweakGroupViewModel> Groups { get; }

    public ICommand ApplyCommand { get; }
    public ICommand ReloadCommand { get; }
    public ICommand RestorePointCommand { get; }

    public bool CreateRestorePoint
    {
        get => _createRestorePoint;
        set => SetProperty(ref _createRestorePoint, value);
    }

    public bool IsBusy
    {
        get => _isBusy;
        private set => SetProperty(ref _isBusy, value);
    }

    public string StatusText
    {
        get => _statusText;
        private set => SetProperty(ref _statusText, value);
    }

    private IEnumerable<TweakItemViewModel> AllItems => Groups.SelectMany(g => g.Items);

    private void Reload()
    {
        foreach (var item in AllItems)
        {
            try
            {
                item.Reload();
            }
            catch (Exception ex) when (ex is UnauthorizedAccessException or System.Security.SecurityException or IOException)
            {
                // Clé illisible : considéré comme non appliqué.
            }
        }

        var applied = AllItems.Count(i => i.IsApplied);
        StatusText = $"{applied} optimisation(s) active(s) sur {AllItems.Count()}. Cochez ou décochez puis cliquez sur « Appliquer ».";
    }

    private async Task ApplyAsync()
    {
        var changes = AllItems.Where(i => i.IsModified).ToList();
        var summary = string.Join("\n", changes.Select(c => (c.IsChecked ? "  ✔ Activer : " : "  ✖ Annuler : ") + c.Name));

        var answer = MessageBox.Show($"Appliquer ces {changes.Count} modification(s) ?\n\n{summary}",
            "Confirmer", MessageBoxButton.YesNo, MessageBoxImage.Question, MessageBoxResult.No);
        if (answer != MessageBoxResult.Yes)
            return;

        IsBusy = true;
        try
        {
            if (CreateRestorePoint)
            {
                StatusText = "Création d'un point de restauration (peut prendre une minute)…";
                var (success, message) = await RestorePoint.CreateAsync("PureClean - avant optimisation");
                if (!success)
                {
                    var proceed = MessageBox.Show(message + "\n\nContinuer sans point de restauration ?",
                        "Point de restauration", MessageBoxButton.YesNo, MessageBoxImage.Warning, MessageBoxResult.No);
                    if (proceed != MessageBoxResult.Yes)
                    {
                        StatusText = "Aucune modification appliquée.";
                        return;
                    }
                }
            }

            var errors = new List<string>();
            foreach (var item in changes)
            {
                try
                {
                    if (item.IsChecked)
                        item.Tweak.Apply();
                    else
                        item.Tweak.Revert();
                }
                catch (Exception ex)
                {
                    errors.Add($"{item.Name} : {ex.Message}");
                }
            }

            var needsRestart = changes.Any(c => c.RequiresRestart);
            Reload();

            if (errors.Count > 0)
                MessageBox.Show("Certaines modifications ont échoué :\n\n" + string.Join("\n", errors),
                    "PureClean", MessageBoxButton.OK, MessageBoxImage.Warning);

            StatusText = $"{changes.Count - errors.Count} modification(s) appliquée(s)." +
                         (needsRestart ? " Redémarrez ou reconnectez-vous pour tout prendre en compte." : "");
        }
        finally
        {
            IsBusy = false;
            CommandManager.InvalidateRequerySuggested();
        }
    }

    private async Task CreateRestorePointAsync()
    {
        IsBusy = true;
        StatusText = "Création d'un point de restauration (peut prendre une minute)…";
        try
        {
            var (success, message) = await RestorePoint.CreateAsync("PureClean - point manuel");
            StatusText = success ? message : "Échec de la création du point de restauration.";
            if (!success)
                MessageBox.Show(message, "Point de restauration", MessageBoxButton.OK, MessageBoxImage.Warning);
        }
        finally
        {
            IsBusy = false;
            CommandManager.InvalidateRequerySuggested();
        }
    }
}
