using System.Collections.ObjectModel;
using System.Diagnostics;
using System.Globalization;
using System.Windows;
using System.Windows.Input;
using Microsoft.Win32;
using PureClean.Core.DiskAnalysis;
using PureClean.Infrastructure;

namespace PureClean.ViewModels;

public sealed record DriveTarget(string Name, string Path, string Details);

public sealed class LargeFileViewModel(LargeFile file)
{
    public LargeFile File { get; } = file;
    public string Name => System.IO.Path.GetFileName(File.Path);
    public string Folder => System.IO.Path.GetDirectoryName(File.Path) ?? "";
    public string SizeText => ByteFormatter.Format(File.Size);
    public string DateText => File.Modified.ToString("d MMM yyyy", CultureInfo.CurrentCulture);
}

public sealed class DiskAnalyzerViewModel : ObservableObject
{
    private CancellationTokenSource? _cts;
    private bool _isScanning;
    private string _statusText = "Choisissez un lecteur ou un dossier à analyser.";
    private FolderNodeViewModel? _selectedNode;

    public DiskAnalyzerViewModel()
    {
        ScanDriveCommand = new RelayCommand<DriveTarget>(d => _ = ScanAsync(d.Path), _ => !IsScanning);
        ScanFolderCommand = new RelayCommand(PickFolder, () => !IsScanning);
        CancelCommand = new RelayCommand(() => _cts?.Cancel(), () => IsScanning);
        OpenSelectedCommand = new RelayCommand(() => OpenInExplorer(SelectedNode?.FullPath, select: false), () => SelectedNode?.FullPath is not null);
        DeleteSelectedCommand = new RelayCommand(DeleteSelected, () => !IsScanning && SelectedNode?.CanDelete == true);
        OpenFileCommand = new RelayCommand<LargeFileViewModel>(f => OpenInExplorer(f.File.Path, select: true));
        DeleteFileCommand = new RelayCommand<LargeFileViewModel>(DeleteFile, _ => !IsScanning);
        LoadDrives();
    }

    public ObservableCollection<DriveTarget> Drives { get; } = [];
    public ObservableCollection<FolderNodeViewModel> Roots { get; } = [];
    public ObservableCollection<LargeFileViewModel> LargestFiles { get; } = [];

    public ICommand ScanDriveCommand { get; }
    public ICommand ScanFolderCommand { get; }
    public ICommand CancelCommand { get; }
    public ICommand OpenSelectedCommand { get; }
    public ICommand DeleteSelectedCommand { get; }
    public ICommand OpenFileCommand { get; }
    public ICommand DeleteFileCommand { get; }

    public bool IsScanning
    {
        get => _isScanning;
        private set => SetProperty(ref _isScanning, value);
    }

    public string StatusText
    {
        get => _statusText;
        private set => SetProperty(ref _statusText, value);
    }

    public bool HasResult => Roots.Count > 0;

    public FolderNodeViewModel? SelectedNode
    {
        get => _selectedNode;
        private set
        {
            if (SetProperty(ref _selectedNode, value))
                OnPropertyChanged(nameof(SelectedText));
        }
    }

    public string SelectedText => SelectedNode is null
        ? "Sélectionnez un dossier dans l'arborescence."
        : $"{SelectedNode.FullPath} — {SelectedNode.SizeText}";

    private void LoadDrives()
    {
        foreach (var drive in DriveInfo.GetDrives().Where(d => d.DriveType == DriveType.Fixed))
        {
            try
            {
                if (drive.IsReady)
                    Drives.Add(new DriveTarget(drive.Name.TrimEnd('\\'), drive.RootDirectory.FullName,
                        $"{ByteFormatter.Format(drive.TotalSize - drive.AvailableFreeSpace)} utilisés"));
            }
            catch (IOException)
            {
                // Lecteur indisponible : ignoré.
            }
        }
    }

    private void PickFolder()
    {
        var dialog = new OpenFolderDialog { Title = "Dossier à analyser" };
        if (dialog.ShowDialog() == true)
            _ = ScanAsync(dialog.FolderName);
    }

