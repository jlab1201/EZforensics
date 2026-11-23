using System.Text.Json;
using EzForensics.Core.Spectre;

namespace EzForensics.Core.Knowledge;

/// <summary>One dot in the Brain. Key is <c>c:&lt;kind&gt;:&lt;key&gt;</c> for a concept and <c>n:&lt;noteId&gt;</c> for a document. Group: source name (document) or tactic (technique).
/// X, Y, Z: a saved (or seeded) 3D position; all null when the page should place the node itself.</summary>
public sealed record GraphNode(string Key, string Kind, string Label, string? Group, double Size, long? NoteId, double? X, double? Y, double? Z = null);

/// <summary>A line between two nodes. Kind: mention, wikilink, moc, semantic, cooccur, category (document to category hub) or structure (distilled document to parent).</summary>
public sealed record GraphLink(string Source, string Target, string Kind, double Weight);

public sealed record GraphData(IReadOnlyList<GraphNode> Nodes, IReadOnlyList<GraphLink> Links, bool DocsHidden);

public sealed record GraphOptions(int MaxNodes = 3000, string? FocusKey = null);

/// <summary>Builds the Brain graph from the accepted notes of the knowledge store.</summary>
public static class KnowledgeGraph
{
    private const int FewDocsLimit = 100;
    private const double SeedRadius = 24;

    private static readonly HashSet<string> DocLinkKinds = new(StringComparer.Ordinal)
    {
        LinkKinds.Wikilink, LinkKinds.Moc, LinkKinds.Semantic, LinkKinds.Mention,
    };

    private static readonly JsonSerializerOptions Json = new() { PropertyNamingPolicy = JsonNamingPolicy.CamelCase };

    public static string ToJson(GraphData g) => JsonSerializer.Serialize(g, Json);

    public static GraphData Build(KnowledgeStore s, GraphOptions? o = null)
    {
        o ??= new GraphOptions();
        var sources = s.Sources().ToDictionary(x => x.Id, x => x.Name);
        var docs = s.Notes(status: "accepted");
        var docIds = docs.Select(d => d.Id).ToHashSet();

        // concept -> accepted docs, only counting accepted documents
        var concepts = s.Concepts().ToDictionary(c => c.Id);
        var docConcepts = new Dictionary<long, List<(long ConceptId, int Count)>>();
        var conceptDocs = new Dictionary<long, List<long>>();
        foreach (var (noteId, conceptId, count) in s.NoteConcepts())
        {
            if (!docIds.Contains(noteId) || !concepts.ContainsKey(conceptId)) continue;
            (docConcepts.TryGetValue(noteId, out var l) ? l : docConcepts[noteId] = new()).Add((conceptId, count));
            (conceptDocs.TryGetValue(conceptId, out var d) ? d : conceptDocs[conceptId] = new()).Add(noteId);
        }

        string ConceptKey(long id) => $"c:{concepts[id].Kind}:{concepts[id].Key}";
        static string DocKey(long id) => $"n:{id}";

        var conceptIds = conceptDocs.Keys.OrderBy(x => x).ToList();
        var links = new List<GraphLink>();

        // document -> concept
        foreach (var d in docs)
            if (docConcepts.TryGetValue(d.Id, out var cs))
                foreach (var (cid, count) in cs.OrderBy(x => x.ConceptId))
                    links.Add(new GraphLink(DocKey(d.Id), ConceptKey(cid), LinkKinds.Mention, count));

        // document -> document (both ends accepted)
        var seen = new HashSet<(long, long, string)>();
        foreach (var (src, dst, kind) in s.Links())
            if (docIds.Contains(src) && docIds.Contains(dst) && DocLinkKinds.Contains(kind) && seen.Add((src, dst, kind)))
                links.Add(new GraphLink(DocKey(src), DocKey(dst), kind, 1));

        // document -> category hub (labelled documents only) and distilled document -> parent (from parent_id: a vault re-import wipes stored structure links)
        var meta = s.NotesByStatus("accepted");
        var catCounts = new Dictionary<string, int>(StringComparer.Ordinal);
        foreach (var d in meta)
        {
            if (!string.IsNullOrEmpty(d.Category))
            {
                catCounts[d.Category] = catCounts.GetValueOrDefault(d.Category) + 1;
                links.Add(new GraphLink(DocKey(d.Id), "cat:" + d.Category, "category", 1));
            }
            if (d.ParentId is { } pid && docIds.Contains(pid) && pid != d.Id)
                links.Add(new GraphLink(DocKey(d.Id), DocKey(pid), LinkKinds.Structure, 1));
        }

        // concept <-> concept by shared accepted documents
        var threshold = docs.Count < FewDocsLimit ? 2 : 3;
        var pairs = new Dictionary<(long, long), int>();
        foreach (var cs in docConcepts.Values)
        {
            var ids = cs.Select(x => x.ConceptId).Distinct().OrderBy(x => x).ToList();
            for (var i = 0; i < ids.Count; i++)
                for (var j = i + 1; j < ids.Count; j++)
                    pairs[(ids[i], ids[j])] = pairs.GetValueOrDefault((ids[i], ids[j])) + 1;
        }
        foreach (var (pair, n) in pairs.Where(p => p.Value >= threshold).OrderBy(p => p.Key.Item1).ThenBy(p => p.Key.Item2))
            links.Add(new GraphLink(ConceptKey(pair.Item1), ConceptKey(pair.Item2), "cooccur", n));

        // nodes
        var nodes = new List<GraphNode>();
        foreach (var cid in conceptIds)
        {
            var c = concepts[cid];
            var group = c.Kind == "technique" ? TechniqueCatalog.TacticOf(c.Key) : null;
            nodes.Add(new GraphNode(ConceptKey(cid), c.Kind, c.Label, group, Math.Log2(1 + conceptDocs[cid].Count), null, null, null));
        }
        foreach (var (cat, count) in catCounts.OrderBy(x => x.Key, StringComparer.Ordinal))
            nodes.Add(new GraphNode("cat:" + cat, "category", cat, null, count, null, null, null));
        var distilled = meta.Where(m => m.ParentId != null).Select(m => m.Id).ToHashSet();
        var docNodes = docs.Select(d => new GraphNode(DocKey(d.Id), "doc", d.Title,
            distilled.Contains(d.Id) ? "distilled" : sources.GetValueOrDefault(d.SourceId), 1, d.Id, null, null)).ToList();

        var hidden = nodes.Count + docNodes.Count > o.MaxNodes;
        if (hidden)
        {
            var keep = FocusDocs(o.FocusKey, links);
            docNodes = docNodes.Where(n => keep.Contains(n.Key)).ToList();
            var live = nodes.Select(n => n.Key).Concat(docNodes.Select(n => n.Key)).ToHashSet();
            links = links.Where(l => live.Contains(l.Source) && live.Contains(l.Target)).ToList();
        }
        nodes.AddRange(docNodes);

        return new GraphData(Seed(nodes, links, s.Layout()), links, hidden);
    }

