using System.Globalization;
using System.Text;
using EzForensics.Core.Timeline;

namespace EzForensics.Core.Rita;

/// <summary>Outcome of <see cref="RitaResultConverter.Convert"/>.</summary>
/// <param name="Files">The CSV files written (always all four, header-only when there is nothing).</param>
/// <param name="DataSpanHours">Hours between the first and last imported record; null when unknown.</param>
public sealed record RitaConversion(IReadOnlyList<string> Files, int Threats, int Beacons, int LongConnections, int DnsTunneling,
    double? DataSpanHours, DateTime? FirstUtc, DateTime? LastUtc, bool NoData, string? RitaVersion, string? ClickHouseVersion, string? Database,
    int MissingSpans);

/// <summary>
/// Turns RITA's <c>rita view --stdout</c> CSV plus the enrichment files (<see cref="RitaCommandBuilder"/> work files) into
/// <c>Rita_Threats.csv</c> (every finding), <c>Rita_Beacons.csv</c> (beacon score &gt; 0 or strobe), <c>Rita_LongConnections.csv</c>
/// (long connection score &gt; 0) and <c>Rita_DnsTunneling.csv</c> (C2 over DNS score &gt; 0). Columns: DateTimeUtc (= FirstSeenUtc),
/// FirstSeenUtc, LastSeenUtc, Severity, Src, Dst, Fqdn, RITA's other columns (unchanged; "First Seen" renamed "First Seen (relative)"),
/// Captures. Missing first / last seen leaves the cells empty.
/// </summary>
public static class RitaResultConverter
{
    public const string ThreatsFile = "Rita_Threats.csv", BeaconsFile = "Rita_Beacons.csv", LongConnectionsFile = "Rita_LongConnections.csv",
        DnsTunnelingFile = "Rita_DnsTunneling.csv";

    public static readonly IReadOnlyList<string> OutputFiles = new[] { ThreatsFile, BeaconsFile, LongConnectionsFile, DnsTunnelingFile };

    /// <summary>RITA v5.1.2 <c>rita view --stdout</c> columns.</summary>
    public static readonly IReadOnlyList<string> UpstreamColumns = new[]
    {
        "Severity", "Source IP", "Destination IP", "FQDN", "Beacon Score", "Strobe", "Total Duration", "Long Connection Score", "Subdomains",
        "C2 Over DNS Score", "Threat Intel", "Prevalence", "First Seen", "Missing Host Header", "Connection Count", "Total Bytes",
        "Port:Proto:Service", "Modifiers",
    };

    public const string DateTimeColumn = "DateTimeUtc";
    public const string RelativeFirstSeenColumn = "First Seen (relative)";

    private static readonly HashSet<string> Replaced = new(StringComparer.Ordinal) { "Severity", "Source IP", "Destination IP", "FQDN" };

