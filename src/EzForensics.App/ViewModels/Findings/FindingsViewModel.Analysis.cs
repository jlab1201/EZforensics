using CommunityToolkit.Mvvm.ComponentModel;
using EzForensics.App.Services;
using EzForensics.Core.Analysis;
using EzForensics.Core.Spectre;

namespace EzForensics.App.ViewModels.Findings;

/// <summary>
/// The page's data source: the case analysis store (<c>.ezforensics\analysis.json</c>). The page loads from it whenever the case has a folder;
/// Spectre's results are imported into it (after a run, and on open when the engine has newer results). Every change re-reads the store from disk,
/// applies the edit and saves, so edits made in Analyze are never overwritten (see <see cref="Mutate"/>).
/// </summary>
public sealed partial class FindingsViewModel
{
    private CaseAnalysisStore? _store;
    private AdaptedCase _adapted = new();
    private Dictionary<string, EnginePlain> _enginePlain = new();
    private IReadOnlyList<FlagCsv> _flags = Array.Empty<FlagCsv>();
    private bool _engineStale;
    private string _engineStaleReason = "";

    /// <summary>Changes a sync would make ("3 changes since last added"); the badge on Add my analysis.</summary>
    [ObservableProperty, NotifyPropertyChangedFor(nameof(SyncBadgeText), nameof(HasSyncBadge))] private int _syncChangeCount;
    /// <summary>The workspace or a flag sidecar has content, so "Use my analysis" is enabled.</summary>
    [ObservableProperty] private bool _canUseMyAnalysis;

    public bool HasSyncBadge => SyncChangeCount > 0;
    public string SyncBadgeText => SyncChangeCount.ToString(System.Globalization.CultureInfo.InvariantCulture);
    public string SyncBadgeTip => SyncChangeCount == 1 ? "1 change since last added" : $"{SyncChangeCount} changes since last added";

    /// <summary>The loaded case analysis (null before a case is open).</summary>
    internal CaseAnalysis? Analysis => _store?.Data;
    internal AdaptedCase Adapted => _adapted;

    /// <summary>No items yet: the page shows its two-card empty state.</summary>
    public bool ShowEmptyState => HasCase && !HasAnalysis && !IsLoadingFindings;

    partial void OnSyncChangeCountChanged(int value) => OnPropertyChanged(nameof(SyncBadgeTip));

    // ------------------------------------------------------------ load

    private void OpenStore(string dir)
    {
        try
        {
            CurrentIdentity.Provider = () => Examiner;
            _store = CaseAnalysisStore.Open(dir);
            if (_store.LoadWarning != null) ShowMessage(_store.LoadWarning, InfoSeverity.Warning);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            AppLog.Warn("Could not open the case analysis: " + ex.Message);
            _store = null;
        }
        RebuildFromStore();
    }

