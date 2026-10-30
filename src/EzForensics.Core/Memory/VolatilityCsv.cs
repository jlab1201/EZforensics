using System.Globalization;
using System.Text;
using System.Text.RegularExpressions;
using EzForensics.Core.Timeline;

namespace EzForensics.Core.Memory;

/// <summary>What <see cref="VolatilityCsv.Normalize"/> did.</summary>
public sealed record VolatilityCsvResult(IReadOnlyList<string> Headers, long Rows, int BannerLinesStripped, long TimestampsConverted);

/// <summary>
/// Turns Volatility's raw <c>-r csv</c> stdout into a plain RFC 4180 CSV: the banner (<c>Volatility 3 Framework x.y.z</c>) and anything else
/// before the header (<c>TreeDepth,...</c>) is dropped, Python's backslash escapes are undone, and timestamps
/// (<c>2024-01-02 03:04:05.000000 UTC</c>, <c>... +00:00</c>) become <see cref="TimelineTimestamp.OutputFormat"/> (UTC).
/// Absent values (<c>N/A</c>, <c>-</c>, <c>None</c>) in time columns become empty.
/// </summary>
public static class VolatilityCsv
{
    /// <summary>First header column of every Volatility CSV.</summary>
    public const string FirstHeader = "TreeDepth";

    private static readonly Regex TimeHeaderRx = new(@"time|date|created|updated|timestamp|written",
        RegexOptions.IgnoreCase | RegexOptions.CultureInvariant | RegexOptions.Compiled);

    private static readonly Regex VolDateRx = new(
        @"^(?<b>\d{4}-\d{2}-\d{2}[ T]\d{2}:\d{2}:\d{2}(?:\.\d{1,9})?)(?<utc>\s*UTC)?(?<off>\s*(?:[+-]\d{2}:?\d{2}|Z))?$",
        RegexOptions.CultureInvariant | RegexOptions.Compiled);

    /// <summary>Normalises <paramref name="rawCsvPath"/> into <paramref name="outCsvPath"/> (the same path = in place).</summary>
    /// <exception cref="InvalidDataException">No output, or no CSV header line.</exception>
    public static VolatilityCsvResult Normalize(string rawCsvPath, string outCsvPath, MemoryPluginDef? plugin = null, CancellationToken ct = default)
    {
        bool inPlace = string.Equals(Path.GetFullPath(rawCsvPath), Path.GetFullPath(outCsvPath), StringComparison.OrdinalIgnoreCase);
        var target = inPlace ? outCsvPath + ".tmp" : outCsvPath;
        VolatilityCsvResult result;
        try
        {
            using (var reader = new StreamReader(new FileStream(rawCsvPath, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete, 1 << 16),
                       new UTF8Encoding(false, false), detectEncodingFromByteOrderMarks: true, 1 << 16))
            using (var writer = new StreamWriter(new FileStream(target, FileMode.Create, FileAccess.Write, FileShare.None, 1 << 16), new UTF8Encoding(false)))
            {
                result = Normalize(reader, writer, plugin, ct);
            }
            if (inPlace) File.Move(target, outCsvPath, overwrite: true);
        }
        catch
        {
            if (inPlace) try { File.Delete(target); } catch (Exception) { }
            throw;
        }
        return result;
    }

    /// <summary>Stream form of <see cref="Normalize(string,string,MemoryPluginDef?,CancellationToken)"/>.</summary>
    public static VolatilityCsvResult Normalize(TextReader reader, TextWriter writer, MemoryPluginDef? plugin = null, CancellationToken ct = default)
    {
        // 1. Banner / pre-header lines.
        int stripped = 0;
        string? firstLine = null;
        List<string>? headers = null;
        string? line;
        while ((line = reader.ReadLine()) != null)
        {
            ct.ThrowIfCancellationRequested();
            if (IsHeaderLine(line))
            {
                headers = ParseSingleLine(line);
                break;
            }
            if (firstLine == null && line.Trim().Length > 0) firstLine = line.Trim();
            stripped++;
        }
        if (headers == null)
        {
            var what = plugin != null ? $" for {plugin.Plugin}" : "";
            throw new InvalidDataException(firstLine == null
                ? $"Volatility produced no output{what}."
                : $"Volatility wrote no CSV header{what} (first line: {Clip(firstLine)}).");
        }

        var timeCols = headers.Select(h => TimeHeaderRx.IsMatch(h)).ToArray();
        WriteRecord(writer, headers);
        long rows = 0, converted = 0;
        var parser = new RecordParser(reader);
        while (parser.Next() is { } rec)
        {
            if ((rows & 0x3FF) == 0) ct.ThrowIfCancellationRequested();
            if (rec.Count == 1 && rec[0].Length == 0) continue;   // blank line
            for (int i = 0; i < rec.Count; i++)
            {
                bool timeCol = i < timeCols.Length && timeCols[i];
                var v = rec[i];
                if (timeCol && (v == "N/A" || v == "-" || v == "None")) { rec[i] = ""; continue; }
                if (v.Length < 19 || !char.IsAsciiDigit(v[0])) continue;
                if (TryConvert(v, timeCol, out var ts)) { rec[i] = ts; converted++; }
            }
            WriteRecord(writer, rec);
            rows++;
        }
        writer.Flush();
        return new VolatilityCsvResult(headers, rows, stripped, converted);
    }

