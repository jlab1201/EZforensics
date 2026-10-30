using System.Globalization;

namespace EzForensics.Core.Results;

/// <summary>
/// Parser for the text <see cref="FilterNode.ToDisplayString"/> produces. Round trip: for a node n, parsing n.ToDisplayString()
/// yields a node whose ToDisplayString equals the original (lists truncated for display with an ellipsis and "(+N)" are rejected,
/// since the hidden values cannot be recovered). Keywords and function names are case-insensitive; empty text is a valid, empty filter (an empty And group). The result is the parsed expression itself: a group for And/Or/Not, otherwise a single condition or set.
/// </summary>
public static class FilterSyntax
{
    private const int MaxRangeDays = 100_000;
    private const int MaxDepth = 100;   // nested parentheses / NOT chains: the parser is recursive and a stack overflow cannot be caught

    public static bool TryParse(string? text, out FilterNode? node, out string? error)
    {
        node = null;
        error = null;
        if (string.IsNullOrWhiteSpace(text)) { node = new FilterGroup(); return true; }
        try
        {
            var p = new Parser(text);
            var n = p.ParseAll();
            node = n;
            return true;
        }
        catch (FormatException ex)
        {
            error = ex.Message;
            return false;
        }
    }

    private enum T { Column, Str, Date, Word, LParen, RParen, Comma, Op, DotDot, Ellipsis, End }

    private readonly record struct Tok(T Kind, string Text, int Pos);

    private sealed class Parser
    {
        private readonly List<Tok> _toks = new();
        private int _i;
        private readonly string _src;

        public Parser(string src)
        {
            _src = src;
            Lex();
        }

        private static FormatException Err(string msg, int pos) => new($"{msg} at position {pos + 1}");

        private void Lex()
        {
            var s = _src;
            int i = 0, n = s.Length;
            while (i < n)
            {
                char c = s[i];
                int start = i;
                if (char.IsWhiteSpace(c)) { i++; continue; }
                switch (c)
                {
                    case '[':
                    {
                        int e = s.IndexOf(']', i + 1);
                        if (e < 0) throw Err("Unterminated column name", start);
                        _toks.Add(new Tok(T.Column, s.Substring(i + 1, e - i - 1), start));
                        i = e + 1;
                        break;
                    }
                    case '*': _toks.Add(new Tok(T.Column, FilterNode.AnyColumn, start)); i++; break;
                    case '\'':
                    {
                        var sb = new System.Text.StringBuilder();
                        i++;
                        bool closed = false;
                        while (i < n)
                        {
                            if (s[i] == '\'')
                            {
                                if (i + 1 < n && s[i + 1] == '\'') { sb.Append('\''); i += 2; continue; }
                                i++; closed = true; break;
                            }
                            sb.Append(s[i++]);
                        }
                        if (!closed) throw Err("Unterminated text value", start);
                        _toks.Add(new Tok(T.Str, sb.ToString(), start));
                        break;
                    }
                    case '#':
                    {
                        int e = s.IndexOf('#', i + 1);
                        if (e < 0) throw Err("Unterminated date", start);
                        _toks.Add(new Tok(T.Date, s.Substring(i + 1, e - i - 1), start));
                        i = e + 1;
                        break;
                    }
                    case '(': _toks.Add(new Tok(T.LParen, "(", start)); i++; break;
                    case ')': _toks.Add(new Tok(T.RParen, ")", start)); i++; break;
                    case ',': _toks.Add(new Tok(T.Comma, ",", start)); i++; break;
                    case '…': _toks.Add(new Tok(T.Ellipsis, "…", start)); i++; break;
                    case '=': _toks.Add(new Tok(T.Op, "=", start)); i++; break;
                    case '<':
                    case '>':
                        i++;
                        if (i < n && (s[i] == '=' || (c == '<' && s[i] == '>'))) i++;
                        _toks.Add(new Tok(T.Op, s.Substring(start, i - start), start));
                        break;
                    case '.':
                        if (i + 1 < n && s[i + 1] == '.') { _toks.Add(new Tok(T.DotDot, "..", start)); i += 2; break; }
                        throw Err("Unexpected '.'", start);
                    default:
                        if (char.IsLetter(c) || c == '_')
                        {
                            while (i < n && (char.IsLetterOrDigit(s[i]) || s[i] == '_')) i++;
                            _toks.Add(new Tok(T.Word, s.Substring(start, i - start), start));
                            break;
                        }
                        throw Err($"Unexpected character '{c}'", start);
                }
            }
            _toks.Add(new Tok(T.End, "", n));
        }

