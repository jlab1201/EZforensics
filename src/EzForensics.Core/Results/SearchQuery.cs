using System.Text;

namespace EzForensics.Core.Results;

/// <summary>
/// Search-box syntax for the Analyze global search (pure). Every term is a case-insensitive "contains" in any cell of the row.
/// Space = AND; <c>AND</c>/<c>OR</c>/<c>NOT</c> are operators only when exactly uppercase and standalone (NOT > AND > OR);
/// <c>( )</c> groups; <c>"..."</c> is an exact phrase (<c>""</c> inside is a quote). There is no <c>-term</c> shorthand.
/// </summary>
public sealed class SearchQuery
{
    private abstract record Node;
    private sealed record Term(string Text) : Node;
    private sealed record And(List<Node> Items) : Node;
    private sealed record Or(List<Node> Items) : Node;
    private sealed record Not(Node Inner) : Node;

    private enum Tok { Word, Phrase, And, Or, Not, Open, Close }
    private readonly record struct Token(Tok Kind, string Text);

    private readonly Node _root;
    private SearchQuery(Node root) => _root = root;

    /// <summary>True when the whole query is one bare term (the engine keeps its plain contains loop).</summary>
    public bool IsSingleTerm => _root is Term;

    /// <summary>The term when <see cref="IsSingleTerm"/>; otherwise null.</summary>
    public string? SingleTerm => (_root as Term)?.Text;

    /// <summary>Parses <paramref name="text"/>. Empty/whitespace input is valid and gives a null query (no constraint); otherwise false with <paramref name="error"/>.</summary>
    public static bool TryParse(string? text, out SearchQuery? query, out string? error)
    {
        query = null; error = null;
        if (string.IsNullOrWhiteSpace(text)) return true;
        try
        {
            var root = new Parser(Tokenize(text)).ParseAll();
            query = new SearchQuery(root);
            return true;
        }
        catch (FormatException ex)
        {
            error = ex.Message;
            return false;
        }
    }

    /// <summary>Row predicate: true when the row satisfies the query.</summary>
    public Func<string[], bool> Compile() => Build(_root);

    /// <summary>Normalised text, e.g. <c>(psexec OR wmic) AND NOT admin</c>; terms with spaces or specials are quoted.</summary>
    public string ToDisplayString() => Render(_root);

    public override string ToString() => ToDisplayString();

    private static Func<string[], bool> Build(Node n)
    {
        switch (n)
        {
            case Term t:
                var s = t.Text;
                return row =>
                {
                    foreach (var c in row)
                        if (c.Contains(s, StringComparison.OrdinalIgnoreCase)) return true;
                    return false;
                };
            case Not x:
                var inner = Build(x.Inner);
                return row => !inner(row);
            case And a:
                var all = a.Items.Select(Build).ToArray();
                return row => { foreach (var f in all) if (!f(row)) return false; return true; };
            case Or o:
                var any = o.Items.Select(Build).ToArray();
                return row => { foreach (var f in any) if (f(row)) return true; return false; };
            default: throw new NotSupportedException(n.GetType().Name);
        }
    }

    private static string Render(Node n) => n switch
    {
        Term t => QuoteIfNeeded(t.Text),
        Not x => "NOT " + (x.Inner is And or Or ? "(" + Render(x.Inner) + ")" : Render(x.Inner)),
        And a => string.Join(" AND ", a.Items.Select(i => i is Or ? "(" + Render(i) + ")" : Render(i))),
        Or o => string.Join(" OR ", o.Items.Select(Render)),
        _ => "",
    };

    private static string QuoteIfNeeded(string s)
    {
        var need = s.Length == 0 || s is "AND" or "OR" or "NOT" || s.Any(c => char.IsWhiteSpace(c) || c is '(' or ')' or '"');
        return need ? "\"" + s.Replace("\"", "\"\"") + "\"" : s;
    }

    private static List<Token> Tokenize(string s)
    {
        var list = new List<Token>();
        var i = 0;
        while (i < s.Length)
        {
            var c = s[i];
            if (char.IsWhiteSpace(c)) { i++; continue; }
            if (c == '(') { list.Add(new Token(Tok.Open, "(")); i++; continue; }
            if (c == ')') { list.Add(new Token(Tok.Close, ")")); i++; continue; }
            if (c == '"')
            {
                var sb = new StringBuilder();
                i++;
                var closed = false;
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
            var start = i;
            while (i < s.Length && !char.IsWhiteSpace(s[i]) && s[i] is not ('(' or ')' or '"')) i++;
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
        private int _p;
        public Parser(List<Token> t) => _t = t;

        private bool More => _p < _t.Count;
        private Tok Peek => _t[_p].Kind;

        public Node ParseAll()
        {
            var n = ParseOr();
            if (More) throw new FormatException(Peek == Tok.Close ? "Unbalanced parentheses: extra \")\"." : $"Unexpected \"{_t[_p].Text}\".");
            return n;
        }

        private Node ParseOr()
        {
            var items = new List<Node> { ParseAnd() };
            while (More && Peek == Tok.Or) { _p++; items.Add(ParseAnd()); }
            return items.Count == 1 ? items[0] : new Or(items);
        }

        private Node ParseAnd()
        {
            var items = new List<Node> { ParseNot() };
            while (More)
            {
                if (Peek == Tok.And) { _p++; items.Add(ParseNot()); }
                else if (Peek is Tok.Word or Tok.Phrase or Tok.Not or Tok.Open) items.Add(ParseNot());
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
            if (!More) throw new FormatException("Missing term at the end of the query (dangling AND/OR).");
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
