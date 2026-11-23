using EzForensics.Core.Knowledge;
using EzForensics.Core.Knowledge.Parsers;
using FluentAssertions;
using UglyToad.PdfPig.Core;
using UglyToad.PdfPig.Fonts.Standard14Fonts;
using UglyToad.PdfPig.Writer;

namespace EzForensics.Core.Tests.Knowledge;

public class DocumentImportTests : IDisposable
{
    private readonly TestVault _v = new();
    private readonly KnowledgeStore _store;
    private readonly FakeEmbedder _emb = new();
    private readonly string _docs;

    public DocumentImportTests()
    {
        _store = new KnowledgeStore(_v.DbPath);
        _docs = Path.Combine(_v.Root, "docs");
        Directory.CreateDirectory(_docs);
    }

    public void Dispose() { _store.Dispose(); _v.Dispose(); }

    private KnowledgeImporter Importer => new(_store, _emb);

    private string MakePdf(string name, string mention = "")
    {
        var b = new PdfDocumentBuilder();
        var font = b.AddStandard14Font(Standard14Font.Helvetica);
        var p1 = b.AddPage(595, 842);
        p1.AddText("Intro Heading", 24, new PdfPoint(50, 780), font);
        p1.AddText("This first page explains how prefetch files record program execution. " + mention, 10, new PdfPoint(50, 740), font);
        p1.AddText("Second line of the first page about execution evidence.", 10, new PdfPoint(50, 725), font);
        var p2 = b.AddPage(595, 842);
        p2.AddText("The second page continues with shimcache and amcache details.", 10, new PdfPoint(50, 780), font);
        var path = Path.Combine(_docs, name);
        File.WriteAllBytes(path, b.Build());
        return path;
    }

    [Fact]
    public void Pdf_parser_returns_raw_text_per_page_and_text_parser_none()
    {
        var doc = new PdfDocumentParser().Parse(MakePdf("guide.pdf"));
        doc.Pages.Should().NotBeNull();
        doc.Pages!.Should().HaveCount(2);
        doc.Pages![1].Should().Contain("shimcache");
        var txt = Path.Combine(_docs, "n.txt");
        File.WriteAllText(txt, "hello there");
        new TextDocumentParser().Parse(txt).Pages.Should().BeNull();
    }

    [Fact]
    public void Pdf_parser_keeps_pages_and_heading()
    {
        var doc = new PdfDocumentParser().Parse(MakePdf("guide.pdf"));
        doc.Ok.Should().BeTrue(doc.Error);
        doc.Sections.Should().Contain(s => s.Page == 1 && s.Heading == "Intro Heading");
        doc.Sections.Should().Contain(s => s.Page == 2 && s.Text.Contains("shimcache"));
        doc.ToMarkdown().Should().Contain("## Intro Heading (p.1)").And.Contain("(p.2)");
    }

    [Fact]
    public void Pdf_without_text_gives_clear_message()
    {
        var b = new PdfDocumentBuilder();
        b.AddPage(200, 200);
        var path = Path.Combine(_docs, "scan.pdf");
        File.WriteAllBytes(path, b.Build());
        var doc = new PdfDocumentParser().Parse(path);
        doc.Ok.Should().BeFalse();
        doc.Error.Should().Contain("No text found").And.Contain("OCR");
    }

    [Fact]
    public void Html_and_txt_parse()
    {
        var h = Path.Combine(_docs, "a.html");
        File.WriteAllText(h, "<html><head><title>My Page</title><style>p{}</style><script>var x=1;</script></head><body><h1>Top</h1><p>Hello &amp; welcome</p><h2>Part</h2><p>Details here</p></body></html>");
        var d = new HtmlDocumentParser().Parse(h);
        d.Title.Should().Be("My Page");
        d.Sections.Should().Contain(s => s.Heading == "Top" && s.Text.Contains("Hello & welcome"));
        d.Sections.Should().Contain(s => s.Heading == "Part" && s.Text == "Details here");
        d.Sections.Should().NotContain(s => s.Text.Contains("var x"));

        var t = Path.Combine(_docs, "notes.txt");
        File.WriteAllText(t, "one\r\n\r\ntwo\r\n\r\n\r\nthree");
        var td = new TextDocumentParser().Parse(t);
        td.Title.Should().Be("notes");
        td.Sections.Single().Text.Should().Be("one\n\ntwo\n\nthree");
    }

