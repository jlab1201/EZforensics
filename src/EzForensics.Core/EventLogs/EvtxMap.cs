using System.Globalization;
using System.Text;
using System.Text.RegularExpressions;
using YamlDotNet.Serialization;

namespace EzForensics.Core.EventLogs;

/// <summary>One EvtxECmd map (<c>Maps\*.map</c>, YAML): which EventData values go into which CSV column for one Channel + Provider + EventId.</summary>
public sealed class EvtxMap
{
    /// <summary>The CSV columns a map can fill (EvtxECmd's validator accepts only these).</summary>
    public static readonly IReadOnlyList<string> Columns = new[]
    {
        "UserName", "RemoteHost", "ExecutableInfo", "PayloadData1", "PayloadData2", "PayloadData3", "PayloadData4", "PayloadData5", "PayloadData6",
    };

    public string Author { get; set; } = "";
    public string Description { get; set; } = "";
    public int EventId { get; set; }
    public string Channel { get; set; } = "";
    public string Provider { get; set; } = "";
    public List<EvtxMapEntry> Maps { get; set; } = new();
    public List<EvtxMapLookup> Lookups { get; set; } = new();

    public EvtxEventKey Key => new(Channel, Provider, EventId);

    /// <summary>Problems EvtxECmd would reject the map for (empty = valid).</summary>
    public IReadOnlyList<string> Validate()
    {
        var p = new List<string>();
        if (EventId <= 0) p.Add("EventId is missing.");
        if (string.IsNullOrWhiteSpace(Channel)) p.Add("Channel is missing.");
        if (string.IsNullOrWhiteSpace(Provider)) p.Add("Provider is missing.");
        if (Maps.Count == 0) p.Add("No column is mapped.");
        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var m in Maps)
        {
            var col = Columns.FirstOrDefault(c => c.Equals(m.Property, StringComparison.OrdinalIgnoreCase));
            if (col == null) { p.Add($"'{m.Property}' is not a map column."); continue; }
            if (!seen.Add(col)) p.Add($"{col} is mapped twice.");
            if (string.IsNullOrWhiteSpace(m.PropertyValue)) p.Add($"{col}: the format is empty.");
            if (m.Values.Count == 0) p.Add($"{col}: no field is assigned.");
            foreach (var v in m.Values)
            {
                if (string.IsNullOrWhiteSpace(v.Name)) p.Add($"{col}: a value has no name.");
                if (string.IsNullOrWhiteSpace(v.Value)) p.Add($"{col}: '{v.Name}' has no XPath.");
                else if (EvtxMapEvaluator.XPathError(v.Value) is { } err) p.Add($"{col}: '{v.Name}' XPath is not valid ({err}).");
                if (!string.IsNullOrEmpty(v.Refine))
                {
                    try { _ = new Regex(v.Refine); }
                    catch (ArgumentException ex) { p.Add($"{col}: '{v.Name}' Refine is not a valid regex ({ex.Message})."); }
                }
            }
        }
        return p;
    }

    public EvtxMap Clone() => EvtxMapYaml.Parse(EvtxMapYaml.Write(this));
}

/// <summary>One column of a map: <see cref="PropertyValue"/> is the text written to <see cref="Property"/>, with <c>%Name%</c> replaced by each value.</summary>
public sealed class EvtxMapEntry
{
    public string Property { get; set; } = "";
    public string PropertyValue { get; set; } = "";
    public List<EvtxMapValue> Values { get; set; } = new();
}

/// <summary>A variable of a map column: XPath into the event XML, optional regex that keeps only the matching parts.</summary>
public sealed class EvtxMapValue
{
    public string Name { get; set; } = "";
    public string Value { get; set; } = "";
    public string? Refine { get; set; }
}

/// <summary>Lookup table applied to the variable of the same name (code to text).</summary>
public sealed class EvtxMapLookup
{
    public string Name { get; set; } = "";
    public string? Default { get; set; }
    public Dictionary<string, string> Values { get; set; } = new();
}

