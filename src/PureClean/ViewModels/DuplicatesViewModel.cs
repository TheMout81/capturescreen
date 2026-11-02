using System.Collections.ObjectModel;
using System.Diagnostics;
using System.Globalization;
using System.Windows;
using System.Windows.Input;
using Microsoft.Win32;
using PureClean.Core.Duplicates;
using PureClean.Infrastructure;

namespace PureClean.ViewModels;

public sealed class DuplicateFileViewModel : ObservableObject
{
    private readonly Action _changed;
    private bool _isChecked;

    public DuplicateFileViewModel(DuplicateFile file, Action changed)
    {
        File = file;
        _changed = changed;
    }

    public DuplicateFile File { get; }
    public string Name => Path.GetFileName(File.Path);
    public string Folder => Path.GetDirectoryName(File.Path) ?? "";
    public string DateText => File.Modified.ToString("d MMM yyyy HH:mm", CultureInfo.CurrentCulture);

    /// <summary>Coché = à supprimer.</summary>
    public bool IsChecked
    {
        get => _isChecked;
        set
        {
            if (SetProperty(ref _isChecked, value))
                _changed();
        }
    }
}

public sealed class DuplicateGroupViewModel : ObservableObject
{
    public DuplicateGroupViewModel(DuplicateGroup group, Action changed)
    {
        Size = group.Size;
        Files = new ObservableCollection<DuplicateFileViewModel>(group.Files.Select(f => new DuplicateFileViewModel(f, changed)));
    }

    public long Size { get; }
    public ObservableCollection<DuplicateFileViewModel> Files { get; }

    public string Header => $"{Files.Count} copies de {ByteFormatter.Format(Size)} — {ByteFormatter.Format(Size * (Files.Count - 1))} récupérables";

    public bool AllChecked => Files.All(f => f.IsChecked);

    public void RemoveMissingFiles()
    {
        foreach (var file in Files.Where(f => !File.Exists(f.File.Path)).ToList())
            Files.Remove(file);
        OnPropertyChanged(nameof(Header));
    }
}

public sealed class DuplicatesViewModel : ObservableObject
{
    private CancellationTokenSource? _cts;
    private bool _isScanning;
    private long _minimumSize = 1024 * 1024;
    private string _statusText = "Choisissez les dossiers à comparer puis lancez la recherche.";
    private string _selectionText = "";

    public DuplicatesViewModel()
    {
        foreach (var folder in new[]
                 {
                     Environment.SpecialFolder.MyPictures, Environment.SpecialFolder.MyDocuments,
                     Environment.SpecialFolder.MyVideos, Environment.SpecialFolder.MyMusic, Environment.SpecialFolder.Desktop,
                 }.Select(Environment.GetFolderPath).Append(DownloadsFolder()))
        {
            if (!string.IsNullOrEmpty(folder) && Directory.Exists(folder) && !Folders.Contains(folder))
                Folders.Add(folder);
        }

        AddFolderCommand = new RelayCommand(AddFolder, () => !IsScanning);
        RemoveFolderCommand = new RelayCommand<string>(f => Folders.Remove(f), _ => !IsScanning);
        ScanCommand = new AsyncRelayCommand(ScanAsync, () => !IsScanning && Folders.Count > 0);
        CancelCommand = new RelayCommand(() => _cts?.Cancel(), () => IsScanning);
        KeepOldestCommand = new RelayCommand(() => AutoSelect(keepOldest: true), () => !IsScanning && Groups.Count > 0);
        KeepNewestCommand = new RelayCommand(() => AutoSelect(keepOldest: false), () => !IsScanning && Groups.Count > 0);
        ClearSelectionCommand = new RelayCommand(ClearSelection, () => !IsScanning && Groups.Count > 0);
        DeleteCommand = new RelayCommand(DeleteSelected, () => !IsScanning && CheckedFiles.Any());
        OpenFileCommand = new RelayCommand<DuplicateFileViewModel>(f =>
            Process.Start(new ProcessStartInfo("explorer.exe", $"/select,\"{f.File.Path}\"") { UseShellExecute = true }));
    }

    public ObservableCollection<string> Folders { get; } = [];
    public ObservableCollection<DuplicateGroupViewModel> Groups { get; } = [];

    public ICommand AddFolderCommand { get; }
    public ICommand RemoveFolderCommand { get; }
    public ICommand ScanCommand { get; }
    public ICommand CancelCommand { get; }
    public ICommand KeepOldestCommand { get; }
    public ICommand KeepNewestCommand { get; }
    public ICommand ClearSelectionCommand { get; }
    public ICommand DeleteCommand { get; }
    public ICommand OpenFileCommand { get; }

    public bool IsScanning
    {
        get => _isScanning;
        private set => SetProperty(ref _isScanning, value);
    }

