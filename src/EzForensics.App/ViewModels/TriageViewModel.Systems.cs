using System.Collections.ObjectModel;
using System.ComponentModel;
using CommunityToolkit.Mvvm.ComponentModel;
using EzForensics.App.Services;
using EzForensics.Core.Network;
using EzForensics.Core.Platforms;
using EzForensics.Core.Triage;

namespace EzForensics.App.ViewModels;

/// <summary>
/// One checkbox of the Disk card ("Detected artifacts" on the Ingest page): a discovered system (checked = planned and run). The host name
/// (the output host folder <c>&lt;case&gt;\&lt;Host&gt;</c>) is the discovered one, unique within the scan.
/// </summary>
public sealed partial class SystemChoiceItem : ObservableObject
{
    [ObservableProperty] private bool _isIncluded = true;
    /// <summary>The checkbox can change (the EZ tools are set up and no run is going on).</summary>
    [ObservableProperty] private bool _isEditable = true;
    [ObservableProperty] [NotifyPropertyChangedFor(nameof(HasError))] private string _hostName;
    [ObservableProperty] [NotifyPropertyChangedFor(nameof(HasError))] private string? _error;
    /// <summary>"Windows 10 Pro 22H2 (build 19045.3803)" read from the SOFTWARE hive after the scan (null until read, or unknown).</summary>
    [ObservableProperty] [NotifyPropertyChangedFor(nameof(DisplayText))] private string? _osVersion;
    [ObservableProperty] [NotifyPropertyChangedFor(nameof(DisplayText))] private bool _osVersionRead;

    public SystemChoiceItem(DiscoveredSystem system)
    {
        Original = system;
        _hostName = system.HostName;
    }

    public DiscoveredSystem Original { get; }
    public string RootPath => Original.RootPath;
    public bool HasError => !string.IsNullOrEmpty(Error);

    public string AcquisitionText => Original.AcquisitionKind switch
    {
        AcquisitionKind.LiveVolume => "Live volume",
        AcquisitionKind.MountedImageOrRawCopy => "Mounted image or raw copy",
        AcquisitionKind.ExtractedArchive => "Extracted archive",
        _ => "Copied collection",
    };

    public string OsText => Original.OsFamily == OsFamily.Unknown ? "OS unknown" : Original.OsFamily.ToString();

    /// <summary>Card line "Type: Folder" (Mounted drive, Live drive, Archive).</summary>
    public string TypeText => "Type: " + ArtifactDetails.EvidenceType(Original);

    /// <summary>Artifact groups planned for this system ($MFT, Registry, Event logs...), in plan order; set with the plan.</summary>
    [ObservableProperty] [NotifyPropertyChangedFor(nameof(ArtifactsText), nameof(ArtifactsToolTip))]
    private IReadOnlyList<string> _artifactGroups = Array.Empty<string>();

    /// <summary>Card line "Artifacts: $MFT, $UsnJrnl, Registry, Event logs, Prefetch, Amcache +2 more".</summary>
    public string ArtifactsText => "Artifacts: " + ArtifactDetails.ShortList(ArtifactGroups);
    public string ArtifactsToolTip => ArtifactGroups.Count == 0 ? "No artifact found for this system" : string.Join(", ", ArtifactGroups);

    /// <summary>"WKS-01, Windows 10 Pro 22H2 (build 19045.3803)" (the OS family until the version is read).</summary>
    public string DisplayText => $"{HostName}, {OsVersion ?? (OsVersionRead ? OsText : OsText + " …")}";

    /// <summary>Card tooltip: where the system is, how it was acquired and where the host name comes from.</summary>
    public string ToolTip => $"{RootPath}{Environment.NewLine}{AcquisitionText}  ·  host name from {Original.HostNameSource}";

    partial void OnHostNameChanged(string value) => OnPropertyChanged(nameof(DisplayText));

    /// <summary>The system with the (trimmed) label as host name; the discovered name while the label is invalid.</summary>
    public SystemChoice Choice => new(Original, IsIncluded, HasError ? Original.HostName : HostName);
}

/// <summary>One checkbox of the Network card: a packet capture found by the scan (checked = its Zeek / RITA jobs are planned).</summary>
public sealed partial class PcapChoiceItem : ObservableObject
{
    [ObservableProperty] private bool _isIncluded = true;
    /// <summary>The checkbox can change (Zeek is set up and no run is going on).</summary>
    [ObservableProperty] private bool _isEditable = true;

    public PcapChoiceItem(PcapFile file) => File = file;

    public PcapFile File { get; }
    public string Path => File.Path;
    /// <summary>"traffic.pcapng, 1.2 GB, pcapng".</summary>
    public string DisplayText => $"{File.Name}, {NetworkTriage.FormatSize(File.Size)}, {File.FormatLabel}";
}

public sealed partial class TriageViewModel
{
    /// <summary>Every system found by the scan; unchecked ones are left out of the plan and the run.</summary>
    public ObservableCollection<SystemChoiceItem> Systems { get; } = new();
    /// <summary>Every packet capture found by the scan; unchecked ones are left out of the plan.</summary>
    public ObservableCollection<PcapChoiceItem> Pcaps { get; } = new();

    public bool HasSystems => Systems.Count > 0;
    public bool HasPcapFiles => Pcaps.Count > 0;
    public bool HasSystemErrors => Systems.Any(s => s.IsIncluded && s.HasError);
    /// <summary>First problem with a host label ("HOST1: Another system already uses this name."), shown on the Disk card; Run is disabled meanwhile.</summary>
    public string SystemErrorText => Systems.Where(s => s.IsIncluded && s.HasError).Select(s => $"{s.HostName}: {s.Error}").FirstOrDefault() ?? "";

