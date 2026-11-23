using System.Globalization;
using System.Text;
using System.Text.Json.Serialization;
using EzForensics.Core.Timeline;

namespace EzForensics.Core.Results;

/// <summary>
/// Per-file statistics shown as columns in the Results case tree (and cached in <c>&lt;file&gt;.ezstats.json</c>):
/// data rows and the earliest / latest timestamp of the column the Results grid auto-detects.
/// </summary>
public sealed class FileStats
{
    public const int CurrentSchemaVersion = 2;

    public int SchemaVersion { get; set; } = CurrentSchemaVersion;
    /// <summary>File size and last-write time the stats were computed for (cache validation).</summary>
    public long Size { get; set; }
    public DateTime LastWriteUtc { get; set; }
    public bool IsCsv { get; set; }
    /// <summary>Timestamp column used (<see cref="TimestampColumnGuesser"/>), null when none was detected.</summary>
    public string? TimestampColumn { get; set; }
    /// <summary>Data rows excluding the header (null for non-CSV files).</summary>
    public long? Events { get; set; }
    /// <summary>Min / max parseable timestamp (UTC); null when there is none.</summary>
    public DateTime? Earliest { get; set; }
    public DateTime? Latest { get; set; }
    /// <summary>Timestamps outside <see cref="TimestampPlausibility"/> left out of Earliest / Latest.</summary>
    public long ImplausibleCount { get; set; }

    /// <summary>True when the stats belong to a file of this size and last-write time and the current schema.</summary>
    public bool Matches(long size, DateTime lastWriteUtc) =>
        SchemaVersion == CurrentSchemaVersion && Size == size && LastWriteUtc.Ticks == lastWriteUtc.Ticks;

    [JsonIgnore]
    public StatsSummary Summary => new(Earliest, Latest, Events, Size, ImplausibleCount);
}

/// <summary>Values of one tree row (a file or the aggregate of a folder).</summary>
public readonly record struct StatsSummary(DateTime? Earliest, DateTime? Latest, long? Events, long Size, long Implausible = 0);

/// <summary>
/// Plausibility window for the Earliest / Latest tree stats: [1980-01-01, now + 1 year]. Epoch-zero (1970), FILETIME-zero
/// (1601) and far-future garbage fall outside it. Only stats are affected; rows are never dropped from grids or the super
/// timeline. Placeholders inside the window (e.g. 2001-01-01) are kept: they cannot be told apart from real values.
/// </summary>
public static class TimestampPlausibility
{
    public static readonly DateTime MinUtc = new(1980, 1, 1, 0, 0, 0, DateTimeKind.Utc);
    public static DateTime MaxUtc(DateTime nowUtc) => nowUtc.AddYears(1);
    public static bool IsPlausible(DateTime utc, DateTime nowUtc) => utc >= MinUtc && utc <= MaxUtc(nowUtc);
    public static string Describe(long implausible) => implausible <= 0 ? "" :
        $"{implausible:N0} implausible timestamp{(implausible == 1 ? "" : "s")} ignored (before 1980 or over a year ahead)";
}

/// <summary>Folder rows: min of Earliest, max of Latest, sums of Events and Size over the descendants.</summary>
public static class FileStatsAggregator
{
    /// <summary>
    /// Combines file or folder summaries. Earliest / Latest stay null when no item has timestamps, Events stays null
    /// when no item has a row count (no CSV). Combining folder results gives the same as combining all their files.
    /// </summary>
    public static StatsSummary Combine(IEnumerable<StatsSummary> items)
    {
        DateTime? min = null, max = null;
        long? events = null;
        long size = 0, implausible = 0;
        foreach (var s in items)
        {
            size += s.Size;
            implausible += s.Implausible;
            if (s.Events is { } e) events = (events ?? 0) + e;
            if (s.Earliest is { } a && (min == null || a < min)) min = a;
            if (s.Latest is { } b && (max == null || b > max)) max = b;
        }
        return new StatsSummary(min, max, events, size, implausible);
    }
}

/// <summary>Display text of the tree columns.</summary>
public static class FileStatsFormat
{
    /// <summary>No value (no timestamp column, not a CSV).</summary>
    public const string None = "-";
    /// <summary>Still being computed.</summary>
    public const string Pending = "…";
    /// <summary>Used when the date format setting is empty (the EZ tools' own default has 7 fraction digits).</summary>
    public const string DefaultTimestampFormat = "yyyy-MM-dd HH:mm:ss";

    /// <summary>UTC timestamp in <paramref name="format"/> (empty/invalid = <see cref="DefaultTimestampFormat"/>), "-" for null.</summary>
    public static string Timestamp(DateTime? utc, string? format)
    {
        if (utc is not { } v) return None;
        v = DateTime.SpecifyKind(v, DateTimeKind.Utc);
        if (!string.IsNullOrWhiteSpace(format))
        {
            try { return v.ToString(format, CultureInfo.InvariantCulture); }
            catch (FormatException) { }
        }
        return v.ToString(DefaultTimestampFormat, CultureInfo.InvariantCulture);
    }

    /// <summary>Row count with thousands separators, "-" for null.</summary>
    public static string Events(long? n, IFormatProvider? culture = null) =>
        n is { } v ? v.ToString("N0", culture ?? CultureInfo.CurrentCulture) : None;

