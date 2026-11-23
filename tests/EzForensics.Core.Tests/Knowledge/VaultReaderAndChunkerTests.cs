using EzForensics.Core.Knowledge;
using FluentAssertions;

namespace EzForensics.Core.Tests.Knowledge;

public class VaultReaderAndChunkerTests
{
    [Fact]
    public void Frontmatter_gives_title_and_tags()
    {
        var n = MarkdownVaultReader.Parse("Windows/Prefetch.md", "---\ntitle: Prefetch\ntags: [execution, windows]\n---\n\n# Other\n\nBody #inline");
        n.Title.Should().Be("Prefetch");
        n.Tags.Should().BeEquivalentTo("execution", "windows", "inline");
        n.Topic.Should().Be("Windows");
        n.Body.Should().StartWith("# Other");
    }

    [Fact]
    public void Title_falls_back_to_H1_then_file_name()
    {
        MarkdownVaultReader.Parse("a/x.md", "# Real Title\n\ntext").Title.Should().Be("Real Title");
        MarkdownVaultReader.Parse("a/file name.md", "no heading").Title.Should().Be("file name");
        MarkdownVaultReader.Parse("root.md", "text").Topic.Should().Be("");
    }

    [Fact]
    public void Wikilink_variants_resolve_to_the_target_title()
    {
        var n = MarkdownVaultReader.Parse("n.md", "# N\n\n[[A]] [[B|alias]] [[C#Heading]] [[D#h|alias]] [[A]]\n```\n[[InCode]]\n```");
        n.Links.Select(l => l.Target).Should().Equal("A", "B", "C", "D");
        n.Links.Should().OnlyContain(l => l.Kind == LinkKinds.Wikilink);
    }

    [Fact]
    public void Links_from_a_MOC_note_have_kind_moc()
    {
        var n = MarkdownVaultReader.Parse("Windows/Windows MOC.md", "# Windows MOC\n\n[[Prefetch]]");
        n.Links.Should().ContainSingle().Which.Kind.Should().Be(LinkKinds.Moc);
    }

    [Fact]
    public void Hash_ignores_line_endings_and_changes_with_content()
    {
        var a = MarkdownVaultReader.Parse("a.md", "# T\r\n\r\ntext");
        var b = MarkdownVaultReader.Parse("a.md", "# T\n\ntext");
        var c = MarkdownVaultReader.Parse("a.md", "# T\n\ntext 2");
        a.Hash.Should().Be(b.Hash);
        a.Hash.Should().NotBe(c.Hash);
    }

    [Fact]
    public void Chunker_splits_at_h2_and_h3_and_prefixes_the_embed_text()
    {
        var chunks = Chunker.Chunk("Note", "Intro text\n\n## First\nalpha\n\n### Second\nbeta");
        chunks.Select(c => c.Heading).Should().Equal("", "First", "Second");
        chunks[1].EmbedText.Should().Be("Note › First\nalpha");
        chunks[0].EmbedText.Should().StartWith("Note\n");
        chunks.Select(c => c.Ord).Should().Equal(0, 1, 2);
    }

    [Fact]
    public void Chunker_splits_long_sections_on_paragraphs_within_the_caps()
    {
        var body = "## Long\n" + string.Join("\n\n", Enumerable.Range(0, 30).Select(i => $"Paragraph {i} " + new string('x', 300)));
        var chunks = Chunker.Chunk("Note", body);
        chunks.Count.Should().BeGreaterThan(3);
        chunks.Should().OnlyContain(c => c.Text.Length <= Chunker.MaxChars);
        chunks.Should().OnlyContain(c => c.Text.Length > 0);
    }

    [Fact]
    public void Chunker_does_not_split_inside_a_code_fence()
    {
        var fence = "```\n" + string.Join("\n\n", Enumerable.Range(0, 8).Select(i => "line " + i + " " + new string('y', 100))) + "\n```";
        var body = "## Code\n" + new string('a', 900) + "\n\n" + fence + "\n\n" + new string('b', 900);
        var chunks = Chunker.Chunk("Note", body);
        chunks.Count.Should().BeGreaterThan(1);
        chunks.Count(c => c.Text.Contains("```")).Should().Be(1);
        chunks.Single(c => c.Text.Contains("```")).Text.Should().Contain("line 0").And.Contain("line 7");
    }

    [Fact]
    public void Chunker_hard_splits_a_single_oversized_paragraph()
    {
        var chunks = Chunker.Chunk("Note", "## Big\n" + string.Join(" ", Enumerable.Repeat("word", 1500)));
        chunks.Count.Should().BeGreaterThan(1);
        chunks.Should().OnlyContain(c => c.Text.Length <= Chunker.MaxChars);
    }
}