    /// <summary>The capture search stopped early (Network card warning: pick a narrower folder), or empty.</summary>
    [ObservableProperty] private string _pcapTruncatedText = "";

    private string _summarySource = "";
    private bool _summaryIsFile;

    private void SetSystems(IReadOnlyList<DiscoveredSystem> systems)
    {
        foreach (var s in Systems) s.PropertyChanged -= OnSystemChanged;
        Systems.Clear();
        foreach (var s in systems)
        {
            var item = new SystemChoiceItem(s);
            item.PropertyChanged += OnSystemChanged;
            Systems.Add(item);
        }
        ValidateSystems();
        OnPropertyChanged(nameof(HasSystems));
        RefreshAvailability();
        if (Systems.Count > 0) _ = ReadOsVersionsAsync(Systems.ToList());
    }

    /// <summary>Reads each system's OS version from its SOFTWARE hive (off the UI thread; the card shows the OS family meanwhile).</summary>
    private static async Task ReadOsVersionsAsync(IReadOnlyList<SystemChoiceItem> items)
    {
        foreach (var item in items)
        {
            try { item.OsVersion = await Task.Run(() => HostDiscovery.ReadOsVersion(item.RootPath)); }
            catch (Exception ex) { AppLog.Error("Reading the OS version failed for " + item.RootPath, ex); }
            finally { item.OsVersionRead = true; }
        }
    }

    private void SetPcaps(PcapScanResult pcaps)
    {
        foreach (var p in Pcaps) p.PropertyChanged -= OnPcapChanged;
        Pcaps.Clear();
        foreach (var f in pcaps.Files)
        {
            var item = new PcapChoiceItem(f);
            item.PropertyChanged += OnPcapChanged;
            Pcaps.Add(item);
        }
        PcapTruncatedText = pcaps.Truncated ? $"Packet capture search {pcaps.TruncatedReason}; pick a narrower folder to search everything." : "";
        OnPropertyChanged(nameof(HasPcapFiles));
        RefreshAvailability();
    }

    private void OnPcapChanged(object? sender, PropertyChangedEventArgs e)
    {
        if (e.PropertyName == nameof(PcapChoiceItem.IsIncluded)) ApplyExclusions();
    }

    private void OnSystemChanged(object? sender, PropertyChangedEventArgs e)
    {
        if (e.PropertyName == nameof(SystemChoiceItem.IsIncluded))
        {
            // a card tick only excludes (or restores) the system's rows: no re-plan, so nothing else changes
            ValidateSystems();
            RaiseMemoryIssue();
            ApplyExclusions();
        }
        else if (e.PropertyName == nameof(SystemChoiceItem.HostName)) ApplySystemChange();
    }

    /// <summary>Re-validates the labels and, when they are valid, re-plans with the new host folders (ticks of kept jobs survive).</summary>
    private void ApplySystemChange()
    {
        ValidateSystems();
        RaiseMemoryIssue();
        if (!HasSystemErrors) ReplanKeepingTicks();
        else UpdateSelectionSummary();
    }

    private void ValidateSystems()
    {
        foreach (var s in Systems)
            s.Error = SystemSelection.ValidateHostLabel(s.HostName, Systems.Where(o => !ReferenceEquals(o, s)).Select(o => o.HostName));
        OnPropertyChanged(nameof(HasSystemErrors));
        OnPropertyChanged(nameof(SystemErrorText));
        RefreshRunGate();
    }

    private List<SystemChoice> Choices() => Systems.Select(s => s.Choice).ToList();

    /// <summary>Systems the plan is built from: checked ones, with the edited host labels (the scan's systems when the list is empty).</summary>
    private IReadOnlyList<DiscoveredSystem> ActiveSystems() => Systems.Count == 0 ? _systems : SystemSelection.Active(Choices());

    /// <summary>All systems with their edited labels (attribution of captures that sit under a system root).</summary>
    private IReadOnlyList<DiscoveredSystem> LabelledSystems() => Systems.Count == 0 ? _systems : SystemSelection.Labelled(Choices());

    /// <summary>Every detected artifact (checked system or not), each carrying its system with the edited host label.</summary>
    private List<DetectedArtifact> AllFound() => _found == null ? new List<DetectedArtifact>() : SystemSelection.Label(_found, Choices());

    /// <summary>Checked packet captures (the Network card).</summary>
    private int IncludedPcapCount() => _pcaps == null ? 0 : Pcaps.Count(p => p.IsIncluded);

    /// <summary>The wanted tick of every row (an excluded row: the tick it gets back), by row key.</summary>
    private Dictionary<string, bool> WantedTicks()
    {
        var ticks = new Dictionary<string, bool>();
        foreach (var p in Planned) ticks.TryAdd(p.Key, p.WantedSelected);
        // rows planned after a restored session (memory plugins, persistence) get their saved tick
        if (_restoredTicks != null) foreach (var (key, on) in _restoredTicks) ticks.TryAdd(key, on);
        return ticks;
    }

    private void ReplanKeepingTicks()
    {
        var caseDir = CaseFolder.Trim();
        if (caseDir.Length == 0 || (_found == null && _pcaps == null) || RefuseWhileRunning("re-plan")) return;
        var ticks = WantedTicks();
        SetPlan(BuildPlan(caseDir).Select(p => (p, ticks.TryGetValue(PlannedJobItem.KeyOf(p), out var on) ? on : p.SelectedByDefault)).ToList());
    }
}
