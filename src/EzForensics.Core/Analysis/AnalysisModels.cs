using System.Security.Cryptography;
using System.Text;
using System.Text.Json.Serialization;
using EzForensics.Core.Spectre;

namespace EzForensics.Core.Analysis;

public enum ItemOrigin { Analyst, Spectre }
public enum ReviewState { ToReview, Confirmed, Rejected }
public enum SourceStatus { Current, RemovedAtSource }
public enum DisplayOrigin { You, Spectre, Both }

/// <summary>A row of a case CSV: case-relative path (forward slashes), 0-based data row, the CSV fingerprint when it was taken, and whether the CSV has changed since.</summary>
public sealed class AnalysisRow
{
    public string CsvRelPath { get; set; } = "";
    public long Row { get; set; }
    /// <summary><c>size|lastWriteUtcTicks|headSha256</c> of the CSV when the row was recorded; null when unknown.</summary>
    public string? Fingerprint { get; set; }
    public bool Stale { get; set; }

    public AnalysisRow() { }
    public AnalysisRow(string csvRelPath, long row, string? fingerprint = null) { CsvRelPath = Norm(csvRelPath); Row = row; Fingerprint = fingerprint; }

    public static string Norm(string? path) => (path ?? "").Trim().Replace('\\', '/');

    [JsonIgnore] public RowKey Key => new(CsvRelPath, Row);

    public AnalysisRow Clone() => new() { CsvRelPath = CsvRelPath, Row = Row, Fingerprint = Fingerprint, Stale = Stale };

    public static string FingerprintOf(Results.CsvFingerprint fp) => $"{fp.Size}|{fp.LastWriteUtc.Ticks}|{fp.HeadSha256}";
}

/// <summary>Identity of a row for set maths: path (case-insensitive) and row.</summary>
public readonly record struct RowKey
{
    public string Csv { get; }
    public long Row { get; }
    public RowKey(string csv, long row) { Csv = AnalysisRow.Norm(csv).ToLowerInvariant(); Row = row; }
    public override string ToString() => $"{Csv}#{Row}";
}

/// <summary>The analyst's finding as built in Analyze (a draft; never shown on the Report page directly).</summary>
public sealed class WorkspaceFinding
{
    public string Id { get; set; } = "";
    public string Title { get; set; } = "";
    public string Severity { get; set; } = "Medium";
    public List<string> Mitre { get; set; } = new();
    public string Notes { get; set; } = "";
    public List<AnalysisRow> Rows { get; set; } = new();
    public DateTimeOffset UpdatedAt { get; set; }
}

public sealed class WorkspaceSection
{
    public List<WorkspaceFinding> Findings { get; set; } = new();
}

public sealed class RowNote
{
    public string CsvRelPath { get; set; } = "";
    public long Row { get; set; }
    public bool? Confirmed { get; set; }
    public string? Note { get; set; }
    [JsonIgnore] public RowKey Key => new(CsvRelPath, Row);
}

/// <summary>Row changes made on the Report page. They survive every sync and import, which only replace <see cref="AnalysisItem.SourceRows"/>.</summary>
public sealed class RowEdits
{
    public List<AnalysisRow> Added { get; set; } = new();
    public List<AnalysisRow> Removed { get; set; } = new();
    public List<RowNote> PerRow { get; set; } = new();
    [JsonIgnore] public bool IsEmpty => Added.Count == 0 && Removed.Count == 0 && PerRow.Count == 0;
}

public sealed class ItemSource
{
    public string? WorkspaceId { get; set; }
    public string? SpectreFindingId { get; set; }
}

public sealed class DupDecision
{
    public string OtherId { get; set; } = "";
    /// <summary>Always <c>keep_both</c> for now.</summary>
    public string Decision { get; set; } = "keep_both";
    public string RowSetHash { get; set; } = "";
}

public sealed class HistoryEntry
{
    public string Who { get; set; } = "";
    public DateTimeOffset When { get; set; }
    public string What { get; set; } = "";
}

/// <summary>Names used in <see cref="AnalysisItem.EditedFields"/>.</summary>
public static class ItemFields
{
    public const string Title = "title", Severity = "severity", Mitre = "mitre", Notes = "notes", Description = "description";
    public static readonly IReadOnlyList<string> All = new[] { Title, Severity, Mitre, Notes, Description };
}

