using System.Collections.ObjectModel;
using PureClean.Core.DiskAnalysis;
using PureClean.Infrastructure;

namespace PureClean.ViewModels;

public enum FolderEntryKind
{
    Folder,
    /// <summary>Fichiers placés directement dans le dossier parent.</summary>
    Files,
    /// <summary>Regroupement des plus petits sous-dossiers au-delà de la limite d'affichage.</summary>
    Others,
}

/// <summary>Ligne de l'arborescence de l'analyseur. Les enfants ne sont créés qu'à l'affichage.</summary>
public sealed class FolderNodeViewModel : ObservableObject
{
    private const int MaxChildren = 200;

    private readonly Action<FolderNodeViewModel> _selected;
    private readonly long _fixedSize;
    private readonly long _fixedCount;
    private ObservableCollection<FolderNodeViewModel>? _children;
    private bool _isExpanded;
    private bool _isSelected;

    public FolderNodeViewModel(FolderNode node, FolderNodeViewModel? parent, Action<FolderNodeViewModel> selected)
    {
        Node = node;
        Parent = parent;
        Kind = FolderEntryKind.Folder;
        Name = node.Name;
        _selected = selected;
    }

    private FolderNodeViewModel(FolderEntryKind kind, string name, long size, long count, FolderNodeViewModel parent, Action<FolderNodeViewModel> selected)
    {
        Kind = kind;
        Name = name;
        Parent = parent;
        _fixedSize = size;
        _fixedCount = count;
        _selected = selected;
    }

    public FolderNode? Node { get; }
    public FolderNodeViewModel? Parent { get; }
    public FolderEntryKind Kind { get; }
    public string Name { get; }

    public long Size => Kind switch
    {
        FolderEntryKind.Folder => Node!.Size,
        FolderEntryKind.Files => Parent!.Node!.OwnFilesSize,
        _ => _fixedSize,
    };

    public long FileCount => Kind switch
    {
        FolderEntryKind.Folder => Node!.FileCount,
        FolderEntryKind.Files => Parent!.Node!.OwnFileCount,
        _ => _fixedCount,
    };

    /// <summary>Chemin à ouvrir dans l'Explorateur (le dossier parent pour les entrées regroupées).</summary>
    public string? FullPath => Node?.FullPath ?? Parent?.Node?.FullPath;

    public bool CanDelete => Kind == FolderEntryKind.Folder && Parent is not null;

    public double Percent => Parent is null || Parent.Size == 0 ? 100 : 100.0 * Size / Parent.Size;

    public string PercentText => $"{Percent:0.0} %";
    public string SizeText => ByteFormatter.Format(Size);

    public string Details => Kind == FolderEntryKind.Folder && Node!.AccessDenied
        ? "Accès refusé : une partie du contenu n'a pas pu être comptée"
        : $"{FileCount:N0} fichier(s)";

    public string Glyph => Kind switch
    {
        FolderEntryKind.Folder => "\uE8B7",
        FolderEntryKind.Files => "\uE7C3",
        _ => "\uE712",
    };

    public ObservableCollection<FolderNodeViewModel> Children => _children ??= BuildChildren();

    public bool IsExpanded
    {
        get => _isExpanded;
        set => SetProperty(ref _isExpanded, value);
    }

    public bool IsSelected
    {
        get => _isSelected;
        set
        {
            if (SetProperty(ref _isSelected, value) && value)
                _selected(this);
        }
    }

    public void RemoveChild(FolderNodeViewModel child) => _children?.Remove(child);

    /// <summary>Met à jour les tailles affichées après une suppression.</summary>
    public void RefreshSizes()
    {
        OnPropertyChanged(nameof(Size));
        OnPropertyChanged(nameof(SizeText));
        OnPropertyChanged(nameof(Percent));
        OnPropertyChanged(nameof(PercentText));
        OnPropertyChanged(nameof(Details));
        if (_children is null)
            return;
        foreach (var child in _children)
            child.RefreshSizes();
    }

    private ObservableCollection<FolderNodeViewModel> BuildChildren()
    {
        var list = new ObservableCollection<FolderNodeViewModel>();
        if (Kind != FolderEntryKind.Folder || Node!.Children.Count == 0)
            return list;

        var entries = Node.Children.Take(MaxChildren).Select(c => new FolderNodeViewModel(c, this, _selected)).ToList();

        var rest = Node.Children.Skip(MaxChildren).ToList();
        if (rest.Count > 0)
            entries.Add(new FolderNodeViewModel(FolderEntryKind.Others, $"{rest.Count:N0} autres dossiers",
                rest.Sum(c => c.Size), rest.Sum(c => c.FileCount), this, _selected));

        if (Node.OwnFileCount > 0)
            entries.Add(new FolderNodeViewModel(FolderEntryKind.Files, $"Fichiers de ce dossier ({Node.OwnFileCount:N0})",
                0, 0, this, _selected));

        foreach (var entry in entries.OrderByDescending(e => e.Size))
            list.Add(entry);
        return list;
    }
}
