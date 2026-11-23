using System.Text.Json.Nodes;

namespace EzForensics.Core.Spectre;

/// <summary>Which findings the Review list shows. A null/empty filter value means "any".</summary>
public sealed record FindingsFilter(string? Severity = null, string? Approval = null, bool IncludedOnly = false, string? Search = null)
{
    public const string Any = "Any";
    public static readonly IReadOnlyList<string> ApprovalChoices = new[] { Any, "Pending", SpectreFinding.Approved, SpectreFinding.Rejected, SpectreFinding.Superseded };

    public bool Matches(SpectreFinding f)
    {
        if (!string.IsNullOrEmpty(Severity) && Severity != Any && !string.Equals(f.EffectiveSeverity, Severity, StringComparison.OrdinalIgnoreCase)) return false;
        if (!string.IsNullOrEmpty(Approval) && Approval != Any && !string.Equals(f.ApprovalText, Approval, StringComparison.OrdinalIgnoreCase)) return false;
        if (IncludedOnly && !f.IsIncluded) return false;
        if (string.IsNullOrWhiteSpace(Search)) return true;
        var hay = string.Join('\n', f.Id, f.FindingId, f.EffectiveTitle, f.EffectiveDescription, string.Join(' ', f.EffectiveMitreAttack), f.ArtifactType, f.Notes);
        return Search.Split(' ', StringSplitOptions.RemoveEmptyEntries).All(w => hay.Contains(w, StringComparison.OrdinalIgnoreCase));
    }

    /// <summary>The matching findings, most severe first, then by time (untimed last) and id.</summary>
    public IReadOnlyList<SpectreFinding> Apply(IEnumerable<SpectreFinding> findings) =>
        findings.Where(Matches).OrderByDescending(f => f.SeverityRank).ThenBy(f => string.IsNullOrEmpty(f.EventTime ?? f.FirstSeen) ? 1 : 0).ThenBy(f => f.EventTime ?? f.FirstSeen ?? "", StringComparer.Ordinal)
            .ThenBy(f => f.Id, StringComparer.OrdinalIgnoreCase).ToList();
}

/// <summary>Turns the editor's values into the minimal <see cref="SpectreFindingChanges"/>: only what differs, and a revert when a field is put back to the engine's value.</summary>
public static class FindingDraft
{
    public const string FieldTitle = "title", FieldDescription = "description", FieldSeverity = "severity", FieldMitre = "mitre_attack", FieldNotes = "notes", FieldInclude = "include_in_report";

