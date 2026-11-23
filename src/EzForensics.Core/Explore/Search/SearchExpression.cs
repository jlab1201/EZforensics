using System.Text;

namespace EzForensics.Core.Explore.Search;

/// <summary>
/// Boolean term expression used by name (Contains / Wildcard) and content (Boolean) criteria. Operators are <c>AND</c> / <c>OR</c> / <c>NOT</c>
/// only when exactly uppercase and standalone (NOT &gt; AND &gt; OR); <c>( )</c> groups; <c>"..."</c> is one term (<c>""</c> inside is a quote).
/// Adjacent bare terms are joined by the default operator: AND for Contains / Boolean content, OR for Wildcard (where <c>;</c> also separates).
/// Same syntax as the Analyze search (<see cref="Results.SearchQuery"/>), but with a pluggable term test.
/// </summary>
public sealed class SearchExpression
{
    private abstract record Node;
    private sealed record Term(string Text) : Node;
    private sealed record And(List<Node> Items) : Node;
    private sealed record Or(List<Node> Items) : Node;
    private sealed record Not(Node Inner) : Node;

    private enum Tok { Word, Phrase, And, Or, Not, Open, Close }
    private readonly record struct Token(Tok Kind, string Text);

    private readonly Node _root;

    /// <summary>Every distinct term.</summary>
    public IReadOnlyList<string> Terms { get; }

    /// <summary>Terms not under an odd number of NOTs (what a hit should highlight).</summary>
    public IReadOnlyList<string> PositiveTerms { get; }

    private SearchExpression(Node root)
    {
        _root = root;
        var all = new List<string>();
        var pos = new List<string>();
        Collect(root, false, all, pos);
        Terms = all.Distinct(StringComparer.Ordinal).ToList();
        PositiveTerms = pos.Distinct(StringComparer.Ordinal).ToList();
    }

    /// <summary>Parses <paramref name="text"/>; false with a readable <paramref name="error"/> on bad syntax or empty input.</summary>
    /// <param name="defaultOr">Adjacent bare terms are OR'ed (wildcard names) instead of AND'ed.</param>
    /// <param name="semicolonSeparates"><c>;</c> separates terms like a space (wildcard names).</param>
    public static bool TryParse(string? text, bool defaultOr, bool semicolonSeparates, out SearchExpression? expr, out string? error)
    {
        expr = null; error = null;
        try
        {
            var tokens = Tokenize(text ?? "", semicolonSeparates);
            if (tokens.Count == 0) { error = "Enter at least one term."; return false; }
            expr = new SearchExpression(new Parser(tokens, defaultOr).ParseAll());
            return true;
        }
        catch (FormatException ex)
        {
            error = ex.Message;
            return false;
        }
    }

    /// <summary>True when the expression holds, given which terms are present.</summary>
    public bool Evaluate(Func<string, bool> termPresent) => Eval(_root, termPresent);

    private static bool Eval(Node n, Func<string, bool> f)
    {
        switch (n)
        {
            case Term t: return f(t.Text);
            case Not x: return !Eval(x.Inner, f);
            case And a: foreach (var i in a.Items) if (!Eval(i, f)) return false; return true;
            case Or o: foreach (var i in o.Items) if (Eval(i, f)) return true; return false;
            default: throw new NotSupportedException(n.GetType().Name);
        }
    }

    private static void Collect(Node n, bool negated, List<string> all, List<string> pos)
    {
        switch (n)
        {
            case Term t: all.Add(t.Text); if (!negated) pos.Add(t.Text); break;
            case Not x: Collect(x.Inner, !negated, all, pos); break;
            case And a: foreach (var i in a.Items) Collect(i, negated, all, pos); break;
            case Or o: foreach (var i in o.Items) Collect(i, negated, all, pos); break;
        }
    }

