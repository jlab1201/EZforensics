using UglyToad.PdfPig;
using UglyToad.PdfPig.Content;
using UglyToad.PdfPig.Outline;

namespace EzForensics.Core.Knowledge.Parsers;

/// <summary>
/// PDF text via PdfPig (pure .NET). One section per page; the heading comes from the bookmark covering the page, else from
/// lines set clearly larger than the body text. Scanned PDFs (no text layer) give a clear error; OCR is not supported.
/// </summary>
public sealed class PdfDocumentParser : IDocumentParser
{
    public const string NoTextMessage = "No text found (scanned PDF? OCR is not supported yet)";
    public bool CanParse(string ext) => ext == ".pdf";

    private sealed record Line(string Text, double Size);

    public ParsedDocument Parse(string path)
    {
        var fallback = DocumentParsers.TitleFromFile(path);
        try
        {
            using var doc = PdfDocument.Open(path);
            var title = doc.Information.Title is { } t && t.Trim().Length > 0 ? t.Trim() : fallback;

            var marks = new List<(int Page, string Title)>();
            if (doc.TryGetBookmarks(out var bm)) Flatten(bm.Roots, marks);

            var pages = new List<(int Number, List<Line> Lines)>();
            foreach (var page in doc.GetPages()) pages.Add((page.Number, Lines(page)));
            var sizes = pages.SelectMany(p => p.Lines).Select(l => l.Size).OrderBy(x => x).ToList();
            if (sizes.Count == 0) return new ParsedDocument(title, Array.Empty<DocSection>(), NoTextMessage);
            var body = sizes[sizes.Count / 2];

            var sections = new List<DocSection>();
            var heading = "";
            foreach (var (number, lines) in pages)
            {
                if (marks.Count > 0)
                {
                    var onPage = marks.Where(m => m.Page == number).ToList();
                    if (onPage.Count > 0) heading = onPage[0].Title;
                    else if (heading.Length == 0 && marks.Any(m => m.Page < number)) heading = marks.Last(m => m.Page < number).Title;
                    var text = string.Join("\n", lines.Select(l => l.Text));
                    if (text.Trim().Length > 0) sections.Add(new DocSection(heading, text, number));
                    continue;
                }
                var cur = new List<string>();
                var curPage = number;
                void Flush()
                {
                    var text = string.Join("\n", cur).Trim();
                    if (text.Length > 0) sections.Add(new DocSection(heading, text, curPage));
                    cur.Clear();
                }
                foreach (var l in lines)
                {
                    if (l.Size >= body * 1.2 && l.Text.Length is > 0 and <= 100) { Flush(); heading = l.Text; continue; }
                    cur.Add(l.Text);
                }
                Flush();
            }
            return sections.Count == 0
                ? new ParsedDocument(title, Array.Empty<DocSection>(), NoTextMessage)
                : new ParsedDocument(title, sections, null, pages.Select(p => string.Join("\n", p.Lines.Select(l => l.Text))).ToList());
        }
        catch (Exception ex)
        {
            return new ParsedDocument(fallback, Array.Empty<DocSection>(), "Could not read PDF: " + ex.Message);
        }
    }

    private static void Flatten(IEnumerable<BookmarkNode> nodes, List<(int, string)> acc)
    {
        foreach (var n in nodes)
        {
            if (n is DocumentBookmarkNode d && d.Title.Trim().Length > 0) acc.Add((d.PageNumber, d.Title.Trim()));
            Flatten(n.Children, acc);
        }
    }

    /// <summary>Words grouped into lines (top to bottom, left to right); line size is the largest letter size.</summary>
    private static List<Line> Lines(Page page)
    {
        var words = page.GetWords().Where(w => w.Text.Trim().Length > 0).ToList();
        var lines = new List<List<Word>>();
        foreach (var w in words.OrderByDescending(w => w.BoundingBox.Bottom).ThenBy(w => w.BoundingBox.Left))
        {
            var last = lines.Count > 0 ? lines[^1] : null;
            if (last != null && Math.Abs(last[0].BoundingBox.Bottom - w.BoundingBox.Bottom) <= Math.Max(2, w.BoundingBox.Height * 0.5)) last.Add(w);
            else lines.Add(new List<Word> { w });
        }
        return lines.Select(l =>
        {
            var ws = l.OrderBy(w => w.BoundingBox.Left).ToList();
            var size = ws.Max(w => w.Letters.Count > 0 ? w.Letters.Max(x => x.PointSize) : w.BoundingBox.Height);
            return new Line(string.Join(" ", ws.Select(w => w.Text)), size);
        }).ToList();
    }
}
