using System.Globalization;
using System.Text;
using System.Text.Json;

namespace EzForensics.Core.Zeek;

/// <summary>One converted log.</summary>
public sealed record ZeekLogConversion(string LogPath, string CsvPath, string LogName, long Rows, int Columns, string Format, string? Error)
{
    public bool Succeeded => Error == null;
}

/// <summary>
/// Converts Zeek ASCII logs (the default TSV format: <c>#separator</c>, <c>#set_separator</c>, <c>#empty_field</c>,
/// <c>#unset_field</c>, <c>#path</c>, <c>#fields</c>, <c>#types</c> headers) into CSV that the Results grid and the super
/// timeline read natively. JSON logs (one object per line) are converted too, as a fallback.
/// <list type="bullet">
/// <item>A <c>DateTimeUtc</c> column (<c>yyyy-MM-dd HH:mm:ss.fffffff</c>, UTC) is added first, from <c>ts</c> (epoch seconds).</item>
/// <item>Every field is kept, in Zeek's order; <c>ts</c> stays as the raw epoch value, other <c>time</c> fields become ISO UTC.</item>
/// <item>Unset (<c>-</c>) and empty (<c>(empty)</c>) values become empty cells.</item>
/// <item>set[...] / vector[...] fields keep their elements separated by <c>,</c> (Zeek's own set separator).</item>
/// <item>Zeek's <c>\xHH</c> escapes are decoded (printable ASCII and valid UTF-8 sequences; control bytes stay escaped).</item>
/// </list>
/// Logs are streamed line by line (multi-GB conn.log is fine) and written to <c>&lt;name&gt;.csv.tmp</c>, then renamed.
/// </summary>
public static class ZeekLogConverter
{
    public const string DateTimeColumn = "DateTimeUtc";
    public const string TimestampFormat = Timeline.TimelineTimestamp.OutputFormat;

    /// <summary>Converts every <c>*.log</c> directly in <paramref name="dir"/> (Zeek's output folder) to <c>&lt;name&gt;.csv</c>.</summary>
    public static IReadOnlyList<ZeekLogConversion> ConvertFolder(string dir, Action<string>? log = null, CancellationToken ct = default)
    {
        var results = new List<ZeekLogConversion>();
        if (!Directory.Exists(dir)) return results;
        foreach (var f in Directory.EnumerateFiles(dir, "*.log").OrderBy(f => f, StringComparer.OrdinalIgnoreCase))
        {
            ct.ThrowIfCancellationRequested();
            var r = Convert(f, null, ct);
            results.Add(r);
            log?.Invoke(r.Succeeded
                ? $"# Converted {Path.GetFileName(f)} -> {Path.GetFileName(r.CsvPath)} ({r.Rows:N0} rows, {r.Columns} columns, {r.Format})"
                : $"# Could not convert {Path.GetFileName(f)}: {r.Error}");
        }
        return results;
    }

