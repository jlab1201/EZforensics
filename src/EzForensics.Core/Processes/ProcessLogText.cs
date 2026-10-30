using System.Globalization;
using System.Text.Json;
using System.Text.RegularExpressions;

namespace EzForensics.Core.Processes;

/// <summary>Defensive text helpers for EvtxECmd payload cells ("Key: value", hex PIDs, label prefixes) and for comparing executable paths.</summary>
public static partial class ProcessLogText
{
    /// <summary>Evidence cells are untrusted: a regex that runs longer than this is treated as "no match" (catastrophic backtracking guard).</summary>
    private const int RegexTimeoutMs = 250;

    // "Parent Process: C:\x.exe" but not "C:\x.exe": at least two characters before the colon and white space after it
    [GeneratedRegex(@"^\s*[A-Za-z][A-Za-z0-9 ]{1,30}:\s+", RegexOptions.None, RegexTimeoutMs)]
    private static partial Regex LabelRegex();

    // start of a "Key: " pair: at the start, or after , ; | or a run of spaces
    [GeneratedRegex(@"(?:^|[,;|]\s*|\s{2,})(?<k>[A-Za-z][A-Za-z0-9]+(?: [A-Za-z0-9]+)?):\s+", RegexOptions.None, RegexTimeoutMs)]
    private static partial Regex KeyRegex();

    public static string StripLabel(string? s)
    {
        if (string.IsNullOrWhiteSpace(s)) return "";
        var m = SafeMatch(LabelRegex(), s);
        return (m.Success ? s[m.Length..] : s).Trim();
    }

    private static Match SafeMatch(Regex rx, string text)
    {
        try { return rx.Match(text); }
        catch (RegexMatchTimeoutException) { return Match.Empty; }
    }

    /// <summary>"0x1a2c", "PID: 0x1a2c", "6700", "Pid: 6700, ..." to a number; hex only with the 0x prefix.</summary>
    public static int? ParsePid(string? s)
    {
        var t = StripLabel(s);
        if (t.Length == 0) return null;
        int end = 0;
        while (end < t.Length && !char.IsWhiteSpace(t[end]) && t[end] != ',' && t[end] != ';') end++;
        t = t[..end];
        if (t.StartsWith("0x", StringComparison.OrdinalIgnoreCase))
            return int.TryParse(t.AsSpan(2), NumberStyles.AllowHexSpecifier, CultureInfo.InvariantCulture, out var h) ? h : null;
        return int.TryParse(t, NumberStyles.Integer, CultureInfo.InvariantCulture, out var d) ? d : null;
    }

    /// <summary>Every "Key: value" pair of the cells, keys lower case without anything but letters and digits (first occurrence wins).</summary>
    public static Dictionary<string, string> KeyValues(params string?[] cells)
    {
        var result = new Dictionary<string, string>();
        foreach (var cell in cells)
        {
            if (string.IsNullOrWhiteSpace(cell)) continue;
            MatchCollection ms;
            try { ms = KeyRegex().Matches(cell); _ = ms.Count; }
            catch (RegexMatchTimeoutException) { continue; }
            for (int i = 0; i < ms.Count; i++)
            {
                var m = ms[i];
                int start = m.Index + m.Length;
                int end = i + 1 < ms.Count ? ms[i + 1].Index : cell.Length;
                var v = cell[start..end].Trim().TrimEnd(',', ';', '|').Trim();
                result.TryAdd(Norm(m.Groups["k"].Value), v);
            }
        }
        return result;
    }

    public static string Norm(string s)
    {
        Span<char> buf = s.Length <= 128 ? stackalloc char[s.Length] : new char[s.Length];
        int n = 0;
        foreach (var ch in s) if (char.IsLetterOrDigit(ch)) buf[n++] = char.ToLowerInvariant(ch);
        return new string(buf[..n]);
    }

    public static string Get(Dictionary<string, string> kv, params string[] keys)
    {
        foreach (var k in keys) if (kv.TryGetValue(k, out var v) && v.Length > 0) return v;
        return "";
    }

