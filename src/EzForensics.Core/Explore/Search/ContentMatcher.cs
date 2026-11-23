using System.Text.RegularExpressions;

namespace EzForensics.Core.Explore.Search;

/// <summary>Finds content matches in one line at a time. One instance per file scan (Boolean keeps per-file state).</summary>
internal abstract class ContentMatcher
{
    public static readonly TimeSpan RegexTimeout = TimeSpan.FromSeconds(1);

    /// <summary>Adds the matches in <paramref name="line"/> (in order, non-overlapping) to <paramref name="spans"/>.</summary>
    public abstract void Scan(string line, List<MatchSpan> spans);

    /// <summary>Whether the file matches, after the lines read so far.</summary>
    public virtual bool FileMatches(int hitCount) => hitCount > 0;

    /// <summary>Reading can stop once the hit cap is reached (Boolean needs every term's presence first).</summary>
    public virtual bool CanStopAtCap => true;

    /// <summary>Factory for a fresh matcher; the options are assumed valid (<see cref="SearchValidation"/>).</summary>
    public static Func<ContentMatcher> CreateFactory(SearchOptions o)
    {
        var text = o.ContentText ?? "";
        var cmp = o.ContentMatchCase ? StringComparison.Ordinal : StringComparison.OrdinalIgnoreCase;
        switch (o.ContentMode)
        {
            case ContentMatchMode.Regex:
                var pattern = o.ContentWholeWord ? @"\b(?:" + text + @")\b" : text;
                var rx = new Regex(pattern, RegexOptions.CultureInvariant | (o.ContentMatchCase ? 0 : RegexOptions.IgnoreCase), RegexTimeout);
                return () => new RegexMatcher(rx);
            case ContentMatchMode.Boolean:
                if (!SearchExpression.TryParse(text, false, false, out var expr, out var err)) throw new ArgumentException(err);
                return () => new BooleanMatcher(expr!, cmp, o.ContentWholeWord);
            default:
                return () => new LiteralMatcher(text, cmp, o.ContentWholeWord);
        }
    }

    internal static bool IsWordChar(char c) => char.IsLetterOrDigit(c) || c == '_';

    /// <summary>Non-overlapping occurrences of <paramref name="term"/>; with whole word, an occurrence touching a word character is skipped.</summary>
    internal static int FindLiteral(string line, string term, StringComparison cmp, bool wholeWord, List<MatchSpan>? spans)
    {
        int n = 0, from = 0;
        while (from <= line.Length - term.Length)
        {
            int i = line.IndexOf(term, from, cmp);
            if (i < 0) break;
            int end = i + term.Length;
            if (wholeWord && ((i > 0 && IsWordChar(line[i - 1])) || (end < line.Length && IsWordChar(line[end]))))
            {
                from = i + 1;
                continue;
            }
            spans?.Add(new MatchSpan(i, term.Length));
            n++;
            if (spans == null) return n;
            from = end;
        }
        return n;
    }

    private sealed class LiteralMatcher : ContentMatcher
    {
        private readonly string _term;
        private readonly StringComparison _cmp;
        private readonly bool _whole;
        public LiteralMatcher(string term, StringComparison cmp, bool whole) { _term = term; _cmp = cmp; _whole = whole; }
        public override void Scan(string line, List<MatchSpan> spans) => FindLiteral(line, _term, _cmp, _whole, spans);
    }

    private sealed class RegexMatcher : ContentMatcher
    {
        private readonly Regex _rx;
        public RegexMatcher(Regex rx) => _rx = rx;

        public override void Scan(string line, List<MatchSpan> spans)
        {
            foreach (var m in _rx.EnumerateMatches(line))
                if (m.Length > 0) spans.Add(new MatchSpan(m.Index, m.Length));
        }
    }

    private sealed class BooleanMatcher : ContentMatcher
    {
        private readonly SearchExpression _expr;
        private readonly StringComparison _cmp;
        private readonly bool _whole;
        private readonly string[] _terms;
        private readonly HashSet<string> _positive;
        private readonly HashSet<string> _found = new(StringComparer.Ordinal);
        private readonly List<MatchSpan> _tmp = new();

        public BooleanMatcher(SearchExpression expr, StringComparison cmp, bool whole)
        {
            _expr = expr; _cmp = cmp; _whole = whole;
            _terms = expr.Terms.ToArray();
            _positive = new HashSet<string>(expr.PositiveTerms, StringComparer.Ordinal);
        }

        public override void Scan(string line, List<MatchSpan> spans)
        {
            _tmp.Clear();
            foreach (var t in _terms)
            {
                bool positive = _positive.Contains(t);
                if (!positive)
                {
                    if (!_found.Contains(t) && FindLiteral(line, t, _cmp, _whole, null) > 0) _found.Add(t);
                    continue;
                }
                if (FindLiteral(line, t, _cmp, _whole, _tmp) > 0) _found.Add(t);
            }
            if (_tmp.Count == 0) return;
            // Order and drop overlaps (e.g. "pass" inside "password").
            _tmp.Sort((a, b) => a.Start != b.Start ? a.Start.CompareTo(b.Start) : b.Length.CompareTo(a.Length));
            int end = -1;
            foreach (var s in _tmp)
            {
                if (s.Start < end) continue;
                spans.Add(s);
                end = s.Start + s.Length;
            }
        }

        public override bool FileMatches(int hitCount) => _expr.Evaluate(_found.Contains);
        public override bool CanStopAtCap => _found.Count == _terms.Length;
    }
}
