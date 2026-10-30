using CommunityToolkit.Mvvm.Input;
using EzForensics.App.Services;
using EzForensics.Core.Triage;

namespace EzForensics.App.ViewModels;

/// <summary>
/// Ingest: tools that are not installed or have an issue. The planned rows that need them are greyed out and unticked (reason as tooltip),
/// and so are the Detected artifacts cards (Disk: EZ tools, Memory: Volatility, Network: Zeek; RITA alone is only noted). The rules live in
/// Core (<see cref="ToolAvailability"/>); this wires them to the page and to the detail window.
/// </summary>
public sealed partial class TriageViewModel
{
    private CardState _diskCard = CardState.Ready;
    private CardState _memoryCard = CardState.Ready;
    private CardState _networkCard = CardState.Ready;

    private void InitAvailability()
    {
        S.PlasoChanged += RefreshAvailability;
        S.ToolsRescanned += RefreshAvailability;
    }

    /// <summary>Tools known to be missing now (a tool still being checked counts as present).</summary>
    private ToolStatus CurrentToolStatus() => new(
        ZeekMissing: S.Zeek != null && !S.IsProbingZeek && S.Zeek.Selected == null,
        RitaMissing: RitaEnabled && S.Rita != null && !RitaProbing && !RitaReady,
        VolatilityMissing: S.VolatilityProbed && !S.IsProbingVolatility && S.Volatility == null,
        PlasoMissing: S.Plaso != null && !S.IsProbingPlaso && S.Plaso.Selected == null);

    private string? RowReason(PlannedJob p, ToolStatus status) => ToolAvailability.RowReason(p, status, S.Locator, S.Queue.PreferredFlavor);

    /// <summary>Re-evaluates the planned rows (rows of a run in progress keep their state) and the three cards.</summary>
    private void RefreshAvailability()
    {
        var status = CurrentToolStatus();
        // during a run no tick may change (a tool found again would tick its rows): re-evaluated when the run has finished
        if (!IsRunLocked)
            foreach (var item in Planned)
                if (!item.HasJob) item.UnavailableReason = RowReason(item.Planned, status);
        var ez = Planned.Where(p => ToolAvailability.IsEzRow(p.Planned)).ToList();
        _diskCard = ToolAvailability.Card(DetectedCardKind.Disk, status, Systems.Count > 0, ez.Count > 0 && ez.All(p => !p.IsAvailable));
        _memoryCard = ToolAvailability.Card(DetectedCardKind.Memory, status, MemoryImages.Count > 0, false);
        _networkCard = ToolAvailability.Card(DetectedCardKind.Network, status, Pcaps.Count > 0, false);
        foreach (var n in new[]
        {
            nameof(DiskEnabled), nameof(DiskOpacity), nameof(DiskCardToolTip), nameof(DiskSetUpText), nameof(HasDiskSetUp),
            nameof(MemoryEnabled), nameof(MemoryOpacity), nameof(MemoryCardToolTip), nameof(MemorySetUpText), nameof(HasMemorySetUp),
            nameof(NetworkEnabled), nameof(NetworkOpacity), nameof(NetworkCardToolTip), nameof(NetworkNote), nameof(HasNetworkNote),
            nameof(NetworkSetUpText), nameof(HasNetworkSetUp), nameof(PagefileEditable),
        }) OnPropertyChanged(n);
        UpdateCardLocks();
    }

    /// <summary>The cards' checkboxes: disabled while the card's tool is missing or a run is going on.</summary>
    private void UpdateCardLocks()
    {
        bool disk = DiskEnabled && !IsRunLocked, memory = MemoryEnabled && !IsRunLocked, network = NetworkEnabled && !IsRunLocked;
        foreach (var s in Systems) s.IsEditable = disk;
        foreach (var m in MemoryImages) m.IsEditable = memory;
        foreach (var p in Pcaps) p.IsEditable = network;
    }

    /// <summary>"Include pagefile" (Memory card) can change: Volatility set up and no run going on.</summary>
    public bool PagefileEditable => MemoryEnabled && !IsRunLocked;

    public bool DiskEnabled => !_diskCard.Disabled;
    public double DiskOpacity => _diskCard.Disabled ? 0.5 : 1.0;
    public string DiskCardToolTip => _diskCard.Reason ?? "Click for details";
    public string? DiskSetUpText => _diskCard.SetUpText;
    public bool HasDiskSetUp => _diskCard.HasLink;

    public bool MemoryEnabled => !_memoryCard.Disabled;
    public double MemoryOpacity => _memoryCard.Disabled ? 0.5 : 1.0;
    public string MemoryCardToolTip => _memoryCard.Reason ?? "Click for details";
    public string? MemorySetUpText => _memoryCard.SetUpText;
    public bool HasMemorySetUp => _memoryCard.HasLink;

    public bool NetworkEnabled => !_networkCard.Disabled;
    public double NetworkOpacity => _networkCard.Disabled ? 0.5 : 1.0;
    public string NetworkCardToolTip => _networkCard.Reason ?? "Click for details";
    public string? NetworkNote => _networkCard.Note;
    public bool HasNetworkNote => _networkCard.Note != null;
    public string? NetworkSetUpText => _networkCard.SetUpText;
    public bool HasNetworkSetUp => _networkCard.HasLink;

    /// <summary>"Set up" on the Disk card: the setup wizard (downloads the EZ tools).</summary>
    [RelayCommand]
    private void SetUpDisk() => GoDownloadToolsCommand.Execute(null);

    /// <summary>"Set up Zeek" / "Set up RITA" on the Network card: that tool's Tools page entry.</summary>
    [RelayCommand]
    private void SetUpNetwork() => S.OpenToolsConfiguration(_networkCard.Target == SetUpTarget.Rita ? ToolsSection.Rita : ToolsSection.Zeek);

    // ------------------------------------------------------------------ detail window boxes (one per system / artifact)

    public IReadOnlyList<DetailBox> DiskDetails() => Systems.Select(s =>
    {
        long? size = s.Original.Archive is { } a && File.Exists(a.ArchivePath) ? new FileInfo(a.ArchivePath).Length : null;
        return ArtifactDetails.Disk(s.HostName, s.OsText, s.OsVersion, s.RootPath, ArtifactDetails.UsersFound(s.RootPath), size,
            ArtifactDetails.EvidenceType(s.Original), s.ArtifactGroups);
    }).ToList();

    public IReadOnlyList<DetailBox> MemoryDetails() => MemoryImages.Select(m =>
    {
        DateTime? modified = null;
        try { if (File.Exists(m.Path)) modified = File.GetLastWriteTimeUtc(m.Path); } catch (Exception) { /* unreadable: skip the key */ }
        return ArtifactDetails.Memory(m.Image, modified);
    }).ToList();

    public IReadOnlyList<DetailBox> NetworkDetails() => Pcaps.Select(p => ArtifactDetails.Network(p.File)).ToList();
}
