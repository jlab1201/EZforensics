using EzForensics.App.Services;
using CommunityToolkit.Mvvm.Input;
using EzForensics.Core.Analysis;
using EzForensics.Core.Spectre;

namespace EzForensics.App.ViewModels.Findings;

/// <summary>The evidence panel: "Show the evidence" opens the rows behind a finding in a panel on this page (Open in Analyze is a link in it).</summary>
public sealed partial class FindingsViewModel : IEvidencePanelHost
{
    private EvidencePanelViewModel? _evidencePanel;
    public EvidencePanelViewModel EvidencePanel => _evidencePanel ??= new EvidencePanelViewModel(this);
    private EvidencePanelViewModel? _detailEvidence;
    /// <summary>Details → Evidence: the same evidence view, inside the tab, on the selected finding.</summary>
    public EvidencePanelViewModel DetailEvidence => _detailEvidence ??= new EvidencePanelViewModel(this) { CanClose = false };

    bool IEvidencePanelHost.CanFlag => Analysis != null && CaseDir != null;
    string? IEvidencePanelHost.CaseFolder => CaseDir;
    string IEvidencePanelHost.ExaminerName => Examiner;

    string? IEvidencePanelHost.SystemOf(string casePath) =>
        _hostInfo is { } info ? info.HostForCasePath(casePath) ?? info.SingleSystem : null;

    IReadOnlyList<SpectreEvidenceFlag> IEvidencePanelHost.EvidenceFlagsOf(IReadOnlyCollection<string> findingIds)
    {
        var list = new List<SpectreEvidenceFlag>();
        foreach (var id in findingIds)
            if (FindRow(id) is { } r) list.AddRange(r.Finding.AnalystFlags);
        return list;
    }

    (bool Ok, string? Reason, IReadOnlyList<SpectreEvidenceFlag> Flags) IEvidencePanelHost.SetRowLabels(IReadOnlyList<RowTarget> rows, string label, string? note)
    {
        var flags = new List<SpectreEvidenceFlag>();
        var resolved = rows.Select(t => (Target: t, Row: FindRow(t.FindingId))).ToList();
        if (resolved.Any(x => x.Row?.Item == null)) return (false, "the finding is not in the case analysis", flags);
        var now = DateTimeOffset.UtcNow;
        bool saved = Mutate(a =>
        {
            foreach (var (t, row) in resolved)
            {
                if (a.Find(row!.Item!.Id) is not { } i) continue;
                var ar = new AnalysisRow(t.CasePath, t.Row);
                var per = i.RowEdits.PerRow.FirstOrDefault(p => p.Key == ar.Key);
                switch (label)
                {
                    case EvidenceFlagLabels.NotRelevant: AnalysisEdits.RemoveRow(i, ar, now); break;
                    case EvidenceFlagLabels.Added: AnalysisEdits.AddRow(i, ar, now); break;
                    case EvidenceFlagLabels.Confirmed: AnalysisEdits.RestoreRow(i, ar, now); AnalysisEdits.SetRowNote(i, ar, true, per?.Note, now); break;
                    case EvidenceFlagLabels.Note: AnalysisEdits.SetRowNote(i, ar, per?.Confirmed, note, now); break;
                    case EvidenceFlagLabels.Clear:
                        AnalysisEdits.RestoreRow(i, ar, now);
                        if (per?.Confirmed != null) AnalysisEdits.SetRowNote(i, ar, null, per.Note, now);
                        if (i.RowEdits.Added.RemoveAll(r => r.Key == ar.Key) > 0) i.Log($"cleared added row {ar.CsvRelPath}#{ar.Row}", now);
                        break;
                }
                flags.Add(new SpectreEvidenceFlag(t.FindingId, t.CasePath, t.Row, label, note, Examiner, now.ToString("o")));
            }
            return true;
        });
        return saved ? (true, null, flags) : (false, "the case analysis could not be saved", flags);
    }

