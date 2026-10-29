using System.Collections.ObjectModel;
using System.Windows;
using System.Windows.Input;
using PureClean.Core.Startup;
using PureClean.Infrastructure;
using PureClean.Models;

namespace PureClean.ViewModels;

public sealed class StartupItemViewModel : ObservableObject
{
    private StartupEntry _entry;

    public StartupItemViewModel(StartupEntry entry) => _entry = entry;

    public string Name => _entry.Name;
    public string Command => _entry.Command;
    public string SourceLabel => _entry.SourceLabel;

    /// <summary>Le changement est appliqué immédiatement, comme dans le Gestionnaire des tâches.</summary>
    public bool IsEnabled
    {
        get => _entry.IsEnabled;
        set
        {
            if (value == _entry.IsEnabled)
                return;
            try
            {
                StartupManager.SetEnabled(_entry, value);
                _entry = _entry with { IsEnabled = value };
            }
            catch (Exception ex)
            {
                MessageBox.Show($"Impossible de modifier « {Name} » :\n{ex.Message}", "PureClean",
                    MessageBoxButton.OK, MessageBoxImage.Error);
            }
            OnPropertyChanged();
        }
    }
}

public sealed class StartupViewModel : ObservableObject
{
    private bool _isLoading;
    private string _statusText = "";

    public StartupViewModel()
    {
        RefreshCommand = new AsyncRelayCommand(RefreshAsync, () => !IsLoading);
    }

    public ObservableCollection<StartupItemViewModel> Items { get; } = [];

    public ICommand RefreshCommand { get; }

    public bool IsLoading
    {
        get => _isLoading;
        private set => SetProperty(ref _isLoading, value);
    }

    public string StatusText
    {
        get => _statusText;
        private set => SetProperty(ref _statusText, value);
    }

    public async Task RefreshAsync()
    {
        IsLoading = true;
        StatusText = "Chargement…";
        try
        {
            var entries = await Task.Run(StartupManager.GetEntries);
            Items.Clear();
            foreach (var entry in entries)
                Items.Add(new StartupItemViewModel(entry));

            var enabled = entries.Count(e => e.IsEnabled);
            StatusText = $"{entries.Count} élément(s), dont {enabled} actif(s). Les tâches système de Microsoft ne sont pas affichées.";
        }
        finally
        {
            IsLoading = false;
            CommandManager.InvalidateRequerySuggested();
        }
    }
}
