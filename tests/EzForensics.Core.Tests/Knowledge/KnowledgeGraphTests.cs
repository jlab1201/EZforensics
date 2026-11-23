using EzForensics.Core.Knowledge;
using FluentAssertions;
using Microsoft.Data.Sqlite;

namespace EzForensics.Core.Tests.Knowledge;

public class KnowledgeGraphTests : IDisposable
{
    private readonly string _dir = Path.Combine(Path.GetTempPath(), "ezf-kg-" + Guid.NewGuid().ToString("N"));
    private readonly KnowledgeStore _s;
    private readonly long _src;

    public KnowledgeGraphTests()
    {
        Directory.CreateDirectory(_dir);
        _s = new KnowledgeStore(Path.Combine(_dir, "k.db"));
        _src = _s.UpsertSource("vault-a", "vault", "p");
    }

    public void Dispose()
    {
        _s.Dispose();
        SqliteConnection.ClearAllPools();
        try { Directory.Delete(_dir, true); } catch (IOException) { }
    }

    private long Doc(string title, string status = "accepted") =>
        _s.InsertNote(_src, new VaultNote(title + ".md", title, "t", Array.Empty<string>(), "h-" + title, "body " + title, Array.Empty<VaultLink>()),
            new[] { new ChunkPiece(0, "", "body " + title, "body " + title) }, null, status);

    private long Concept(string kind, string key) => _s.UpsertConcept(kind, key, key);

    private void Mention(long note, params long[] concepts) =>
        _s.ReplaceNoteConcepts(note, concepts.ToDictionary(c => c, _ => 1));

    [Fact]
    public void Build_FixtureStore_CountsMatch()
    {
        long a = Doc("A"), b = Doc("B"), c = Doc("C"), p = Doc("P", "pending");
        long t1 = Concept("technique", "T1059.001"), t2 = Concept("tool", "mimikatz"), t3 = Concept("eventid", "4624"), t4 = Concept("artifact", "prefetch");
        Mention(a, t1, t2);
        Mention(b, t1, t3);
        Mention(c, t2);
        Mention(p, t4, t1);
        _s.ReplaceLinks(_src, new[] { (a, b, "wikilink"), (p, a, "wikilink"), (b, p, "moc") });

        var g = KnowledgeGraph.Build(_s);

        g.DocsHidden.Should().BeFalse();
        g.Nodes.Count(n => n.Kind == "doc").Should().Be(3);
        g.Nodes.Count(n => n.Kind != "doc").Should().Be(3);
        g.Nodes.Should().NotContain(n => n.Key == $"n:{p}");
        g.Nodes.Should().NotContain(n => n.Kind == "artifact");
        g.Links.Should().NotContain(l => l.Source == $"n:{p}" || l.Target == $"n:{p}");
        g.Links.Should().ContainSingle(l => l.Kind == "wikilink" && l.Source == $"n:{a}" && l.Target == $"n:{b}");
        g.Links.Count(l => l.Kind == "mention").Should().Be(5);
        var t = g.Nodes.Single(n => n.Key == "c:technique:T1059.001");
        t.Group.Should().Be("Execution");
        t.Size.Should().BeApproximately(1.58496, 0.001); // log2(1 + 2 docs)
        g.Nodes.Single(n => n.Key == "c:tool:mimikatz").Group.Should().BeNull();
        var d = g.Nodes.Single(n => n.Key == $"n:{a}");
        d.Group.Should().Be("vault-a");
        d.NoteId.Should().Be(a);
        d.Label.Should().Be("A");
    }