    async Task<(bool Ok, string? Reason, string? Id)> IEvidencePanelHost.CreateKeyEventAsync(string title, string severity, string casePath, IReadOnlyList<long> rows, string? note)
    {
        if (CaseDir is not { } dir) return (false, "no case is open", null);
        await LoadHostInfoAsync();
        var hostOf = SyncOptionsFor(null).HostForCasePath;
        var res = await MutateAsync(a =>
        {
            using var reader = new CsvAnalysisRowReader(dir);
            var key = a.AddFinding(title, severity, null, note, rows.Select(r => new AnalysisRow(casePath, r)));
            var flags = AnalysisSync.ReadSidecars(dir);
            AnalysisSync.Sync(a, new SyncOptions { RowReader = reader, HostForCasePath = hostOf });
            return a.Items.FirstOrDefault(i => i.Source.WorkspaceId == key.Id)?.Id;
        });
        return res == null ? (false, "the key event could not be added", null) : (true, null, res);
    }

    Task IEvidencePanelHost.FindingFlagsChangedAsync(IReadOnlyCollection<string> findingIds) => Task.CompletedTask;   // Mutate already rebuilt the page

    Task IEvidencePanelHost.FindingCreatedAsync(string findingId)
    {
        SelectedFindingId = findingId;
        return Task.CompletedTask;
    }

    void IEvidencePanelHost.OpenEvidenceInAnalyze(string caseDir, IReadOnlyList<EvidenceTarget> plan, string title) => OpenEvidence(caseDir, plan, title);

    /// <summary>The findings to show with their evidence: each one's engine finding (else its timeline row), the primary one first.</summary>
    private IReadOnlyList<(string FindingId, EvidenceSource Source)> PanelFindings(IEnumerable<string> ids, FindingRowViewModel? primary)
    {
        var list = new List<(string, EvidenceSource)>();
        foreach (var id in ids)
        {
            if (FindRow(id) is { } r) list.Add((r.Finding.FindingId, EvidenceSource.From(r.Finding)));
            else if (Timeline.Findings.Concat(Timeline.Undated).FirstOrDefault(f => f.FindingId == id || f.Id == id) is { } tf) list.Add((tf.FindingId, EvidenceSource.From(tf)));
        }
        if (list.Count == 0 && primary != null) list.Add((primary.Finding.FindingId, EvidenceSource.From(primary.Finding)));
        return list;
    }

    /// <summary>Opens the panel on <paramref name="row"/> (and the other findings of a moment); false when there is no evidence file to show.</summary>
    internal async Task<bool> OpenEvidencePanelAsync(FindingRowViewModel row, MomentCardViewModel? card, string title)
    {
        var ids = card?.Moment.FindingIds is { Count: > 0 } m ? m : new[] { row.Finding.FindingId };
        return await EvidencePanel.OpenAsync(title, row.Finding.FindingId, PanelFindings(ids, row));
    }

    /// <summary>Details list: Show the evidence selects the finding and opens its Evidence tab.</summary>
    [RelayCommand]
    private void ShowFindingEvidence(FindingRowViewModel? row)
    {
        row ??= Selected;
        if (row == null) return;
        if (!ReferenceEquals(Selected, row)) Selected = row;
        SelectedTab = FindingsTab.Details;
        SelectedDetailTab = TabEvidence;
    }

    /// <summary>For the chat (findings_show_evidence): opens the panel for a finding id, or the selected one. False when it cannot.</summary>
    internal async Task<string?> ShowEvidenceForChatAsync(string? findingId)
    {
        var row = string.IsNullOrWhiteSpace(findingId) ? Selected : FindRow(findingId.Trim());
        if (row == null) return string.IsNullOrWhiteSpace(findingId) ? "No finding is selected. Give finding_id." : $"No finding {findingId} in the loaded list. Use findings_list_view for the ids.";
        if (!ReferenceEquals(Selected, row)) SelectFindingCore(row.Id);
        var card = Moments.FirstOrDefault(m => m.PrimaryFindingId != null && IdsMatch(m.PrimaryFindingId, row.Id));
        var title = card?.PlainTitle is { Length: > 0 } t ? t : row.Title;
        return await OpenEvidencePanelAsync(row, card, title) ? null : $"Finding {row.Id} names no evidence file that can be opened.";
    }
}
