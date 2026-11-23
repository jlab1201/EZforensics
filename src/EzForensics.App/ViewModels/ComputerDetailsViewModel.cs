using System.Collections.ObjectModel;
using System.Text;
using System.Windows;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using EzForensics.App.Services;
using EzForensics.Core.Execution;
using EzForensics.Core.SystemInfo;
using EzForensics.Core.Triage;

namespace EzForensics.App.ViewModels;

/// <summary>Fields of one category (Network: ..., Operating system, ...).</summary>
public sealed record DetailGroup(string Category, IReadOnlyList<DetailField> Fields);

/// <summary>One key fact line of the compact box.</summary>
public sealed record KeyFact(string Label, string Value);

/// <summary>One system (system root) of the case in the Computer details panel.</summary>
public sealed class SystemDetailsItem
{
    public SystemDetailsItem(SystemDetails d)
    {
        Details = d;
        KeyFacts = ComputerDetailsPresentation.KeyFacts(d).Select(k => new KeyFact(k.Label, k.Value)).ToList();
        Groups = ComputerDetailsPresentation.DetailedFields(d).GroupBy(f => f.Category).Select(g => new DetailGroup(g.Key, g.ToList())).ToList();
    }

    public SystemDetails Details { get; }
    public IReadOnlyList<KeyFact> KeyFacts { get; }
    public string Title => Details.Title;
    public string Summary => Details.Summary;
    /// <summary>Summary line with the machine type slotted in after the OS ("HOST · Windows 11 Pro · Virtual machine (VMware) · 10.0.0.5").</summary>
    public string DisplaySummary
    {
        get
        {
            var mt = Details.Fields.FirstOrDefault(f => f.Category == "Hardware" && f.Field == "Machine type")?.Value;
            if (string.IsNullOrWhiteSpace(mt) || mt.Equals("Unknown", StringComparison.OrdinalIgnoreCase)) return Details.Summary;
            var parts = Details.Summary.Split(" · ").ToList();
            if (parts.Any(p => p.Contains(mt, StringComparison.OrdinalIgnoreCase))) return Details.Summary;
            parts.Insert(Math.Min(2, parts.Count), mt);
            return string.Join(" · ", parts);
        }
    }
    public string SystemRoot => Details.SystemRoot;
    public IReadOnlyList<DetailGroup> Groups { get; }
}

/// <summary>
/// Analyze page "Computer details" panel: the evidence's identity read from its offline hives (<c>&lt;case&gt;\SystemInfo\ComputerDetails.json</c>).
/// Computed by the built-in job at the end of triage, on demand for a case that lacks it (from the run manifest's source path), or on Refresh.
/// </summary>
public sealed partial class ComputerDetailsViewModel : ObservableObject
{
    private readonly Action<string, Exception> _showError;
    private readonly Action<string, InfoSeverity> _showMessage;
    private readonly HashSet<string> _autoTried = new(StringComparer.OrdinalIgnoreCase);
    private Job? _job;

    [ObservableProperty] private string? _caseFolder;
    /// <summary>Detailed view shown; collapsed (compact key facts only) by default and not persisted.</summary>
    [ObservableProperty] private bool _isExpanded;
    [ObservableProperty] private bool _isBusy;
    [ObservableProperty] private bool _hasReport;
    [ObservableProperty] private string _summaryText = "";
    [ObservableProperty] private string _statusText = "";

    public ComputerDetailsViewModel(Action<string, Exception> showError, Action<string, InfoSeverity> showMessage)
    {
        _showError = showError;
        _showMessage = showMessage;
    }

    private static AppServices S => AppServices.Current;

    public string DetailsButtonText => IsExpanded ? "Hide details" : "Show details";

    partial void OnIsExpandedChanged(bool value) => OnPropertyChanged(nameof(DetailsButtonText));

    [RelayCommand]
    private void ToggleDetails() => IsExpanded = !IsExpanded;

    public ObservableCollection<SystemDetailsItem> Systems { get; } = new();
    public ComputerDetailsReport? Report { get; private set; }
    public bool HasSeveralSystems => Systems.Count > 1;
    /// <summary>The one-line summary / status stands in for the key-facts box when there is no system to show.</summary>
    public bool ShowSummary => Systems.Count == 0;

    /// <summary>Raised by "Open full report": the view model opens the CSV in the Results grid.</summary>
    public event Action<string>? OpenCsvRequested;

    /// <summary>Shows the case's report; computes it in the background (as a job) when it is missing and the source is known.</summary>
    public async Task LoadAsync(string? caseDir, bool autoCompute = true)
    {
        try { await LoadCoreAsync(caseDir, autoCompute); }
        catch (Exception ex) { AppLog.Error("Could not load the computer details", ex); StatusText = "Could not load the computer details: " + ex.Message; }
    }

    private async Task LoadCoreAsync(string? caseDir, bool autoCompute)
    {
        CaseFolder = caseDir;
        if (string.IsNullOrEmpty(caseDir) || !Directory.Exists(caseDir)) { Show(null); StatusText = ""; return; }
        var report = await Task.Run(() => ComputerDetailsStore.TryLoad(caseDir));
        if (!string.Equals(caseDir, CaseFolder, StringComparison.OrdinalIgnoreCase)) return;
        Show(report);
        if (report != null || !autoCompute || IsBusy || !_autoTried.Add(caseDir)) return;
        var (sources, systems) = await Task.Run(() => FromManifests(caseDir));
        if (!string.Equals(caseDir, CaseFolder, StringComparison.OrdinalIgnoreCase)) return;
        if (sources.Count == 0 && systems.Count == 0)
        {
            StatusText = "Not computed: the evidence source is unknown or no longer available. Use Refresh to pick it.";
            return;
        }
        Start(sources, systems, caseDir);
    }