    public static string Size(long b) =>
        b >= 1L << 30 ? $"{b / (double)(1L << 30):0.0} GB" :
        b >= 1L << 20 ? $"{b / (double)(1L << 20):0.0} MB" :
        b >= 1L << 10 ? $"{b / 1024.0:0} KB" : $"{b} B";
}

/// <summary>Computes <see cref="FileStats"/> with one streaming pass over the file.</summary>
public static class FileStatsCalculator
{
    private const int BufferSize = 1024 * 1024;
    private const int MaxTimestampBytes = 64;

    public static bool IsCsvPath(string path) => path.EndsWith(".csv", StringComparison.OrdinalIgnoreCase);

    public static FileStats Compute(string path, CancellationToken ct = default)
    {
        var fi = new FileInfo(path);
        fi.Refresh();
        var stats = new FileStats { Size = fi.Length, LastWriteUtc = fi.LastWriteTimeUtc, IsCsv = IsCsvPath(path) };
        if (!stats.IsCsv) return stats;

        var headers = CsvIndex.ReadHeaderNames(path);
        var tsColumn = TimestampColumnGuesser.Guess(headers, path);
        int tsIdx = -1;
        if (tsColumn != null)
            for (int i = 0; i < headers.Length; i++)
                if (string.Equals(headers[i], tsColumn, StringComparison.OrdinalIgnoreCase)) { tsIdx = i; break; }
        stats.TimestampColumn = tsIdx >= 0 ? tsColumn : null;
        Scan(path, tsIdx, Timeline.SlashDates.IsL2tcsvHeader(headers), stats, ct);
        return stats;
    }

    /// <summary>
    /// Counts records exactly like <see cref="CsvIndex"/> (RFC 4180, quoted fields may span lines, blank lines skipped,
    /// first record = header) and collects the min / max of field <paramref name="tsIdx"/>.
    /// </summary>
    private static void Scan(string path, int tsIdx, bool monthFirst, FileStats stats, CancellationToken ct)
    {
        using var fs = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete, 4096, FileOptions.SequentialScan);
        var buf = new byte[BufferSize];
        var fb = new byte[MaxTimestampBytes];
        int fl = 0;
        bool overflow = false;
        long events = 0;
        DateTime? min = null, max = null;
        long implausible = 0;
        var nowUtc = DateTime.UtcNow;
        bool headerSeen = false, isData = false;
        bool atRecordStart = true, atFieldStart = true, inQuotes = false, quoteSeen = false;
        int field = 0;
        bool first = true;

        void Append(byte b)
        {
            if (fl < fb.Length) fb[fl++] = b; else overflow = true;
        }

        void Finish()
        {
            if (fl > 0 && !overflow)
            {
                var s = Encoding.UTF8.GetString(fb, 0, fl);
                if (TimelineTimestamp.TryParse(s, out var utc, monthFirstSlash: monthFirst) == TimestampParseResult.Ok)
                {
                    if (!TimestampPlausibility.IsPlausible(utc, nowUtc)) implausible++;
                    else
                    {
                        if (min == null || utc < min) min = utc;
                        if (max == null || utc > max) max = utc;
                    }
                }
            }
            fl = 0;
            overflow = false;
        }

        int n;
        while ((n = fs.Read(buf, 0, buf.Length)) > 0)
        {
            ct.ThrowIfCancellationRequested();
            int i = 0;
            if (first)
            {
                first = false;
                if (n >= 3 && buf[0] == 0xEF && buf[1] == 0xBB && buf[2] == 0xBF) i = 3;
            }
            for (; i < n; i++)
            {
                byte b = buf[i];
                if (inQuotes)
                {
                    if (quoteSeen)
                    {
                        quoteSeen = false;
                        if (b == (byte)'"')
                        {
                            if (isData && field == tsIdx) Append(b); // escaped quote
                            continue;
                        }
                        inQuotes = false; // closing quote; handle b below
                    }
                    else
                    {
                        if (b == (byte)'"') quoteSeen = true;
                        else if (isData && field == tsIdx) Append(b);
                        continue;
                    }
                }

                if (atRecordStart)
                {
                    if (b == (byte)'\n' || b == (byte)'\r') continue;
                    if (!headerSeen) { headerSeen = true; isData = false; }
                    else { events++; isData = true; }
                    atRecordStart = false;
                    atFieldStart = true;
                    field = 0;
                    fl = 0;
                    overflow = false;
                }

                if (b == (byte)'\n')
                {
                    if (isData && field == tsIdx) Finish();
                    atRecordStart = true;
                    atFieldStart = true;
                }
                else if (b == (byte)',')
                {
                    if (isData && field == tsIdx) Finish();
                    field++;
                    atFieldStart = true;
                }
                else if (b == (byte)'"' && atFieldStart) { inQuotes = true; atFieldStart = false; }
                else if (b != (byte)'\r')
                {
                    atFieldStart = false;
                    if (isData && field == tsIdx) Append(b);
                }
            }
        }
        if (!atRecordStart && isData && field == tsIdx) Finish(); // last record without a trailing newline

        stats.Events = events;
        stats.Earliest = min;
        stats.Latest = max;
        stats.ImplausibleCount = implausible;
    }
}
