using System.Text.Json;
using EzForensics.Core.Results;

namespace EzForensics.Core.Analysis;

/// <summary>The flagged rows of one CSV with one flag label (what "Add my analysis" groups into one item).</summary>
public sealed record FlagCsv(string CsvRelPath, string Label, IReadOnlyList<AnalysisRow> Rows)
{
    /// <summary>Text for titles ("Suspicious"); null falls back to the capitalised label.</summary>
    public string? DisplayLabel { get; init; }
}

/// <summary>What is known about one row, for deriving host and times of analyst items.</summary>
public sealed record AnalysisRowInfo(DateTimeOffset? Time, string? Host);

/// <summary>Reads facts of a CSV row (injected so Core stays testable without files; the app backs it with the CSV reader).</summary>
public interface IAnalysisRowReader
{
    AnalysisRowInfo? Read(AnalysisRow row);
}

public sealed class SyncOptions
{
    public DateTimeOffset? Now { get; init; }
    /// <summary>Row facts for host / first seen / last seen of analyst items; null leaves them as they are.</summary>
    public IAnalysisRowReader? RowReader { get; init; }
    /// <summary>The case host of a case-relative CSV path (see <c>CaseHostInfo</c>); preferred over the row's own host.</summary>
    public Func<string, string?>? HostForCasePath { get; init; }
    /// <summary>Display text of a flag label (from the flag catalogue).</summary>
    public Func<string, string>? LabelText { get; init; }
}

public sealed class SyncResult
{
    public int Created, Updated, RemovedAtSource, Restored, Dismissed;
    /// <summary>Engine analyst findings migrated into the workspace by a Spectre import.</summary>
    public int Migrated;
    /// <summary>Items that are new, have a different row set, or a different kind: the ones the duplicate check looks at.</summary>
    public List<string> ChangedItemIds { get; } = new();
    /// <summary>New duplicate or possible-duplicate pairs among findings that involve a changed item (open ones only).</summary>
    public List<DuplicatePair> Duplicates { get; } = new();
    public bool AnyChange => Created + Updated + RemovedAtSource + Restored + Dismissed + Migrated > 0;
}

/// <summary>
/// "Add my analysis": brings the analyst's workspace key events and flagged rows into the Report items. Fixed rules (design section 3):
/// edited fields win, only source rows are replaced, a vanished source marks the item (never deletes it), dismissed items are never recreated,
/// and running it twice with the same input changes nothing.
/// </summary>
public static class AnalysisSync
{
    public static SyncResult Sync(CaseAnalysis a, SyncOptions? options = null)
    {
        var o = options ?? new SyncOptions();
        var now = o.Now ?? DateTimeOffset.UtcNow;
        var before = Snapshot(a);
        var res = new SyncResult();
        SyncKeyEvents(a, o, now, res, only: null);
        Finish(a, before, res, now, touchMeta: true);
        return res;
    }

    /// <summary>
    /// Pulls the current title, severity, mapping, notes and rows of each Analyze finding into the item already made from it (fields the examiner edited
    /// in Review stay). Makes no new items and removes none; that is what Add my analysis is for. Returns how many items changed.
    /// </summary>
    public static int RefreshLinked(CaseAnalysis a, SyncOptions? options = null)
    {
        var o = options ?? new SyncOptions();
        var now = o.Now ?? DateTimeOffset.UtcNow;
        int changed = 0;
        foreach (var w in a.Workspace.Findings.ToList())
        {
            var item = a.Items.FirstOrDefault(i => i.Source.WorkspaceId == w.Id);
            if (item == null || item.Dismissed || item.SourceStatus == SourceStatus.RemovedAtSource) continue;
            var sig = Sig(item);
            ApplyWorkspace(a, item, w, o);
            if (Sig(item) != sig) { item.Log("updated from Analyze", now); changed++; }
        }
        return changed;
    }

    /// <summary>What a sync would change, counted on a copy; the store is left alone (the "3 changes since last added" badge).</summary>
    public static SyncResult Preview(CaseAnalysis a, SyncOptions? options = null) =>
        Sync(CaseAnalysisStore.Clone(a), options);

    // ------------------------------------------------------------------ key events

