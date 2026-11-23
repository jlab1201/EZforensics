using System.Text.Json;
using System.Text.RegularExpressions;
using EzForensics.Core.Spectre;

namespace EzForensics.Core.Knowledge;

/// <summary>A concept seen in the text. Kind: technique, tactic, artifact, eventid or tool. Count = number of chunks that mention it.</summary>
public sealed record ConceptHit(string Kind, string Key, string Label, int Count);

/// <summary>Finds forensic concepts (ATT&amp;CK techniques and tactics, artifacts, event IDs, tools) in chunk texts. Pure; the artifact and tool lists are in Resources/knowledge/concepts.json.</summary>
public static class ConceptExtractor
{
    private sealed record Term(string Key, string Label, Regex Pattern);
    private sealed class Entry { public string Key { get; set; } = ""; public string Label { get; set; } = ""; public List<string>? Aliases { get; set; } }
    private sealed class ConceptFile { public List<Entry> Artifacts { get; set; } = new(); public List<Entry> Tools { get; set; } = new(); }

    private static readonly Regex TechniqueId = new(@"\bT\d{4}(\.\d{3})?\b", RegexOptions.CultureInvariant | RegexOptions.IgnoreCase);
    private static readonly Regex EventId = new(@"\b(?:event\s*id|eid|eventid)\s*[:#]?\s*(\d{3,5})\b", RegexOptions.CultureInvariant | RegexOptions.IgnoreCase);

    private static readonly Lazy<ConceptFile> File = new(Load);
    private static readonly Lazy<IReadOnlyList<Term>> Artifacts = new(() => Terms(File.Value.Artifacts));
    private static readonly Lazy<IReadOnlyList<Term>> Tools = new(() => Terms(File.Value.Tools));
    private static readonly Lazy<IReadOnlyList<Term>> TechniqueNames = new(() =>
        TechniqueCatalog.Entries.Select(e => new Term(e.Id.ToUpperInvariant(), e.Name, WholeWord(e.Name))).ToList());

    /// <summary>Every artifact label, key and alias in concepts.json (for the drift test).</summary>
    public static IReadOnlyList<string> KnownArtifactTerms() => AllTerms(File.Value.Artifacts);

    /// <summary>Every tool label, key and alias in concepts.json (for the drift test).</summary>
    public static IReadOnlyList<string> KnownToolTerms() => AllTerms(File.Value.Tools);

    public static IReadOnlyList<ConceptHit> Extract(IEnumerable<string> chunkTexts)
    {
        var counts = new Dictionary<(string Kind, string Key), (string Label, int Count)>();
        void Add(HashSet<(string, string)> seen, string kind, string key, string label)
        {
            if (!seen.Add((kind, key))) return;
            counts[(kind, key)] = (label, counts.TryGetValue((kind, key), out var c) ? c.Count + 1 : 1);
        }

        foreach (var chunk in chunkTexts)
        {
            if (string.IsNullOrWhiteSpace(chunk)) continue;
            var seen = new HashSet<(string, string)>();

            var techniques = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            foreach (Match m in TechniqueId.Matches(chunk)) techniques.Add(m.Value.ToUpperInvariant());
            foreach (var t in TechniqueNames.Value) if (t.Pattern.IsMatch(chunk)) techniques.Add(t.Key);
            foreach (var id in techniques)
            {
                Add(seen, "technique", id, TechniqueCatalog.Name(id) is { } n ? $"{id} {n}" : id);
                if (TechniqueCatalog.TacticOf(id) is { } tactic) Add(seen, "tactic", tactic.ToLowerInvariant(), tactic);
            }

            foreach (var t in Artifacts.Value) if (t.Pattern.IsMatch(chunk)) Add(seen, "artifact", t.Key, t.Label);
            foreach (var t in Tools.Value) if (t.Pattern.IsMatch(chunk)) Add(seen, "tool", t.Key, t.Label);
            foreach (Match m in EventId.Matches(chunk)) Add(seen, "eventid", m.Groups[1].Value, "Event ID " + m.Groups[1].Value);
        }

        return counts
            .Select(kv => new ConceptHit(kv.Key.Kind, kv.Key.Key, kv.Value.Label, kv.Value.Count))
            .OrderBy(h => h.Kind, StringComparer.Ordinal).ThenByDescending(h => h.Count).ThenBy(h => h.Key, StringComparer.Ordinal)
            .ToList();
    }

    private static Regex WholeWord(string term) =>
        new(@"(?<!\w)" + Regex.Escape(term) + @"(?!\w)", RegexOptions.CultureInvariant | RegexOptions.IgnoreCase | RegexOptions.Compiled);

    private static IReadOnlyList<Term> Terms(IEnumerable<Entry> entries) =>
        entries.Select(e =>
        {
            var words = new[] { e.Label }.Concat(e.Aliases ?? new()).Where(w => w.Length > 0).Distinct(StringComparer.OrdinalIgnoreCase);
            return new Term(e.Key, e.Label, new Regex("(?:" + string.Join("|", words.Select(w => @"(?<!\w)" + Regex.Escape(w) + @"(?!\w)")) + ")",
                RegexOptions.CultureInvariant | RegexOptions.IgnoreCase | RegexOptions.Compiled));
        }).ToList();

    private static IReadOnlyList<string> AllTerms(IEnumerable<Entry> entries) =>
        entries.SelectMany(e => new[] { e.Key, e.Label }.Concat(e.Aliases ?? new())).ToList();

    private static ConceptFile Load()
    {
        var asm = typeof(ConceptExtractor).Assembly;
        var name = asm.GetManifestResourceNames().First(n => n.EndsWith(".Resources.knowledge.concepts.json", StringComparison.Ordinal));
        using var s = asm.GetManifestResourceStream(name)!;
        return JsonSerializer.Deserialize<ConceptFile>(s, new JsonSerializerOptions { PropertyNameCaseInsensitive = true }) ?? new ConceptFile();
    }
}