    /// <summary>
    /// Converts the work files in <paramref name="dir"/> and writes the four CSVs to <paramref name="outputDir"/> (null = <paramref name="dir"/>).
    /// <paramref name="captures"/> = capture names (single capture fallback for the Captures column).
    /// </summary>
    public static RitaConversion Convert(string dir, string? outputDir = null, IReadOnlyList<string>? captures = null, CancellationToken ct = default)
    {
        outputDir ??= dir;
        Directory.CreateDirectory(outputDir);
        var info = ReadInfo(Path.Combine(dir, RitaCommandBuilder.InfoFile));
        var (header, rows) = ReadView(Path.Combine(dir, RitaCommandBuilder.ViewFile));
        var pairs = ReadSpans(Path.Combine(dir, RitaCommandBuilder.PairsFile), "src", "dst");
        var sni = ReadSpans(Path.Combine(dir, RitaCommandBuilder.SniFile), "src", "fqdn");
        var dns = ReadSpans(Path.Combine(dir, RitaCommandBuilder.DnsFile), "fqdn", null);
        var span = ReadCaptureSpans(Path.Combine(dir, RitaCommandBuilder.SpanFile));
        var fallbackCaptures = captures is { Count: 1 } ? captures[0] : "";

        // RITA columns kept after our own (anything a later RITA adds is kept too).
        var extra = (header ?? UpstreamColumns.ToList()).Where(h => !Replaced.Contains(h)).ToList();
        var outHeader = new List<string> { DateTimeColumn, "FirstSeenUtc", "LastSeenUtc", "Severity", "Src", "Dst", "Fqdn" };
        outHeader.AddRange(extra.Select(h => h == "First Seen" ? RelativeFirstSeenColumn : h));
        outHeader.Add("Captures");

        var all = new List<string[]>();
        var beacons = new List<string[]>();
        var longConns = new List<string[]>();
        var dnsRows = new List<string[]>();
        int missing = 0;
        DateTime? minSeen = null, maxSeen = null;
        foreach (var r in rows)
        {
            ct.ThrowIfCancellationRequested();
            string Get(string col) => header == null ? "" : header.IndexOf(col) is var i and >= 0 && i < r.Length ? r[i] : "";
            var src = Ip(Get("Source IP"));
            var dst = Ip(Get("Destination IP"));
            var fqdn = Get("FQDN").Trim();
            Span? s = null;
            if (fqdn.Length > 0 && src.Length > 0) s = sni.GetValueOrDefault(Key(src, fqdn));
            if (s == null && fqdn.Length > 0) s = dns.GetValueOrDefault(Key(fqdn, ""));
            if (s == null && src.Length > 0 && dst.Length > 0) s = pairs.GetValueOrDefault(Key(src, dst));
            if (s == null) missing++;
            var first = s?.First is { } f ? TimelineTimestamp.Format(f) : "";
            var last = s?.Last is { } l ? TimelineTimestamp.Format(l) : "";
            if (s?.First is { } f2 && (minSeen == null || f2 < minSeen)) minSeen = f2;
            if (s?.Last is { } l2 && (maxSeen == null || l2 > maxSeen)) maxSeen = l2;
            var cells = new List<string> { first, first, last, Get("Severity"), src, dst, fqdn };
            cells.AddRange(extra.Select(Get));
            cells.Add(s?.Captures is { Length: > 0 } c ? c.Replace("|", "; ") : fallbackCaptures);
            var row = cells.ToArray();
            all.Add(row);
            if (Num(Get("Beacon Score")) > 0 || Get("Strobe").Equals("true", StringComparison.OrdinalIgnoreCase)) beacons.Add(row);
            if (Num(Get("Long Connection Score")) > 0) longConns.Add(row);
            if (Num(Get("C2 Over DNS Score")) > 0) dnsRows.Add(row);
        }

        var files = new List<string>
        {
            Write(Path.Combine(outputDir, ThreatsFile), outHeader, all),
            Write(Path.Combine(outputDir, BeaconsFile), outHeader, beacons),
            Write(Path.Combine(outputDir, LongConnectionsFile), outHeader, longConns),
            Write(Path.Combine(outputDir, DnsTunnelingFile), outHeader, dnsRows),
        };

        // Data span: the time the captures cover (union of each capture's first..last), not first..last overall: two short
        // captures years apart are still short captures for RITA's scoring.
        var intervals = span.Count > 0 ? span : (minSeen != null && maxSeen != null ? new List<(DateTime, DateTime)> { (minSeen.Value, maxSeen.Value) } : new());
        DateTime? dataFirst = intervals.Count > 0 ? intervals.Min(i => i.Item1) : null;
        DateTime? dataLast = intervals.Count > 0 ? intervals.Max(i => i.Item2) : null;
        double? hours = intervals.Count > 0 ? Math.Round(CoveredHours(intervals), 2) : null;
        return new RitaConversion(files, all.Count, beacons.Count, longConns.Count, dnsRows.Count, hours, dataFirst, dataLast,
            info.GetValueOrDefault("nodata") == "1", RitaLocator.ParseRitaVersion(info.GetValueOrDefault("rita.version")),
            RitaLocator.ParseClickHouseVersion(info.GetValueOrDefault("clickhouse.version")), info.GetValueOrDefault("rita.db"), missing);
    }

    /// <summary>Invariant "12.5" (the manifest detail <c>rita.dataSpanHours</c>).</summary>
    public static string FormatHours(double hours) => hours.ToString("0.##", CultureInfo.InvariantCulture);

    /// <summary>Hours covered by the union of the intervals.</summary>
    public static double CoveredHours(IEnumerable<(DateTime First, DateTime Last)> intervals)
    {
        double total = 0;
        DateTime? curStart = null, curEnd = null;
        foreach (var (f, l) in intervals.Where(i => i.Last >= i.First).OrderBy(i => i.First))
        {
            if (curEnd == null || f > curEnd) { if (curEnd != null) total += (curEnd.Value - curStart!.Value).TotalHours; curStart = f; curEnd = l; }
            else if (l > curEnd) curEnd = l;
        }
        if (curEnd != null) total += (curEnd.Value - curStart!.Value).TotalHours;
        return total;
    }

    /// <summary>Span file: capture, first_ts, last_ts per capture.</summary>
    private static List<(DateTime, DateTime)> ReadCaptureSpans(string path)
    {
        var list = new List<(string Capture, DateTime First, DateTime Last)>();
        if (!File.Exists(path)) return new();
        string[]? h = null;
        foreach (var rec in ReadRecords(File.ReadAllText(path, Encoding.UTF8)))
        {
            if (h == null) { h = rec; continue; }
            string V(string col) => Array.IndexOf(h, col) is var i and >= 0 && i < rec.Length ? rec[i] : "";
            if (Epoch(V("first_ts")) is { } f && Epoch(V("last_ts")) is { } l && l >= f) list.Add((V("capture"), f, l));
        }
        // records not linked to a capture only count when nothing is (they could stretch across several captures)
        if (list.Any(x => x.Capture.Length > 0)) list.RemoveAll(x => x.Capture.Length == 0);
        return list.Select(x => (x.First, x.Last)).ToList();
    }

    private sealed record Span(DateTime? First, DateTime? Last, string Captures);

    private static string Key(string a, string b) => a.ToLowerInvariant() + "\u0001" + b.ToLowerInvariant();