    private async Task ScanAsync(string path)
    {
        _cts = new CancellationTokenSource();
        IsScanning = true;
        Roots.Clear();
        LargestFiles.Clear();
        SelectedNode = null;
        OnPropertyChanged(nameof(HasResult));
        StatusText = $"Analyse de {path}…";
        CommandManager.InvalidateRequerySuggested();

        var progress = new Progress<(long Folders, string Current)>(p =>
            StatusText = $"{p.Folders:N0} dossiers analysés — {p.Current}");
        try
        {
            var token = _cts.Token;
            var result = await Task.Run(() => new DiskScanner().Scan(path, progress, token), token);

            var root = new FolderNodeViewModel(result.Root, null, node => SelectedNode = node) { IsExpanded = true };
            Roots.Add(root);
            foreach (var file in result.LargestFiles)
                LargestFiles.Add(new LargeFileViewModel(file));

            StatusText = $"{result.Root.FullPath} : {ByteFormatter.Format(result.Root.Size)} dans {result.Root.FileCount:N0} fichiers " +
                         $"et {result.FolderCount:N0} dossiers, analysé en {result.Duration.TotalSeconds:0} s.";
        }
        catch (OperationCanceledException)
        {
            StatusText = "Analyse annulée.";
        }
        finally
        {
            IsScanning = false;
            OnPropertyChanged(nameof(HasResult));
            _cts.Dispose();
            _cts = null;
            CommandManager.InvalidateRequerySuggested();
        }
    }

    private void DeleteSelected()
    {
        var node = SelectedNode;
        if (node?.Node is not { } folder || node.Parent?.Node is not { } parentFolder)
            return;
        if (!ConfirmDelete(folder.FullPath, $"le dossier {folder.FullPath} ({node.SizeText}, {folder.FileCount:N0} fichiers)"))
            return;

        parentFolder.Children.Remove(folder);
        parentFolder.Subtract(folder.Size, folder.FileCount);
        node.Parent.RemoveChild(node);
        SelectedNode = null;

        var prefix = folder.FullPath + Path.DirectorySeparatorChar;
        foreach (var file in LargestFiles.Where(f => f.File.Path.StartsWith(prefix, StringComparison.OrdinalIgnoreCase)).ToList())
            LargestFiles.Remove(file);
        RefreshTree();
    }

    private void DeleteFile(LargeFileViewModel file)
    {
        if (!ConfirmDelete(file.File.Path, $"le fichier {file.File.Path} ({file.SizeText})"))
            return;

        LargestFiles.Remove(file);
        if (Roots.FirstOrDefault()?.Node?.Find(file.Folder) is { } folder)
        {
            folder.OwnFilesSize = Math.Max(0, folder.OwnFilesSize - file.File.Size);
            folder.OwnFileCount = Math.Max(0, folder.OwnFileCount - 1);
            folder.Subtract(file.File.Size, 1);
        }
        RefreshTree();
    }

    /// <summary>Demande confirmation puis envoie à la corbeille. Renvoie true si l'élément a été supprimé.</summary>
    private static bool ConfirmDelete(string path, string description)
    {
        if (PathSafety.WhyProtected(path) is { } reason)
        {
            MessageBox.Show($"Suppression refusée par sécurité :\n{reason}", AppInfo.Title, MessageBoxButton.OK, MessageBoxImage.Warning);
            return false;
        }

        var answer = MessageBox.Show($"Envoyer {description} à la corbeille ?", AppInfo.Title,
            MessageBoxButton.YesNo, MessageBoxImage.Question, MessageBoxResult.No);
        if (answer != MessageBoxResult.Yes)
            return false;

        try
        {
            return RecycleBin.Send(path);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            MessageBox.Show($"Suppression impossible :\n{ex.Message}", AppInfo.Title, MessageBoxButton.OK, MessageBoxImage.Error);
            return false;
        }
    }

    private void RefreshTree()
    {
        foreach (var root in Roots)
            root.RefreshSizes();
        OnPropertyChanged(nameof(SelectedText));
    }

    private static void OpenInExplorer(string? path, bool select)
    {
        if (string.IsNullOrEmpty(path))
            return;
        var arguments = select ? $"/select,\"{path}\"" : $"\"{path}\"";
        Process.Start(new ProcessStartInfo("explorer.exe", arguments) { UseShellExecute = true });
    }
}