/// <summary>One thing the Report page shows (a flag or a key event) with its own history.</summary>
public sealed class AnalysisItem
{
    public string Id { get; set; } = "";
    public ItemOrigin Origin { get; set; }
    public string Title { get; set; } = "";
    /// <summary>Info, Low, Medium, High or Critical.</summary>
    public string Severity { get; set; } = "Medium";
    public List<string> Mitre { get; set; } = new();
    public string? Tactic { get; set; }
    public string Notes { get; set; } = "";
    /// <summary>The source's own description (Spectre); analysts use <see cref="Notes"/>.</summary>
    public string Description { get; set; } = "";
    /// <summary>The flag this item stands for (suspicious, malicious, ...), for flags.</summary>
    public string? FlagLabel { get; set; }
    public string? Host { get; set; }
    public DateTimeOffset? FirstSeen { get; set; }
    public DateTimeOffset? LastSeen { get; set; }
    /// <summary>Null until the finding has been reviewed.</summary>
    public ReviewState? Review { get; set; }
    public List<AnalysisRow> SourceRows { get; set; } = new();
    public RowEdits RowEdits { get; set; } = new();
    public string? DuplicateOf { get; set; }
    public string? MergedInto { get; set; }
    public List<DupDecision> DupDecisions { get; set; } = new();
    public bool KeptByUser { get; set; }
    /// <summary>Tombstone left by Remove: sync and import never recreate or touch the item.</summary>
    public bool Dismissed { get; set; }
    public ItemSource Source { get; set; } = new();
    public List<string> EditedFields { get; set; } = new();
    public SourceStatus SourceStatus { get; set; } = SourceStatus.Current;
    public string? MigratedFrom { get; set; }
    public List<HistoryEntry> History { get; set; } = new();

    [JsonIgnore] public bool IsHidden => DuplicateOf != null || MergedInto != null;
    public bool IsEdited(string field) => EditedFields.Contains(field, StringComparer.Ordinal);
    public void MarkEdited(string field) { if (!IsEdited(field)) EditedFields.Add(field); }

    public void Log(string what, DateTimeOffset when) =>
        History.Add(new HistoryEntry { Who = CurrentIdentity.Name, When = when, What = what });

    public static string NewId() => "I-" + Guid.NewGuid().ToString("N")[..8];

    public static string? TacticOfMitre(IEnumerable<string> mitre) =>
        mitre.Select(TechniqueCatalog.TacticOf).FirstOrDefault(t => !string.IsNullOrEmpty(t));
}

/// <summary>Names used in <see cref="KeyEvent.EditedFields"/>.</summary>
public static class KeyEventFields
{
    public const string Title = "title", Time = "time", Severity = "severity", Mitre = "mitre", Notes = "notes", Findings = "findings";
    public static readonly IReadOnlyList<string> All = new[] { Title, Time, Severity, Mitre, Notes, Findings };
}

public sealed class KeyEventSource
{
    public string? SpectreMomentId { get; set; }
}

/// <summary>A timeline entry built on the Review page from one or more findings.</summary>
public sealed class KeyEvent
{
    public string Id { get; set; } = "";
    public string Title { get; set; } = "";
    public DateTimeOffset? Time { get; set; }
    public string Severity { get; set; } = "Medium";
    public List<string> Mitre { get; set; } = new();
    public string Notes { get; set; } = "";
    public List<string> FindingIds { get; set; } = new();
    public KeyEventSource Source { get; set; } = new();
    public List<string> EditedFields { get; set; } = new();
    public SourceStatus SourceStatus { get; set; } = SourceStatus.Current;
    public bool KeptByUser { get; set; }
    public bool Dismissed { get; set; }
    public string? PromotedReason { get; set; }
    /// <summary>Findings the key event wanted but another key event already holds (chip "already in key event").</summary>
    public List<string> SkippedFindingIds { get; set; } = new();
    public List<HistoryEntry> History { get; set; } = new();

