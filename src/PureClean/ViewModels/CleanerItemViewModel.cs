using PureClean.Core;
using PureClean.Infrastructure;
using PureClean.Models;

namespace PureClean.ViewModels;

public sealed class CleanerItemViewModel : ObservableObject
{
    private bool _isSelected;
    private string _resultText = "";

    public CleanerItemViewModel(ICleaner cleaner)
    {
        Cleaner = cleaner;
        _isSelected = cleaner.EnabledByDefault;
    }

    public ICleaner Cleaner { get; }
    public string Name => Cleaner.Name;
    public string Description => Cleaner.Description;

    public bool IsSelected
    {
        get => _isSelected;
        set => SetProperty(ref _isSelected, value);
    }

    /// <summary>Taille trouvée lors de l'analyse, ou bilan du nettoyage.</summary>
    public string ResultText
    {
        get => _resultText;
        private set => SetProperty(ref _resultText, value);
    }

    public ScanResult? LastScan { get; private set; }

    public void ShowScan(ScanResult result)
    {
        LastScan = result;
        ResultText = result.FileCount == 0 ? "Rien à nettoyer" : $"{ByteFormatter.Format(result.Bytes)} · {result.FileCount:N0} fichier(s)";
    }

    public void ShowClean(CleanResult result)
    {
        LastScan = null;
        ResultText = result.FilesSkipped > 0
            ? $"{ByteFormatter.Format(result.BytesFreed)} libérés · {result.FilesSkipped:N0} en cours d'utilisation"
            : result.FilesDeleted == 0 ? "Terminé" : $"{ByteFormatter.Format(result.BytesFreed)} libérés";
    }

    public void ShowText(string text)
    {
        LastScan = null;
        ResultText = text;
    }
}
