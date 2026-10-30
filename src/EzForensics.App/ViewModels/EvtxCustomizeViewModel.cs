using System.Collections.ObjectModel;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using EzForensics.App.Services;
using EzForensics.Core.EventLogs;

namespace EzForensics.App.ViewModels;

public sealed partial class EvtxEventViewModel : ObservableObject
{
    private readonly EvtxGroupViewModel _group;
    [ObservableProperty] private bool _isVisible = true;

    public EvtxEventViewModel(EvtxGroupViewModel group, EvtxEventEntry entry) { _group = group; Entry = entry; }

    public EvtxEventEntry Entry { get; }
    public string Channel => Entry.Channel;
    public string EventId => Entry.EventId.ToString();
    public string Title => Entry.Title;
    public string Fields => Entry.FieldsText;
    public string CountText => Entry.EvidenceCount is { } c ? $"{c:N0} in evidence" : "";
    public bool Unverified => Entry.MapStatus.Equals("unverified", StringComparison.OrdinalIgnoreCase);

    public bool IsSelected
    {
        get => Entry.Selected;
        set { if (Entry.Selected == value) return; Entry.Selected = value; OnPropertyChanged(); _group.Refresh(); }
    }

    public void Raise() => OnPropertyChanged(nameof(IsSelected));

    public bool Matches(string q) =>
        q.Length == 0 || Entry.EventId.ToString().Contains(q) || Entry.Channel.Contains(q, StringComparison.OrdinalIgnoreCase)
        || Entry.Title.Contains(q, StringComparison.OrdinalIgnoreCase) || Entry.Provider.Contains(q, StringComparison.OrdinalIgnoreCase)
        || Entry.Fields.Any(f => f.Contains(q, StringComparison.OrdinalIgnoreCase));
}

public sealed partial class EvtxGroupViewModel : ObservableObject
{
    private readonly EvtxCustomizeViewModel _owner;
    [ObservableProperty] private bool _isVisible = true;
    [ObservableProperty] private bool _isExpanded = true;

    public EvtxGroupViewModel(EvtxCustomizeViewModel owner, EvtxEventGroup group)
    {
        _owner = owner;
        Group = group;
        Events = new ObservableCollection<EvtxEventViewModel>(group.Events.Select(e => new EvtxEventViewModel(this, e)));
    }

    public EvtxEventGroup Group { get; }
    public ObservableCollection<EvtxEventViewModel> Events { get; }
    public string Title => Group.Title;
    public string CountText => $"{Events.Count(e => e.IsSelected)} / {Events.Count}";

    /// <summary>Group checkbox: all, none, or mixed (null). Setting it ticks / unticks every event of the group.</summary>
    public bool? AllSelected
    {
        get
        {
            var n = Events.Count(e => e.IsSelected);
            return n == 0 ? false : n == Events.Count ? true : null;
        }
        set
        {
            var on = value == true;
            foreach (var e in Events) { e.Entry.Selected = on; e.Raise(); }
            Refresh();
        }
    }

    public void Refresh()
    {
        OnPropertyChanged(nameof(AllSelected));
        OnPropertyChanged(nameof(CountText));
        _owner.RaiseSummary();
    }

    public void SetAll(bool on) => AllSelected = on;

    public void ApplyFilter(string q)
    {
        foreach (var e in Events) e.IsVisible = e.Matches(q) || Title.Contains(q, StringComparison.OrdinalIgnoreCase);
        IsVisible = Events.Any(e => e.IsVisible);
        if (q.Length > 0 && IsVisible) IsExpanded = true;
    }
}

/// <summary>Customize event selection window: grouped, searchable checkbox tree + "Scan evidence…".</summary>
public sealed partial class EvtxCustomizeViewModel : ObservableObject
{
    private readonly AppServices _s;
    private readonly Dictionary<EvtxEventKey, bool> _snapshot;
    private CancellationTokenSource? _scanCts;

    [ObservableProperty] private string _search = "";
    [ObservableProperty] private bool _isScanning;
    [ObservableProperty] private string _scanStatus = "";

    public EvtxCustomizeViewModel(AppServices s)
    {
        _s = s;
        _snapshot = s.Evtx.AllEvents.ToDictionary(e => e.Key, e => e.Selected);
        Rebuild();
    }

    public ObservableCollection<EvtxGroupViewModel> Groups { get; } = new();
    public bool HasScanStatus => ScanStatus.Length > 0;
    partial void OnScanStatusChanged(string value) => OnPropertyChanged(nameof(HasScanStatus));
    public string Summary => EvtxArgs.Summary(_s.Evtx);

    public void RaiseSummary() => OnPropertyChanged(nameof(Summary));

    private void Rebuild()
    {
        var expanded = Groups.ToDictionary(g => g.Group.Id, g => g.IsExpanded);
        Groups.Clear();
        foreach (var g in _s.Evtx.Groups.Where(g => g.Events.Count > 0))
        {
            var vm = new EvtxGroupViewModel(this, g);
            if (expanded.TryGetValue(g.Id, out var x)) vm.IsExpanded = x;
            Groups.Add(vm);
        }
        ApplySearch();
        RaiseSummary();
    }

    partial void OnSearchChanged(string value) => ApplySearch();

    private void ApplySearch()
    {
        var q = Search.Trim();
        foreach (var g in Groups) g.ApplyFilter(q);
    }

    [RelayCommand] private void SelectAll() { foreach (var g in Groups) g.SetAll(true); }
    [RelayCommand] private void SelectNone() { foreach (var g in Groups) g.SetAll(false); }
    [RelayCommand] private void ResetRecommended() { _s.Evtx.Reset(); Rebuild(); }

    /// <summary>Window closed with Cancel: ticks go back to what they were on opening.</summary>
    public void Revert()
    {
        foreach (var e in _s.Evtx.AllEvents) if (_snapshot.TryGetValue(e.Key, out var on)) e.Selected = on;
    }

    public void Commit() => _s.SaveEvtx();

    [RelayCommand]
    private async Task ScanEvidenceAsync()
    {
        var dir = Dialogs.PickFolder("Select the folder with the .evtx files (searched recursively)", _s.LastCaseDir ?? _s.Settings.CaseRoot);
        if (string.IsNullOrEmpty(dir)) return;
        _scanCts?.Dispose();
        _scanCts = new CancellationTokenSource();
        var ct = _scanCts.Token;
        IsScanning = true;
        ScanStatus = "Scanning…";
        var progress = new Progress<EvtxScanProgress>(p => ScanStatus = p.FilesTotal == 0 ? "No .evtx files found." : $"Scanning {p.FilesDone}/{p.FilesTotal} {p.CurrentFile}");
        try
        {
            var result = await Task.Run(() => EvtxScanner.Scan(dir, new EventLogReaderSource(), progress, ct), ct);
            var added = _s.Evtx.AddEvidence(result.Events);
            Rebuild();
            var total = result.Events.Sum(e => (long)e.Count);
            ScanStatus = result.FilesScanned == 0
                ? "No .evtx files found in that folder."
                : $"Scanned {result.FilesScanned} file(s): {total:N0} events, {result.Events.Count} distinct; {added} not in the list" +
                  (result.Errors.Count > 0 ? $"; {result.Errors.Count} file(s) could not be read ({result.Errors[0]})" : ".");
        }
        catch (OperationCanceledException) { ScanStatus = "Scan cancelled."; }
        catch (Exception ex) { ScanStatus = "Scan failed: " + ex.Message; AppLog.Error("Event log scan failed", ex); }
        finally { IsScanning = false; }
    }

    [RelayCommand]
    private void CancelScan() => _scanCts?.Cancel();
}