    /// <summary>"T1059, T1003.001" or one per line → trimmed, distinct, upper-cased ids.</summary>
    public static IReadOnlyList<string> ParseMitre(string? text) =>
        (text ?? "").Split(new[] { ',', ';', ' ', '\n', '\r', '\t' }, StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
            .Select(s => s.ToUpperInvariant()).Distinct().ToList();

    public static string MitreText(IEnumerable<string> ids) => string.Join(", ", ids);

    public static SpectreFindingChanges BuildChanges(SpectreFinding f, string title, string description, string severity, string mitre, string notes, bool include)
    {
        var c = new SpectreFindingChanges();
        var a = f.Analyst;
        // text fields: equal to the engine value = no override (revert if one exists); otherwise set when it differs from what is shown now
        Text(c, FieldTitle, title, f.Title, a?.Title, f.EffectiveTitle, c.Title);
        Text(c, FieldDescription, description, f.Description, a?.Description, f.EffectiveDescription, c.Description);
        if (!string.Equals(severity, f.EffectiveSeverity, StringComparison.OrdinalIgnoreCase))
        {
            if (string.Equals(severity, f.Severity, StringComparison.OrdinalIgnoreCase) && a?.Severity != null) c.Revert(FieldSeverity);
            else c.Severity(severity);
        }
        var ids = ParseMitre(mitre);
        if (!ids.SequenceEqual(f.EffectiveMitreAttack.Select(s => s.ToUpperInvariant())))
        {
            if (ids.SequenceEqual(f.MitreAttack.Select(s => s.ToUpperInvariant())) && a?.MitreAttack != null) c.Revert(FieldMitre);
            else c.MitreAttack(ids);
        }
        var n = notes.Trim();
        if (n != (f.Notes ?? "").Trim())
        {
            if (n.Length == 0 && a?.Notes != null) c.Revert(FieldNotes); else c.Notes(n);
        }
        if (include != f.IsIncluded)
        {
            if (include == f.IncludeInReport && a?.IncludeInReport != null) c.Revert(FieldInclude); else c.IncludeInReport(include);
        }
        return c;
    }

    private static void Text(SpectreFindingChanges c, string field, string value, string engine, string? over, string effective, Func<string?, SpectreFindingChanges> set)
    {
        var v = value.Trim();
        if (v == effective.Trim()) return;
        if (v == engine.Trim() && over != null) c.Revert(field); else set(v);
    }

    /// <summary>True when the examiner has an override for <paramref name="field"/> (a Revert button makes sense).</summary>
    public static bool HasOverride(SpectreFinding f, string field) => field switch
    {
        FieldTitle => f.Analyst?.Title != null,
        FieldDescription => f.Analyst?.Description != null,
        FieldSeverity => f.Analyst?.Severity != null,
        FieldMitre => f.Analyst?.MitreAttack != null,
        FieldNotes => f.Analyst?.Notes != null,
        FieldInclude => f.Analyst?.IncludeInReport != null,
        _ => false,
    };
}

/// <summary>A change Spectre (the chat) proposes for one finding; nothing is sent to the engine until the examiner accepts it.</summary>
public sealed record FindingSuggestion(string FindingId, SpectreFindingChanges Changes, string Why)
{
    /// <summary>One "Field: current → proposed" line per changed field.</summary>
    public IReadOnlyList<string> DiffLines(SpectreFinding f)
    {
        var j = Changes.ToJson();
        var lines = new List<string>();
        foreach (var (field, value) in j)
        {
            var (label, cur) = field switch
            {
                FindingDraft.FieldTitle => ("Title", f.EffectiveTitle),
                FindingDraft.FieldDescription => ("Description", f.EffectiveDescription),
                FindingDraft.FieldSeverity => ("Severity", f.EffectiveSeverity),
                FindingDraft.FieldMitre => ("MITRE", FindingDraft.MitreText(f.EffectiveMitreAttack)),
                FindingDraft.FieldNotes => ("Notes", f.Notes ?? ""),
                FindingDraft.FieldInclude => ("In report", f.IsIncluded ? "yes" : "no"),
                _ => (field, ""),
            };
            var next = value switch
            {
                null => "(engine value)",
                JsonArray a => string.Join(", ", a.Select(x => x?.ToString())),
                JsonValue v when v.TryGetValue<bool>(out var b) => b ? "yes" : "no",
                _ => value.ToString(),
            };
            lines.Add($"{label}: {(cur.Length == 0 ? "(empty)" : cur)} → {(next.Length == 0 ? "(empty)" : next)}");
        }
        return lines;
    }
}

/// <summary>What one finding says about its evidence: where it came from, which record, and the engine's explicit rows (if it sent them).</summary>
public sealed record EvidenceSource(string? CasePath, string? SourceFile, string? RecordId, IReadOnlyList<SpectreEvidenceRef> Evidence)
{
    public static EvidenceSource From(SpectreFinding f) => new(f.CasePath, f.SourceFile, f.SourceRecordId, f.Evidence);
    public static EvidenceSource From(SpectreTimelineFinding f) => new(f.CasePath, f.SourceFile, f.SourceRecordId, f.Evidence);
}

/// <summary>One case CSV with the rows to show (0-based data rows) and record ids still to be looked up in it.</summary>
public sealed record EvidenceTarget(string Csv, IReadOnlyList<long> Rows, IReadOnlyList<string> RecordIds);

/// <summary>Finds the case CSV an evidence row came from, so the page can offer "Open in Analyze".</summary>
public static class FindingsEvidence
{
    private static readonly string[] SourceKeys = { "source_file", "source", "csv", "file", "path", "source_csv" };

    /// <summary>The first existing .csv named by an event row (absolute, or relative to the case folder); null when none.</summary>
    public static string? FindSourceCsv(IEnumerable<JsonObject> rows, string caseDir)
    {
        foreach (var row in rows)
        {
            foreach (var key in SourceKeys)
            {
                if (row[key] is not JsonValue v || !v.TryGetValue<string>(out var s) || string.IsNullOrWhiteSpace(s)) continue;
                s = s.Trim().Trim('"');
                if (!s.EndsWith(".csv", StringComparison.OrdinalIgnoreCase)) continue;
                try
                {
                    var full = Path.IsPathRooted(s) ? s : Path.Combine(caseDir, s);
                    if (File.Exists(full)) return Path.GetFullPath(full);
                }
                catch (ArgumentException) { }
            }
        }
        return null;
    }

    /// <summary>Columns that hold a record's id in the case CSVs (EvtxECmd, MFTECmd, ...), tried in this order.</summary>
    public static readonly IReadOnlyList<string> RecordIdColumns = new[]
        { "EventRecordId", "EventRecordID", "RecordNumber", "RecordId", "record_id", "EntryNumber", "Inode", "RecordNum" };