    private void Show(ComputerDetailsReport? report)
    {
        Report = report;
        Systems.Clear();
        if (report != null) foreach (var s in report.Systems) Systems.Add(new SystemDetailsItem(s));
        HasReport = report != null;
        OnPropertyChanged(nameof(HasSeveralSystems));
        OnPropertyChanged(nameof(ShowSummary));
        if (report == null) { SummaryText = "Not computed yet"; return; }
        SummaryText = report.Systems.Count == 0
            ? "No Windows system found in the source"
            : string.Join("   |   ", Systems.Select(s => s.DisplaySummary));
        StatusText = "";
    }

    [RelayCommand]
    private void Refresh()
    {
        var caseDir = CaseFolder;
        if (string.IsNullOrEmpty(caseDir)) { _showMessage("Open a case folder first.", InfoSeverity.Warning); return; }
        // The systems / folders the case's runs actually read (never a broader parent of several extracted archives).
        var (sources, systems) = FromManifests(caseDir);
        if (sources.Count == 0 && systems.Count == 0)
        {
            var source = Report?.Source is { Length: > 0 } rs && Directory.Exists(rs) ? rs
                : Dialogs.PickFolder("Select the evidence source (the folder that holds the Windows directory, or a KAPE collection)", null);
            if (source == null) return;
            sources = new[] { source };
        }
        Start(sources, systems, caseDir);
    }

    /// <summary>Recorded system roots (with their host folders) and source folders of the case's run manifests.</summary>
    private static (IReadOnlyList<string> Sources, IReadOnlyList<DiscoveredSystem> Systems) FromManifests(string caseDir) =>
        (ComputerDetailsStore.FindSourcesFromManifests(caseDir), ComputerDetailsJob.SystemsFromManifests(caseDir));

    private void Start(IReadOnlyList<string> sources, IReadOnlyList<DiscoveredSystem> systems, string caseDir)
    {
        try
        {
            var job = ComputerDetailsJob.Enqueue(S, sources, caseDir, systems.Count > 0 ? systems : null).LastOrDefault();
            if (job == null) { StatusText = "Could not start the computer details step (EZ Forensics executable not found)."; return; }
            _job = job;
            IsBusy = true;
            var what = systems.Count > 0 ? systems.Select(x => x.RootPath).ToList() : sources;
            StatusText = "Reading the registry hives of " + (what.Count == 1 ? what[0] : $"{what.Count} systems") + "…";
        }
        catch (Exception ex) { _showError("Could not start the computer details step", ex); }
    }

    /// <summary>Called by the Analyze page when a job of the open case finishes.</summary>
    public void OnJobFinished(Job job)
    {
        if (!ComputerDetailsJob.Is(job)) return;
        if (job == _job) { IsBusy = false; _job = null; }
        if (!string.Equals(Path.GetFullPath(job.CaseDir).TrimEnd('\\'), Path.GetFullPath(CaseFolder ?? ".").TrimEnd('\\'), StringComparison.OrdinalIgnoreCase)) return;
        _ = LoadAsync(CaseFolder, autoCompute: false).ContinueWith(_ =>
        {
            if (job.State != JobState.Succeeded)
                AppServices.OnUi(() => StatusText = "Computer details step: " + (job.StatusReason ?? job.State.ToString()) + " (see the Jobs list on Ingest for the log).");
        });
    }

    [RelayCommand]
    private void OpenReport()
    {
        if (CaseFolder == null) return;
        var csv = ComputerDetailsStore.CsvPathFor(CaseFolder);
        if (File.Exists(csv)) OpenCsvRequested?.Invoke(csv);
        else _showMessage("No computer details report yet. Use Refresh to compute it.", InfoSeverity.Warning);
    }

    [RelayCommand]
    private void CopyValue(DetailField? f) { if (f != null) Copy(f.Value); }

    [RelayCommand]
    private void CopyRow(DetailField? f) { if (f != null) Copy($"{f.Field}\t{f.Value}\t{f.Source}"); }

    [RelayCommand]
    private void CopyAll()
    {
        if (Report == null) return;
        var sb = new StringBuilder();
        foreach (var s in Report.Systems)
        {
            sb.AppendLine($"{s.Title} ({s.SystemRoot})");
            sb.AppendLine(s.Summary);
            foreach (var g in s.Fields.GroupBy(f => f.Category))
            {
                sb.AppendLine();
                sb.AppendLine(g.Key);
                foreach (var f in g) sb.AppendLine($"  {f.Field}\t{f.Value}\t[{f.Source}]");
            }
            foreach (var n in s.Notes) sb.AppendLine("Note: " + n);
            sb.AppendLine();
        }
        Copy(sb.ToString());
    }

    private void Copy(string text)
    {
        try { Clipboard.SetDataObject(text, true); }
        catch (Exception ex) { _showError("Could not copy", ex); }
    }
}