/// <summary>Reads and writes EvtxECmd map YAML. Reading uses YamlDotNet (same library EvtxECmd uses); writing is a small fixed-layout writer.</summary>
public static class EvtxMapYaml
{
    private static readonly IDeserializer Reader = new DeserializerBuilder().IgnoreUnmatchedProperties().Build();

    /// <summary>Parses a map. Throws <see cref="FormatException"/> when the text is not a map.</summary>
    public static EvtxMap Parse(string yaml)
    {
        EvtxMap? m;
        try { m = Reader.Deserialize<EvtxMap>(yaml); }
        catch (YamlDotNet.Core.YamlException ex) { throw new FormatException("Not a valid map: " + (ex.InnerException?.Message ?? ex.Message), ex); }
        if (m == null) throw new FormatException("The map is empty.");
        m.Author ??= ""; m.Description ??= ""; m.Channel ??= ""; m.Provider ??= "";
        m.Maps ??= new(); m.Lookups ??= new();
        foreach (var e in m.Maps)
        {
            e.Property ??= ""; e.PropertyValue ??= ""; e.Values ??= new();
            foreach (var v in e.Values) { v.Name ??= ""; v.Value ??= ""; }
        }
        foreach (var l in m.Lookups) { l.Name ??= ""; l.Values ??= new(); }
        return m;
    }

    public static EvtxMap Load(string path) => Parse(File.ReadAllText(path));

    /// <summary>The map as YAML in EvtxECmd's layout; every string is double-quoted. <paramref name="comment"/> lines go at the end as <c># </c> comments.</summary>
    public static string Write(EvtxMap m, string? comment = null)
    {
        var sb = new StringBuilder();
        void Line(string s) => sb.Append(s).Append('\n');
        Line("Author: " + Q(m.Author));
        Line("Description: " + Q(m.Description));
        Line("EventId: " + m.EventId.ToString(CultureInfo.InvariantCulture));
        Line("Channel: " + Q(m.Channel));
        Line("Provider: " + Q(m.Provider));
        Line("Maps:");
        foreach (var e in m.Maps)
        {
            Line("  -");
            Line("    Property: " + Q(e.Property));
            Line("    PropertyValue: " + Q(e.PropertyValue));
            Line("    Values:");
            foreach (var v in e.Values)
            {
                Line("      -");
                Line("        Name: " + Q(v.Name));
                Line("        Value: " + Q(v.Value));
                if (!string.IsNullOrEmpty(v.Refine)) Line("        Refine: " + Q(v.Refine));
            }
        }
        if (m.Lookups.Count > 0)
        {
            Line("Lookups:");
            foreach (var l in m.Lookups)
            {
                Line("  -");
                Line("    Name: " + Q(l.Name));
                if (l.Default != null) Line("    Default: " + Q(l.Default));
                Line("    Values:");
                foreach (var kv in l.Values) Line("        " + Q(kv.Key) + ": " + Q(kv.Value));
            }
        }
        if (!string.IsNullOrWhiteSpace(comment))
        {
            Line("");
            foreach (var c in comment.Replace("\r", "").Split('\n')) Line("# " + c);
        }
        return sb.ToString();
    }

    /// <summary>YAML double-quoted scalar.</summary>
    public static string Q(string? s)
    {
        var sb = new StringBuilder("\"");
        foreach (var ch in s ?? "")
        {
            switch (ch)
            {
                case '\\': sb.Append("\\\\"); break;
                case '"': sb.Append("\\\""); break;
                case '\n': sb.Append("\\n"); break;
                case '\r': sb.Append("\\r"); break;
                case '\t': sb.Append("\\t"); break;
                default:
                    if (char.IsControl(ch)) sb.Append("\\x").Append(((int)ch).ToString("X2", CultureInfo.InvariantCulture));
                    else sb.Append(ch);
                    break;
            }
        }
        return sb.Append('"').ToString();
    }
}

/// <summary>Map file names and lookups in a Maps folder.</summary>
public static class EvtxMapFiles
{
    private static readonly Regex HeaderLine = new(@"^(EventId|Channel|Provider)\s*:\s*(.*?)\s*$", RegexOptions.Compiled);

