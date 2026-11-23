using System.Text.Json;
using System.Text.RegularExpressions;

namespace EzForensics.Core.Spectre;

/// <summary>
/// The ATT&amp;CK techniques the Findings editor suggests, each with its name and main tactic (the tactic follows the technique), and the id check
/// the engine applies to an edit (<c>T1234</c> or <c>T1234.567</c>). Not the whole matrix: other valid ids are accepted as free text.
/// </summary>
public static class TechniqueCatalog
{
    private static readonly Regex IdRegex = new(@"^T\d{4}(\.\d{3})?$", RegexOptions.CultureInvariant | RegexOptions.IgnoreCase);

    private sealed record Row(string Id, string Name, string Tactic, string Search);

    private sealed class JsonTechnique
    {
        public string Id { get; set; } = "";
        public string Name { get; set; } = "";
        public List<string> Tactics { get; set; } = new();
    }

    // Embedded Resources/mitre/enterprise-techniques.json: {"source": "...", "techniques": [{id,name,tactics:[...]}]} (a bare array also works).
    // MITRE ATT&CK Enterprise (about 700 entries); read once.

    /// <summary>Ids the app used before the full matrix was bundled and that the matrix no longer lists (revoked or restructured); kept so they still resolve.</summary>
    private static JsonTechnique[] Legacy => LegacyList;
    private static readonly JsonTechnique[] LegacyList =
    {
        new() { Id = "T1070.001", Name = "Indicator removal: clear Windows event logs", Tactics = { "Defense Evasion" } },
    };

    /// <summary>ATT&amp;CK v19+ splits "Defense Evasion" into Stealth and Defense Impairment; the app's stages (story, radar) still use the one name.</summary>
    /// <summary>
    /// The tactic a technique shows under when it has several (T1078 spans four): the earliest in ATT&amp;CK kill-chain order
    /// (Initial Access first), not the order the data file happens to list them in.
    /// </summary>
    private static string FirstInKillChain(IEnumerable<string> tactics)
    {
        var all = tactics.Select(Stage).Where(s => s.Length > 0).ToList();
        if (all.Count == 0) return "";
        return all.OrderBy(s => { var i = StoryGraphics.OrderOf(s); return i; }).ThenBy(s => all.IndexOf(s)).First();
    }
    private static string Stage(string tactic) => tactic is "Stealth" or "Defense Impairment" ? "Defense Evasion" : tactic;

    private static readonly Row[] Rows = Load();

    private static Row[] Load()
    {
        var asm = typeof(TechniqueCatalog).Assembly;
        var name = asm.GetManifestResourceNames().FirstOrDefault(n => n.EndsWith("enterprise-techniques.json", StringComparison.OrdinalIgnoreCase))
            ?? throw new InvalidOperationException("Embedded resource mitre/enterprise-techniques.json not found.");
        using var s = asm.GetManifestResourceStream(name)!;
        using var doc = JsonDocument.Parse(s);
        var arr = doc.RootElement.ValueKind == JsonValueKind.Array ? doc.RootElement : doc.RootElement.GetProperty("techniques");
        var list = (arr.Deserialize<List<JsonTechnique>>(new JsonSerializerOptions { PropertyNameCaseInsensitive = true }) ?? new()).ToList();
        var have = list.Select(t => t.Id.Trim().ToUpperInvariant()).ToHashSet();
        list.AddRange(Legacy.Where(l => !have.Contains(l.Id)));
        return list.Where(t => !string.IsNullOrWhiteSpace(t.Id))
            .Select(t => new Row(t.Id.Trim().ToUpperInvariant(), t.Name.Trim(), FirstInKillChain(t.Tactics),
                (t.Id.Trim() + " " + t.Name.Trim() + " " + string.Join(' ', t.Tactics)).ToLowerInvariant()))
            .GroupBy(r => r.Id).Select(g => g.First()).ToArray();
    }

    private static readonly IReadOnlyDictionary<string, Row> ById = Rows.ToDictionary(k => k.Id, StringComparer.OrdinalIgnoreCase);

    /// <summary>The suggested technique ids, in id order.</summary>
    public static IReadOnlyList<string> KnownIds { get; } = Rows.Select(k => k.Id).OrderBy(i => i, StringComparer.Ordinal).ToList();

    /// <summary>Every catalog technique as (id, name), for matching names in free text.</summary>
    public static IReadOnlyList<(string Id, string Name)> Entries { get; } = Rows.Select(k => (k.Id, k.Name)).ToList();

    /// <summary>One picker row: <c>Display</c> is "T1036 · Masquerading — Defense Evasion".</summary>
    public sealed record Match(string Id, string Name, string Tactic)
    {
        public string Display => Tactic.Length > 0 ? $"{Id} · {Name} — {Tactic}" : $"{Id} · {Name}";
        public override string ToString() => Display;
    }

    /// <summary>
    /// Type-ahead: techniques whose id, name or tactic contains <paramref name="query"/> (case-insensitive substring), id matches first, then name
    /// prefix matches, then the rest, each in id order. An empty query returns the first <paramref name="max"/> entries.
    /// </summary>
    public static IReadOnlyList<Match> Search(string? query, int max = 30)
    {
        var q = (query ?? "").Trim().ToLowerInvariant();
        IEnumerable<Row> hits = q.Length == 0
            ? Rows
            : Rows.Where(r => r.Search.Contains(q, StringComparison.Ordinal))
                  .OrderBy(r => r.Id.StartsWith(q, StringComparison.OrdinalIgnoreCase) ? 0 : r.Name.StartsWith(q, StringComparison.OrdinalIgnoreCase) ? 1 : 2);
        return hits.Take(max).Select(r => new Match(r.Id, r.Name, r.Tactic)).ToList();
    }

    /// <summary>The technique id of a picker choice ("T1036 · Masquerading — Defense Evasion" gives "T1036"); other text is returned trimmed.</summary>
    public static string IdOfChoice(string? text)
    {
        var t = (text ?? "").Trim();
        var i = t.IndexOf(" · ", StringComparison.Ordinal);
        return i > 0 ? t[..i].Trim() : t;
    }

    /// <summary>One technique id: <c>T1059</c> or <c>T1059.001</c> (any case).</summary>
    public static bool IsValid(string? id) => !string.IsNullOrWhiteSpace(id) && IdRegex.IsMatch(id.Trim());

    /// <summary>The entries of a comma / space separated list that are not valid ids.</summary>
    public static IReadOnlyList<string> Invalid(IEnumerable<string> ids) => ids.Where(i => !IsValid(i)).ToList();

    /// <summary>"Spearphishing attachment" for T1566.001; null for an id that is not in the list (a sub-technique falls back to its parent).</summary>
    public static string? Name(string? id) => Find(id)?.Name;

    /// <summary>The plain tactic of a technique ("Execution"); null for an unknown id (a sub-technique falls back to its parent).</summary>
    public static string? TacticOf(string? id) => Find(id)?.Tactic;

    private static Row? Find(string? id)
    {
        if (string.IsNullOrWhiteSpace(id)) return null;
        id = id.Trim();
        if (ById.TryGetValue(id, out var hit)) return hit;
        var dot = id.IndexOf('.');
        return dot > 0 && ById.TryGetValue(id[..dot], out var parent) ? parent : null;
    }
}
