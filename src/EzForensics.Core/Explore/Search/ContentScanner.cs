using System.Text;
using System.Text.RegularExpressions;

namespace EzForensics.Core.Explore.Search;

internal readonly record struct ScanOutcome(SearchResult? Result, SkipReason? Skip, string? Message);

/// <summary>
/// Content search of one file: opened read-only and shared (never executed), decoded per <see cref="SearchOptions.Encoding"/>, read line by
/// line with a bounded buffer. Cancellation is checked on every line.
/// </summary>
internal static class ContentScanner
{
    public const int WindowChars = 400;
    private const FileAttributes RecallOnOpen = (FileAttributes)0x40000;
    private const FileAttributes RecallOnDataAccess = (FileAttributes)0x400000;

    public static ScanOutcome Scan(FileEntry e, SearchOptions o, Func<ContentMatcher> newMatcher, CancellationToken ct)
    {
        if (e.Length > o.MaxFileSizeForContent)
            return Skip(SkipReason.TooLarge, $"{e.Length:N0} bytes is over the content limit of {o.MaxFileSizeForContent:N0}.");
        if ((e.Attributes & (FileAttributes.Offline | RecallOnOpen | RecallOnDataAccess)) != 0)
            return Skip(SkipReason.CloudPlaceholder, "Cloud placeholder or offline file: reading it would download or recall it.");
        if (e.IsReparsePoint && IsFileLink(e.FullPath))
            return Skip(SkipReason.LinkNotFollowed, "Symbolic link: not read through.");

        try
        {
            using var fs = new FileStream(e.FullPath, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete, 1 << 16, FileOptions.SequentialScan);
            var head = new byte[TextDecoding.SniffBytes];
            int n = fs.ReadAtLeast(head, head.Length, throwOnEndOfStream: false);
            var det = TextDecoding.Detect(head.AsSpan(0, n), o.Encoding, o.SkipBinary);
            if (det.Encoding == null) return Skip(SkipReason.Binary, "Binary file (NUL bytes in the first 8 KB).");
            fs.Position = det.PreambleLength;
            using var sr = new StreamReader(fs, det.Encoding, detectEncodingFromByteOrderMarks: false, bufferSize: 1 << 16, leaveOpen: true);
            return ScanText(e, o, newMatcher(), sr, det.Name, ct);
        }
        catch (RegexMatchTimeoutException)
        {
            return Skip(SkipReason.RegexTimeout, "The regular expression took too long on a line of this file.");
        }
        catch (OperationCanceledException) { throw; }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or System.Security.SecurityException or NotSupportedException
                                       or ArgumentException or DecoderFallbackException)
        {
            return Skip(SkipReason.FileUnreadable, ex.Message);
        }

        static ScanOutcome Skip(SkipReason r, string m) => new(null, r, m);
    }

    private static bool IsFileLink(string path)
    {
        try { return new FileInfo(path).LinkTarget != null; }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { return true; }
    }

    internal static ScanOutcome ScanText(FileEntry e, SearchOptions o, ContentMatcher m, TextReader text, string encodingName, CancellationToken ct)
    {
        var reader = new BoundedLineReader(text);
        int ctx = Math.Clamp(o.ContextLines, 0, 50);
        var before = new Queue<string>(ctx + 1);
        var pending = new List<(List<string> After, int Need)>();
        var hits = new List<ContentHit>();
        var spans = new List<MatchSpan>();
        int hitCount = 0, lineNo = 1;
        bool capped = false;

        while (reader.TryRead(out var line, out var endsLine))
        {
            ct.ThrowIfCancellationRequested();

            if (pending.Count > 0)
            {
                var cut = Cut(line);
                for (int i = pending.Count - 1; i >= 0; i--)
                {
                    var (after, need) = pending[i];
                    after.Add(cut);
                    if (need <= 1) pending.RemoveAt(i); else pending[i] = (after, need - 1);
                }
            }

            spans.Clear();
            if (!capped || !m.CanStopAtCap) m.Scan(line, spans);
            if (!capped && spans.Count > 0)
            {
                int take = Math.Min(spans.Count, o.MaxHitsPerFile - hitCount);
                if (take < spans.Count) spans.RemoveRange(take, spans.Count - take);
                hitCount += take;
                var (lt, off, ls) = Window(line, spans);
                var after = new List<string>(ctx);
                hits.Add(new ContentHit(lineNo, lt, off, ls, before.ToArray(), after));
                if (ctx > 0) pending.Add((after, ctx));
                capped = hitCount >= o.MaxHitsPerFile;
            }

            if (ctx > 0)
            {
                before.Enqueue(Cut(line));
                if (before.Count > ctx) before.Dequeue();
            }
            if (endsLine) lineNo++;
            if (capped && pending.Count == 0 && m.CanStopAtCap) break;
        }

        if (!m.FileMatches(hitCount)) return default;
        return new ScanOutcome(SearchResult.From(e) with
        {
            HitCount = hitCount,
            HitsTruncated = capped,
            Hits = hits,
            EncodingName = encodingName,
        }, null, null);
    }

    private static string Cut(string line) => line.Length > WindowChars ? line[..WindowChars] : line;

    /// <summary>The line, or a <see cref="WindowChars"/> window around the first match, with spans moved into the window.</summary>
    internal static (string Text, int Offset, IReadOnlyList<MatchSpan> Spans) Window(string line, List<MatchSpan> spans)
    {
        if (line.Length <= WindowChars) return (line, 0, spans.ToArray());
        int start = Math.Clamp(spans[0].Start - 100, 0, line.Length - WindowChars);
        int end = start + WindowChars;
        var list = new List<MatchSpan>(spans.Count);
        foreach (var s in spans)
        {
            int s0 = Math.Max(s.Start, start), s1 = Math.Min(s.Start + s.Length, end);
            if (s1 > s0) list.Add(new MatchSpan(s0 - start, s1 - s0));
        }
        return (line.Substring(start, WindowChars), start, list);
    }
}