    public bool IsEdited(string field) => EditedFields.Contains(field, StringComparer.Ordinal);
    public void MarkEdited(string field) { if (!IsEdited(field)) EditedFields.Add(field); }
    public void Log(string what, DateTimeOffset when) =>
        History.Add(new HistoryEntry { Who = CurrentIdentity.Name, When = when, What = what });

    public static string NewId() => "K-" + Guid.NewGuid().ToString("N")[..8];
}

public sealed class AnalysisMeta
{
    /// <summary>The one-time import of the engine's analyst edits, approvals, evidence flags and analyst findings has run.</summary>
    public bool EngineMigrated { get; set; }
    public DateTimeOffset? LastSyncedAt { get; set; }
    public DateTimeOffset? LastImportedAt { get; set; }
}

/// <summary>The whole case file: the analyst's workspace and the Report page's items.</summary>
public sealed class CaseAnalysis
{
    public const int CurrentVersion = 2;
    public int Version { get; set; } = CurrentVersion;
    public WorkspaceSection Workspace { get; set; } = new();
    public List<AnalysisItem> Items { get; set; } = new();
    public List<KeyEvent> KeyEvents { get; set; } = new();
    public AnalysisMeta Meta { get; set; } = new();

    public AnalysisItem? Find(string? id) => id == null ? null : Items.FirstOrDefault(i => i.Id == id);
    public KeyEvent? FindKeyEvent(string? id) => id == null ? null : KeyEvents.FirstOrDefault(k => k.Id == id);

    /// <summary>
    /// Effective rows = (source rows + added + effective rows of every item merged into this one) - removed. Order: source, added, merged.
    /// </summary>
    public IReadOnlyList<AnalysisRow> EffectiveRows(AnalysisItem item) => EffectiveRows(item, new HashSet<string>());

    private List<AnalysisRow> EffectiveRows(AnalysisItem item, HashSet<string> visiting)
    {
        var seen = new HashSet<RowKey>();
        var list = new List<AnalysisRow>();
        void Add(AnalysisRow r) { if (seen.Add(r.Key)) list.Add(r); }
        if (!visiting.Add(item.Id)) return list;
        foreach (var r in item.SourceRows) Add(r);
        foreach (var r in item.RowEdits.Added) Add(r);
        foreach (var m in Items.Where(i => i.MergedInto == item.Id)) foreach (var r in EffectiveRows(m, visiting)) Add(r);
        visiting.Remove(item.Id);
        var removed = item.RowEdits.Removed.Select(r => r.Key).ToHashSet();
        return list.Where(r => !removed.Contains(r.Key)).ToList();
    }

    public HashSet<RowKey> EffectiveKeys(AnalysisItem item) => EffectiveRows(item).Select(r => r.Key).ToHashSet();

