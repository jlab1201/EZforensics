namespace EzForensics.Core.Analysis;

/// <summary>Effective (edited-or-default) values of a key event.</summary>
public sealed record KeyEventValues(string Title, DateTimeOffset? Time, string Severity, IReadOnlyList<string> Mitre, string? Tactic);

/// <summary>Creating, grouping and changing key events, and computing their default values from their findings.</summary>
public static class KeyEventOps
{
    private static readonly string[] SeverityOrder = { "Info", "Low", "Medium", "High", "Critical" };
    /// <summary>The stored title wins when the user edited it, or when Spectre gave the moment its own headline.</summary>
    private static bool OwnTitle(KeyEvent k) => k.IsEdited(KeyEventFields.Title) || (k.Source.SpectreMomentId != null && !string.IsNullOrWhiteSpace(k.Title));

    private static DateTimeOffset T(DateTimeOffset? now) => now ?? DateTimeOffset.UtcNow;
    private static int Rank(string? s) => Array.FindIndex(SeverityOrder, x => string.Equals(x, s, StringComparison.OrdinalIgnoreCase));

    public static KeyEvent AddToTimeline(CaseAnalysis a, string findingId, DateTimeOffset? now = null)
        => KeyEventOf(a, findingId) ?? Group(a, new[] { findingId }, now);

    public static KeyEvent Group(CaseAnalysis a, IReadOnlyList<string> findingIds, DateTimeOffset? now = null)
    {
        var t = T(now);
        var ids = findingIds.Distinct().Where(id => a.Find(id) != null).ToList();
        var k = new KeyEvent { Id = KeyEvent.NewId() };
        foreach (var id in ids) MoveOut(a, id, k, t);
        k.FindingIds.AddRange(ids);
        k.Log($"created from {ids.Count} finding(s)", t);
        a.KeyEvents.Add(k);
        RefreshDefaults(a, k);
        return k;
    }

    public static bool AddFinding(CaseAnalysis a, string keyEventId, string findingId, DateTimeOffset? now = null)
    {
        var k = a.FindKeyEvent(keyEventId);
        if (k == null || k.Dismissed || a.Find(findingId) == null || k.FindingIds.Contains(findingId)) return false;
        var t = T(now);
        MoveOut(a, findingId, k, t);
        k.FindingIds.Add(findingId);
        k.MarkEdited(KeyEventFields.Findings);
        k.Log($"added finding {findingId}", t);
        RefreshDefaults(a, k);
        return true;
    }

    public static bool RemoveFinding(CaseAnalysis a, string keyEventId, string findingId, DateTimeOffset? now = null)
    {
        var k = a.FindKeyEvent(keyEventId);
        if (k == null || !k.FindingIds.Remove(findingId)) return false;
        var t = T(now);
        k.MarkEdited(KeyEventFields.Findings);
        k.Log($"removed finding {findingId}", t);
        if (k.FindingIds.Count == 0) { k.Dismissed = true; k.Log("removed (no findings left)", t); return true; }
        RefreshDefaults(a, k);
        return false;
    }

    public static KeyEvent? KeyEventOf(CaseAnalysis a, string findingId) =>
        a.KeyEvents.FirstOrDefault(k => !k.Dismissed && k.FindingIds.Contains(findingId));

    public static KeyEventValues Effective(CaseAnalysis a, KeyEvent k)
    {
        var (title, time, sev, mitre) = Defaults(a, k);
        IReadOnlyList<string> m = k.IsEdited(KeyEventFields.Mitre) ? k.Mitre : mitre;
        return new KeyEventValues(
            OwnTitle(k) ? k.Title : title,
            k.IsEdited(KeyEventFields.Time) ? k.Time : time,
            k.IsEdited(KeyEventFields.Severity) ? k.Severity : sev,
            m, AnalysisItem.TacticOfMitre(m));
    }