    /// <summary>"{abc-...}" in upper case with braces, "" when the text holds no GUID.</summary>
    public static string NormGuid(string? s)
    {
        if (string.IsNullOrWhiteSpace(s)) return "";
        var t = s.Trim().Trim('{', '}', ' ');
        return Guid.TryParse(t, out var g) ? "{" + g.ToString("D").ToUpperInvariant() + "}" : "";
    }

    /// <summary>First token of ExecutableInfo as the program path: quoted, up to the first program extension, or up to the first blank.</summary>
    public static string ExePath(string? commandLine)
    {
        var t = (commandLine ?? "").Trim();
        if (t.Length == 0) return "";
        if (t[0] == '"')
        {
            int q = t.IndexOf('"', 1);
            return q > 1 ? t[1..q] : t.Trim('"');
        }
        var m = SafeMatch(ProgramRegex(), t);
        if (m.Success) return m.Groups[1].Value;
        int sp = t.IndexOf(' ');
        return sp > 0 ? t[..sp] : t;
    }

    [GeneratedRegex(@"^(.+?\.(?:exe|com|bat|cmd|scr|msi|cpl))(?:\s|$)", RegexOptions.IgnoreCase, RegexTimeoutMs)]
    private static partial Regex ProgramRegex();

    public static string FileNameOf(string path)
    {
        var i = path.LastIndexOfAny(new[] { '\\', '/' });
        return i >= 0 ? path[(i + 1)..] : path;
    }