    [Fact]
    public void Graph_has_category_hub_per_used_category_linked_to_docs()
    {
        long a = Doc("A"), b = Doc("B"), c = Doc("C"), p = Doc("P", "pending");
        _s.SetLabels(a, "s", "Memory", "Reference", Array.Empty<string>(), "m", "t");
        _s.SetLabels(b, "s", "Memory", "Reference", Array.Empty<string>(), "m", "t");
        _s.SetLabels(c, "s", "Network", "Reference", Array.Empty<string>(), "m", "t");
        _s.SetLabels(p, "s", "Tools", "Reference", Array.Empty<string>(), "m", "t");

        var g = KnowledgeGraph.Build(_s);

        g.Nodes.Where(n => n.Kind == "category").Select(n => n.Key).Should().BeEquivalentTo("cat:Memory", "cat:Network");
        g.Nodes.Single(n => n.Key == "cat:Memory").Size.Should().Be(2);
        g.Nodes.Single(n => n.Key == "cat:Memory").Label.Should().Be("Memory");
        g.Links.Should().Contain(l => l.Kind == "category" && l.Source == $"n:{a}" && l.Target == "cat:Memory");
        g.Links.Should().Contain(l => l.Kind == "category" && l.Source == $"n:{c}" && l.Target == "cat:Network");
    }

    [Fact]
    public void Unlabelled_docs_get_no_category_link()
    {
        Doc("A");
        var g = KnowledgeGraph.Build(_s);
        g.Nodes.Should().NotContain(n => n.Kind == "category");
        g.Links.Should().NotContain(l => l.Kind == "category");
    }

    [Fact]
    public void Category_hubs_stay_when_documents_are_hidden()
    {
        var a = Doc("A");
        _s.SetLabels(a, "s", "Memory", "Reference", Array.Empty<string>(), "m", "t");
        var g = KnowledgeGraph.Build(_s, new GraphOptions(MaxNodes: 1));
        g.DocsHidden.Should().BeTrue();
        g.Nodes.Should().Contain(n => n.Key == "cat:Memory");
        g.Links.Should().NotContain(l => l.Kind == "category");
    }

    [Fact]
    public void Distilled_doc_links_parent()
    {
        long parent = Doc("Big"), child = Doc("Small"), pending = Doc("Pend", "pending"), orphan = Doc("Orphan");
        _s.SetParent(child, parent);
        _s.SetParent(orphan, pending);

        var g = KnowledgeGraph.Build(_s);

        g.Links.Should().ContainSingle(l => l.Kind == "structure" && l.Source == $"n:{child}" && l.Target == $"n:{parent}");
        g.Links.Should().NotContain(l => l.Kind == "structure" && l.Source == $"n:{orphan}");
        g.Nodes.Single(n => n.Key == $"n:{child}").Group.Should().Be("distilled");
        g.Nodes.Single(n => n.Key == $"n:{parent}").Group.Should().Be("vault-a");
    }

    [Fact]
    public void Build_Cooccur_Threshold2UnderHundredDocs()
    {
        long a = Doc("A"), b = Doc("B"), c = Doc("C");
        long x = Concept("tool", "x"), y = Concept("tool", "y"), z = Concept("tool", "z");
        Mention(a, x, y, z);
        Mention(b, x, y);
        Mention(c, x);

        var g = KnowledgeGraph.Build(_s);

        var co = g.Links.Where(l => l.Kind == "cooccur").ToList();
        co.Should().ContainSingle(); // x-y share 2 docs; x-z and y-z share only 1
        co[0].Source.Should().Be("c:tool:x");
        co[0].Target.Should().Be("c:tool:y");
        co[0].Weight.Should().Be(2);
    }

    [Fact]
    public void Build_Cooccur_Threshold3AtHundredDocs()
    {
        long x = Concept("tool", "x"), y = Concept("tool", "y");
        for (var i = 0; i < 100; i++)
        {
            var d = Doc("D" + i);
            if (i < 2) Mention(d, x, y);
        }
        KnowledgeGraph.Build(_s).Links.Should().NotContain(l => l.Kind == "cooccur");
        _s.ReplaceNoteConcepts(_s.Notes()[2].Id, new Dictionary<long, int> { [x] = 1, [y] = 1 });
        KnowledgeGraph.Build(_s).Links.Should().ContainSingle(l => l.Kind == "cooccur" && l.Weight == 3);
    }

