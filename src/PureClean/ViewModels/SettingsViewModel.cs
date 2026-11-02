using System.Diagnostics;
using System.Globalization;
using System.Windows;
using System.Windows.Input;
using PureClean.Core;
using PureClean.Infrastructure;

namespace PureClean.ViewModels;

/// <summary>Choix d'une liste déroulante : affiché par son libellé.</summary>
public sealed record Option<T>(T Value, string Label)
{
    public override string ToString() => Label;
}

public sealed class SettingsViewModel : ObservableObject
{
    private readonly AppSettings _settings = AppSettings.Current;

    private bool _scheduleEnabled;
    private ScheduleFrequency _frequency;
    private Option<DayOfWeek> _selectedDay;
    private Option<int> _selectedHour;
    private string _scheduleStatus = "";

    public SettingsViewModel()
    {
        OpenDataFolderCommand = new RelayCommand(OpenDataFolder);
        ClearHistoryCommand = new RelayCommand(ClearHistory);
        SaveScheduleCommand = new RelayCommand(SaveSchedule);
        RunNowCommand = new RelayCommand(RunNow, () => _settings.Schedule.Enabled);
        OpenLogCommand = new RelayCommand(OpenLog, () => File.Exists(AutoClean.LogPath));

        var culture = CultureInfo.CurrentCulture;
        DayOptions = Enum.GetValues<DayOfWeek>()
            .Select(d => new Option<DayOfWeek>(d, culture.DateTimeFormat.GetDayName(d)))
            .ToList();
        HourOptions = Enumerable.Range(0, 24).Select(h => new Option<int>(h, $"{h} h")).ToList();

        var schedule = _settings.Schedule;
        _scheduleEnabled = schedule.Enabled;
        _frequency = schedule.Frequency;
        _selectedDay = DayOptions.First(d => d.Value == schedule.Day);
        _selectedHour = HourOptions.First(h => h.Value == Math.Clamp(schedule.Hour, 0, 23));
        _scheduleStatus = DescribeSchedule();
    }

    public ICommand OpenDataFolderCommand { get; }
    public ICommand ClearHistoryCommand { get; }
    public ICommand SaveScheduleCommand { get; }
    public ICommand RunNowCommand { get; }
    public ICommand OpenLogCommand { get; }

    public IReadOnlyList<Option<DayOfWeek>> DayOptions { get; }
    public IReadOnlyList<Option<int>> HourOptions { get; }

    public bool ScheduleEnabled
    {
        get => _scheduleEnabled;
        set => SetProperty(ref _scheduleEnabled, value);
    }

    public ScheduleFrequency Frequency
    {
        get => _frequency;
        set
        {
            if (SetProperty(ref _frequency, value))
                OnPropertyChanged(nameof(IsWeekly));
        }
    }

    public bool IsWeekly => Frequency == ScheduleFrequency.Weekly;

    public Option<DayOfWeek> SelectedDay
    {
        get => _selectedDay;
        set => SetProperty(ref _selectedDay, value);
    }

    public Option<int> SelectedHour
    {
        get => _selectedHour;
        set => SetProperty(ref _selectedHour, value);
    }

    public string ScheduleStatus
    {
        get => _scheduleStatus;
        private set => SetProperty(ref _scheduleStatus, value);
    }

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

    private void SaveSchedule()
    {
        var schedule = new ScheduleSettings
        {
            Enabled = ScheduleEnabled,
            Frequency = Frequency,
            Day = SelectedDay.Value,
            Hour = SelectedHour.Value,
        };

        var executable = Environment.ProcessPath;
        if (schedule.Enabled && string.IsNullOrEmpty(executable))
        {
            MessageBox.Show("Impossible de déterminer l'emplacement de PureClean.exe.", AppInfo.Title,
                MessageBoxButton.OK, MessageBoxImage.Error);
            return;
        }

        try
        {
            ScheduledCleaning.Apply(schedule, executable ?? "", AutoClean.Argument);
            _settings.Schedule = schedule;
            _settings.Save();
            ScheduleStatus = DescribeSchedule();
        }
        catch (InvalidOperationException ex)
        {
            MessageBox.Show("La tâche planifiée n'a pas pu être enregistrée :\n" + ex.Message, AppInfo.Title,
                MessageBoxButton.OK, MessageBoxImage.Error);
        }
    }

    private void RunNow()
    {
        try
        {
            ScheduledCleaning.RunNow();
            ScheduleStatus = "Nettoyage automatique lancé en arrière-plan. Le résultat apparaîtra dans le journal et sur l'accueil.";
        }
        catch (InvalidOperationException ex)
        {
            MessageBox.Show(ex.Message, AppInfo.Title, MessageBoxButton.OK, MessageBoxImage.Error);
        }
    }

    private static void OpenLog()
        => Process.Start(new ProcessStartInfo("notepad.exe", $"\"{AutoClean.LogPath}\"") { UseShellExecute = true });

    private string DescribeSchedule()
    {
        var schedule = _settings.Schedule;
        if (!schedule.Enabled)
            return "Désactivé.";

        var when = schedule.Frequency switch
        {
            ScheduleFrequency.Daily => "chaque jour",
            ScheduleFrequency.Monthly => "le 1er de chaque mois",
            _ => "chaque " + CultureInfo.CurrentCulture.DateTimeFormat.GetDayName(schedule.Day),
        };
        return $"Actif : {when} à {schedule.Hour} h (ou dès que possible si le PC était éteint).";
    }

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

        CleanHistory.Clear();
    }
}
