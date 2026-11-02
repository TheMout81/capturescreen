using System.Collections.ObjectModel;
using System.Windows;
using System.Windows.Input;
using PureClean.Core.Updates;
using PureClean.Infrastructure;

namespace PureClean.ViewModels;

public sealed class UpdateItemViewModel(PackageUpdate package) : ObservableObject
{
    private string _status = "";
    private bool _isDone;

    public PackageUpdate Package { get; } = package;
    public string Name => Package.Name;
    public string Versions => $"{Package.Version}  →  {Package.Available}";
    public string Id => Package.Id;

    public string Status
    {
        get => _status;
        set => SetProperty(ref _status, value);
    }

    /// <summary>Mise à jour terminée avec succès : le bouton disparaît.</summary>
    public bool IsDone
    {
        get => _isDone;
        set => SetProperty(ref _isDone, value);
    }
}

public sealed class UpdatesViewModel : ObservableObject
{
    private CancellationTokenSource? _cts;
    private bool _isBusy;
    private bool _hasChecked;
    private string _statusText = "Cliquez sur « Rechercher » pour lister les logiciels qui ont une nouvelle version.";

    public UpdatesViewModel()
    {
        CheckCommand = new AsyncRelayCommand(CheckAsync, () => !IsBusy);
        CancelCommand = new RelayCommand(() => _cts?.Cancel(), () => IsBusy && _cts is not null);
        UpdateCommand = new RelayCommand<UpdateItemViewModel>(i => _ = UpdateOneAsync(i), i => !IsBusy && !i.IsDone);
        UpdateAllCommand = new AsyncRelayCommand(UpdateAllAsync, () => !IsBusy && Updates.Any(u => !u.IsDone));
    }

    public ObservableCollection<UpdateItemViewModel> Updates { get; } = [];

    public ICommand CheckCommand { get; }
    public ICommand CancelCommand { get; }
    public ICommand UpdateCommand { get; }
    public ICommand UpdateAllCommand { get; }

    public bool IsBusy
    {
        get => _isBusy;
        private set => SetProperty(ref _isBusy, value);
    }

    public bool HasChecked
    {
        get => _hasChecked;
        private set => SetProperty(ref _hasChecked, value);
    }

    public string StatusText
    {
        get => _statusText;
        private set => SetProperty(ref _statusText, value);
    }

    private async Task CheckAsync()
    {
        _cts = new CancellationTokenSource();
        IsBusy = true;
        StatusText = "Recherche des mises à jour avec winget (peut prendre jusqu'à une minute)…";
        try
        {
            var updates = await WingetClient.ListAsync(_cts.Token);
            Updates.Clear();
            foreach (var update in updates)
                Updates.Add(new UpdateItemViewModel(update));
            HasChecked = true;
            StatusText = updates.Count == 0
                ? "Tous vos logiciels gérés par winget sont à jour."
                : $"{updates.Count} mise(s) à jour disponible(s).";
        }
        catch (OperationCanceledException)
        {
            StatusText = "Recherche annulée.";
        }
        catch (InvalidOperationException ex)
        {
            StatusText = "La recherche a échoué : " + ex.Message;
        }
        finally
        {
            IsBusy = false;
            _cts.Dispose();
            _cts = null;
            CommandManager.InvalidateRequerySuggested();
        }
    }

    private async Task UpdateOneAsync(UpdateItemViewModel item)
    {
        if (item.Package.IdTruncated)
        {
            MessageBox.Show("winget n'a pas affiché l'identifiant complet de ce logiciel : utilisez « Tout mettre à jour ».",
                AppInfo.Title, MessageBoxButton.OK, MessageBoxImage.Information);
            return;
        }

        IsBusy = true;
        try
        {
            await UpgradeAsync(item);
            StatusText = item.IsDone ? $"{item.Name} est à jour." : $"La mise à jour de {item.Name} a échoué.";
        }
        finally
        {
            IsBusy = false;
            CommandManager.InvalidateRequerySuggested();
        }
    }

    private async Task UpdateAllAsync()
    {
        var pending = Updates.Where(u => !u.IsDone).ToList();
        var answer = MessageBox.Show(
            $"Mettre à jour {pending.Count} logiciel(s) ?\n\nFermez-les avant de continuer. Les installations se font en arrière-plan, l'une après l'autre.",
            AppInfo.Title, MessageBoxButton.YesNo, MessageBoxImage.Question, MessageBoxResult.Yes);
        if (answer != MessageBoxResult.Yes)
            return;

        IsBusy = true;
        try
        {
            if (pending.Any(u => u.Package.IdTruncated))
            {
                // Identifiant incomplet : seule la commande globale de winget peut cibler ces logiciels.
                foreach (var item in pending)
                    item.Status = "Mise à jour…";
                StatusText = "Mise à jour de tous les logiciels avec winget…";
                await WingetClient.UpgradeAsync(null);
                IsBusy = false;
                await CheckAsync();
                return;
            }

            for (var i = 0; i < pending.Count; i++)
            {
                StatusText = $"Mise à jour {i + 1} / {pending.Count} : {pending[i].Name}…";
                await UpgradeAsync(pending[i]);
            }

            var failed = pending.Count(u => !u.IsDone);
            StatusText = failed == 0 ? "Toutes les mises à jour sont installées." : $"{failed} mise(s) à jour en échec.";
        }
        finally
        {
            IsBusy = false;
            CommandManager.InvalidateRequerySuggested();
        }
    }

    private static async Task UpgradeAsync(UpdateItemViewModel item)
    {
        item.Status = "Mise à jour…";
        try
        {
            var exitCode = await WingetClient.UpgradeAsync(item.Id);
            item.IsDone = exitCode == 0;
            item.Status = exitCode == 0 ? "À jour" : $"Échec (code 0x{exitCode:X8})";
        }
        catch (InvalidOperationException ex)
        {
            item.Status = "Échec : " + ex.Message;
        }
    }
}