    /// <summary>Rebuilds the rows, the timeline and the moment cards from the store, keeping the selection.</summary>
    internal void RebuildFromStore()
    {
        var a = _store?.Data ?? new CaseAnalysis();
        _adapted = AnalysisAdapter.Build(a, _enginePlain);
        var keep = Selected?.Id;
        var keepMoment = SelectedMoment?.Id;
        var old = _all.GroupBy(r => r.Id).ToDictionary(g => g.Key, g => g.First());
        _all.Clear();
        foreach (var f in _adapted.Findings)
        {
            _adapted.Items.TryGetValue(f.Id, out var item);
            var chips = _adapted.Chips.GetValueOrDefault(f.Id);
            var active = _adapted.Active.Contains(f.Id);
            if (old.TryGetValue(f.Id, out var row)) row.Update(f, item, chips, active);   // same row object: selection, suggestion and open panels stay
            else row = new FindingRowViewModel(f, item, chips, active);
            _all.Add(row);
        }
        var tl = _adapted.Timeline with { AnalysisStale = _engineStale, StaleReason = _engineStale ? _engineStaleReason : "" };
        bool first = Timeline.Findings.Count == 0 && Timeline.Undated.Count == 0;
        TimelineError = "";
        RebuildMoments(() =>
        {
            Timeline = tl;
            var oldCards = Moments.ToDictionary(c => c.Id);
            Moments.Clear();
            var byId = StoryText.Index(tl);
            foreach (var m in tl.Moments)
            {
                var primary = StoryText.Primary(m, byId);
                var severity = _adapted.MomentSeverities.TryGetValue(m.Id, out var ks) ? ks : StoryText.Severity(m, byId);
                var techniques = StoryGraphics.MomentTechniques(m, byId);
                // a card that did not change stays the same object (its stamp, its read and the selection survive an edit elsewhere)
                if (oldCards.TryGetValue(m.Id, out var oc) && SameMoment(oc, m, primary, severity, techniques)) Moments.Add(oc);
                else Moments.Add(new MomentCardViewModel(m, primary, severity, techniques, byId));
            }
            foreach (var c in Moments) c.OriginText = _adapted.MomentKeyEvents.TryGetValue(c.Id, out var ck) ? OriginWord(AnalysisView.OriginOf(a, ck)) : "";
        });
        if (first || TimelineView == null) TimelineView = FindingsTimelineLayout.FitRange(tl);
        _syncingSelection = true;
        try { SelectedMoment = keepMoment != null ? Moments.FirstOrDefault(m => m.Id == keepMoment) : null; }
        finally { _syncingSelection = false; }
        MarkSelectedMoment();
        OnPropertyChanged(nameof(HasAnyFindings)); OnPropertyChanged(nameof(ShowEmptyState)); DuplicatesChanged();
        StatusChanged();
        ApplyFilter(keep);
        RebuildKeyEventRows(a);
    }

    private static bool SameMoment(MomentCardViewModel c, SpectreMoment m, SpectreTimelineFinding? primary, string severity, IReadOnlyList<string> techniques)
    {
        var o = c.Moment;
        return o.Number == m.Number && o.Time == m.Time && o.End == m.End && o.Headline == m.Headline && o.PlainTitle == m.PlainTitle && o.Why == m.Why && o.Reason == m.Reason
               && o.Phase == m.Phase && o.EvidenceLine == m.EvidenceLine && o.CantTell == m.CantTell && o.PrimaryFindingId == m.PrimaryFindingId
               && c.Severity == StoryText.SeverityWord(severity) && c.Techniques.SequenceEqual(techniques) && c.PrimaryFinding?.OccurrenceCount == primary?.OccurrenceCount;
    }

    /// <summary>Re-reads the store from disk (Analyze may have changed the workspace) and rebuilds when anything differs; then refreshes the badge.</summary>
    private async Task ReloadStoreAsync(string dir)
    {
        var fresh = await Task.Run(() => CaseAnalysisStore.Open(dir));
        if (!string.Equals(dir, _openedDir, StringComparison.OrdinalIgnoreCase)) return;
        if (_store == null || CaseAnalysisStore.ToJson(fresh.Data) != CaseAnalysisStore.ToJson(_store.Data)) { _store = fresh; RebuildFromStore(); }
        await RefreshSyncBadgeAsync(dir);
    }

    /// <summary>Applies one change to the store (re-read from disk first), saves it and rebuilds the page. False when nothing was saved.</summary>
    internal bool Mutate(Func<CaseAnalysis, bool> change)
    {
        if (CaseDir is not { } dir) return false;
        try
        {
            CurrentIdentity.Provider = () => Examiner;
            var store = CaseAnalysisStore.Open(dir);
            if (store.ReadOnlyReason != null) { ShowMessage(store.ReadOnlyReason, InfoSeverity.Warning); return false; }
            if (!change(store.Data)) return false;
            store.Save();
            _store = store;
            RebuildFromStore();
            return true;
        }
        catch (Exception ex)
        {
            AppLog.Error("Could not save the case analysis", ex);
            ShowError("Could not save your change", ex);
            return false;
        }
    }

    /// <summary>The engine's own moment id for a card (a key event imported from a Spectre moment); null for the rest.</summary>
    private string? EngineMomentOf(MomentCardViewModel card) =>
        _adapted.MomentKeyEvents.TryGetValue(card.Id, out var k) ? k.Source.SpectreMomentId : null;