    [Fact]
    public async Task Pdf_import_chunks_carry_pages_and_same_file_is_noop()
    {
        var pdf = MakePdf("guide.pdf");
        var r = await Importer.ImportFilesAsync(new[] { pdf }, "Documents", null, default);
        r.Errors.Should().BeEmpty();
        r.Result.Added.Should().Be(1);
        _store.Chunks().Select(c => c.Heading).Should().Contain(h => h.Contains("(p.1)")).And.Contain(h => h.Contains("(p.2)"));

        var again = await Importer.ImportFilesAsync(new[] { pdf }, "Documents", null, default);
        again.Result.NothingChanged.Should().BeTrue();
        again.Result.Unchanged.Should().Be(1);
        _store.NoteCount().Should().Be(1);
    }

    [Fact]
    public async Task Bad_files_are_reported_not_swallowed()
    {
        var b = new PdfDocumentBuilder();
        b.AddPage(200, 200);
        var scan = Path.Combine(_docs, "scan.pdf");
        File.WriteAllBytes(scan, b.Build());
        var odd = Path.Combine(_docs, "x.bin");
        File.WriteAllText(odd, "zz");
        var r = await Importer.ImportFilesAsync(new[] { scan, odd }, "Documents", null, default);
        r.Errors.Should().HaveCount(2);
        r.Errors.Select(e => e.Error).Should().Contain(e => e.Contains("OCR")).And.Contain(e => e.Contains("Unsupported"));
        _store.GetMeta("src_status:" + _store.FindSource("Documents")).Should().Contain("scan.pdf");
    }

    [Fact]
    public async Task Remove_deletes_notes_chunks_links_and_copied_files()
    {
        await Importer.ImportFolderAsync(_v.Vault, "vault", null, default);
        await Importer.ImportFilesAsync(new[] { MakePdf("guide.pdf", "See the Windows MOC.") }, "Documents", null, default);
        var sid = _store.FindSource("Documents")!.Value;
        var dir = Importer.FilesDir(sid);
        Directory.Exists(dir).Should().BeTrue();
        File.Exists(Path.Combine(dir, "guide.pdf")).Should().BeTrue();

        await Importer.RemoveSourceAsync("Documents");
        _store.FindSource("Documents").Should().BeNull();
        Directory.Exists(dir).Should().BeFalse();
        _store.Notes().Should().NotContain(n => n.Title == "guide");
        var ids = _store.Notes().Select(n => n.Id).ToHashSet();
        _store.Links().Should().OnlyContain(l => ids.Contains(l.Src) && ids.Contains(l.Dst));
        _store.Chunks().Should().OnlyContain(c => ids.Contains(c.NoteId));
    }

    [Fact]
    public async Task Mention_link_from_pdf_to_vault_note()
    {
        await Importer.ImportFolderAsync(_v.Vault, "vault", null, default);
        await Importer.ImportFilesAsync(new[] { MakePdf("guide.pdf", "See the Windows MOC for the full map.") }, "Documents", null, default);
        var titles = _store.Notes().ToDictionary(n => n.Id, n => n.Title);
        _store.Links().Select(l => (titles[l.Src], titles[l.Dst], l.Kind)).Should().Contain(("guide", "Windows MOC", LinkKinds.Mention));
    }

    [Fact]
    public async Task Semantic_link_to_similar_note()
    {
        await Importer.ImportFolderAsync(_v.Vault, "vault", null, default);
        var beacon = _store.Notes().First(n => n.Title == "Beaconing");
        var body = _store.Note(beacon.Id)!.Value.Body;
        var f = Path.Combine(_docs, "copy.txt");
        File.WriteAllText(f, body);
        await Importer.ImportFilesAsync(new[] { f }, "Documents", null, default);
        var titles = _store.Notes().ToDictionary(n => n.Id, n => n.Title);
        _store.Links().Select(l => (titles[l.Src], titles[l.Dst], l.Kind)).Should().Contain(("copy", "Beaconing", LinkKinds.Semantic));
    }
}