        private Tok Cur => _toks[_i];
        private bool IsWord(string w) => Cur.Kind == T.Word && Cur.Text.Equals(w, StringComparison.OrdinalIgnoreCase);

        private static string Describe(Tok t) => t.Kind switch
        {
            T.End => "end of text",
            T.Str => "'" + t.Text.Replace("'", "''") + "'",
            T.Column => t.Text == FilterNode.AnyColumn ? "*" : "[" + t.Text + "]",
            _ => "'" + t.Text + "'",
        };

        private Tok Expect(T kind, string what)
        {
            if (Cur.Kind != kind) throw Err($"Expected {what} but found {Describe(Cur)}", Cur.Pos);
            return _toks[_i++];
        }

        public FilterNode ParseAll()
        {
            var n = ParseOr();
            if (Cur.Kind != T.End)
            {
                if (Cur.Kind == T.RParen) throw Err("Unbalanced ')'", Cur.Pos);
                throw Err($"Unexpected {Describe(Cur)} after the expression", Cur.Pos);
            }
            return n;
        }

        private FilterNode ParseOr()
        {
            var first = ParseAnd();
            if (!IsWord("Or")) return first;
            var list = new List<FilterNode> { first };
            while (IsWord("Or")) { _i++; list.Add(ParseAnd()); }
            return MergeBlanks(new FilterGroup(FilterGroupKind.Or, list));
        }

        private FilterNode ParseAnd()
        {
            var first = ParseUnary();
            if (!IsWord("And")) return first;
            var list = new List<FilterNode> { first };
            while (IsWord("And")) { _i++; list.Add(ParseUnary()); }
            return new FilterGroup(FilterGroupKind.And, list);
        }

        /// <summary>"c In (..) Or IsNullOrEmpty(c)" (and InDays) is how value/date sets with blanks print; turn it back into one node.</summary>
        private static FilterNode MergeBlanks(FilterGroup g)
        {
            if (g.Children.Count == 2 && g.Children[1] is FilterCondition { Op: ColumnFilterMode.IsEmpty } e)
            {
                if (g.Children[0] is FilterValueSet { IncludeBlanks: false } vs && string.Equals(vs.Column, e.Column, StringComparison.Ordinal))
                    return new FilterValueSet(vs.Column, vs.Values, true);
                if (g.Children[0] is FilterDateSet { IncludeBlanks: false } ds && string.Equals(ds.Column, e.Column, StringComparison.Ordinal))
                    return new FilterDateSet(ds.Column, ds.Days, true);
            }
            return g;
        }

        private int _depth;

        private FilterNode ParseUnary()
        {
            if (IsWord("Not"))
            {
                if (++_depth > MaxDepth) throw Err("Expression is nested too deeply", Cur.Pos);
                _i++;
                FilterNode child;
                try { child = ParseUnary(); } finally { _depth--; }
                if (child is FilterCondition { Op: ColumnFilterMode.Contains } c) { c.Op = ColumnFilterMode.NotContains; return c; }
                if (child is FilterCondition { Op: ColumnFilterMode.IsEmpty } e) { e.Op = ColumnFilterMode.IsNotEmpty; return e; }
                if (child is FilterGroup { Negate: false } g && g.Children.Count > 1) { g.Negate = true; return g; }
                return FilterGroup.Not(child);
            }
            return ParsePrimary();
        }

        private FilterNode ParsePrimary()
        {
            var t = Cur;
            switch (t.Kind)
            {
                case T.LParen:
                {
                    _i++;
                    if (Cur.Kind == T.RParen) throw Err("Empty parentheses", Cur.Pos);
                    if (++_depth > MaxDepth) throw Err("Expression is nested too deeply", Cur.Pos);
                    FilterNode inner;
                    try { inner = ParseOr(); } finally { _depth--; }
                    if (Cur.Kind != T.RParen) throw Err(Cur.Kind == T.End ? "Missing ')'" : $"Expected ')' but found {Describe(Cur)}", Cur.Pos);
                    _i++;
                    return inner;
                }
                case T.Column:
                    _i++;
                    return ParseColumnPredicate(t.Text);
                case T.Word:
                    return ParseFunction();
                case T.End:
                    throw Err("Unexpected end of text, expected a condition", t.Pos);
                default:
                    throw Err($"Expected a condition but found {Describe(t)}", t.Pos);
            }
        }