    /// <summary>Document keys linked to the focus node (a concept's documents, or a document and its document neighbours).</summary>
    private static HashSet<string> FocusDocs(string? focusKey, List<GraphLink> links)
    {
        var keep = new HashSet<string>(StringComparer.Ordinal);
        if (string.IsNullOrEmpty(focusKey)) return keep;
        if (focusKey.StartsWith("n:", StringComparison.Ordinal)) keep.Add(focusKey);
        foreach (var l in links)
        {
            if (l.Source == focusKey && l.Target.StartsWith("n:", StringComparison.Ordinal)) keep.Add(l.Target);
            if (l.Target == focusKey && l.Source.StartsWith("n:", StringComparison.Ordinal)) keep.Add(l.Source);
        }
        return keep;
    }

    /// <summary>
    /// Fills saved 3D positions; a node without one is placed next to its strongest neighbour that has one. A row from the old 2D Brain
    /// (no z) counts as unset: a flat layout would stay flat in 3D.
    /// </summary>
    private static List<GraphNode> Seed(List<GraphNode> nodes, List<GraphLink> links, Dictionary<string, (double X, double Y, double? Z)> saved)
    {
        var pos = new Dictionary<string, (double X, double Y, double Z)>(StringComparer.Ordinal);
        foreach (var n in nodes) if (saved.TryGetValue(n.Key, out var p) && p.Z is { } z) pos[n.Key] = (p.X, p.Y, z);
        if (pos.Count == 0) return nodes;

        var neighbours = new Dictionary<string, List<(string Key, double Weight)>>(StringComparer.Ordinal);
        foreach (var l in links)
        {
            (neighbours.TryGetValue(l.Source, out var a) ? a : neighbours[l.Source] = new()).Add((l.Target, l.Weight));
            (neighbours.TryGetValue(l.Target, out var b) ? b : neighbours[l.Target] = new()).Add((l.Source, l.Weight));
        }

        // repeat so that a node can follow a neighbour that was itself just seeded
        var pending = nodes.Where(n => !pos.ContainsKey(n.Key)).Select(n => n.Key).ToList();
        var progress = true;
        while (pending.Count > 0 && progress)
        {
            progress = false;
            var round = new Dictionary<string, (double X, double Y, double Z)>(StringComparer.Ordinal);
            foreach (var key in pending)
            {
                if (!neighbours.TryGetValue(key, out var ns)) continue;
                var cands = ns.Where(x => pos.ContainsKey(x.Key)).OrderByDescending(x => x.Weight).ThenBy(x => x.Key, StringComparer.Ordinal).ToList();
                if (cands.Count == 0) continue;
                var (bx, by, bz) = pos[cands[0].Key];
                // a stable direction on the sphere around the neighbour
                var h = StableHash(key);
                var angle = h % 3600 / 3600.0 * 2 * Math.PI;
                var u = (h / 3600 % 2000) / 1000.0 - 1;   // cos of the polar angle, -1..1
                var ring = Math.Sqrt(1 - u * u);
                round[key] = (bx + SeedRadius * ring * Math.Cos(angle), by + SeedRadius * ring * Math.Sin(angle), bz + SeedRadius * u);
            }
            foreach (var (k, p) in round) { pos[k] = p; progress = true; }
            pending.RemoveAll(round.ContainsKey);
        }

        return nodes.Select(n => pos.TryGetValue(n.Key, out var p) ? n with { X = p.X, Y = p.Y, Z = p.Z } : n).ToList();
    }

    private static int StableHash(string s)
    {
        var h = 17;
        foreach (var c in s) h = unchecked(h * 31 + c);
        return h & 0x7fffffff;
    }
}
