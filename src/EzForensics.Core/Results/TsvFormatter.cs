using System.Globalization;
using System.Text;

namespace EzForensics.Core.Results;

/// <summary>Builds tab-separated text (header row + rows) for the clipboard.</summary>
public static class TsvFormatter
{
    private static readonly char[] Special = { '\t', '\r', '\n' };

    /// <summary>
    /// Tabs and line breaks inside a value become spaces so every row stays on one line. With <paramref name="escapeFormulas"/>, a value that
    /// Excel would run as a formula when pasted (first character <c>= + - @</c>) gets a leading single quote (<c>'=HYPERLINK(...)</c>);
    /// plain numbers (<c>-5</c>, <c>+1.5e3</c>) are left alone.
    /// </summary>
    public static string Clean(string? v, bool escapeFormulas = false)
    {
        if (string.IsNullOrEmpty(v)) return "";
        var s = v.IndexOfAny(Special) < 0 ? v : v.Replace('\t', ' ').Replace('\r', ' ').Replace('\n', ' ');
        return escapeFormulas && IsFormulaLike(s) ? "'" + s : s;
    }

    /// <summary>First character is <c>= + - @</c> and the value is not just a number.</summary>
    public static bool IsFormulaLike(string? v)
    {
        if (string.IsNullOrEmpty(v) || v[0] is not ('=' or '+' or '-' or '@')) return false;
        if (v[0] is '+' or '-' && double.TryParse(v, NumberStyles.Float, CultureInfo.InvariantCulture, out _)) return false;
        return true;
    }

    public static void AppendRow(StringBuilder sb, IReadOnlyList<string> cells, IReadOnlyList<int> columns, bool escapeFormulas = false)
    {
        for (int k = 0; k < columns.Count; k++)
        {
            if (k > 0) sb.Append('\t');
            int c = columns[k];
            sb.Append(Clean((uint)c < (uint)cells.Count ? cells[c] : "", escapeFormulas));
        }
        sb.Append("\r\n");
    }

    /// <summary>Header row of the chosen columns followed by the rows, stopping before <paramref name="maxChars"/> is exceeded.</summary>
    /// <param name="rowsWritten">Rows written (the header is not counted).</param>
    /// <returns>True when every row fitted.</returns>
    /// <param name="escapeFormulas">Prefix formula-like cells with <c>'</c> (<see cref="Clean"/>; AppSettings.EscapeFormulasOnCopy).</param>
    public static bool Build(IReadOnlyList<string> headers, IReadOnlyList<int> columns, IEnumerable<string[]> rows, int maxChars,
        out string text, out int rowsWritten, CancellationToken ct = default, bool escapeFormulas = false)
    {
        var sb = new StringBuilder();
        AppendRow(sb, headers, columns, escapeFormulas);
        rowsWritten = 0;
        bool complete = true;
        foreach (var r in rows)
        {
            ct.ThrowIfCancellationRequested();
            int before = sb.Length;
            AppendRow(sb, r, columns, escapeFormulas);
            if (sb.Length > maxChars) { sb.Length = before; complete = false; break; }
            rowsWritten++;
        }
        text = sb.ToString();
        return complete;
    }
}
