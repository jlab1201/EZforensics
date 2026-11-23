using System.Text;

namespace EzForensics.Core.Knowledge;

/// <summary>Splits a note at H2/H3 headings into chunks of about 350 tokens (4 chars per token), at most about 500.</summary>
public static class Chunker
{
    public const int TargetChars = 1400;
    public const int MaxChars = 2000;

    public static List<ChunkPiece> Chunk(string title, string body)
    {
        var result = new List<ChunkPiece>();
        foreach (var (heading, text) in Sections(body))
            foreach (var part in SplitSection(text))
            {
                var prefix = heading.Length > 0 ? $"{title} › {heading}\n" : $"{title}\n";
                result.Add(new ChunkPiece(result.Count, heading, part, prefix + part));
            }
        return result;
    }

    private static List<(string Heading, string Text)> Sections(string body)
    {
        var list = new List<(string, string)>();
        var heading = "";
        var sb = new StringBuilder();
        var inFence = false;
        void Flush()
        {
            var t = sb.ToString().Trim();
            var onlyTitle = heading.Length == 0 && t.StartsWith("# ") && !t.Contains('\n'); // the H1 alone adds nothing: the title is in every chunk prefix
            if (t.Length > 0 && !onlyTitle) list.Add((heading, t));
            sb.Clear();
        }
        foreach (var line in body.Split('\n'))
        {
            if (line.TrimStart().StartsWith("```")) inFence = !inFence;
            if (!inFence && (line.StartsWith("## ") || line.StartsWith("### ")))
            {
                Flush();
                heading = line.TrimStart('#').Trim();
                continue;
            }
            sb.Append(line).Append('\n');
        }
        Flush();
        return list;
    }

    /// <summary>Paragraph-aware split. A fenced block is one unit unless it alone exceeds the max.</summary>
    private static List<string> SplitSection(string text)
    {
        if (text.Length <= TargetChars) return new List<string> { text };
        var units = new List<string>();
        var cur = new StringBuilder();
        var inFence = false;
        void EndUnit() { if (cur.ToString().Trim().Length > 0) units.Add(cur.ToString().TrimEnd()); cur.Clear(); }
        foreach (var line in text.Split('\n'))
        {
            if (line.TrimStart().StartsWith("```")) inFence = !inFence;
            if (!inFence && line.Trim().Length == 0) { EndUnit(); continue; }
            cur.Append(line).Append('\n');
        }
        EndUnit();

        var parts = new List<string>();
        var acc = new StringBuilder();
        foreach (var u0 in units)
            foreach (var u in u0.Length > MaxChars ? HardSplit(u0) : new List<string> { u0 })
            {
                if (acc.Length > 0 && acc.Length + u.Length + 2 > TargetChars)
                {
                    parts.Add(acc.ToString());
                    acc.Clear();
                }
                if (acc.Length > 0) acc.Append("\n\n");
                acc.Append(u);
            }
        if (acc.Length > 0) parts.Add(acc.ToString());
        return parts;
    }

    private static List<string> HardSplit(string unit)
    {
        var res = new List<string>();
        var sb = new StringBuilder();
        foreach (var line in unit.Split('\n'))
        {
            var l = line;
            while (l.Length > MaxChars)
            {
                if (sb.Length > 0) { res.Add(sb.ToString().TrimEnd()); sb.Clear(); }
                var cut = l.LastIndexOf(' ', MaxChars - 1);
                if (cut <= 0) cut = MaxChars;
                res.Add(l[..cut]);
                l = l[cut..].TrimStart();
            }
            if (sb.Length + l.Length + 1 > MaxChars && sb.Length > 0) { res.Add(sb.ToString().TrimEnd()); sb.Clear(); }
            sb.Append(l).Append('\n');
        }
        if (sb.ToString().Trim().Length > 0) res.Add(sb.ToString().TrimEnd());
        return res;
    }
}
