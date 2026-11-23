using System.Globalization;

namespace EzForensics.Core.Time;

/// <summary>
/// The one timestamp parser for process-tree inputs (Volatility, Zeek, event-log and correlator CSVs). Results are always UTC
/// (<see cref="DateTimeKind.Utc"/>); text without an offset is read as UTC, text with an offset is converted. Unparseable or empty text is null.
/// Not for filter columns (FilterEngine) or the timeline (<see cref="Timeline.TimelineTimestamp"/>, which also yields null-date results).
/// </summary>
public static class TimeParse
{
    private const DateTimeStyles Utc = DateTimeStyles.AssumeUniversal | DateTimeStyles.AdjustToUniversal;

    /// <summary>
    /// Any date text the invariant culture reads (ISO with or without T / Z / offset, slash dates month-first as the invariant culture does).
    /// Plain epoch numbers (Zeek "1700000000.123") are not dates here and return null; callers handle epochs themselves.
    /// </summary>
    public static DateTime? Lenient(string? s) =>
        !string.IsNullOrEmpty(s) && DateTime.TryParse(s, CultureInfo.InvariantCulture, Utc, out var t) ? t : null;

    /// <summary>Exactly <paramref name="format"/> (the format the app itself wrote), nothing else.</summary>
    public static DateTime? Exact(string? s, string format) =>
        !string.IsNullOrEmpty(s) && DateTime.TryParseExact(s, format, CultureInfo.InvariantCulture, Utc, out var t) ? t : null;
}
