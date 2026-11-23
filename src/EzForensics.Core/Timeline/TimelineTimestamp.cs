using System.Globalization;

namespace EzForensics.Core.Timeline;

public enum TimestampParseResult { Empty, NullDate, Ok, Invalid }

/// <summary>Parsing and formatting of EZ tool timestamps (all treated as UTC).</summary>
public static class TimelineTimestamp
{
    public const string OutputFormat = "yyyy-MM-dd HH:mm:ss.fffffff";

    private static readonly string[] ExactFormats =
    {
        "yyyy-MM-dd HH:mm:ss.FFFFFFF", "yyyy-MM-ddTHH:mm:ss.FFFFFFF", "yyyy-MM-ddTHH:mm:ss.FFFFFFFZ",
        "yyyy-MM-dd HH:mm:ss.FFFFFFFZ", "yyyy-MM-dd HH:mm", "yyyy-MM-dd", "yyyy/MM/dd HH:mm:ss.FFFFFFF",
    };

    public static string Format(DateTime utc) => utc.ToString(OutputFormat, CultureInfo.InvariantCulture);

    /// <summary>
    /// Parses an EZ timestamp. Fast path for <c>yyyy-MM-dd HH:mm:ss[.fffffff]</c>; when <paramref name="strict"/>
    /// is false a few other invariant formats are accepted too. 1601-01-01 / 0001-01-01 are "null" dates.
    /// An ambiguous slash date (<c>01/02/2020</c>) is Invalid unless <paramref name="monthFirstSlash"/> (a Plaso l2tcsv file: MM/dd/yyyy by spec).
    /// </summary>
    public static TimestampParseResult TryParse(string? s, out DateTime utc, bool strict = false, bool monthFirstSlash = false)
    {
        utc = default;
        if (s == null) return TimestampParseResult.Empty;
        var span = s.AsSpan().Trim();
        if (span.Length == 0) return TimestampParseResult.Empty;

        if (!TryFast(span, out utc))
        {
            if (strict) return TimestampParseResult.Invalid;
            if (!(span.Length >= 8 && char.IsAsciiDigit(span[0])) ||
                !SlashDates.TryNormalize(span, monthFirstSlash, out var text) ||
                !(DateTime.TryParseExact(text ?? span.ToString(), ExactFormats, CultureInfo.InvariantCulture,
                      DateTimeStyles.AssumeUniversal | DateTimeStyles.AdjustToUniversal, out utc) ||
                  DateTime.TryParse(text ?? span.ToString(), CultureInfo.InvariantCulture,
                      DateTimeStyles.AssumeUniversal | DateTimeStyles.AdjustToUniversal, out utc)))
                return TimestampParseResult.Invalid;
            utc = DateTime.SpecifyKind(utc, DateTimeKind.Utc);
        }
        if (utc.Year <= 1601 && utc.Month == 1 && utc.Day == 1 || utc.Year <= 1) return TimestampParseResult.NullDate;
        return TimestampParseResult.Ok;
    }

    /// <summary><c>yyyy-MM-dd[ T]HH:mm:ss[.f{1,7}][Z]</c> without allocation.</summary>
    private static bool TryFast(ReadOnlySpan<char> s, out DateTime utc)
    {
        utc = default;
        if (s.Length < 19 || s[4] != '-' || s[7] != '-' || (s[10] != ' ' && s[10] != 'T') || s[13] != ':' || s[16] != ':') return false;
        if (!D(s, 0, 4, out int y) || !D(s, 5, 2, out int mo) || !D(s, 8, 2, out int d) ||
            !D(s, 11, 2, out int h) || !D(s, 14, 2, out int mi) || !D(s, 17, 2, out int sec)) return false;
        long frac = 0;
        int p = 19;
        if (p < s.Length && s[p] == '.')
        {
            p++;
            int digits = 0;
            while (p < s.Length && char.IsAsciiDigit(s[p]))
            {
                if (digits < 7) { frac = frac * 10 + (s[p] - '0'); digits++; }
                p++;
            }
            if (digits == 0) return false;
            for (int k = digits; k < 7; k++) frac *= 10;
        }
        if (p < s.Length && (s[p] == 'Z' || s[p] == 'z')) p++;
        else if (p + 6 == s.Length && (s[p] == '+' || s[p] == '-') && s.Slice(p + 1).SequenceEqual("00:00")) p += 6;
        if (p != s.Length) return false;
        if (y < 1 || mo < 1 || mo > 12 || d < 1 || d > DateTime.DaysInMonth(y, mo) || h > 23 || mi > 59 || sec > 59) return false;
        utc = new DateTime(y, mo, d, h, mi, sec, DateTimeKind.Utc).AddTicks(frac);
        return true;
    }

    private static bool D(ReadOnlySpan<char> s, int start, int len, out int v)
    {
        v = 0;
        for (int i = start; i < start + len; i++)
        {
            char c = s[i];
            if (c < '0' || c > '9') return false;
            v = v * 10 + (c - '0');
        }
        return true;
    }
}

/// <summary>
/// Slash dates (<c>aa/bb/yyyy</c>) are never guessed: when either part could be the month or the day the text stays unparsed,
/// unless month-first is allowed (Plaso l2tcsv). A part above 12 settles it (<c>13/02/2020</c> is day-first).
/// </summary>
public static class SlashDates
{
    /// <summary>The first row of a Plaso l2tcsv file: <c>date,time,timezone,MACB,...</c>, whose date column is MM/dd/yyyy by spec.</summary>
    public static bool IsL2tcsvHeader(IReadOnlyList<string> headers) =>
        headers.Count >= 4 && Is(headers[0], "date") && Is(headers[1], "time") && Is(headers[2], "timezone") && Is(headers[3], "MACB");

    private static bool Is(string h, string name) => string.Equals(h?.Trim(), name, StringComparison.OrdinalIgnoreCase);

    /// <summary>
    /// False when <paramref name="s"/> is an ambiguous or impossible slash date. Otherwise <paramref name="text"/> is the text to parse with the
    /// invariant (month-first) culture: null when unchanged, or the day-first date rewritten month-first.
    /// </summary>
    public static bool TryNormalize(ReadOnlySpan<char> s, bool monthFirst, out string? text)
    {
        text = null;
        int i = 0;
        if (!Num(s, ref i, out int a) || i >= s.Length || !IsSep(s[i])) return true; // not aa/bb/yyyy
        char sep = s[i++];
        if (!Num(s, ref i, out int b) || i >= s.Length || s[i] != sep) return true;
        int rest = i + 1;
        if (rest >= s.Length || !char.IsAsciiDigit(s[rest])) return true;
        if (a > 12 && b > 12) return false;
        if (a > 12) { text = string.Concat(b.ToString(CultureInfo.InvariantCulture), sep.ToString(), a.ToString(CultureInfo.InvariantCulture), s.Slice(i).ToString()); return true; }
        if (b > 12 || a == b) return true;
        return monthFirst;
    }

    private static bool IsSep(char c) => c is '/' or '-' or '.';

    private static bool Num(ReadOnlySpan<char> s, ref int i, out int v)
    {
        v = 0;
        int start = i;
        while (i < s.Length && i - start < 3 && char.IsAsciiDigit(s[i])) { v = v * 10 + (s[i] - '0'); i++; }
        return i > start && i - start <= 2;
    }
}