    /// <summary>
    /// Comparison key of an executable path: lower case, no drive letter / device prefix / quotes, <c>\SystemRoot</c> as <c>\windows</c>
    /// (so <c>C:\Windows\System32\cmd.exe</c> and <c>\Device\HarddiskVolume3\Windows\System32\cmd.exe</c> are equal). Empty when the path has no directory.
    /// </summary>
    public static string PathKey(string? path)
    {
        if (string.IsNullOrWhiteSpace(path)) return "";
        var p = path.Trim().Trim('"').Replace('/', '\\');
        foreach (var pre in new[] { @"\\?\", @"\??\" })
            if (p.StartsWith(pre, StringComparison.Ordinal)) p = p[pre.Length..];
        if (p.Length >= 2 && char.IsLetter(p[0]) && p[1] == ':') p = p[2..];
        else if (p.StartsWith(@"\Device\", StringComparison.OrdinalIgnoreCase))
        {
            int i = p.IndexOf('\\', 8);
            p = i > 0 ? p[i..] : "";
        }
        else if (p.StartsWith(@"\VOLUME{", StringComparison.OrdinalIgnoreCase))   // Prefetch FilesLoaded
        {
            int i = p.IndexOf('\\', 1);
            p = i > 0 ? p[i..] : "";
        }
        else if (p.StartsWith(@"\SystemRoot\", StringComparison.OrdinalIgnoreCase)) p = @"\Windows\" + p[12..];
        p = p.ToLowerInvariant();
        return p.IndexOf('\\') >= 0 ? p : "";
    }

    /// <summary>The program path of a node: its path, else the first token of its command line when that is a rooted path.</summary>
    public static string EffectivePath(ProcessNode n)
    {
        if (n.Path.Length > 0) return n.Path;
        var exe = ExePath(n.CmdLine);
        return exe.Length > 1 && (exe.Contains('\\') || exe.Contains('/')) ? exe : "";
    }

    /// <summary>Best hash of a Sysmon "Hashes" value (<c>SHA1=..,MD5=..,SHA256=..,IMPHASH=..</c>) as "SHA256 abc..."; empty when none.</summary>
    public static string BestHash(string? hashes)
    {
        if (string.IsNullOrWhiteSpace(hashes)) return "";
        var found = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        foreach (var part in hashes.Split(new[] { ',', ';' }, StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
        {
            int eq = part.IndexOf('=');
            if (eq > 0 && eq < part.Length - 1) found.TryAdd(part[..eq].Trim(), part[(eq + 1)..].Trim());
        }
        foreach (var alg in new[] { "SHA256", "SHA1", "MD5" })
            if (found.TryGetValue(alg, out var h) && h.Length > 0) return alg + " " + h.ToLowerInvariant();
        return "";
    }

    /// <summary>Text after the first colon ("New type \ value: 4 \ x" gives "4 \ x"); the whole text when there is no colon.</summary>
    public static string AfterColon(string? s)
    {
        if (string.IsNullOrWhiteSpace(s)) return "";
        int i = s.IndexOf(':');
        return (i >= 0 ? s[(i + 1)..] : s).Trim();
    }

    /// <summary>"Key = value" or "Key: value" to the value (PowerShell "Host Application = ..."); the whole text when it has neither.</summary>
    public static string AfterSeparator(string? s)
    {
        if (string.IsNullOrWhiteSpace(s)) return "";
        var t = s.Trim();
        int eq = t.IndexOf(" = ", StringComparison.Ordinal);
        if (eq > 0 && eq < 40) return t[(eq + 3)..].Trim();
        var m = SafeMatch(LabelRegex(), t);
        return (m.Success ? t[m.Length..] : t).Trim();
    }

    [GeneratedRegex(@"\(PID:\s*(?<p>[^)]+)\)\s*$", RegexOptions.IgnoreCase, RegexTimeoutMs)]
    private static partial Regex TrailingPidRegex();

    /// <summary>EvtxECmd "ProcessName (PID: 0x1a4)" (Security 4656 / 4657) to the name and the PID; no PID when the text has none.</summary>
    public static (string Name, int? Pid) SplitTrailingPid(string? s)
    {
        var t = (s ?? "").Trim();
        var m = SafeMatch(TrailingPidRegex(), t);
        if (!m.Success) return (t, null);
        return (t[..m.Index].Trim(), ParsePid(m.Groups["p"].Value));
    }

    /// <summary>
    /// EventData fields of the EvtxECmd <c>Payload</c> column (JSON: <c>{"EventData":{"Data":[{"@Name":"Signature","#text":"x"}]}}</c> or a flat object),
    /// keys lower case without punctuation. Empty when the text is missing or not JSON.
    /// </summary>
    public static Dictionary<string, string> PayloadFields(string? payload)
    {
        var res = new Dictionary<string, string>();
        if (string.IsNullOrWhiteSpace(payload)) return res;
        try
        {
            using var doc = JsonDocument.Parse(payload, new JsonDocumentOptions { AllowTrailingCommas = true });
            Walk(doc.RootElement, res, 0);
        }
        catch (JsonException) { }
        return res;
    }

    private static void Walk(JsonElement e, Dictionary<string, string> res, int depth)
    {
        if (depth > 6) return;
        if (e.ValueKind == JsonValueKind.Array)
        {
            foreach (var x in e.EnumerateArray()) Walk(x, res, depth + 1);
            return;
        }
        if (e.ValueKind != JsonValueKind.Object) return;
        if (e.TryGetProperty("@Name", out var n) && n.ValueKind == JsonValueKind.String)
        {
            var text = e.TryGetProperty("#text", out var tx) ? (tx.ValueKind == JsonValueKind.String ? tx.GetString() : tx.GetRawText()) : "";
            res.TryAdd(Norm(n.GetString() ?? ""), text ?? "");
            return;
        }
        foreach (var p in e.EnumerateObject())
        {
            if (p.Value.ValueKind is JsonValueKind.String or JsonValueKind.Number) res.TryAdd(Norm(p.Name), p.Value.ValueKind == JsonValueKind.String ? p.Value.GetString() ?? "" : p.Value.GetRawText());
            else Walk(p.Value, res, depth + 1);
        }
    }

    /// <summary>AppLocker paths use %OSDRIVE% and friends; expands them to the usual locations so they compare with other paths.</summary>
    public static string ExpandEnvironmentTokens(string? path)
    {
        if (string.IsNullOrEmpty(path)) return "";
        var p = path.Trim();
        if (p.IndexOf('%') < 0) return p;
        p = p.Replace("%OSDRIVE%", "C:", StringComparison.OrdinalIgnoreCase);
        p = p.Replace("%WINDIR%", @"C:\Windows", StringComparison.OrdinalIgnoreCase);
        p = p.Replace("%SYSTEM32%", @"C:\Windows\System32", StringComparison.OrdinalIgnoreCase);
        p = p.Replace("%PROGRAMFILES%", @"C:\Program Files", StringComparison.OrdinalIgnoreCase);
        return p;
    }
}