    /// <summary>The key event behind a timeline card.</summary>
    internal KeyEvent? KeyEventOfCard(MomentCardViewModel? card) =>
        card != null && _adapted.MomentKeyEvents.TryGetValue(card.Id, out var k) ? k : null;

    internal static string OriginWord(DisplayOrigin o) => o switch { DisplayOrigin.Both => "Both", DisplayOrigin.Spectre => "Spectre", _ => "You" };

    // ------------------------------------------------------------ Spectre import

    /// <summary>
    /// Reads Spectre's findings and timeline and imports them into the store: after a run, and on open when the engine has newer results.
    /// Nothing is imported unless both engine calls succeed (an engine failure never marks items as gone).
    /// </summary>
    internal async Task<bool> ImportFromEngineAsync(string dir, bool force = false)
    {
        if (!HasEngineResults || !EngineAvailable || CaseId == null) return false;
        SpectreFindingList list;
        SpectreTimeline tl;
        try
        {
            list = await Client.ListAsync(CaseId, CancellationToken.None);
            tl = await Client.TimelineAsync(CaseId, CancellationToken.None);
        }
        catch (Exception ex) when (ex is SpectreToolException or IOException or TimeoutException)
        {
            FindingsError = "Could not read the findings. " + SpectreErrors.Friendly(ex);
            TimelineError = TimelineErrorText(ex);
            AppLog.Warn("Spectre import: " + ex.Message);
            return false;
        }
        if (!string.Equals(dir, _openedDir, StringComparison.OrdinalIgnoreCase)) return false;
        FindingsError = "";
        _enginePlain = AnalysisAdapter.PlainFrom(tl);
        _engineStale = tl.AnalysisStale; _engineStaleReason = tl.StaleReason;
        _unregistered = list.Unregistered;
        try
        {
            await LoadHostInfoAsync();
            CurrentIdentity.Provider = () => Examiner;
            var store = CaseAnalysisStore.Open(dir);
            var a = store.Data;
            bool newer = force || !a.Meta.EngineMigrated || a.Meta.LastImportedAt == null
                         || (_loadedLinkStamp is { } s && new DateTimeOffset(DateTime.SpecifyKind(s, DateTimeKind.Utc)) > a.Meta.LastImportedAt);
            if (newer && store.ReadOnlyReason == null)
            {
                SpectreImport.Import(a, list.Findings, tl, new ImportOptions { Hosts = _hostInfo });
                a.Meta.LastImportedAt = DateTimeOffset.UtcNow;
                store.Save();
            }
            _store = store;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            ShowError("Could not save the imported findings", ex);
        }
        RebuildFromStore();
        return true;
    }

    // ------------------------------------------------------------ Add my analysis (badge + sync)

    private SyncOptions SyncOptionsFor(IAnalysisRowReader? reader) => new()
    {
        RowReader = reader,
        HostForCasePath = p => _hostInfo is { } h ? h.HostForCasePath(p) ?? h.SingleSystem : null,
    };

    /// <summary>Reads the flag sidecars and counts what a sync would change (the badge) and whether "Use my analysis" has anything to use.</summary>
    internal async Task RefreshSyncBadgeAsync(string dir)
    {
        if (_store == null) return;
        try
        {
            var data = _store.Data;
            var opts = SyncOptionsFor(null);
            var (flags, preview) = await Task.Run(() =>
            {
                var f = AnalysisSync.ReadSidecars(dir);
                return (f, AnalysisSync.Preview(data, opts));
            });
            if (!string.Equals(dir, _openedDir, StringComparison.OrdinalIgnoreCase)) return;
            _flags = flags;
            SyncChangeCount = preview.Created + preview.Updated + preview.RemovedAtSource + preview.Restored + preview.Dismissed;
            CanUseMyAnalysis = AnalysisView.CanUseMyAnalysis(data);
        }
        catch (Exception ex) { AppLog.Warn("Could not check for changes in your analysis: " + ex.Message); }
    }
}