    /// <summary>"::" (RITA's empty address) → ""; "::ffff:1.2.3.4" → "1.2.3.4".</summary>
    public static string Ip(string? value)
    {
        var v = (value ?? "").Trim();
        if (v is "::" or "0.0.0.0") return "";
        return v.StartsWith("::ffff:", StringComparison.OrdinalIgnoreCase) && v.Count(c => c == '.') == 3 ? v[7..] : v;
    }

    private static double Num(string s) => double.TryParse(s, NumberStyles.Float, CultureInfo.InvariantCulture, out var d) ? d : 0;

    private static Dictionary<string, string> ReadInfo(string path)
    {
        var map = new Dictionary<string, string>(StringComparer.Ordinal);
        if (!File.Exists(path)) return map;
        foreach (var line in File.ReadAllLines(path))
        {
            int eq = line.IndexOf('=');
            if (eq > 0) map[line[..eq].Trim()] = line[(eq + 1)..].Trim();
        }
        return map;
    }

    /// <summary>The view CSV (lines before the "Severity,..." header, e.g. "Viewing database: x", are skipped). Header null when absent.</summary>
    public static (List<string>? Header, List<string[]> Rows) ReadView(string path)
    {
        var rows = new List<string[]>();
        if (!File.Exists(path)) return (null, rows);
        List<string>? header = null;
        foreach (var rec in ReadRecords(File.ReadAllText(path, Encoding.UTF8)))
        {
            if (header == null)
            {
                if (rec.Length > 3 && rec[0] == "Severity" && rec.Contains("Source IP")) header = rec.ToList();
                continue;
            }
            if (rec.Length == 1 && rec[0].Length == 0) continue;
            rows.Add(rec);
        }
        return (header, rows);
    }

    private static Dictionary<string, Span> ReadSpans(string path, string? k1, string? k2)
    {
        var map = new Dictionary<string, Span>(StringComparer.Ordinal);
        if (!File.Exists(path)) return map;
        string[]? h = null;
        foreach (var rec in ReadRecords(File.ReadAllText(path, Encoding.UTF8)))
        {
            if (h == null) { h = rec; continue; }
            string V(string? col) => col == null ? "" : Array.IndexOf(h, col) is var i and >= 0 && i < rec.Length ? rec[i] : "";
            var key = k1 == null ? "*" : Key(k1 is "src" or "dst" ? Ip(V(k1)) : V(k1), k2 is "src" or "dst" ? Ip(V(k2)) : V(k2));
            map[key] = new Span(Epoch(V("first_ts")), Epoch(V("last_ts")), V("captures"));
        }
        return map;
    }

    private static DateTime? Epoch(string s) =>
        long.TryParse(s, NumberStyles.AllowLeadingSign, CultureInfo.InvariantCulture, out var v) && v > 0 && v < 253_402_300_799L
            ? DateTime.UnixEpoch.AddSeconds(v) : null;

    /// <summary>RFC 4180 records (quoted fields with "" escapes and line breaks; CRLF or LF).</summary>
    public static IEnumerable<string[]> ReadRecords(string text)
    {
        var fields = new List<string>();
        var sb = new StringBuilder();
        bool quoted = false, any = false;
        for (int i = 0; i < text.Length; i++)
        {
            char c = text[i];
            if (quoted)
            {
                if (c == '"')
                {
                    if (i + 1 < text.Length && text[i + 1] == '"') { sb.Append('"'); i++; }
                    else quoted = false;
                }
                else sb.Append(c);
                continue;
            }
            switch (c)
            {
                case '"': quoted = true; any = true; break;
                case ',': fields.Add(sb.ToString()); sb.Clear(); any = true; break;
                case '\r': break;
                case '\n':
                    fields.Add(sb.ToString()); sb.Clear();
                    yield return fields.ToArray();
                    fields.Clear(); any = false;
                    break;
                default: sb.Append(c); any = true; break;
            }
        }
        if (any || sb.Length > 0 || fields.Count > 0)
        {
            fields.Add(sb.ToString());
            yield return fields.ToArray();
        }
    }

    private static string Write(string path, IReadOnlyList<string> header, IEnumerable<string[]> rows)
    {
        var tmp = path + ".tmp";
        using (var w = new StreamWriter(new FileStream(tmp, FileMode.Create, FileAccess.Write, FileShare.None), new UTF8Encoding(false)))
        {
            WriteRow(w, header);
            foreach (var r in rows) WriteRow(w, r);
        }
        File.Move(tmp, path, overwrite: true);
        return path;
    }

    private static readonly char[] QuoteChars = { ',', '"', '\r', '\n' };

    private static void WriteRow(TextWriter w, IReadOnlyList<string> cells)
    {
        for (int i = 0; i < cells.Count; i++)
        {
            if (i > 0) w.Write(',');
            var c = cells[i] ?? "";
            if (c.IndexOfAny(QuoteChars) >= 0) { w.Write('"'); w.Write(c.Replace("\"", "\"\"")); w.Write('"'); }
            else w.Write(c);
        }
        w.Write("\r\n");
    }
}
