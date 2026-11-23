using System.Text.Json.Nodes;
using EzForensics.Core.Assistant;
using EzForensics.Core.Knowledge;
using FluentAssertions;

namespace EzForensics.Core.Tests.Knowledge;

public class KnowledgeAssistantTests : IDisposable
{
    private readonly TestVault _v = new();
    private readonly KnowledgeService _svc;

    public KnowledgeAssistantTests()
    {
        using (var store = new KnowledgeStore(_v.DbPath))
            new KnowledgeImporter(store, new FakeEmbedder()).ImportFolderAsync(_v.Vault, "test", null, default).GetAwaiter().GetResult();
        _svc = new KnowledgeService(_v.DbPath, () => new FakeEmbedder());
    }

    public void Dispose() { _svc.Dispose(); _v.Dispose(); }

    private static ToolCall Call(string name, JsonObject input) => new("1", name, input);
    private static ToolOutcome Run(IKnowledgeBase kb, string tool, JsonObject input) =>
        KnowledgeTools.Create(kb).First(t => t.Spec.Name == tool).RunAsync(Call(tool, input), default).GetAwaiter().GetResult();

    [Fact]
    public void Service_is_empty_and_creates_nothing_when_the_db_is_missing()
    {
        var path = Path.Combine(_v.Root, "none", "knowledge.db");
        var s = new KnowledgeService(path, () => null);
        s.IsEmpty.Should().BeTrue();
        s.Search("x", 3).Hits.Should().BeEmpty();
        File.Exists(path).Should().BeFalse();
        Directory.Exists(Path.GetDirectoryName(path)).Should().BeFalse();
    }

    [Fact]
    public void Notes_block_holds_accepted_notes_in_order_capped_and_skips_others()
    {
        List<(long Id, string Title, string Path, long SourceId)> notes;
        using (var store = new KnowledgeStore(_v.DbPath))
        {
            notes = store.Notes(status: "accepted");
            store.SetStatus(notes[2].Id, "rejected", "t", "now");
        }
        _svc.Invalidate();

        var block = _svc.NotesBlock(new[] { notes[1].Id, notes[0].Id, notes[2].Id, 999_999 }, 4800)!;

        block.Should().StartWith("<knowledge>\n").And.EndWith("</knowledge>\n");
        block.IndexOf($"[{notes[1].Title}]", StringComparison.Ordinal).Should().BeLessThan(block.IndexOf($"[{notes[0].Title}]", StringComparison.Ordinal));
        block.Should().NotContain($"[{notes[2].Title}]");
        _svc.NotesBlock(new[] { notes[0].Id }, 200)!.Length.Should().BeLessThanOrEqualTo(200);
        _svc.NotesBlock(new[] { notes[2].Id }, 4800).Should().BeNull();
    }

    [Fact]
    public void Context_pinned_knowledge_replaces_the_search()
    {
        var ctx = new AssistantContext(Strong());
        var text = ctx.Build(SpectrePage.Any, null, null, "Tell me about kerberoasting", pinnedKnowledge: "<knowledge>\n[Mine]\nx\n</knowledge>\n");
        text.Should().Contain("[Mine]").And.NotContain("[Kerberoasting");
    }

    [Fact]
    public void Search_tool_lists_hits_with_headings_and_related()
    {
        _svc.IsEmpty.Should().BeFalse();
        var o = Run(_svc, "kb_search", new JsonObject { ["query"] = "prefetch execution evidence" });
        o.IsError.Should().BeFalse();
        o.Text.Should().Contain("[Prefetch").And.Contain("›");
        o.Text.Length.Should().BeLessThanOrEqualTo(KnowledgeTools.MaxSearchChars + 5);
    }

    [Fact]
    public void Search_tool_caps_hits_and_output()
    {
        var big = new FakeKb(Enumerable.Range(0, 10).Select(i => new KnowledgeHit(i, "Note" + i, "p", "H", new string('x', 5000), 1, true)).ToList());
        var o = Run(big, "kb_search", new JsonObject { ["query"] = "x", ["k"] = 50 });
        o.Text.Length.Should().BeLessThanOrEqualTo(KnowledgeTools.MaxSearchChars + 5);
        big.LastK.Should().Be(KnowledgeTools.MaxHits);
    }

    [Fact]
    public void Get_note_is_case_insensitive_and_suggests_when_missing()
    {
        var ok = Run(_svc, "kb_get_note", new JsonObject { ["title"] = "pReFeTcH" });
        ok.IsError.Should().BeFalse();
        ok.Text.Should().StartWith("Prefetch").And.Contain("Prefetch files");
        var miss = Run(_svc, "kb_get_note", new JsonObject { ["title"] = "Prefetch files" });
        miss.IsError.Should().BeTrue();
        miss.Text.Should().Contain("Did you mean").And.Contain("Prefetch");
    }

