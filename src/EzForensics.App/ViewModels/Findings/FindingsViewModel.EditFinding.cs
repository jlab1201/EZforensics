using EzForensics.Core.Analysis;
using EzForensics.Core.Results;

namespace EzForensics.App.ViewModels.Findings;

/// <summary>"Edit finding…" on the list: opens the same editor as Analyze for a finding the analyst made there (it has a workspace finding behind it).</summary>
public sealed partial class FindingsViewModel
{
    private WorkspaceFinding? WorkspaceFindingOf(FindingRowViewModel? row) =>
        row?.Item?.Source.WorkspaceId is { } id ? Analysis?.Workspace.Findings.FirstOrDefault(w => w.Id == id) : null;

    /// <summary>The row is a finding made in Analyze, so the editor can change it.</summary>
    public bool CanEditWorkspaceFinding(FindingRowViewModel? row) => WorkspaceFindingOf(row) != null;

    /// <summary>The editor's starting values (notes split into evidence and notes); null when the row is not an Analyze finding.</summary>
    public KeyEventDraft? WorkspaceDraftOf(FindingRowViewModel? row)
    {
        if (WorkspaceFindingOf(row) is not { } w) return null;
        var (evidence, notes) = FindingNotes.Split(w.Notes);
        var rows = w.Rows.GroupBy(r => r.Key.Csv)
            .Select(g => new FindingFileRows(g.First().CsvRelPath, g.Select(r => r.Row).Distinct().OrderBy(r => r).ToList()))
            .OrderBy(f => f.CsvRelPath, StringComparer.OrdinalIgnoreCase).ToList();
        return new KeyEventDraft(w.Title, w.Severity, w.Mitre.ToList(), notes, evidence, Rows: rows);
    }

    /// <summary>Saves the editor's result into the workspace finding and refreshes the page.</summary>
    public bool SaveWorkspaceDraft(FindingRowViewModel? row, KeyEventDraft draft)
    {
        var id = row?.Item?.Source.WorkspaceId;
        if (id == null) return false;
        var added = new List<(string Rel, List<long> Rows)>();
        var ok = Mutate(a =>
        {
            var w = a.Workspace.Findings.FirstOrDefault(e => e.Id == id);
            if (w == null) return false;
            if (!string.IsNullOrWhiteSpace(draft.Title)) w.Title = draft.Title.Trim();
            w.Severity = draft.Severity; w.Mitre = draft.Mitre.ToList(); w.Notes = FindingNotes.Compose(draft.Evidence, draft.Notes);
            w.UpdatedAt = DateTimeOffset.UtcNow;
            foreach (var f in draft.Rows ?? Array.Empty<FindingFileRows>())
                added.Add((f.CsvRelPath, a.SetFindingFileRows(id, f.CsvRelPath, f.Rows)));
            AnalysisSync.Sync(a, SyncOptionsFor(null));
            return true;
        });
        // rows the finding gained are flagged, as "Add to finding" does in Analyze
        if (ok) FlagAddedRows(added);
        return ok;
    }
}
