using System.Diagnostics;
using System.Windows;
using System.Windows.Input;
using PureClean.Infrastructure;

namespace PureClean.ViewModels;

public sealed class SettingsViewModel : ObservableObject
{
    private readonly AppSettings _settings = AppSettings.Current;

    public SettingsViewModel()
    {
        OpenDataFolderCommand = new RelayCommand(OpenDataFolder);
        ClearHistoryCommand = new RelayCommand(ClearHistory);
    }

    public ICommand OpenDataFolderCommand { get; }
    public ICommand ClearHistoryCommand { get; }

    public ThemeMode Theme
    {
        get => _settings.Theme;
        set
        {
            if (_settings.Theme == value)
                return;
            _settings.Theme = value;
            _settings.Save();
            ThemeManager.Apply(value);
            OnPropertyChanged();
        }
    }

    public string DataFolder => AppSettings.Folder;

    public string Version => AppInfo.Version;

    private void OpenDataFolder()
    {
        Directory.CreateDirectory(AppSettings.Folder);
        Process.Start(new ProcessStartInfo("explorer.exe", $"\"{AppSettings.Folder}\"") { UseShellExecute = true });
    }

    private void ClearHistory()
    {
        var answer = MessageBox.Show("Effacer l'historique des nettoyages et remettre le compteur d'espace libéré à zéro ?",
            AppInfo.Title, MessageBoxButton.YesNo, MessageBoxImage.Question, MessageBoxResult.No);
        if (answer != MessageBoxResult.Yes)
            return;

        _settings.History.Clear();
        _settings.TotalBytesFreed = 0;
        _settings.Save();
    }
}