    private static List<Token> Tokenize(string s, bool semicolon)
    {
        var list = new List<Token>();
        int i = 0;
        bool IsSep(char c) => char.IsWhiteSpace(c) || (semicolon && c == ';');
        while (i < s.Length)
        {
            var c = s[i];
            if (IsSep(c)) { i++; continue; }
            if (c == '(') { list.Add(new Token(Tok.Open, "(")); i++; continue; }
            if (c == ')') { list.Add(new Token(Tok.Close, ")")); i++; continue; }
            if (c == '"')
            {
                var sb = new StringBuilder();
                i++;
                bool closed = false;
                while (i < s.Length)
                {
                    if (s[i] == '"')
                    {
                        if (i + 1 < s.Length && s[i + 1] == '"') { sb.Append('"'); i += 2; continue; }
                        closed = true; i++; break;
                    }
                    sb.Append(s[i++]);
                }
                if (!closed) throw new FormatException("Unterminated quote: add a closing \".");
                if (sb.Length == 0) throw new FormatException("Empty quoted phrase.");
                list.Add(new Token(Tok.Phrase, sb.ToString()));
                continue;
            }
            int start = i;
            while (i < s.Length && !IsSep(s[i]) && s[i] is not ('(' or ')' or '"')) i++;
            var w = s[start..i];
            list.Add(w switch
            {
                "AND" => new Token(Tok.And, w),
                "OR" => new Token(Tok.Or, w),
                "NOT" => new Token(Tok.Not, w),
                _ => new Token(Tok.Word, w),
            });
        }
        return list;
    }

    private sealed class Parser
    {
        private readonly List<Token> _t;
        private readonly bool _defaultOr;
        private int _p;
        public Parser(List<Token> t, bool defaultOr) { _t = t; _defaultOr = defaultOr; }

        private bool More => _p < _t.Count;
        private Tok Peek => _t[_p].Kind;
        private bool StartsOperand => More && Peek is Tok.Word or Tok.Phrase or Tok.Not or Tok.Open;

        public Node ParseAll()
        {
            var n = ParseOr();
            if (More) throw new FormatException(Peek == Tok.Close ? "Unbalanced parentheses: extra \")\"." : $"Unexpected \"{_t[_p].Text}\".");
            return n;
        }

        private Node ParseOr()
        {
            var items = new List<Node> { ParseAnd() };
            while (More)
            {
                if (Peek == Tok.Or) { _p++; items.Add(ParseAnd()); }
                else if (_defaultOr && StartsOperand) items.Add(ParseAnd());
                else break;
            }
            return items.Count == 1 ? items[0] : new Or(items);
        }

        private Node ParseAnd()
        {
            var items = new List<Node> { ParseNot() };
            while (More)
            {
                if (Peek == Tok.And) { _p++; items.Add(ParseNot()); }
                else if (!_defaultOr && StartsOperand) items.Add(ParseNot());
                else break;
            }
            return items.Count == 1 ? items[0] : new And(items);
        }

        private Node ParseNot()
        {
            if (More && Peek == Tok.Not)
            {
                _p++;
                if (!More || Peek is Tok.Close or Tok.And or Tok.Or) throw new FormatException("NOT needs a term after it.");
                return new Not(ParseNot());
            }
            return ParsePrimary();
        }

        private Node ParsePrimary()
        {
            if (!More) throw new FormatException("Missing term at the end (dangling AND/OR).");
            var t = _t[_p];
            switch (t.Kind)
            {
                case Tok.Word:
                case Tok.Phrase:
                    _p++;
                    return new Term(t.Text);
                case Tok.Open:
                    _p++;
                    if (More && Peek == Tok.Close) throw new FormatException("Empty parentheses ().");
                    var inner = ParseOr();
                    if (!More || Peek != Tok.Close) throw new FormatException("Unbalanced parentheses: missing \")\".");
                    _p++;
                    return inner;
                case Tok.Close:
                    throw new FormatException("Unbalanced parentheses: unexpected \")\".");
                default:
                    throw new FormatException($"{t.Text} needs a term before it.");
            }
        }
    }
}