    [Fact]
    public void Get_note_body_is_capped()
    {
        var kb = new FakeKb(new List<KnowledgeHit>()) { Note = new KnowledgeNote("Big", "p", new string('y', 20000)) };
        Run(kb, "kb_get_note", new JsonObject { ["title"] = "big" }).Text.Length.Should().BeLessThan(KnowledgeTools.MaxNoteChars + 20);
    }

    [Fact]
    public void Related_lists_linked_titles()
    {
        var o = Run(_svc, "kb_related", new JsonObject { ["title"] = "Windows MOC" });
        o.IsError.Should().BeFalse();
        o.Text.Should().Contain("Prefetch");
    }

    [Fact]
    public void Step_lines_are_readable()
    {
        var c = Call("kb_search", new JsonObject { ["query"] = "kerberoasting" });
        SpectrePageTools.Describe(c, new ToolOutcome("x", false)).Should().Be("Searched the knowledge base for kerberoasting");
    }

    [Fact]
    public void Knowledge_tools_stay_on_every_page()
    {
        var tools = KnowledgeTools.Create(_svc);
        foreach (var page in Enum.GetValues<SpectrePage>())
            SpectreSkills.ForPage(page, tools).Should().HaveCount(3);
    }

    private static FakeKb Strong() => new(new List<KnowledgeHit> { new(1, "Kerberoasting", "k.md", "Overview", "Service tickets cracked offline.", 0.03, true) });

    [Theory]
    [InlineData("what is kerberoasting?", true)]
    [InlineData("filter powershell", false)]
    [InlineData("flag rows 12 and 40", false)]
    [InlineData("what does this row mean?", false)]
    public void Context_attaches_knowledge_only_for_general_questions(string ask, bool expected)
    {
        var logs = new List<string>();
        var msg = new AssistantContext(Strong()).Build(SpectrePage.Analyze, null, null, ask, logs.Add);
        msg.Contains("<knowledge>").Should().Be(expected);
        if (expected)
        {
            msg.Should().Contain("[Kerberoasting › Overview]");
            logs.Should().Contain(l => l.StartsWith("Spectre: attached knowledge 1 hits (Kerberoasting)"));
        }
    }

    [Fact]
    public void Context_skips_empty_weak_and_missing_knowledge()
    {
        new AssistantContext(new FakeKb(new List<KnowledgeHit>()) { Empty = true }).Build(SpectrePage.Analyze, null, null, "what is kerberoasting?").Should().NotContain("<knowledge>");
        var weak = new FakeKb(new List<KnowledgeHit> { new(1, "T", "p", "H", "t", 0.01, false) });
        new AssistantContext(weak).Build(SpectrePage.Analyze, null, null, "what is kerberoasting?").Should().NotContain("<knowledge>");
        new AssistantContext().Build(SpectrePage.Analyze, null, null, "what is kerberoasting?").Should().NotContain("<knowledge>");
    }

    [Fact]
    public void Context_block_is_capped()
    {
        var kb = new FakeKb(Enumerable.Range(0, 4).Select(i => new KnowledgeHit(i, "N" + i, "p", "H", new string('z', 3000), 1, i == 0)).ToList());
        var msg = new AssistantContext(kb).Build(SpectrePage.Analyze, null, null, "what is zzz?");
        var start = msg.IndexOf("<knowledge>", StringComparison.Ordinal);
        (msg.IndexOf("</knowledge>", StringComparison.Ordinal) - start).Should().BeLessThanOrEqualTo(AssistantContext.MaxKnowledgeChars + 5);
    }

    [Fact]
    public void Real_store_flags_a_matching_question_as_strong()
    {
        _svc.Search("what is prefetch execution evidence", 4).Hits[0].Strong.Should().BeTrue();
        _svc.Search("quantum banana smoothie", 4).Hits.Should().OnlyContain(h => !h.Strong);
    }

    [Fact]
    public void Protocol_splits_the_knowledge_block()
    {
        var (q, blocks) = TextToolProtocol.SplitContext("<knowledge>\n[A › B]\ntext\n</knowledge>\nwhat is kerberoasting?");
        q.Should().Be("what is kerberoasting?");
        blocks.Should().ContainSingle().Which.Tag.Should().Be("knowledge");
        TextToolProtocol.StripContextTags("<knowledge>Yes.</knowledge>").Should().Be("Yes.");
    }

    private sealed class FakeKb(IReadOnlyList<KnowledgeHit> hits) : IKnowledgeBase
    {
        public bool Empty { get; init; }
        public KnowledgeNote? Note { get; init; }
        public int LastK { get; private set; }
        public bool IsEmpty => Empty;
        public KnowledgeResult Search(string query, int k) { LastK = k; return new KnowledgeResult(hits.Take(k).ToList(), Array.Empty<string>()); }
        public KnowledgeNote? GetNote(string title) => Note;
        public IReadOnlyList<string> Related(string title) => Array.Empty<string>();
        public IReadOnlyList<string> Suggest(string title) => Array.Empty<string>();
    }
}
