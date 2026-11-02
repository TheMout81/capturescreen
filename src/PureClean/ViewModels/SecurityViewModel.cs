using System.Collections.ObjectModel;
using System.Diagnostics;
using System.Globalization;
using System.Net.Http;
using System.Windows;
using System.Windows.Input;
using Microsoft.Win32;
using PureClean.Core.Security;
using PureClean.Infrastructure;

namespace PureClean.ViewModels;

public enum SecurityTab
{
    Protection,
    Audit,
    SecondOpinion,
}

/// <summary>Élément affiché avec une pastille de couleur selon sa gravité.</summary>
public interface ISeverityItem
{
    FindingSeverity Severity { get; }
}

public static class SeverityText
{
    public static string Label(FindingSeverity severity) => severity switch
    {
        FindingSeverity.Danger => "Important",
        FindingSeverity.Warning => "À vérifier",
        FindingSeverity.Info => "Information",
        _ => "OK",
    };

    public static string Glyph(FindingSeverity severity) => severity switch
    {
        FindingSeverity.Danger => "",
        FindingSeverity.Warning => "",
        FindingSeverity.Info => "",
        _ => "",
    };
}

public sealed record StatusItem(string Label, string Value, FindingSeverity Severity) : ISeverityItem
{
    public string Glyph => SeverityText.Glyph(Severity);
}

public sealed record FindingViewModel(SecurityFinding Finding) : ISeverityItem
{
    public FindingSeverity Severity => Finding.Severity;
    public string Title => Finding.Title;
    public string Detail => Finding.Detail;
    public string FixLabel => Finding.FixLabel ?? "";
    public bool HasFix => Finding.Fix is not null;
    public string SeverityLabel => SeverityText.Label(Severity);
    public string Glyph => SeverityText.Glyph(Severity);
}

public sealed class ScannerViewModel(SecondOpinionScanner scanner) : ObservableObject
{
    private string _status = "";
    private bool _isBusy;

    public SecondOpinionScanner Scanner { get; } = scanner;
    public string Name => Scanner.Name;
    public string Publisher => Scanner.Publisher;
    public string Description => Scanner.Description;

    public string Status
    {
        get => _status;
        set => SetProperty(ref _status, value);
    }

    public bool IsBusy
    {
        get => _isBusy;
        set => SetProperty(ref _isBusy, value);
    }
}

/// <summary>Page « Sécurité » : Microsoft Defender, bilan de sécurité, scanners de second avis.</summary>
public sealed class SecurityViewModel : ObservableObject
{
    private SecurityTab _tab = SecurityTab.Protection;
    private SecurityProbe? _probe;
    private bool _isBusy;
    private bool _isLoaded;
    private bool _auditDone;
    private string _statusText = "";
    private string _auditSummary = "Le bilan contrôle les réglages que les logiciels malveillants et publicitaires modifient le plus souvent.";

    public SecurityViewModel()
    {
        RefreshCommand = new AsyncRelayCommand(RefreshAsync, () => !IsBusy);
        UpdateCommand = new AsyncRelayCommand(() => RunDefenderAsync("Mise à jour des signatures…", DefenderClient.UpdateSignaturesAsync,
            "Signatures à jour."), () => !IsBusy && DefenderAvailable);
        QuickScanCommand = new AsyncRelayCommand(() => ScanAsync(ScanKind.Quick), () => !IsBusy && DefenderAvailable);
        FullScanCommand = new AsyncRelayCommand(() => ScanAsync(ScanKind.Full), () => !IsBusy && DefenderAvailable);
        FolderScanCommand = new AsyncRelayCommand(() => ScanAsync(ScanKind.Folder), () => !IsBusy && DefenderAvailable);
        OfflineScanCommand = new AsyncRelayCommand(OfflineScanAsync, () => !IsBusy && DefenderAvailable);
        OpenSecurityCenterCommand = new RelayCommand(() => Process.Start(new ProcessStartInfo("windowsdefender:") { UseShellExecute = true }));
        RunAuditCommand = new AsyncRelayCommand(RunAuditAsync, () => !IsBusy);
        FixCommand = new RelayCommand<FindingViewModel>(f => _ = FixAsync(f), f => !IsBusy && f.HasFix);
        RunScannerCommand = new RelayCommand<ScannerViewModel>(s => _ = RunScannerAsync(s), s => !s.IsBusy);
        Scanners = new ObservableCollection<ScannerViewModel>(SecondOpinionScanner.All.Select(s => new ScannerViewModel(s)));
    }

    public ICommand RefreshCommand { get; }
    public ICommand UpdateCommand { get; }
    public ICommand QuickScanCommand { get; }
    public ICommand FullScanCommand { get; }
    public ICommand FolderScanCommand { get; }
    public ICommand OfflineScanCommand { get; }
    public ICommand OpenSecurityCenterCommand { get; }
    public ICommand RunAuditCommand { get; }
    public ICommand FixCommand { get; }
    public ICommand RunScannerCommand { get; }