    /// <summary>True for Volatility's CSV header line (<c>TreeDepth,...</c>, possibly quoted, possibly with a BOM).</summary>
    public static bool IsHeaderLine(string line)
    {
        var s = line.TrimStart('﻿', ' ');
        if (s.StartsWith('"')) s = s[1..];
        return s.StartsWith(FirstHeader, StringComparison.Ordinal)
               && (s.Length == FirstHeader.Length || s[FirstHeader.Length] is ',' or '"');
    }

    /// <summary>
    /// A Volatility timestamp as <see cref="TimelineTimestamp.OutputFormat"/>. Values with a <c>UTC</c> / offset suffix are always converted;
    /// bare <c>yyyy-MM-dd HH:mm:ss[.f]</c> values only in time columns (<paramref name="timeColumn"/>).
    /// </summary>
    public static bool TryConvert(string value, bool timeColumn, out string formatted)
    {
        formatted = value;
        var m = VolDateRx.Match(value.Trim());
        if (!m.Success) return false;
        bool suffixed = m.Groups["utc"].Success || m.Groups["off"].Success;
        if (!suffixed && !timeColumn) return false;
        var b = m.Groups["b"].Value.Replace('T', ' ');
        int dot = b.IndexOf('.');
        if (dot >= 0 && b.Length - dot - 1 > 7) b = b[..(dot + 8)];
        if (!DateTime.TryParseExact(b, "yyyy-MM-dd HH:mm:ss.FFFFFFF", CultureInfo.InvariantCulture,
                DateTimeStyles.AssumeUniversal | DateTimeStyles.AdjustToUniversal, out var utc)
            && !DateTime.TryParseExact(b, "yyyy-MM-dd HH:mm:ss", CultureInfo.InvariantCulture,
                DateTimeStyles.AssumeUniversal | DateTimeStyles.AdjustToUniversal, out utc))
            return false;
        utc = DateTime.SpecifyKind(utc, DateTimeKind.Utc);
        var off = m.Groups["off"].Success ? m.Groups["off"].Value.Trim() : "";
        if (off.Length > 1 && off != "Z")
        {
            var digits = off[1..].Replace(":", "");
            if (digits.Length != 4 || !int.TryParse(digits[..2], NumberStyles.None, CultureInfo.InvariantCulture, out var hh)
                || !int.TryParse(digits[2..], NumberStyles.None, CultureInfo.InvariantCulture, out var mm) || hh > 23 || mm > 59) return false;
            var span = new TimeSpan(hh, mm, 0);
            try { utc = off[0] == '+' ? utc - span : utc + span; }
            catch (ArgumentOutOfRangeException) { return false; }
        }
        formatted = TimelineTimestamp.Format(utc);
        return true;
    }

    private static List<string> ParseSingleLine(string line) => new RecordParser(new StringReader(line)).Next() ?? new List<string>();

    private static void WriteRecord(TextWriter w, IReadOnlyList<string> fields)
    {
        for (int i = 0; i < fields.Count; i++)
        {
            if (i > 0) w.Write(',');
            var f = fields[i];
            if (f.AsSpan().IndexOfAny(",\"\r\n") >= 0 || (f.Length > 0 && (f[0] == ' ' || f[^1] == ' ')))
            {
                w.Write('"');
                w.Write(f.Replace("\"", "\"\""));
                w.Write('"');
            }
            else w.Write(f);
        }
        w.Write("\r\n");
    }

    private static string Clip(string s) => s.Length <= 200 ? s : s[..200] + "…";

    /// <summary>Python <c>csv</c> reader semantics: quotechar <c>"</c> (doubled inside quotes), escapechar <c>\</c>, records may span lines inside quotes.</summary>
    private sealed class RecordParser
    {
        private readonly TextReader _r;
        private readonly StringBuilder _sb = new();
        public RecordParser(TextReader r) => _r = r;

        public List<string>? Next()
        {
            int c = _r.Read();
            if (c < 0) return null;
            var rec = new List<string>();
            _sb.Clear();
            bool quoted = false, afterQuote = false;
            while (true)
            {
                if (c < 0) { rec.Add(_sb.ToString()); return rec; }
                char ch = (char)c;
                if (quoted)
                {
                    if (ch == '\\') { int n = _r.Read(); if (n >= 0) _sb.Append((char)n); }
                    else if (ch == '"')
                    {
                        if (_r.Peek() == '"') { _r.Read(); _sb.Append('"'); }
                        else { quoted = false; afterQuote = true; }
                    }
                    else _sb.Append(ch);
                }
                else if (ch == ',') { rec.Add(_sb.ToString()); _sb.Clear(); afterQuote = false; }
                else if (ch == '\n') { rec.Add(_sb.ToString()); return rec; }
                else if (ch == '\r') { if (_r.Peek() == '\n') _r.Read(); rec.Add(_sb.ToString()); return rec; }
                else if (ch == '\\') { int n = _r.Read(); if (n >= 0) _sb.Append((char)n); }
                else if (ch == '"' && _sb.Length == 0 && !afterQuote) quoted = true;
                else _sb.Append(ch);
                c = _r.Read();
            }
        }
    }
}