        private FilterNode ParseFunction()
        {
            var name = Cur;
            _i++;
            var fn = name.Text.ToLowerInvariant();
            ColumnFilterMode? mode = fn switch
            {
                "contains" => ColumnFilterMode.Contains,
                "startswith" => ColumnFilterMode.BeginsWith,
                "endswith" => ColumnFilterMode.EndsWith,
                "regex" => ColumnFilterMode.Regex,
                "isnullorempty" => ColumnFilterMode.IsEmpty,
                _ => null,
            };
            if (mode == null)
                throw Err($"Unknown function '{name.Text}'", name.Pos);
            Expect(T.LParen, $"'(' after {name.Text}");
            var col = Expect(T.Column, "a column ([Name] or *)").Text;
            if (mode == ColumnFilterMode.IsEmpty)
            {
                Expect(T.RParen, "')'");
                return new FilterCondition(col, ColumnFilterMode.IsEmpty);
            }
            if (Cur.Kind != T.Comma) throw Err($"{name.Text} needs a second argument (the text to match)", Cur.Pos);
            _i++;
            var v = Expect(T.Str, "a quoted text value").Text;
            Expect(T.RParen, "')'");
            return new FilterCondition(col, mode.Value, v);
        }

        private FilterNode ParseColumnPredicate(string col)
        {
            var t = Cur;
            if (t.Kind == T.Op)
            {
                _i++;
                var v = Expect(T.Str, "a quoted text value").Text;
                return t.Text switch
                {
                    "=" => new FilterCondition(col, ColumnFilterMode.Equals, v),
                    "<>" => new FilterCondition(col, ColumnFilterMode.NotEquals, v),
                    ">" => new FilterCondition(col, ColumnFilterMode.Greater, v),
                    "<" => new FilterCondition(col, ColumnFilterMode.Less, v),
                    ">=" => new FilterCondition(col, ColumnFilterMode.Between, v),
                    _ => new FilterCondition(col, ColumnFilterMode.Between, "", v), // "<="
                };
            }
            if (t.Kind == T.Word)
            {
                if (IsWord("Like"))
                {
                    _i++;
                    return new FilterCondition(col, ColumnFilterMode.Like, Expect(T.Str, "a quoted text value").Text);
                }
                if (IsWord("Between"))
                {
                    _i++;
                    Expect(T.LParen, "'(' after Between");
                    var lo = Expect(T.Str, "a quoted lower bound").Text;
                    if (Cur.Kind != T.Comma) throw Err("Between needs two arguments (lower, upper)", Cur.Pos);
                    _i++;
                    var hi = Expect(T.Str, "a quoted upper bound").Text;
                    Expect(T.RParen, "')'");
                    return new FilterCondition(col, ColumnFilterMode.Between, lo, hi);
                }
                if (IsWord("In"))
                {
                    _i++;
                    Expect(T.LParen, "'(' after In");
                    var vals = new List<string>();
                    while (Cur.Kind != T.RParen)
                    {
                        if (vals.Count > 0) Expect(T.Comma, "',' or ')'");
                        if (Cur.Kind == T.Ellipsis) throw Err("This list was shortened for display and cannot be parsed back", Cur.Pos);
                        vals.Add(Expect(T.Str, "a quoted text value").Text);
                    }
                    _i++;
                    return new FilterValueSet(col, vals);
                }
                if (IsWord("InDays"))
                {
                    _i++;
                    Expect(T.LParen, "'(' after InDays");
                    var days = new HashSet<DateOnly>();
                    bool first = true;
                    while (Cur.Kind != T.RParen)
                    {
                        if (!first) Expect(T.Comma, "',' or ')'");
                        first = false;
                        if (Cur.Kind == T.Ellipsis) throw Err("This list was shortened for display and cannot be parsed back", Cur.Pos);
                        var a = Expect(T.Date, "a date like #2024-01-31#");
                        var da = ParseDay(a);
                        if (Cur.Kind == T.DotDot)
                        {
                            _i++;
                            var b = Expect(T.Date, "a date like #2024-01-31#");
                            var db = ParseDay(b);
                            if (db < da) throw Err("Date range ends before it starts", b.Pos);
                            if (db.DayNumber - da.DayNumber >= MaxRangeDays) throw Err("Date range is too long", b.Pos);
                            for (var d = da; d <= db; d = d.AddDays(1)) days.Add(d);
                        }
                        else days.Add(da);
                    }
                    _i++;
                    return new FilterDateSet(col, days);
                }
            }
            throw Err($"Expected an operator (=, <>, >, <, >=, <=, Like, In, InDays, Between) after the column but found {Describe(t)}", t.Pos);
        }

        private static DateOnly ParseDay(Tok t)
        {
            if (DateOnly.TryParseExact(t.Text, "yyyy-MM-dd", CultureInfo.InvariantCulture, DateTimeStyles.None, out var d)) return d;
            throw Err($"Invalid date '#{t.Text}#' (use yyyy-MM-dd)", t.Pos);
        }
    }
}