    internal static void SyncKeyEvents(CaseAnalysis a, SyncOptions o, DateTimeOffset now, SyncResult res, WorkspaceFinding? only)
    {
        var events = only != null ? new List<WorkspaceFinding> { only } : a.Workspace.Findings.ToList();
        foreach (var w in events)
        {
            var item = a.Items.FirstOrDefault(i => i.Source.WorkspaceId == w.Id);
            if (item == null)
            {
                item = new AnalysisItem
                {
                    Id = AnalysisItem.NewId(), Origin = ItemOrigin.Analyst, Review = ReviewState.Confirmed,
                    Source = { WorkspaceId = w.Id },
                };
                ApplyWorkspace(a, item, w, o);
                item.Log("added from my analysis", now);
                a.Items.Add(item);
                res.Created++;
                continue;
            }
            if (item.Dismissed) continue;
            var sig = Sig(item);
            var wasRemoved = item.SourceStatus == SourceStatus.RemovedAtSource;
            ApplyWorkspace(a, item, w, o);
            if (wasRemoved) { item.SourceStatus = SourceStatus.Current; item.Log("key event is back in Analyze", now); res.Restored++; }
            if (Sig(item) != sig) { item.Log("updated from Analyze", now); }
        }
        if (only != null) return;
        var live = a.Workspace.Findings.Select(k => k.Id).ToHashSet();
        foreach (var i in a.Items.Where(i => i.Source.WorkspaceId != null && !i.Dismissed && !live.Contains(i.Source.WorkspaceId)))
            if (i.SourceStatus == SourceStatus.Current)
            {
                i.SourceStatus = SourceStatus.RemovedAtSource;
                i.Log("key event removed in Analyze", now);
                res.RemovedAtSource++;
            }
    }

    private static void ApplyWorkspace(CaseAnalysis a, AnalysisItem item, WorkspaceFinding w, SyncOptions o)
    {
        if (!item.IsEdited(ItemFields.Title)) item.Title = w.Title;
        if (!item.IsEdited(ItemFields.Severity)) item.Severity = w.Severity;
        if (!item.IsEdited(ItemFields.Mitre)) { item.Mitre = w.Mitre.ToList(); item.Tactic = AnalysisItem.TacticOfMitre(item.Mitre); }
        if (!item.IsEdited(ItemFields.Notes)) item.Notes = w.Notes;
        item.SourceRows = w.Rows.Select(r => r.Clone()).ToList();
        ApplyFacts(a, item, o);
    }

    public static string SeverityOfFlag(string? label) => label?.ToLowerInvariant() switch
    {
        FlagCatalog.Malicious => "High",
        FlagCatalog.Suspicious => "Medium",
        FlagCatalog.Interesting => "Low",
        FlagCatalog.Informational => "Info",
        _ => "Medium",
    };

    // ------------------------------------------------------------------ derived facts

