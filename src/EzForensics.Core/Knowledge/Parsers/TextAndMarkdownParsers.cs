using System.Text;
using System.Text.RegularExpressions;

namespace EzForensics.Core.Knowledge.Parsers;

/// <summary>Plain text: blank-line separated paragraphs in a single section.</summary>
public sealed class TextDocumentParser : IDocumentParser
{
    public bool CanParse(string ext) => ext == ".txt";

    public ParsedDocument Parse(string path)
    {
        var text = MarkdownVaultReader.Normalize(File.ReadAllText(path));
        var title = DocumentParsers.TitleFromFile(path);
        if (text.Length == 0) return new ParsedDocument(title, Array.Empty<DocSection>(), "No text found (empty file)");
        var paras = Regex.Split(text, @"\n\s*\n").Select(p => p.Trim()).Where(p => p.Length > 0);
        return new ParsedDocument(title, new[] { new DocSection("", string.Join("\n\n", paras), null) });
    }
}

/// <summary>Markdown reuses the vault reader (frontmatter, title, H1); the body keeps its own headings.</summary>
public sealed class MarkdownDocumentParser : IDocumentParser
{
    public bool CanParse(string ext) => ext is ".md" or ".markdown";

    public ParsedDocument Parse(string path)
    {
        var note = MarkdownVaultReader.Parse(Path.GetFileName(path), File.ReadAllText(path));
        if (note.Body.Trim().Length == 0) return new ParsedDocument(note.Title, Array.Empty<DocSection>(), "No text found (empty file)");
        return new ParsedDocument(note.Title, new[] { new DocSection("", note.Body, null) });
    }
}

/// <summary>HTML to text without extra dependencies: scripts/styles dropped, h1-h3 become headings, block tags become line breaks.</summary>
public sealed class HtmlDocumentParser : IDocumentParser
{
    public bool CanParse(string ext) => ext is ".html" or ".htm";

    public ParsedDocument Parse(string path)
    {
        var html = File.ReadAllText(path);
        var title = DocumentParsers.TitleFromFile(path);
        var t = Regex.Match(html, @"<title[^>]*>(.*?)</title>", RegexOptions.Singleline | RegexOptions.IgnoreCase);
        if (t.Success && Decode(t.Groups[1].Value).Trim() is { Length: > 0 } tt) title = tt;

        html = Regex.Replace(html, @"<(script|style|noscript|head)\b.*?</\1\s*>", " ", RegexOptions.Singleline | RegexOptions.IgnoreCase);
        html = Regex.Replace(html, @"<!--.*?-->", " ", RegexOptions.Singleline);

        var sections = new List<DocSection>();
        var heading = "";
        var sb = new StringBuilder();
        void Flush()
        {
            var text = Regex.Replace(sb.ToString(), @"[ \t]+", " ");
            text = Regex.Replace(text, @"[ \t]*\n[ \t]*", "\n").Trim();
            text = Regex.Replace(text, @"\n{3,}", "\n\n");
            if (text.Length > 0) sections.Add(new DocSection(heading, text, null));
            sb.Clear();
        }
        var pos = 0;
        foreach (Match m in Regex.Matches(html, @"<h([1-3])\b[^>]*>(.*?)</h\1\s*>", RegexOptions.Singleline | RegexOptions.IgnoreCase))
        {
            sb.Append(ToText(html[pos..m.Index]));
            Flush();
            heading = Decode(StripTags(m.Groups[2].Value)).Trim();
            pos = m.Index + m.Length;
        }
        sb.Append(ToText(html[pos..]));
        Flush();
        return sections.Count == 0
            ? new ParsedDocument(title, Array.Empty<DocSection>(), "No text found")
            : new ParsedDocument(title, sections);
    }

    private static string ToText(string fragment)
    {
        fragment = Regex.Replace(fragment, @"</?(br|p|div|li|tr|table|ul|ol|h\d)\b[^>]*>", "\n", RegexOptions.IgnoreCase);
        return Decode(StripTags(fragment));
    }

    private static string StripTags(string s) => Regex.Replace(s, @"<[^>]+>", " ");
    private static string Decode(string s) => System.Net.WebUtility.HtmlDecode(s);
}