    public ObservableCollection<StatusItem> StatusItems { get; } = [];
    public ObservableCollection<ThreatRecord> Threats { get; } = [];
    public ObservableCollection<FindingViewModel> Findings { get; } = [];
    public ObservableCollection<BrowserExtension> Extensions { get; } = [];
    public ObservableCollection<ScannerViewModel> Scanners { get; }

    public SecurityTab Tab
    {
        get => _tab;
        set
        {
            if (!SetProperty(ref _tab, value))
                return;
            OnPropertyChanged(nameof(IsProtectionTab));
            OnPropertyChanged(nameof(IsAuditTab));
            OnPropertyChanged(nameof(IsSecondOpinionTab));
            if (value == SecurityTab.Audit && !_auditDone && !IsBusy)
                RunAuditCommand.Execute(null);
        }
    }

    public bool IsProtectionTab => Tab == SecurityTab.Protection;
    public bool IsAuditTab => Tab == SecurityTab.Audit;
    public bool IsSecondOpinionTab => Tab == SecurityTab.SecondOpinion;

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

    public string AuditSummary
    {
        get => _auditSummary;
        private set => SetProperty(ref _auditSummary, value);
    }

    public bool DefenderAvailable => _probe?.Defender.Available == true;
    public bool HasThreats => Threats.Count > 0;
    public bool HasExtensions => Extensions.Count > 0;

    /// <summary>Premier affichage de la page : lit l'état de Defender.</summary>
    public void EnsureLoaded()
    {
        if (_isLoaded || IsBusy)
            return;
        _isLoaded = true;
        RefreshCommand.Execute(null);
    }

