namespace EzForensics.Core.Knowledge.Parsers;

/// <summary>One section of a document: heading (may be empty), text, and the page it starts on (null for formats without pages).</summary>
public sealed record DocSection(string Heading, string Text, int? Page);

/// <summary>A parsed document. <see cref="Error"/> is set (and sections empty) when nothing usable was found.</summary>
public sealed record ParsedDocument(string Title, IReadOnlyList<DocSection> Sections, string? Error = null, IReadOnlyList<string>? Pages = null)
{
    public bool Ok => Error == null && Sections.Count > 0;

    /// <summary>Markdown body for the knowledge store: each section becomes a "## Heading (p.N)" block, so chunks cite the page.</summary>
    public string ToMarkdown()
    {
        var sb = new System.Text.StringBuilder();
        foreach (var s in Sections)
        {
            var h = s.Heading.Trim();
            if (s.Page is int p) h = h.Length > 0 ? $"{h} (p.{p})" : $"Page {p}";
            if (h.Length > 0) sb.Append("## ").Append(h).Append("\n\n");
            sb.Append(s.Text.Trim()).Append("\n\n");
        }
        return sb.ToString().Trim();
    }
}

public interface IDocumentParser
{
    bool CanParse(string extension);
    ParsedDocument Parse(string path);
}

public static class DocumentParsers
{
    public static readonly string[] Extensions = { ".pdf", ".md", ".markdown", ".txt", ".html", ".htm" };

    public static IDocumentParser? For(string path)
    {
        var ext = Path.GetExtension(path).ToLowerInvariant();
        IDocumentParser[] all = { new PdfDocumentParser(), new HtmlDocumentParser(), new TextDocumentParser(), new MarkdownDocumentParser() };
        return all.FirstOrDefault(p => p.CanParse(ext));
    }

    internal static string TitleFromFile(string path) => Path.GetFileNameWithoutExtension(path);
}