    [Fact]
    public void Build_OverMaxNodes_HidesDocs_KeepsFocusNeighbours()
    {
        long a = Doc("A"), b = Doc("B"), c = Doc("C"), d = Doc("D");
        long x = Concept("tool", "x"), y = Concept("tool", "y");
        Mention(a, x); Mention(b, x); Mention(c, y); Mention(d, y);

        var g = KnowledgeGraph.Build(_s, new GraphOptions(MaxNodes: 4, FocusKey: "c:tool:x"));

        g.DocsHidden.Should().BeTrue();
        g.Nodes.Where(n => n.Kind == "doc").Select(n => n.NoteId).Should().BeEquivalentTo(new long?[] { a, b });
        g.Nodes.Count(n => n.Kind != "doc").Should().Be(2);
        g.Links.Should().OnlyContain(l => g.Nodes.Any(n => n.Key == l.Source) && g.Nodes.Any(n => n.Key == l.Target));

        var none = KnowledgeGraph.Build(_s, new GraphOptions(MaxNodes: 4));
        none.DocsHidden.Should().BeTrue();
        none.Nodes.Should().NotContain(n => n.Kind == "doc");

        KnowledgeGraph.Build(_s, new GraphOptions(MaxNodes: 6)).DocsHidden.Should().BeFalse();
    }

    [Fact]
    public void Build_NewNodeSeededNearNeighbour()
    {
        long a = Doc("A");
        long x = Concept("tool", "x");
        Mention(a, x);
        _s.SaveLayout(new Dictionary<string, (double X, double Y, double? Z)> { ["c:tool:x"] = (100, 200, 300) });

        var g = KnowledgeGraph.Build(_s);

        var cx = g.Nodes.Single(n => n.Key == "c:tool:x");
        cx.X.Should().Be(100); cx.Y.Should().Be(200); cx.Z.Should().Be(300);
        var d = g.Nodes.Single(n => n.Key == $"n:{a}");
        d.X.Should().NotBeNull();
        d.Z.Should().NotBeNull();
        var dist = Math.Sqrt(Math.Pow(d.X!.Value - 100, 2) + Math.Pow(d.Y!.Value - 200, 2) + Math.Pow(d.Z!.Value - 300, 2));
        dist.Should().BeGreaterThan(1).And.BeLessThan(100);   // next to it, not on top of it

        // no saved positions at all: stay null
        _s.ClearLayout();
        KnowledgeGraph.Build(_s).Nodes.Should().OnlyContain(n => n.X == null && n.Y == null && n.Z == null);
    }

    [Fact]
    public void Build_Old2DLayout_CountsAsUnset()
    {
        long a = Doc("A");
        long x = Concept("tool", "x");
        Mention(a, x);
        _s.SaveLayout(new Dictionary<string, (double X, double Y)> { ["c:tool:x"] = (100, 200) });   // z NULL, from the 2D Brain

        KnowledgeGraph.Build(_s).Nodes.Should().OnlyContain(n => n.X == null && n.Y == null && n.Z == null);
    }

    [Fact]
    public void Build_EmptyStore_EmptyGraph()
    {
        var g = KnowledgeGraph.Build(_s);
        g.Nodes.Should().BeEmpty();
        g.Links.Should().BeEmpty();
        g.DocsHidden.Should().BeFalse();
    }

    [Fact]
    public void ToJson_IsCamelCase()
    {
        long a = Doc("A");
        Mention(a, Concept("tool", "x"));
        var json = KnowledgeGraph.ToJson(KnowledgeGraph.Build(_s));
        json.Should().Contain("\"nodes\"").And.Contain("\"links\"").And.Contain("\"docsHidden\"").And.Contain("\"noteId\"").And.Contain("\"key\"");
    }
}