    private async Task RefreshAsync()
    {
        IsBusy = true;
        StatusText = "Lecture de l'état de Microsoft Defender…";
        try
        {
            await LoadProbeAsync();
            StatusText = "";
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

    private async Task LoadProbeAsync()
    {
        _probe = await DefenderClient.ProbeAsync();
        OnPropertyChanged(nameof(DefenderAvailable));

        StatusItems.Clear();
        foreach (var item in BuildStatus(_probe))
            StatusItems.Add(item);

        Threats.Clear();
        foreach (var threat in _probe.Threats)
            Threats.Add(threat);
        OnPropertyChanged(nameof(HasThreats));
    }

    private static IEnumerable<StatusItem> BuildStatus(SecurityProbe probe)
    {
        var d = probe.Defender;
        if (!d.Available)
        {
            yield return new StatusItem("Microsoft Defender", "Inaccessible (un autre antivirus est peut-être installé)", FindingSeverity.Warning);
        }
        else
        {
            yield return d.Mode.Contains("Passive", StringComparison.OrdinalIgnoreCase)
                ? new StatusItem("Protection en temps réel", "Assurée par un autre antivirus", FindingSeverity.Info)
                : new StatusItem("Protection en temps réel", d.RealTime ? "Active" : "Désactivée", d.RealTime ? FindingSeverity.Ok : FindingSeverity.Danger);
            yield return new StatusItem("Signatures antivirus", $"{Age(d.SignatureAgeDays)} · v{d.SignatureVersion}",
                d.SignatureAgeDays <= 3 ? FindingSeverity.Ok : FindingSeverity.Warning);
            yield return new StatusItem("Dernière analyse rapide", Age(d.QuickScanAgeDays), d.QuickScanAgeDays <= 7 ? FindingSeverity.Ok : FindingSeverity.Info);
            yield return new StatusItem("Dernière analyse complète", Age(d.FullScanAgeDays), d.FullScanAgeDays <= 60 ? FindingSeverity.Ok : FindingSeverity.Info);
            yield return new StatusItem("Protection contre les falsifications", d.TamperProtected ? "Active" : "Désactivée",
                d.TamperProtected ? FindingSeverity.Ok : FindingSeverity.Info);
        }

        if (probe.Firewall.Count > 0)
        {
            var off = probe.Firewall.Where(f => !f.Enabled).Select(f => f.Name).ToList();
            yield return new StatusItem("Pare-feu", off.Count == 0 ? "Actif" : "Désactivé : " + string.Join(", ", off),
                off.Count == 0 ? FindingSeverity.Ok : FindingSeverity.Danger);
        }
    }

    private static string Age(long days) => days switch
    {
        >= 10000 => "jamais",
        0 => "aujourd'hui",
        1 => "hier",
        _ => $"il y a {days} jours",
    };

    private async Task ScanAsync(ScanKind kind)
    {
        string? folder = null;
        if (kind == ScanKind.Folder)
        {
            var dialog = new OpenFolderDialog { Title = "Dossier à analyser avec Microsoft Defender" };
            if (dialog.ShowDialog() != true)
                return;
            folder = dialog.FolderName;
        }
        else if (kind == ScanKind.Full)
        {
            var answer = MessageBox.Show(
                "L'analyse complète examine tous les fichiers et peut durer plus d'une heure. Vous pouvez continuer à utiliser le PC pendant ce temps.\n\nLancer l'analyse complète ?",
                AppInfo.Title, MessageBoxButton.YesNo, MessageBoxImage.Question, MessageBoxResult.Yes);
            if (answer != MessageBoxResult.Yes)
                return;
        }

        var label = kind switch
        {
            ScanKind.Quick => "Analyse rapide en cours (quelques minutes)…",
            ScanKind.Full => "Analyse complète en cours (peut durer plus d'une heure)…",
            _ => $"Analyse de {folder}…",
        };
        var before = _probe?.Threats.Count ?? 0;
        await RunDefenderAsync(label, () => DefenderClient.ScanAsync(kind, folder), null);

        var found = (_probe?.Threats.Count ?? 0) - before;
        StatusText = found > 0
            ? $"Analyse terminée : {found} nouvelle(s) menace(s) détectée(s), voir l'historique ci-dessous."
            : "Analyse terminée : aucune nouvelle menace détectée.";
    }

    private async Task OfflineScanAsync()
    {
        var answer = MessageBox.Show(
            "L'analyse hors ligne détecte les menaces qui se cachent quand Windows fonctionne.\n\n" +
            "Le PC va redémarrer IMMÉDIATEMENT et l'analyse durera environ 15 minutes. Enregistrez votre travail et fermez vos applications avant de continuer.\n\n" +
            "Redémarrer maintenant pour l'analyse hors ligne ?",
            AppInfo.Title, MessageBoxButton.YesNo, MessageBoxImage.Warning, MessageBoxResult.No);
        if (answer == MessageBoxResult.Yes)
            await RunDefenderAsync("Préparation de l'analyse hors ligne…", DefenderClient.OfflineScanAsync, "Redémarrage en cours…");
    }

    private async Task RunDefenderAsync(string busyText, Func<Task> action, string? doneText)
    {
        IsBusy = true;
        StatusText = busyText;
        try
        {
            await action();
            await LoadProbeAsync();
            if (doneText is not null)
                StatusText = doneText;
        }
        catch (InvalidOperationException ex)
        {
            StatusText = "Échec : " + ex.Message;
        }
        finally
        {
            IsBusy = false;
            CommandManager.InvalidateRequerySuggested();
        }
    }

    private async Task RunAuditAsync()
    {
        IsBusy = true;
        AuditSummary = "Bilan en cours…";
        try
        {
            if (_probe is null)
            {
                try
                {
                    await LoadProbeAsync();
                }
                catch (InvalidOperationException)
                {
                    // Le bilan se fait sans les contrôles de Defender.
                }
            }

            var findings = await SecurityAudit.RunAsync(_probe);
            Findings.Clear();
            foreach (var finding in findings)
                Findings.Add(new FindingViewModel(finding));

            var extensions = await Task.Run(BrowserExtensions.ReadAll);
            Extensions.Clear();
            foreach (var extension in extensions)
                Extensions.Add(extension);
            OnPropertyChanged(nameof(HasExtensions));

            var danger = findings.Count(f => f.Severity == FindingSeverity.Danger);
            var warning = findings.Count(f => f.Severity == FindingSeverity.Warning);
            AuditSummary = danger + warning == 0
                ? $"Aucun problème détecté ({findings.Count(f => f.Severity == FindingSeverity.Ok)} points contrôlés). Vérifiez tout de même la liste des extensions ci-dessous."
                : $"{danger} point(s) important(s) et {warning} point(s) à vérifier. Chaque correction est proposée, jamais appliquée sans votre accord.";
            _auditDone = true;
        }
        finally
        {
            IsBusy = false;
            CommandManager.InvalidateRequerySuggested();
        }
    }

    private async Task FixAsync(FindingViewModel item)
    {
        var fix = item.Finding.Fix;
        if (fix is null)
            return;

        var question = item.Finding.FixConfirmation ?? $"{item.FixLabel} ?";
        if (MessageBox.Show(question, AppInfo.Title, MessageBoxButton.YesNo, MessageBoxImage.Question, MessageBoxResult.No) != MessageBoxResult.Yes)
            return;

        try
        {
            await fix();
        }
        catch (Exception ex) when (ex is InvalidOperationException or IOException or UnauthorizedAccessException
                                       or System.Security.SecurityException or System.ComponentModel.Win32Exception)
        {
            MessageBox.Show("La correction n'a pas pu être appliquée :\n" + ex.Message, AppInfo.Title, MessageBoxButton.OK, MessageBoxImage.Warning);
        }

        // Relit l'état pour afficher le résultat réel de la correction.
        _probe = null;
        await RunAuditAsync();
    }

    private async Task RunScannerAsync(ScannerViewModel item)
    {
        item.IsBusy = true;
        CommandManager.InvalidateRequerySuggested();
        try
        {
            await item.Scanner.DownloadAndRunAsync(new Progress<string>(text => item.Status = text));
            item.Status = $"Lancé le {DateTime.Now.ToString("g", CultureInfo.CurrentCulture)}. Suivez les instructions de {item.Name}.";
        }
        catch (Exception ex) when (ex is InvalidOperationException or HttpRequestException or IOException
                                       or TaskCanceledException or System.ComponentModel.Win32Exception)
        {
            item.Status = "Échec : " + ex.Message;
        }
        finally
        {
            item.IsBusy = false;
            CommandManager.InvalidateRequerySuggested();
        }
    }
}
