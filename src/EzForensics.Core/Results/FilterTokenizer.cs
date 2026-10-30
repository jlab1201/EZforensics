namespace EzForensics.Core.Results;

public enum FilterTokenKind { Column, Function, Operator, String, Number, Date, Logical, Not, Paren, Comma, Whitespace, Unknown }

/// <summary>A lexical span of a filter expression. <see cref="Text"/> is the exact source slice.</summary>
public readonly record struct FilterToken(FilterTokenKind Kind, int Start, int Length, string Text);

/// <summary>
/// Lossless lexer for the text produced by <see cref="FilterNode.ToDisplayString"/> / FilterSpec.ToDisplayString.
/// Never throws; unterminated constructs run to end of input, unrecognised characters become <see cref="FilterTokenKind.Unknown"/>.
/// </summary>
public static class FilterTokenizer
{
    public static IReadOnlyList<FilterToken> Tokenize(string? input)
    {
        var list = new List<FilterToken>();
        if (string.IsNullOrEmpty(input)) return list;
        var s = input;
        int i = 0, n = s.Length;

        void Add(FilterTokenKind k, int start, int end) => list.Add(new FilterToken(k, start, end - start, s.Substring(start, end - start)));

        while (i < n)
        {
            char c = s[i];
            int start = i;
            if (char.IsWhiteSpace(c))
            {
                while (i < n && char.IsWhiteSpace(s[i])) i++;
                Add(FilterTokenKind.Whitespace, start, i);
            }
            else if (c == '[')
            {
                i++;
                while (i < n && s[i] != ']') i++;
                if (i < n) i++;
                Add(FilterTokenKind.Column, start, i);
            }
            else if (c == '\'')
            {
                i++;
                while (i < n)
                {
                    if (s[i] == '\'')
                    {
                        if (i + 1 < n && s[i + 1] == '\'') { i += 2; continue; }
                        i++;
                        break;
                    }
                    i++;
                }
                Add(FilterTokenKind.String, start, i);
            }
            else if (c == '#')
            {
                i++;
                while (i < n && s[i] != '#') i++;
                if (i < n) i++;
                Add(FilterTokenKind.Date, start, i);
            }
            else if (c == '(' || c == ')')
            {
                i++;
                Add(FilterTokenKind.Paren, start, i);
            }
            else if (c == ',')
            {
                i++;
                Add(FilterTokenKind.Comma, start, i);
            }
            else if (char.IsDigit(c) || ((c == '-' || c == '+') && i + 1 < n && char.IsDigit(s[i + 1])))
            {
                i++;
                while (i < n && (char.IsDigit(s[i]) || (s[i] == '.' && i + 1 < n && char.IsDigit(s[i + 1])))) i++;
                Add(FilterTokenKind.Number, start, i);
            }
            else if (c == '=' || c == '<' || c == '>' || c == '?' || c == '*')
            {
                i++;
                if ((c == '<' || c == '>') && i < n && (s[i] == '=' || (c == '<' && s[i] == '>'))) i++;
                Add(FilterTokenKind.Operator, start, i);
            }
            else if (c == '.' && i + 1 < n && s[i + 1] == '.')
            {
                i += 2;
                Add(FilterTokenKind.Operator, start, i);
            }
            else if (char.IsLetter(c) || c == '_')
            {
                while (i < n && (char.IsLetterOrDigit(s[i]) || s[i] == '_')) i++;
                var word = s.AsSpan(start, i - start);
                FilterTokenKind kind;
                if (word.Equals("And", StringComparison.OrdinalIgnoreCase) || word.Equals("Or", StringComparison.OrdinalIgnoreCase)) kind = FilterTokenKind.Logical;
                else if (word.Equals("Not", StringComparison.OrdinalIgnoreCase)) kind = FilterTokenKind.Not;
                else if (word.Equals("Like", StringComparison.OrdinalIgnoreCase) || word.Equals("In", StringComparison.OrdinalIgnoreCase) || word.Equals("Between", StringComparison.OrdinalIgnoreCase)) kind = FilterTokenKind.Operator;
                else
                {
                    int j = i;
                    while (j < n && char.IsWhiteSpace(s[j])) j++;
                    kind = j < n && s[j] == '(' ? FilterTokenKind.Function : FilterTokenKind.Unknown;
                }
                Add(kind, start, i);
            }
            else
            {
                i++;
                Add(FilterTokenKind.Unknown, start, i);
            }
        }
        return list;
    }
}