    /// <summary>Stable hash of a row set (sorted keys).</summary>
    public static string HashRows(IEnumerable<RowKey> keys)
    {
        var sb = new StringBuilder();
        foreach (var k in keys.OrderBy(k => k.Csv, StringComparer.Ordinal).ThenBy(k => k.Row)) sb.Append(k.Csv).Append('#').Append(k.Row).Append(';');
        return Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(sb.ToString())))[..16];
    }

    // ---- workspace (Analyze side) ----

    public WorkspaceFinding AddFinding(string title, string severity, IEnumerable<string>? mitre, string? notes, IEnumerable<AnalysisRow> rows, DateTimeOffset? now = null)
    {
        var e = new WorkspaceFinding
        {
            Id = "W-" + Guid.NewGuid().ToString("N")[..8], Title = title, Severity = severity,
            Mitre = mitre?.ToList() ?? new(), Notes = notes ?? "", UpdatedAt = now ?? DateTimeOffset.UtcNow,
        };
        var seen = new HashSet<RowKey>();
        foreach (var r in rows) if (seen.Add(r.Key)) e.Rows.Add(r.Clone());
        Workspace.Findings.Add(e);
        return e;
    }

    /// <summary>Removes rows from a workspace key event; a key event left without rows is deleted (returns true then).</summary>
    public bool RemoveRowsFromFinding(string id, IEnumerable<RowKey> keys, DateTimeOffset? now = null)
    {
        var e = Workspace.Findings.FirstOrDefault(k => k.Id == id);
        if (e == null) return false;
        var drop = keys.ToHashSet();
        e.Rows.RemoveAll(r => drop.Contains(r.Key));
        e.UpdatedAt = now ?? DateTimeOffset.UtcNow;
        if (e.Rows.Count > 0) return false;
        Workspace.Findings.Remove(e);
        return true;
    }

    /// <summary>Adds rows to a workspace key event (rows already in it are skipped). Returns how many were added.</summary>
    public int AddRowsToFinding(string id, IEnumerable<AnalysisRow> rows, DateTimeOffset? now = null)
    {
        var e = Workspace.Findings.FirstOrDefault(k => k.Id == id);
        if (e == null) return 0;
        var seen = e.Rows.Select(r => r.Key).ToHashSet();
        int n = 0;
        foreach (var r in rows) if (seen.Add(r.Key)) { e.Rows.Add(r.Clone()); n++; }
        if (n > 0) e.UpdatedAt = now ?? DateTimeOffset.UtcNow;
        return n;
    }

    /// <summary>
    /// Makes the finding's rows of one CSV exactly <paramref name="rows"/> (0-based): new rows are added (with the fingerprint of the
    /// file's existing rows, else <paramref name="fingerprint"/>), missing ones removed. The finding is kept even when it ends up empty
    /// for this file, but not when it has no rows left at all. Returns the rows that were added.
    /// </summary>
    public List<long> SetFindingFileRows(string id, string csvRelPath, IEnumerable<long> rows, string? fingerprint = null, DateTimeOffset? now = null)
    {
        var e = Workspace.Findings.FirstOrDefault(k => k.Id == id);
        if (e == null) return new();
        var csv = new RowKey(csvRelPath, 0).Csv;
        var want = rows.ToHashSet();
        var mine = e.Rows.Where(r => r.Key.Csv == csv).ToList();
        var fp = mine.Select(r => r.Fingerprint).FirstOrDefault(f => f != null) ?? fingerprint;
        var have = mine.Select(r => r.Row).ToHashSet();
        var added = want.Where(r => !have.Contains(r)).OrderBy(r => r).ToList();
        int removed = e.Rows.RemoveAll(r => r.Key.Csv == csv && !want.Contains(r.Row));
        foreach (var r in added) e.Rows.Add(new AnalysisRow(csvRelPath, r, fp));
        if (added.Count > 0 || removed > 0) e.UpdatedAt = now ?? DateTimeOffset.UtcNow;
        if (e.Rows.Count == 0) Workspace.Findings.Remove(e);
        return added;
    }

    /// <summary>The workspace key events that hold this row.</summary>
    public IReadOnlyList<WorkspaceFinding> FindingsWithRow(RowKey key) =>
        Workspace.Findings.Where(k => k.Rows.Any(r => r.Key == key)).ToList();

    public bool RemoveFinding(string id) => Workspace.Findings.RemoveAll(k => k.Id == id) > 0;
}

/// <summary>Edits made on the Report page. Each one writes history and, for fields, records the field in <see cref="AnalysisItem.EditedFields"/>.</summary>
public static class AnalysisEdits
{
    private static DateTimeOffset T(DateTimeOffset? now) => now ?? DateTimeOffset.UtcNow;

    public static void SetTitle(AnalysisItem i, string title, DateTimeOffset? now = null) { if (i.Title == title) return; i.Title = title; i.MarkEdited(ItemFields.Title); i.Log("edited title", T(now)); }
    public static void SetSeverity(AnalysisItem i, string sev, DateTimeOffset? now = null) { if (i.Severity == sev) return; i.Severity = sev; i.MarkEdited(ItemFields.Severity); i.Log($"set severity {sev}", T(now)); }
    public static void SetDescription(AnalysisItem i, string description, DateTimeOffset? now = null) { if (i.Description == description) return; i.Description = description; i.MarkEdited(ItemFields.Description); i.Log("edited description", T(now)); }
    public static void SetNotes(AnalysisItem i, string notes, DateTimeOffset? now = null) { if (i.Notes == notes) return; i.Notes = notes; i.MarkEdited(ItemFields.Notes); i.Log("edited notes", T(now)); }
    public static void SetMitre(AnalysisItem i, IEnumerable<string> mitre, DateTimeOffset? now = null)
    {
        var m = mitre.ToList();
        if (m.SequenceEqual(i.Mitre)) return;
        i.Mitre = m; i.Tactic = AnalysisItem.TacticOfMitre(m); i.MarkEdited(ItemFields.Mitre); i.Log("edited mapping", T(now));
    }