    /// <summary>
    /// The case file a finding came from (its <c>source_file</c>): an existing absolute path, a path relative to the case folder, an engine path
    /// (<c>/mnt/d/...</c>, or one that contains the case folder's name), or, last, the only file of that name under the case folder. Null when none is found.
    /// </summary>
    public static string? ResolveSourceFile(string? sourceFile, string caseDir, int maxSearch = 20000)
    {
        if (string.IsNullOrWhiteSpace(sourceFile) || string.IsNullOrWhiteSpace(caseDir)) return null;
        var s = sourceFile.Trim().Trim('"');
        var root = Path.GetFullPath(caseDir).TrimEnd('\\', '/');
        var candidates = new List<string>();
        if (s.StartsWith("/mnt/", StringComparison.Ordinal) && s.Length > 6 && char.IsAsciiLetter(s[5]) && (s.Length == 6 || s[6] == '/'))
            candidates.Add(char.ToUpperInvariant(s[5]) + ":" + s[6..].Replace('/', '\\'));
        var win = s.Replace('/', '\\');
        if (Path.IsPathRooted(win) && !win.StartsWith('\\')) candidates.Add(win);
        else if (!s.StartsWith('/')) candidates.Add(Path.Combine(root, win.TrimStart('\\')));
        // an engine path that runs through the case folder: keep what follows the folder name
        var name = Path.GetFileName(root);
        var marker = "\\" + name + "\\";
        int at = win.IndexOf(marker, StringComparison.OrdinalIgnoreCase);
        if (name.Length > 0 && at >= 0) candidates.Add(Path.Combine(root, win[(at + marker.Length)..]));
        foreach (var c in candidates)
        {
            try { if (File.Exists(c)) return Path.GetFullPath(c); }
            catch (Exception ex) when (ex is ArgumentException or NotSupportedException or PathTooLongException) { }
        }
        // last: a unique file of that name in the case folder
        var file = Path.GetFileName(win);
        if (string.IsNullOrEmpty(file)) return null;
        try
        {
            string? found = null;
            int seen = 0;
            foreach (var f in Directory.EnumerateFiles(root, "*", new EnumerationOptions { RecurseSubdirectories = true, IgnoreInaccessible = true }))
            {
                if (++seen > maxSearch) return null;
                if (!string.Equals(Path.GetFileName(f), file, StringComparison.OrdinalIgnoreCase)) continue;
                if (found != null) return null;   // two files of that name: do not guess
                found = f;
            }
            return found;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or ArgumentException) { return null; }
    }

    /// <summary>
    /// The data row (0-based, as Analyze counts) whose record-id column (<see cref="RecordIdColumns"/>) equals <paramref name="recordId"/>.
    /// Null when the CSV has no such column or no row matches: a row is never guessed from the id alone.
    /// </summary>
    public static long? FindRecordRow(Results.CsvIndex index, string? recordId, CancellationToken ct = default)
    {
        var want = recordId?.Trim();
        if (string.IsNullOrEmpty(want)) return null;
        int col = -1;
        foreach (var name in RecordIdColumns)
        {
            for (int i = 0; i < index.Headers.Count && col < 0; i++)
                if (string.Equals(index.Headers[i].Trim(), name, StringComparison.OrdinalIgnoreCase)) col = i;
            if (col >= 0) break;
        }
        if (col < 0) return null;
        bool numeric = long.TryParse(want, out var wantNum);
        long total = index.RowCount;
        for (long start = 0; start < total; start += Results.CsvIndex.PageSize)
        {
            ct.ThrowIfCancellationRequested();
            var rows = index.ReadRows(start, (int)Math.Min(Results.CsvIndex.PageSize, total - start));
            for (int r = 0; r < rows.Length; r++)
            {
                var cell = col < rows[r].Length ? rows[r][col].Trim() : "";
                if (string.Equals(cell, want, StringComparison.OrdinalIgnoreCase) || (numeric && long.TryParse(cell, out var n) && n == wantNum)) return start + r;
            }
        }
        return null;
    }

    private static int IdColumn(Results.CsvIndex index)
    {
        foreach (var name in RecordIdColumns)
            for (int i = 0; i < index.Headers.Count; i++)
                if (string.Equals(index.Headers[i].Trim(), name, StringComparison.OrdinalIgnoreCase)) return i;
        return -1;
    }