    /// <summary>EvtxECmd naming convention: <c>Channel_Provider_EventId.map</c>, with '/', spaces and other unsafe characters in each part turned into '-'.</summary>
    public static string FileName(string channel, string provider, int eventId) => $"{Part(channel)}_{Part(provider)}_{eventId.ToString(CultureInfo.InvariantCulture)}.map";

    public static string FileName(EvtxEventKey key) => FileName(key.Channel, key.Provider, key.EventId);

    private static string Part(string s)
    {
        var sb = new StringBuilder();
        foreach (var ch in s.Trim())
            sb.Append(char.IsLetterOrDigit(ch) || ch is '.' or '(' or ')' ? ch : '-');
        var r = Regex.Replace(sb.ToString(), "-{2,}", "-").Trim('-');
        return r.Length == 0 ? "Unknown" : r;
    }

    /// <summary>EventId / Channel / Provider of a map file from its top-level lines (fast; no full parse). Null when the file has no EventId or Channel.</summary>
    public static EvtxEventKey? ReadKey(string path)
    {
        try
        {
            string? id = null, ch = null, pr = null;
            foreach (var line in File.ReadLines(path))
            {
                if (line.StartsWith("Maps", StringComparison.Ordinal)) break;
                var m = HeaderLine.Match(line);
                if (!m.Success) continue;
                var v = Unquote(m.Groups[2].Value);
                switch (m.Groups[1].Value) { case "EventId": id = v; break; case "Channel": ch = v; break; default: pr = v; break; }
            }
            if (ch == null || !int.TryParse(id, NumberStyles.Integer, CultureInfo.InvariantCulture, out var n)) return null;
            return new EvtxEventKey(ch, pr ?? "", n);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { return null; }
    }

    private static string Unquote(string v)
    {
        var hash = v.IndexOf(" #", StringComparison.Ordinal);
        if (!v.StartsWith('"') && !v.StartsWith('\'') && hash >= 0) v = v[..hash].TrimEnd();
        if (v.Length >= 2 && (v[0] == '"' && v[^1] == '"' || v[0] == '\'' && v[^1] == '\''))
        {
            var inner = v[1..^1];
            return v[0] == '"' ? inner.Replace("\\\"", "\"").Replace("\\\\", "\\") : inner.Replace("''", "'");
        }
        return v;
    }

    /// <summary>
    /// The map EvtxECmd would use for <paramref name="key"/> in <paramref name="mapsDir"/>: maps load in file name order and the first one
    /// for a Channel + Provider + EventId wins. Files with <paramref name="skipPrefix"/> (our own copies) are ignored. Null when none.
    /// </summary>
    public static string? Find(string mapsDir, EvtxEventKey key, string? skipPrefix = null)
    {
        if (!Directory.Exists(mapsDir)) return null;
        bool Skip(string f) => skipPrefix != null && Path.GetFileName(f).StartsWith(skipPrefix, StringComparison.OrdinalIgnoreCase);
        bool Match(string f) => ReadKey(f) is { } k && KeyMatches(k, key);
        var files = Directory.EnumerateFiles(mapsDir, "*.map").Where(f => !Skip(f)).OrderBy(f => f, StringComparer.OrdinalIgnoreCase).ToList();
        // the convention name first (cheap), then every header
        var conv = files.Where(f => Path.GetFileName(f).EndsWith(FileName(key), StringComparison.OrdinalIgnoreCase)).ToList();
        return conv.FirstOrDefault(Match) ?? files.FirstOrDefault(Match);
    }

    /// <summary>EvtxECmd's map key: EventId + Channel + Provider, case-insensitive. An empty provider on <paramref name="want"/> matches any.</summary>
    public static bool KeyMatches(EvtxEventKey map, EvtxEventKey want) =>
        map.EventId == want.EventId && string.Equals(map.Channel, want.Channel, StringComparison.OrdinalIgnoreCase)
        && (want.Provider.Length == 0 || string.Equals(map.Provider, want.Provider, StringComparison.OrdinalIgnoreCase));
}