    /// <summary>Taille minimale des fichiers comparés, en octets.</summary>
    public long MinimumSize
    {
        get => _minimumSize;
        set => SetProperty(ref _minimumSize, value);
    }

    public string StatusText
    {
        get => _statusText;
        private set => SetProperty(ref _statusText, value);
    }

    public string SelectionText
    {
        get => _selectionText;
        private set => SetProperty(ref _selectionText, value);
    }

    public bool HasResults => Groups.Count > 0;

    private IEnumerable<DuplicateFileViewModel> CheckedFiles => Groups.SelectMany(g => g.Files).Where(f => f.IsChecked);

    private static string DownloadsFolder()
        => Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), "Downloads");

    private void AddFolder()
    {
        var dialog = new OpenFolderDialog { Title = "Dossier à inclure dans la recherche" };
        if (dialog.ShowDialog() == true && !Folders.Contains(dialog.FolderName, StringComparer.OrdinalIgnoreCase))
            Folders.Add(dialog.FolderName);
    }

    private async Task ScanAsync()
    {
        _cts = new CancellationTokenSource();
        IsScanning = true;
        Groups.Clear();
        OnPropertyChanged(nameof(HasResults));
        var progress = new Progress<string>(text => StatusText = text);
        var folders = Folders.ToList();
        var minimum = MinimumSize;
        try
        {
            var token = _cts.Token;
            var groups = await Task.Run(() => new DuplicateFinder(minimum).Find(folders, progress, token), token);
            foreach (var group in groups)
                Groups.Add(new DuplicateGroupViewModel(group, UpdateSelection));

            var wasted = groups.Sum(g => g.Wasted);
            StatusText = groups.Count == 0
                ? "Aucun doublon trouvé."
                : $"{groups.Count:N0} groupe(s) de fichiers identiques, {ByteFormatter.Format(wasted)} récupérables. " +
                  "Cochez les copies à supprimer, ou utilisez la sélection automatique.";
        }
        catch (OperationCanceledException)
        {
            StatusText = "Recherche annulée.";
        }
        finally
        {
            IsScanning = false;
            OnPropertyChanged(nameof(HasResults));
            UpdateSelection();
            _cts.Dispose();
            _cts = null;
            CommandManager.InvalidateRequerySuggested();
        }
    }

    /// <summary>Coche toutes les copies sauf une par groupe (la plus ancienne ou la plus récente).</summary>
    private void AutoSelect(bool keepOldest)
    {
        foreach (var group in Groups)
        {
            var keep = keepOldest
                ? group.Files.MinBy(f => f.File.Modified)
                : group.Files.MaxBy(f => f.File.Modified);
            foreach (var file in group.Files)
                file.IsChecked = file != keep;
        }
    }

    private void ClearSelection()
    {
        foreach (var file in Groups.SelectMany(g => g.Files))
            file.IsChecked = false;
    }

    private void UpdateSelection()
    {
        var files = CheckedFiles.ToList();
        SelectionText = files.Count == 0
            ? "Aucune copie cochée"
            : $"{files.Count:N0} copie(s) cochée(s) — {ByteFormatter.Format(files.Sum(f => f.File.Size))}";
    }

    private void DeleteSelected()
    {
        // Garde-fou : chaque groupe doit conserver au moins un exemplaire.
        if (Groups.FirstOrDefault(g => g.AllChecked) is { } unsafeGroup)
        {
            MessageBox.Show(
                $"Toutes les copies de « {unsafeGroup.Files[0].Name} » sont cochées.\n\nDécochez au moins un exemplaire à conserver.",
                AppInfo.Title, MessageBoxButton.OK, MessageBoxImage.Warning);
            return;
        }

        var files = CheckedFiles.ToList();
        var answer = MessageBox.Show(
            $"Envoyer {files.Count:N0} copie(s) ({ByteFormatter.Format(files.Sum(f => f.File.Size))}) à la corbeille ?\n\n" +
            "Un exemplaire de chaque fichier est conservé.",
            AppInfo.Title, MessageBoxButton.YesNo, MessageBoxImage.Question, MessageBoxResult.No);
        if (answer != MessageBoxResult.Yes)
            return;

        try
        {
            RecycleBin.SendMany(files.Select(f => f.File.Path).ToList());
        }
        catch (IOException ex)
        {
            MessageBox.Show(ex.Message, AppInfo.Title, MessageBoxButton.OK, MessageBoxImage.Error);
        }

        // Retire de la liste ce qui a réellement disparu du disque.
        foreach (var group in Groups.ToList())
        {
            group.RemoveMissingFiles();
            if (group.Files.Count < 2)
                Groups.Remove(group);
        }
        OnPropertyChanged(nameof(HasResults));
        UpdateSelection();
        StatusText = $"{Groups.Count:N0} groupe(s) restant(s).";
    }
}