    /// <summary>Sets the review state. Stored for flags too, but it only counts while the item is a key event.</summary>
    public static void SetReview(AnalysisItem i, ReviewState r, DateTimeOffset? now = null) { if (i.Review == r) return; i.Review = r; i.Log($"review {r}", T(now)); }

    public static void RemoveRow(AnalysisItem i, AnalysisRow row, DateTimeOffset? now = null)
    {
        i.RowEdits.Added.RemoveAll(r => r.Key == row.Key);
        if (!i.RowEdits.Removed.Any(r => r.Key == row.Key)) i.RowEdits.Removed.Add(row.Clone());
        i.Log($"removed row {row.CsvRelPath}#{row.Row}", T(now));
    }

    public static void RestoreRow(AnalysisItem i, AnalysisRow row, DateTimeOffset? now = null)
    {
        if (i.RowEdits.Removed.RemoveAll(r => r.Key == row.Key) > 0) i.Log($"restored row {row.CsvRelPath}#{row.Row}", T(now));
    }

    public static void AddRow(AnalysisItem i, AnalysisRow row, DateTimeOffset? now = null)
    {
        i.RowEdits.Removed.RemoveAll(r => r.Key == row.Key);
        if (!i.SourceRows.Any(r => r.Key == row.Key) && !i.RowEdits.Added.Any(r => r.Key == row.Key)) i.RowEdits.Added.Add(row.Clone());
        i.Log($"added row {row.CsvRelPath}#{row.Row}", T(now));
    }

    public static void SetRowNote(AnalysisItem i, AnalysisRow row, bool? confirmed, string? note, DateTimeOffset? now = null)
    {
        var n = i.RowEdits.PerRow.FirstOrDefault(p => p.Key == row.Key);
        if (n == null) i.RowEdits.PerRow.Add(n = new RowNote { CsvRelPath = row.CsvRelPath, Row = row.Row });
        n.Confirmed = confirmed; n.Note = note;
        i.Log($"row note {row.CsvRelPath}#{row.Row}", T(now));
    }

    /// <summary>"Keep anyway" on a removed-at-source item.</summary>
    public static void KeepAnyway(AnalysisItem i, DateTimeOffset? now = null) { if (i.KeptByUser) return; i.KeptByUser = true; i.Log("kept anyway", T(now)); }

    /// <summary>
    /// "Remove": leaves a tombstone (<see cref="AnalysisItem.Dismissed"/>). Items hidden behind it (duplicates, merged) are restored when
    /// <paramref name="restoreHidden"/> is true; otherwise they stay hidden.
    /// </summary>
    public static void Remove(CaseAnalysis a, AnalysisItem i, bool restoreHidden = true, DateTimeOffset? now = null)
    {
        i.Dismissed = true;
        i.Log("removed", T(now));
        if (restoreHidden) RestoreHiddenBy(a, i, now);
    }

    /// <summary>Items hidden because of <paramref name="kept"/> (their <c>duplicateOf</c> or <c>mergedInto</c> points at it).</summary>
    public static IReadOnlyList<AnalysisItem> HiddenBy(CaseAnalysis a, AnalysisItem kept) =>
        a.Items.Where(x => x.DuplicateOf == kept.Id || x.MergedInto == kept.Id).ToList();

    /// <summary>"Restore A": brings back everything hidden behind <paramref name="kept"/>.</summary>
    public static int RestoreHiddenBy(CaseAnalysis a, AnalysisItem kept, DateTimeOffset? now = null)
    {
        int n = 0;
        foreach (var h in HiddenBy(a, kept))
        {
            h.DuplicateOf = null; h.MergedInto = null; n++;
            h.Log($"restored (was hidden behind {kept.Id})", T(now));
        }
        return n;
    }
}
