using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using EzForensics.Core.Spectre;

namespace EzForensics.App.ViewModels.Findings;

/// <summary>The Overview's page-level computer selector, the Severity and Activity-by-tactic tiles.</summary>
public sealed partial class FindingsViewModel
{
    public const string AllComputers = "All computers";
    public const string AnyChoice = "Any";

    // ------------------------------------------------------------ computer selector (header, all tabs)

    /// <summary>"All computers" followed by each computer of the case.</summary>
    public IReadOnlyList<string> HostChoices => new[] { AllComputers }.Concat(_hostTiles.Select(t => t.Host)).ToList();
    /// <summary>Several computers: the header shows a box to choose one.</summary>
    public bool ShowHostPicker => HasAnalysis && _hostTiles.Count > 1;
    /// <summary>One computer: the header shows its name as a plain label.</summary>
    public bool ShowHostLabel => HasAnalysis && _hostTiles.Count == 1;
    public bool ShowHostBar => ShowHostPicker || ShowHostLabel;
    public string SingleHostName => _hostTiles.Count == 1 ? _hostTiles[0].Host : "";

    /// <summary>The selector's choice; setting "All computers" (or nothing) clears <see cref="HostFilter"/>.</summary>
    public string SelectedHostChoice
    {
        get => HostFilter ?? AllComputers;
        set { var host = string.IsNullOrWhiteSpace(value) || value == AllComputers ? null : value; if (!Same(host, HostFilter)) HostFilter = host; }
    }

    // ------------------------------------------------------------ the chosen computer scopes the page

    private string RowHost(FindingRowViewModel r) =>
        _findingsById.TryGetValue(r.Id, out var tf) || _findingsById.TryGetValue(r.Finding.FindingId, out tf) ? _storyHosts.ComputerOf(tf) : StoryGraphics.NoHost;

    internal bool RowInHost(FindingRowViewModel r) => HostFilter == null || Same(RowHost(r), HostFilter);

    private IEnumerable<FindingRowViewModel> ScopedRows => HostFilter == null ? _all : _all.Where(RowInHost);

    private IEnumerable<SpectreTimelineFinding> ScopedTimelineFindings =>
        Timeline.Findings.Concat(Timeline.Undated).Where(f => HostFilter == null || Same(_storyHosts.ComputerOf(f), HostFilter));

    partial void OnHostFilterChanged(string? value)
    {
        foreach (var t in _hostTiles) t.IsActive = value != null && Same(t.Host, value);
        foreach (var n in new[] { nameof(SelectedHostChoice), nameof(StoryMoments), nameof(SeverityCounts), nameof(SeverityTotal),
                                  nameof(SeverityLegend), nameof(SeverityRingName), nameof(Radar), nameof(RadarUnmappedText), nameof(ReviewBreakdown),
                                  nameof(ReviewedCount), nameof(ReviewTotal), nameof(ReviewText), nameof(HostTimeline), nameof(AttackFlowNodes), nameof(ShowFlowHost) })
            OnPropertyChanged(n);
        if (_all.Count > 0) ApplyFilter();
        _walkthrough?.Refresh();
    }

    private SpectreTimeline? _hostTimeline;
    private SpectreTimeline? _hostTimelineOf;
    private string? _hostTimelineHost;
    private StoryHosts? _hostTimelineHosts;

    /// <summary>The timeline for the Timeline tab: the whole case, or only the chosen computer's key events and findings.</summary>
    public SpectreTimeline HostTimeline
    {
        get
        {
            if (ConfirmedOnly) return ConfirmedTimeline();
            if (HostFilter == null) return Timeline;
            if (_hostTimeline != null && ReferenceEquals(_hostTimelineOf, Timeline) && Same(_hostTimelineHost, HostFilter) && ReferenceEquals(_hostTimelineHosts, _storyHosts)) return _hostTimeline;
            var byId = StoryGraphics.ById(Timeline.Findings.Concat(Timeline.Undated));
            bool In(SpectreTimelineFinding f) => Same(_storyHosts.ComputerOf(f), HostFilter);
            _hostTimeline = Timeline with
            {
                Findings = Timeline.Findings.Where(In).ToList(),
                Undated = Timeline.Undated.Where(In).ToList(),
                Moments = Timeline.Moments.Where(m => Same(_storyHosts.ComputerOf(m, byId), HostFilter)).ToList(),
            };
            _hostTimelineOf = Timeline; _hostTimelineHost = HostFilter; _hostTimelineHosts = _storyHosts;
            return _hostTimeline;
        }
    }

    /// <summary>Confirmed only: the host-scoped timeline with just the key events whose finding is approved (not cached; approvals change).</summary>
    private SpectreTimeline ConfirmedTimeline()
    {
        var ids = AllStoryMoments.Select(c => c.Id).ToHashSet();
        var byId = StoryGraphics.ById(Timeline.Findings.Concat(Timeline.Undated));
        bool In(SpectreTimelineFinding f) => HostFilter == null || Same(_storyHosts.ComputerOf(f), HostFilter);
        return Timeline with
        {
            Findings = Timeline.Findings.Where(In).ToList(),
            Undated = Timeline.Undated.Where(In).ToList(),
            Moments = Timeline.Moments.Where(m => ids.Contains(m.Id)).ToList(),
        };
    }

    // ------------------------------------------------------------ Activity by tactic (radar)

    /// <summary>Findings per tactic for the chosen computer, on the 12 fixed axes.</summary>
    public TacticRadarData Radar => StoryGraphics.Radar(Timeline.Findings.Concat(Timeline.Undated), HostFilter, _storyHosts);
    public string RadarUnmappedText => Radar.Unmapped > 0 ? $"{Radar.Unmapped} unmapped" : "";
}