    /// <summary>Host, first seen and last seen of an analyst item from its rows (the case host of the file, else the row's own host).</summary>
    internal static void ApplyFacts(CaseAnalysis a, AnalysisItem item, SyncOptions o)
    {
        if (o.RowReader == null && o.HostForCasePath == null) return;
        var rows = a.EffectiveRows(item);
        var hosts = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase);
        DateTimeOffset? min = null, max = null;
        foreach (var r in rows)
        {
            var info = o.RowReader?.Read(r);
            var h = o.HostForCasePath?.Invoke(r.CsvRelPath);
            if (string.IsNullOrWhiteSpace(h)) h = info?.Host;
            if (!string.IsNullOrWhiteSpace(h)) hosts[h.Trim()] = hosts.GetValueOrDefault(h.Trim()) + 1;
            if (info?.Time is { } t) { if (min == null || t < min) min = t; if (max == null || t > max) max = t; }
        }
        if (hosts.Count > 0) item.Host = hosts.OrderByDescending(kv => kv.Value).ThenBy(kv => kv.Key, StringComparer.OrdinalIgnoreCase).First().Key;
        if (min != null) { item.FirstSeen = min; item.LastSeen = max; }
    }

    // ------------------------------------------------------------------ change tracking

    internal sealed record Snap(string Sig, string RowHash);

    internal static string Sig(AnalysisItem i) =>
        string.Join("\u0001", i.Title, i.Severity, string.Join(",", i.Mitre), i.Notes, i.SourceStatus, i.Dismissed, i.Description, i.FlagLabel, i.Review, string.Join(",", i.EditedFields), i.RowEdits.Added.Count, i.RowEdits.Removed.Count, i.RowEdits.PerRow.Count, i.Host, i.FirstSeen, i.LastSeen,
            string.Join(";", i.SourceRows.Select(r => r.Key + "/" + r.Fingerprint + "/" + r.Stale)));

    internal static Dictionary<string, Snap> Snapshot(CaseAnalysis a) =>
        a.Items.ToDictionary(i => i.Id, i => new Snap(Sig(i), CaseAnalysis.HashRows(a.EffectiveKeys(i))));

    internal static void Finish(CaseAnalysis a, Dictionary<string, Snap> before, SyncResult res, DateTimeOffset now, bool touchMeta)
    {
        foreach (var i in a.Items)
        {
            if (!before.TryGetValue(i.Id, out var b)) { res.ChangedItemIds.Add(i.Id); continue; }
            if (Sig(i) != b.Sig) res.Updated++;
            if (CaseAnalysis.HashRows(a.EffectiveKeys(i)) != b.RowHash) res.ChangedItemIds.Add(i.Id);
        }
        // "Created" items were not counted as updates; removed/restored/dismissed items changed their signature, so they are not counted twice.
        res.Updated = Math.Max(0, res.Updated - res.RemovedAtSource - res.Restored - res.Dismissed);
        if (res.ChangedItemIds.Count > 0)
            res.Duplicates.AddRange(DuplicateDetector.FindPairs(a, res.ChangedItemIds.ToHashSet()));
        if (touchMeta && res.AnyChange) a.Meta.LastSyncedAt = now;
    }

    // ------------------------------------------------------------------ sidecar reader

    private static readonly JsonSerializerOptions SidecarJson = new() { PropertyNameCaseInsensitive = true, PropertyNamingPolicy = JsonNamingPolicy.CamelCase };

    /// <summary>
    /// Reads every <c>*.ezflags.json</c> under <paramref name="caseRoot"/> through the format of <see cref="RowFlagStore"/> and groups the rows by CSV and
    /// flag. Sidecars of CSVs that were changed since flagging are still read: the rows are what the examiner flagged, and the fingerprint stored with them lets
    /// the app mark them stale. Unreadable sidecars are skipped.
    /// </summary>
    public static List<FlagCsv> ReadSidecars(string caseRoot, Func<string, string>? labelText = null)
    {
        var list = new List<FlagCsv>();
        var root = Path.GetFullPath(caseRoot);
        IEnumerable<string> files;
        try { files = Directory.EnumerateFiles(root, "*" + RowFlagStore.SidecarSuffix, SearchOption.AllDirectories).OrderBy(f => f, StringComparer.OrdinalIgnoreCase).ToList(); }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { return list; }
        foreach (var path in files)
        {
            RowFlagFile? f;
            try { f = JsonSerializer.Deserialize<RowFlagFile>(File.ReadAllText(path), SidecarJson); }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or JsonException) { continue; }
            if (f == null) continue;
            var dir = Path.GetDirectoryName(path)!;
            var csvName = string.IsNullOrWhiteSpace(f.CsvFile) ? Path.GetFileName(path)[..^RowFlagStore.SidecarSuffix.Length] + ".csv" : f.CsvFile;
            var rel = AnalysisRow.Norm(Path.GetRelativePath(root, Path.Combine(dir, csvName)));
            var fp = $"{f.CsvSize}|{f.CsvLastWriteUtc.Ticks}|{f.CsvHeadSha256}";
            bool legacy = f.Version < 3;
            var byFlag = new Dictionary<string, SortedSet<long>>(StringComparer.Ordinal);
            var assigned = new HashSet<long>();
            if (f.Colors != null)
                foreach (var (name, rows) in f.Colors)
                {
                    if (rows == null || string.IsNullOrWhiteSpace(name)) continue;
                    var id = FlagCatalog.IdFromStored(name, legacy);
                    if (id == FlagCatalog.RetiredSpectreEvidenceId) { foreach (var r in rows) assigned.Add(r); continue; }
                    if (!byFlag.TryGetValue(id, out var set)) byFlag[id] = set = new();
                    foreach (var r in rows) if (r >= 0) { set.Add(r); assigned.Add(r); }
                }
            foreach (var r in f.Rows)
                if (r >= 0 && !assigned.Contains(r))
                {
                    if (!byFlag.TryGetValue(FlagCatalog.LegacyDefaultId, out var set)) byFlag[FlagCatalog.LegacyDefaultId] = set = new();
                    set.Add(r);
                }
            foreach (var (id, rows) in byFlag.OrderBy(kv => kv.Key, StringComparer.Ordinal))
                if (rows.Count > 0)
                    list.Add(new FlagCsv(rel, id, rows.Select(r => new AnalysisRow(rel, r, fp)).ToList()) { DisplayLabel = labelText?.Invoke(id) });
        }
        return list;
    }
}
