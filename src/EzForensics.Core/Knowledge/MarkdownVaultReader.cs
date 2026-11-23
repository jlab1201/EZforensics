using System.Security.Cryptography;
using System.Text;
using System.Text.RegularExpressions;
using YamlDotNet.Serialization;

namespace EzForensics.Core.Knowledge;

/// <summary>Reads a folder of Markdown notes (an Obsidian vault): frontmatter, title, topic, tags, wikilinks.</summary>
public static class MarkdownVaultReader
{
    private static readonly Regex WikiLink = new(@"\[\[([^\[\]\r\n]+?)\]\]", RegexOptions.Compiled);
    private static readonly Regex H1 = new(@"^#\s+(.+?)\s*#*\s*$", RegexOptions.Multiline | RegexOptions.Compiled);
    private static readonly Regex InlineTag = new(@"(?<![\w/&])#([A-Za-z][\w/-]*)", RegexOptions.Compiled);

    /// <summary>All .md files under the folder (hidden folders such as .obsidian skipped), sorted by path.</summary>
    public static List<VaultNote> ReadFolder(string root)
    {
        var notes = new List<VaultNote>();
        var files = new List<string>();
        Collect(root, files);
        files.Sort(StringComparer.OrdinalIgnoreCase);
        foreach (var f in files)
        {
            var rel = Path.GetRelativePath(root, f).Replace('\\', '/');
            notes.Add(Parse(rel, File.ReadAllText(f)));
        }
        return notes;
    }

    private static void Collect(string dir, List<string> files)
    {
        foreach (var f in Directory.EnumerateFiles(dir, "*.md")) files.Add(f);
        foreach (var d in Directory.EnumerateDirectories(dir))
            if (!Path.GetFileName(d).StartsWith('.')) Collect(d, files);
    }

    /// <summary>Parses one note. <paramref name="relPath"/> uses forward slashes.</summary>
    public static VaultNote Parse(string relPath, string raw)
    {
        var text = Normalize(raw);
        var hash = Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(text))).ToLowerInvariant();
        var (front, body) = SplitFrontmatter(text);

        string? title = null;
        var tags = new List<string>();
        if (front != null)
        {
            try
            {
                var map = new DeserializerBuilder().Build().Deserialize<Dictionary<string, object?>>(front);
                if (map != null)
                    foreach (var kv in map)
                    {
                        if (kv.Key.Equals("title", StringComparison.OrdinalIgnoreCase) && kv.Value is string t && t.Trim().Length > 0) title = t.Trim();
                        else if (kv.Key.Equals("tags", StringComparison.OrdinalIgnoreCase) || kv.Key.Equals("tag", StringComparison.OrdinalIgnoreCase))
                            AddTags(tags, kv.Value);
                    }
            }
            catch (Exception) { /* bad frontmatter: treated as no metadata */ }
        }
        var plain = StripFences(body);
        if (title == null && H1.Match(plain) is { Success: true } m) title = m.Groups[1].Value.Trim();
        title ??= Path.GetFileNameWithoutExtension(relPath);

        foreach (Match m2 in InlineTag.Matches(plain))
            if (!tags.Contains(m2.Groups[1].Value, StringComparer.OrdinalIgnoreCase)) tags.Add(m2.Groups[1].Value);

        var slash = relPath.IndexOf('/');
        var topic = slash > 0 ? relPath[..slash] : "";

        var kind = title.EndsWith("MOC", StringComparison.OrdinalIgnoreCase) ? LinkKinds.Moc : LinkKinds.Wikilink;
        var links = new List<VaultLink>();
        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (Match m3 in WikiLink.Matches(plain))
        {
            var target = m3.Groups[1].Value;
            var pipe = target.IndexOf('|');
            if (pipe >= 0) target = target[..pipe];
            var hashAt = target.IndexOf('#');
            if (hashAt >= 0) target = target[..hashAt];
            target = target.Trim();
            if (target.Length == 0) continue;
            if (seen.Add(target)) links.Add(new VaultLink(target, kind));
        }
        return new VaultNote(relPath, title, topic, tags, hash, body, links);
    }

    private static void AddTags(List<string> tags, object? v)
    {
        IEnumerable<string> items = v switch
        {
            string s => s.Split(new[] { ',', ' ' }, StringSplitOptions.RemoveEmptyEntries),
            IEnumerable<object> list => list.Select(o => o?.ToString() ?? ""),
            _ => Array.Empty<string>(),
        };
        foreach (var t in items.Select(x => x.Trim().TrimStart('#')).Where(x => x.Length > 0))
            if (!tags.Contains(t, StringComparer.OrdinalIgnoreCase)) tags.Add(t);
    }

    internal static string Normalize(string raw) => raw.TrimStart('﻿').Replace("\r\n", "\n").Replace('\r', '\n').Trim();

    private static (string? Front, string Body) SplitFrontmatter(string text)
    {
        if (!text.StartsWith("---\n")) return (null, text);
        var end = text.IndexOf("\n---", 3, StringComparison.Ordinal);
        if (end < 0) return (null, text);
        var after = end + 4;
        if (after < text.Length && text[after] != '\n') return (null, text);
        return (end < 4 ? "" : text[4..end], after < text.Length ? text[(after + 1)..].TrimStart('\n') : "");
    }

    /// <summary>Body with fenced code blocks removed, so links and tags inside code are ignored.</summary>
    private static string StripFences(string body)
    {
        if (!body.Contains("```")) return body;
        var sb = new StringBuilder();
        var inFence = false;
        foreach (var line in body.Split('\n'))
        {
            if (line.TrimStart().StartsWith("```")) { inFence = !inFence; continue; }
            if (!inFence) sb.Append(line).Append('\n');
        }
        return sb.ToString();
    }
}