    /// <summary>Converts one log; <paramref name="csvPath"/> defaults to the same name with <c>.csv</c>.</summary>
    public static ZeekLogConversion Convert(string logPath, string? csvPath = null, CancellationToken ct = default)
    {
        csvPath ??= Path.ChangeExtension(logPath, ".csv");
        var name = Path.GetFileNameWithoutExtension(logPath);
        var tmp = csvPath + ".tmp";
        try
        {
            string format;
            long rows;
            int cols;
            using (var input = new StreamReader(new FileStream(logPath, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete, 1 << 16),
                       new UTF8Encoding(false), detectEncodingFromByteOrderMarks: true, 1 << 16))
            {
                int first = input.Peek();
                if (first == '{')
                {
                    input.Dispose();
                    format = "json";
                    (rows, cols) = ConvertJson(logPath, tmp, ct);
                }
                else
                {
                    format = "tsv";
                    using var output = OpenOutput(tmp);
                    (rows, cols) = ConvertTsv(input, output, ct);
                }
            }
            File.Move(tmp, csvPath, overwrite: true);
            return new ZeekLogConversion(logPath, csvPath, name, rows, cols, format, null);
        }
        catch (OperationCanceledException) { TryDelete(tmp); throw; }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or InvalidDataException or JsonException)
        {
            TryDelete(tmp);
            return new ZeekLogConversion(logPath, csvPath, name, 0, 0, "?", ex.Message);
        }
    }

    private static StreamWriter OpenOutput(string path) =>
        new(new FileStream(path, FileMode.Create, FileAccess.Write, FileShare.Read, 1 << 16), new UTF8Encoding(false), 1 << 16) { NewLine = "\r\n" };

    // ------------------------------------------------------------------ TSV

    private sealed class TsvHeader
    {
        public char Separator = '\t';
        public string SetSeparator = ",";
        public string Empty = "(empty)";
        public string Unset = "-";
        public string[]? Fields;
        public string[]? Types;
    }

    /// <summary>Streams a TSV log into CSV; returns (data rows, output columns).</summary>
    public static (long Rows, int Columns) ConvertTsv(TextReader input, TextWriter output, CancellationToken ct = default)
    {
        var h = new TsvHeader();
        string[]? outHeaders = null;
        int[]? map = null;          // output column -> field index of the current header block (-1 = none)
        int tsField = -1;
        bool[] isTime = Array.Empty<bool>(), isContainer = Array.Empty<bool>();
        long rows = 0;
        var cells = new List<string>();
        string? line;
        while ((line = input.ReadLine()) != null)
        {
            if ((rows & 0x3FF) == 0) ct.ThrowIfCancellationRequested();
            if (line.Length == 0) continue;
            if (line[0] == '#')
            {
                ParseDirective(line, h);
                if (line.StartsWith("#types", StringComparison.Ordinal) && h.Fields != null)
                {
                    tsField = Array.IndexOf(h.Fields, "ts");
                    if (outHeaders == null)
                    {
                        outHeaders = (tsField >= 0 ? new[] { DateTimeColumn } : Array.Empty<string>()).Concat(h.Fields).ToArray();
                        WriteRow(output, outHeaders);
                    }
                    // (Re)map for this header block (concatenated logs may repeat or reorder the header).
                    map = MapColumns(outHeaders, h.Fields);
                    isTime = h.Fields.Select((_, i) => h.Types != null && i < h.Types.Length && h.Types[i] == "time").ToArray();
                    isContainer = h.Fields.Select((_, i) => h.Types != null && i < h.Types.Length &&
                                                             (h.Types[i].StartsWith("set[", StringComparison.Ordinal) || h.Types[i].StartsWith("vector[", StringComparison.Ordinal))).ToArray();
                }
                continue;
            }
            if (map == null || h.Fields == null)
            {
                if (outHeaders == null && h.Fields != null)
                {
                    // #fields without #types: treat every field as a string.
                    h.Types = h.Fields.Select(_ => "string").ToArray();
                    tsField = Array.IndexOf(h.Fields, "ts");
                    outHeaders = (tsField >= 0 ? new[] { DateTimeColumn } : Array.Empty<string>()).Concat(h.Fields).ToArray();
                    WriteRow(output, outHeaders);
                    map = MapColumns(outHeaders, h.Fields);
                    isTime = new bool[h.Fields.Length];
                    isContainer = new bool[h.Fields.Length];
                }
                else throw new InvalidDataException("Not a Zeek ASCII log: no #fields header before the first record.");
            }
            var raw = line.Split(h.Separator);
            cells.Clear();
            foreach (var idx in map)
            {
                if (idx == -2) { cells.Add(tsField >= 0 && tsField < raw.Length ? FormatEpoch(Value(raw[tsField], h, false)) : ""); continue; }
                if (idx < 0 || idx >= raw.Length) { cells.Add(""); continue; }
                var v = Value(raw[idx], h, isContainer[idx]);
                if (isTime[idx] && h.Fields[idx] != "ts" && v.Length > 0) v = FormatEpoch(v) is { Length: > 0 } iso ? iso : v;
                cells.Add(v);
            }
            WriteRow(output, cells);
            rows++;
        }
        if (outHeaders == null)
        {
            if (h.Fields == null) throw new InvalidDataException("Not a Zeek ASCII log: no #fields header.");
            outHeaders = h.Fields;
            WriteRow(output, outHeaders);
        }
        output.Flush();
        return (rows, outHeaders.Length);
    }

    /// <summary>Output column -> field index (-2 = the DateTimeUtc column built from ts, -1 = not in this header block).</summary>
    private static int[] MapColumns(string[] outHeaders, string[] fields) =>
        outHeaders.Select((c, i) => i == 0 && c == DateTimeColumn ? -2 : Array.IndexOf(fields, c)).ToArray();

    private static void ParseDirective(string line, TsvHeader h)
    {
        if (line.StartsWith("#separator", StringComparison.Ordinal))
        {
            var v = line.Length > 11 ? line[11..].Trim() : "\\x09";
            var d = Unescape(v);
            h.Separator = d.Length > 0 ? d[0] : '\t';
            return;
        }
        int sp = line.IndexOf(h.Separator);
        if (sp < 0) return;
        var key = line[..sp];
        var rest = line[(sp + 1)..];
        switch (key)
        {
            case "#set_separator": h.SetSeparator = Unescape(rest); break;
            case "#empty_field": h.Empty = Unescape(rest); break;
            case "#unset_field": h.Unset = Unescape(rest); break;
            case "#fields": h.Fields = rest.Split(h.Separator); h.Types = null; break;
            case "#types": h.Types = rest.Split(h.Separator); break;
        }
    }

    /// <summary>One raw TSV value -> CSV cell text.</summary>
    private static string Value(string raw, TsvHeader h, bool container)
    {
        if (raw == h.Unset || raw == h.Empty) return "";
        if (!container) return Unescape(raw);
        var parts = raw.Split(h.SetSeparator);
        for (int i = 0; i < parts.Length; i++) parts[i] = parts[i] == h.Unset ? "" : Unescape(parts[i]);
        return string.Join(",", parts);
    }

    /// <summary>
    /// Decodes Zeek's <c>\xHH</c> escapes: printable ASCII and tab become the character, runs of bytes &gt;= 0x80 become text
    /// when they are valid UTF-8; control bytes (and invalid UTF-8) stay as the literal escape so the CSV stays readable.
    /// </summary>
    public static string Unescape(string s)
    {
        int i = s.IndexOf("\\x", StringComparison.Ordinal);
        if (i < 0) return s;
        var sb = new StringBuilder(s.Length);
        sb.Append(s, 0, i);
        var bytes = new List<byte>();
        int runStart = -1;
        void FlushRun(int end)
        {
            if (bytes.Count == 0) return;
            try { sb.Append(new UTF8Encoding(false, throwOnInvalidBytes: true).GetString(bytes.ToArray())); }
            catch (DecoderFallbackException) { sb.Append(s, runStart, end - runStart); }
            bytes.Clear();
            runStart = -1;
        }
        while (i < s.Length)
        {
            if (i + 3 < s.Length && s[i] == '\\' && s[i + 1] == 'x' && IsHex(s[i + 2]) && IsHex(s[i + 3]))
            {
                int b = System.Convert.ToInt32(s.Substring(i + 2, 2), 16);
                if (b >= 0x80)
                {
                    if (runStart < 0) runStart = i;
                    bytes.Add((byte)b);
                    i += 4;
                    continue;
                }
                FlushRun(i);
                if (b == 0x09 || (b >= 0x20 && b < 0x7F)) sb.Append((char)b);
                else sb.Append(s, i, 4);
                i += 4;
                continue;
            }
            FlushRun(i);
            sb.Append(s[i]);
            i++;
        }
        FlushRun(s.Length);
        return sb.ToString();
    }

    private static bool IsHex(char c) => c is >= '0' and <= '9' or >= 'a' and <= 'f' or >= 'A' and <= 'F';

    /// <summary>Zeek epoch seconds ("1234567890.123456") -> "2009-02-13 23:31:30.1234560" (UTC); "" when not a time.</summary>
    public static string FormatEpoch(string epoch)
    {
        if (string.IsNullOrWhiteSpace(epoch)) return "";
        var s = epoch.Trim();
        int dot = s.IndexOf('.');
        var secPart = dot < 0 ? s : s[..dot];
        var fracPart = dot < 0 ? "" : s[(dot + 1)..];
        if (!long.TryParse(secPart, NumberStyles.AllowLeadingSign, CultureInfo.InvariantCulture, out var secs)) return "";
        if (fracPart.Length > 0 && !fracPart.All(char.IsAsciiDigit)) return "";
        if (secs < 0 || secs > 253_402_300_799L) return ""; // 9999-12-31
        long ticks = secs * TimeSpan.TicksPerSecond;
        if (fracPart.Length > 0)
        {
            var f = (fracPart + "0000000")[..7];
            ticks += long.Parse(f, CultureInfo.InvariantCulture);
        }
        try { return DateTime.UnixEpoch.AddTicks(ticks).ToString(TimestampFormat, CultureInfo.InvariantCulture); }
        catch (ArgumentOutOfRangeException) { return ""; }
    }

    // ------------------------------------------------------------------ JSON (LogAscii::use_json=T)

    private static (long Rows, int Columns) ConvertJson(string logPath, string tmp, CancellationToken ct)
    {
        // Pass 1: union of keys in first-seen order (Zeek omits unset fields per record).
        var keys = new List<string>();
        var seen = new HashSet<string>(StringComparer.Ordinal);
        foreach (var line in File.ReadLines(logPath))
        {
            ct.ThrowIfCancellationRequested();
            if (line.Length == 0 || line[0] != '{') continue;
            using var doc = JsonDocument.Parse(line);
            foreach (var p in doc.RootElement.EnumerateObject())
                if (seen.Add(p.Name)) keys.Add(p.Name);
        }
        bool hasTs = seen.Contains("ts");
        var headers = (hasTs ? new[] { DateTimeColumn } : Array.Empty<string>()).Concat(keys).ToArray();
        long rows = 0;
        using var output = OpenOutput(tmp);
        WriteRow(output, headers);
        var cells = new string[headers.Length];
        foreach (var line in File.ReadLines(logPath))
        {
            if ((rows & 0x3FF) == 0) ct.ThrowIfCancellationRequested();
            if (line.Length == 0 || line[0] != '{') continue;
            using var doc = JsonDocument.Parse(line);
            Array.Fill(cells, "");
            int off = hasTs ? 1 : 0;
            foreach (var p in doc.RootElement.EnumerateObject())
            {
                int k = keys.IndexOf(p.Name);
                if (k < 0) continue;
                cells[k + off] = JsonCell(p.Value);
            }
            if (hasTs && doc.RootElement.TryGetProperty("ts", out var ts))
                cells[0] = ts.ValueKind == JsonValueKind.Number ? FormatEpoch(ts.GetRawText())
                    : ts.ValueKind == JsonValueKind.String && DateTime.TryParse(ts.GetString(), CultureInfo.InvariantCulture,
                        DateTimeStyles.AdjustToUniversal | DateTimeStyles.AssumeUniversal, out var dt)
                        ? dt.ToString(TimestampFormat, CultureInfo.InvariantCulture) : "";
            WriteRow(output, cells);
            rows++;
        }
        return (rows, headers.Length);
    }

    private static string JsonCell(JsonElement e) => e.ValueKind switch
    {
        JsonValueKind.String => e.GetString() ?? "",
        JsonValueKind.Null or JsonValueKind.Undefined => "",
        JsonValueKind.True => "T",
        JsonValueKind.False => "F",
        JsonValueKind.Array => string.Join(",", e.EnumerateArray().Select(JsonCell)),
        _ => e.GetRawText(),
    };

    // ------------------------------------------------------------------ CSV

    private static void WriteRow(TextWriter w, IReadOnlyList<string> cells)
    {
        for (int i = 0; i < cells.Count; i++)
        {
            if (i > 0) w.Write(',');
            var c = cells[i];
            if (c.IndexOfAny(QuoteChars) >= 0) { w.Write('"'); w.Write(c.Replace("\"", "\"\"")); w.Write('"'); }
            else w.Write(c);
        }
        w.Write("\r\n");
    }

    private static readonly char[] QuoteChars = { ',', '"', '\r', '\n' };

    private static void TryDelete(string p)
    {
        try { if (File.Exists(p)) File.Delete(p); } catch (Exception) { }
    }
}