    /// <summary>Stores the defaults into the fields the user has not edited, so plain readers of the stored values see current data.</summary>
    internal static void RefreshDefaults(CaseAnalysis a, KeyEvent k)
    {
        var (title, time, sev, mitre) = Defaults(a, k);
        if (!OwnTitle(k)) k.Title = title;
        if (!k.IsEdited(KeyEventFields.Time)) k.Time = time;
        if (!k.IsEdited(KeyEventFields.Severity)) k.Severity = sev;
        if (!k.IsEdited(KeyEventFields.Mitre)) k.Mitre = mitre;
    }

    private static (string Title, DateTimeOffset? Time, string Severity, List<string> Mitre) Defaults(CaseAnalysis a, KeyEvent k)
    {
        var fs = k.FindingIds.Select(a.Find).Where(i => i != null && !i.Dismissed && !i.IsHidden).Select(i => i!).ToList();
        if (fs.Count == 0) return (k.Title, k.Time, k.Severity, k.Mitre.ToList());
        var times = fs.Where(i => i.FirstSeen != null).Select(i => i.FirstSeen!.Value).ToList();
        var sev = fs.OrderByDescending(i => Rank(i.Severity)).First().Severity;
        DateTimeOffset? time = times.Count > 0 ? times.Min() : null;
        return (fs[0].Title, time, sev, fs.SelectMany(i => i.Mitre).Distinct().ToList());
    }

    private static void MoveOut(CaseAnalysis a, string findingId, KeyEvent target, DateTimeOffset t)
    {
        var from = KeyEventOf(a, findingId);
        if (from == null || from == target) return;
        from.FindingIds.Remove(findingId);
        from.MarkEdited(KeyEventFields.Findings);
        from.Log($"finding {findingId} moved to {target.Id}", t);
        target.Log($"finding {findingId} moved in from {from.Id}", t);
        if (from.FindingIds.Count == 0) { from.Dismissed = true; from.Log("removed (no findings left)", t); }
        else RefreshDefaults(a, from);
    }
}

/// <summary>Edits made on a key event. Each marks its field edited and logs.</summary>
public static class KeyEventEdits
{
    private static DateTimeOffset T(DateTimeOffset? now) => now ?? DateTimeOffset.UtcNow;

    public static void SetTitle(KeyEvent k, string title, DateTimeOffset? now = null) { if (k.Title == title && k.IsEdited(KeyEventFields.Title)) return; k.Title = title; k.MarkEdited(KeyEventFields.Title); k.Log("edited title", T(now)); }
    public static void SetTime(KeyEvent k, DateTimeOffset? time, DateTimeOffset? now = null) { if (k.Time == time && k.IsEdited(KeyEventFields.Time)) return; k.Time = time; k.MarkEdited(KeyEventFields.Time); k.Log("edited time", T(now)); }
    public static void SetSeverity(KeyEvent k, string sev, DateTimeOffset? now = null) { if (k.Severity == sev && k.IsEdited(KeyEventFields.Severity)) return; k.Severity = sev; k.MarkEdited(KeyEventFields.Severity); k.Log($"set severity {sev}", T(now)); }
    public static void SetNotes(KeyEvent k, string notes, DateTimeOffset? now = null) { if (k.Notes == notes) return; k.Notes = notes; k.MarkEdited(KeyEventFields.Notes); k.Log("edited notes", T(now)); }
    public static void SetMitre(KeyEvent k, IEnumerable<string> mitre, DateTimeOffset? now = null)
    {
        var m = mitre.ToList();
        if (m.SequenceEqual(k.Mitre) && k.IsEdited(KeyEventFields.Mitre)) return;
        k.Mitre = m; k.MarkEdited(KeyEventFields.Mitre); k.Log("edited mapping", T(now));
    }
    public static void Remove(KeyEvent k, DateTimeOffset? now = null) { k.Dismissed = true; k.Log("removed", T(now)); }
}