    /// <summary>
    /// The data rows (0-based, as Analyze counts) behind <paramref name="target"/>, sorted, no repeats: its explicit rows (engine <c>evidence[].row</c>);
    /// plus each record id found in the file's record-id column; plus, when the file has no such column, a numeric id read as the engine's
    /// row number (<c>csv_row</c>, a 1-based data row: id 1 is the first row under the header). Rows outside the file are dropped.
    /// </summary>
    public static IReadOnlyList<long> ResolveRows(Results.CsvIndex index, EvidenceTarget target, CancellationToken ct = default)
    {
        long total = index.RowCount;
        var rows = new SortedSet<long>(target.Rows.Where(r => r >= 0 && r < total));
        var ids = target.RecordIds.Select(i => i?.Trim() ?? "").Where(i => i.Length > 0).Distinct(StringComparer.OrdinalIgnoreCase).ToList();
        if (ids.Count == 0) return rows.ToList();
        int col = IdColumn(index);
        if (col < 0)
        {
            foreach (var id in ids)
                if (long.TryParse(id, out var n) && n >= 1 && n <= total) rows.Add(n - 1);
            return rows.ToList();
        }
        var want = new HashSet<string>(ids, StringComparer.OrdinalIgnoreCase);
        var wantNum = new HashSet<long>(ids.Select(i => long.TryParse(i, out var n) ? (long?)n : null).Where(n => n != null).Select(n => n!.Value));
        for (long start = 0; start < total; start += Results.CsvIndex.PageSize)
        {
            ct.ThrowIfCancellationRequested();
            var page = index.ReadRows(start, (int)Math.Min(Results.CsvIndex.PageSize, total - start));
            for (int r = 0; r < page.Length; r++)
            {
                var cell = col < page[r].Length ? page[r][col].Trim() : "";
                if (want.Contains(cell) || (long.TryParse(cell, out var c) && wantNum.Contains(c))) rows.Add(start + r);
            }
        }
        return rows.ToList();
    }

    /// <summary>
    /// Groups what findings name as evidence by case CSV (first-named file first). A finding with an <c>evidence</c> list contributes its rows;
    /// one without contributes its record id (resolved against the file later by <see cref="ResolveRows"/>). Non-CSV or missing files are skipped.
    /// </summary>
    public static IReadOnlyList<EvidenceTarget> Plan(string caseDir, IEnumerable<EvidenceSource> sources)
    {
        var order = new List<string>();
        var rows = new Dictionary<string, List<long>>(StringComparer.OrdinalIgnoreCase);
        var ids = new Dictionary<string, List<string>>(StringComparer.OrdinalIgnoreCase);
        var cache = new Dictionary<string, string?>(StringComparer.OrdinalIgnoreCase);
        string? Resolve(string? p)
        {
            if (string.IsNullOrWhiteSpace(p)) return null;
            if (!cache.TryGetValue(p, out var f)) cache[p] = f = ResolveSourceFile(p, caseDir);
            return f != null && f.EndsWith(".csv", StringComparison.OrdinalIgnoreCase) ? f : null;
        }
        void Touch(string f)
        {
            if (rows.ContainsKey(f)) return;
            order.Add(f); rows[f] = new(); ids[f] = new();
        }
        foreach (var s in sources)
        {
            if (s.Evidence.Count > 0)
            {
                foreach (var e in s.Evidence)
                {
                    var f = Resolve(e.CasePath);
                    if (f == null) continue;
                    Touch(f); rows[f].Add(e.Row);
                }
                continue;
            }
            var file = Resolve(s.CasePath) ?? Resolve(s.SourceFile);
            if (file == null) continue;
            Touch(file);
            if (!string.IsNullOrWhiteSpace(s.RecordId)) ids[file].Add(s.RecordId!.Trim());
        }
        return order.Select(f => new EvidenceTarget(f, rows[f].Distinct().OrderBy(r => r).ToList(), ids[f].Distinct(StringComparer.OrdinalIgnoreCase).ToList())).ToList();
    }

    /// <summary>
    /// The case-relative path (forward slashes) the engine uses for <paramref name="csv"/> (<c>evidence[].case_path</c>): a path one of
    /// <paramref name="known"/> resolves to, else the path under <paramref name="caseDir"/>, else the full path.
    /// </summary>
    public static string CasePathOf(string caseDir, string csv, IEnumerable<string>? known = null)
    {
        foreach (var k in known ?? Array.Empty<string>())
        {
            if (string.IsNullOrWhiteSpace(k)) continue;
            var full = ResolveSourceFile(k, caseDir, maxSearch: 0);
            if (full != null && string.Equals(full, csv, StringComparison.OrdinalIgnoreCase)) return k.Trim();
        }
        try
        {
            var rel = Path.GetRelativePath(caseDir, csv);
            if (!rel.StartsWith("..", StringComparison.Ordinal) && !Path.IsPathRooted(rel)) return rel.Replace('\\', '/');
        }
        catch (ArgumentException) { }
        return csv;
    }

    /// <summary>One display line for an event row: "key: value · key: value", long values cut.</summary>
    public static string Line(JsonObject row, int maxLength = 400)
    {
        var parts = row.Where(kv => kv.Value != null).Select(kv => $"{kv.Key}: {kv.Value}".Replace('\n', ' ').Replace('\r', ' '));
        var s = string.Join(" · ", parts);
        return s.Length <= maxLength ? s : s[..maxLength] + "…";
    }
}
