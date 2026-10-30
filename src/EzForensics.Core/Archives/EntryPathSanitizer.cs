using System.Security.Cryptography;
using System.Text;

namespace EzForensics.Core.Archives;

/// <summary>Stateless path-safety rules for one archive entry name (see <see cref="EntryPathSanitizer"/> for the per-extraction rules).</summary>
public static class ArchivePaths
{
    private static readonly HashSet<string> Reserved = new(StringComparer.OrdinalIgnoreCase)
    {
        "CON", "PRN", "AUX", "NUL", "CONIN$", "CONOUT$",
        "COM0", "COM1", "COM2", "COM3", "COM4", "COM5", "COM6", "COM7", "COM8", "COM9",
        "LPT0", "LPT1", "LPT2", "LPT3", "LPT4", "LPT5", "LPT6", "LPT7", "LPT8", "LPT9",
        // Windows also treats the superscript digits as device names (COM¹ ... LPT³).
        "COM¹", "COM²", "COM³", "LPT¹", "LPT²", "LPT³",
    };

    /// <summary>
    /// Normalises '/' and '\', drops '.' segments, refuses '..' (returns null), strips roots (/, C:\, \\srv, \\?\), and reversibly percent-encodes
    /// invalid characters and ':' (file%3AZone.Identifier), reserved device names (NUL.txt → %4EUL.txt) and trailing dots/spaces. $MFT is kept.
    /// Returns a relative path with '\' separators, or null when the entry must be skipped; <paramref name="reason"/> says what changed or why it was skipped.
    /// </summary>
    public static string? Sanitize(string entryName, out string? reason)
    {
        var reasons = new List<string>();
        var parts = SplitSegments(entryName, reasons, out bool traversal);
        if (traversal) { reason = "path traversal ('..') refused"; return null; }
        if (parts.Count == 0) { reason = "empty name"; return null; }
        bool encoded = false;
        for (int i = 0; i < parts.Count; i++)
        {
            var enc = EncodeSegment(parts[i]);
            if (enc != parts[i]) { parts[i] = enc; encoded = true; }
        }
        if (encoded) reasons.Add("percent-encoded (invalid character, ':' stream name, reserved device name or trailing dot/space)");
        reason = reasons.Count == 0 ? null : string.Join("; ", reasons);
        return string.Join('\\', parts);
    }

    private static List<string> SplitSegments(string name, List<string> reasons, out bool traversal)
    {
        traversal = false;
        var s = name.Replace('/', '\\');
        bool rootStripped = false;
        var raw = s.Split('\\').ToList();
        if (s.StartsWith(@"\\", StringComparison.Ordinal))
        {
            // \\server\share\..., \\?\C:\..., \\.\C:\..., \\?\UNC\server\share\...
            raw = raw.SkipWhile(p => p.Length == 0).ToList();
            rootStripped = true;
            if (raw.Count > 0 && (raw[0] == "?" || raw[0] == "."))
            {
                raw.RemoveAt(0);
                if (raw.Count > 0 && raw[0].Equals("UNC", StringComparison.OrdinalIgnoreCase)) { raw.RemoveAt(0); if (raw.Count > 0) raw.RemoveAt(0); }
            }
            else if (raw.Count > 0) raw.RemoveAt(0);   // the server name
        }
        else if (s.StartsWith('\\')) rootStripped = true;
        var first = raw.FindIndex(p => p.Length > 0);
        if (first >= 0 && raw[first].Length >= 2 && char.IsAsciiLetter(raw[first][0]) && raw[first][1] == ':')
        {
            raw[first] = raw[first][2..];   // "C:" or drive-relative "C:foo"
            rootStripped = true;
        }
        if (rootStripped) reasons.Add("absolute path: root stripped");
        var parts = new List<string>();
        foreach (var p in raw)
        {
            if (p.Length == 0 || p == ".") continue;
            if (p == "..") { traversal = true; return parts; }
            parts.Add(p);
        }
        return parts;
    }

    /// <summary>Percent-encodes one path component when Windows cannot store it as is ('%' is then encoded too, so it stays reversible).</summary>
    public static string EncodeSegment(string seg)
    {
        int trailing = seg.Length;
        while (trailing > 0 && (seg[trailing - 1] == '.' || seg[trailing - 1] == ' ')) trailing--;
        var dot = seg.IndexOf('.');
        var stem = (dot >= 0 ? seg[..dot] : seg).TrimEnd(' ');
        bool reserved = Reserved.Contains(stem);
        bool needs = reserved || trailing < seg.Length || seg.Any(IsInvalid);
        if (!needs) return seg;
        var sb = new StringBuilder(seg.Length + 8);
        for (int i = 0; i < seg.Length; i++)
        {
            char c = seg[i];
            if (IsInvalid(c) || c == '%' || i >= trailing || (reserved && i == 0)) AppendEncoded(sb, c);
            else sb.Append(c);
        }
        return sb.ToString();
    }

    /// <summary>Reverses <see cref="EncodeSegment"/>.</summary>
    public static string DecodeSegment(string seg) => PercentDecode(seg);

    internal static string PercentDecode(string s)
    {
        if (!s.Contains('%')) return s;
        var sb = new StringBuilder(s.Length);
        var bytes = new List<byte>();
        for (int i = 0; i < s.Length; i++)
        {
            if (s[i] == '%' && i + 2 < s.Length && IsHex(s[i + 1]) && IsHex(s[i + 2]))
            {
                bytes.Add(Convert.ToByte(s.Substring(i + 1, 2), 16));
                i += 2;
                continue;
            }
            if (bytes.Count > 0) { sb.Append(Encoding.UTF8.GetString(bytes.ToArray())); bytes.Clear(); }
            sb.Append(s[i]);
        }
        if (bytes.Count > 0) sb.Append(Encoding.UTF8.GetString(bytes.ToArray()));
        return sb.ToString();
    }

    private static bool IsHex(char c) => char.IsAsciiHexDigit(c);

    private static bool IsInvalid(char c) => c < 0x20 || c is '<' or '>' or ':' or '"' or '|' or '?' or '*' or '\\' or '/';

    private static void AppendEncoded(StringBuilder sb, char c)
    {
        foreach (var b in Encoding.UTF8.GetBytes(c.ToString())) sb.Append('%').Append(b.ToString("X2"));
    }
}

/// <summary>The sanitised relative path for an entry, or null with the skip reason.</summary>
public sealed record SanitizedEntry(string? RelativePath, string? Reason)
{
    public bool Skipped => RelativePath == null;
}

/// <summary>
/// Per-extraction path rules on top of <see cref="ArchivePaths.Sanitize"/>: names that differ only in case (or repeat) get a "~2" suffix,
/// paths longer than the limit shorten a component with a hash suffix, and the final full path must stay inside the extraction folder.
/// Not thread-safe; one instance per extraction.
/// </summary>
public sealed class EntryPathSanitizer
{
    private readonly string _root;
    private readonly int _max;
    private readonly HashSet<string> _taken = new(StringComparer.OrdinalIgnoreCase);
    private readonly Dictionary<string, string> _dirMap = new(StringComparer.Ordinal);
    private readonly HashSet<string> _files = new(StringComparer.OrdinalIgnoreCase);

    public EntryPathSanitizer(string root, int maxPathLength = 240, IEnumerable<string>? reservedNames = null)
    {
        _root = Path.GetFullPath(root).TrimEnd('\\', '/');
        _max = maxPathLength;
        foreach (var r in reservedNames ?? Array.Empty<string>()) { _taken.Add(r); _files.Add(r); }
    }

    public SanitizedEntry Sanitize(string entryName, bool isDirectory)
    {
        var rel = ArchivePaths.Sanitize(entryName, out var reason);
        if (rel == null) return new SanitizedEntry(null, reason);
        var reasons = new List<string>();
        if (reason != null) reasons.Add(reason);
        var comps = rel.Split('\\');
        int dirCount = isDirectory ? comps.Length : comps.Length - 1;

        string prefix = "";
        for (int i = 0; i < dirCount; i++)
        {
            var origKey = string.Join('\\', comps, 0, i + 1);
            if (_dirMap.TryGetValue(origKey, out var mapped)) { prefix = mapped; continue; }
            var comp = comps[i];
            // Keep at least 40 characters for the names below this folder; the shortened name is remembered so siblings land in the same folder.
            int allowed = Math.Max(12, (_max - 40) - (_root.Length + 1 + (prefix.Length == 0 ? 0 : prefix.Length + 1)));
            if (comp.Length > allowed && _root.Length + 1 + Join(prefix, comp).Length > _max - 40)
            {
                comp = Shorten(comp, allowed, keepExtension: false);
                reasons.Add("folder name shortened (path too long)");
            }
            var candidate = Join(prefix, comp);
            if (_files.Contains(candidate) || (_taken.Contains(candidate) && !IsSameDir(candidate)))
            {
                candidate = Unique(prefix, comp);
                reasons.Add("case-only duplicate renamed (~N)");
            }
            _dirMap[origKey] = candidate;
            _taken.Add(candidate);
            _dirSet.Add(candidate);
            prefix = candidate;
        }

        string final = prefix;
        if (!isDirectory)
        {
            var name = comps[^1];
            int room = _max - (_root.Length + 1 + (prefix.Length == 0 ? 0 : prefix.Length + 1));
            if (name.Length > room)
            {
                if (room < 20) return new SanitizedEntry(null, "path too long even after shortening");
                name = Shorten(name, room - 4, keepExtension: true);
                reasons.Add("file name shortened with a hash suffix (path too long)");
            }
            var candidate = Join(prefix, name);
            if (_taken.Contains(candidate))
            {
                candidate = Unique(prefix, name);
                reasons.Add("case-only or repeated duplicate renamed (~N)");
            }
            final = candidate;
        }
        if (final.Length == 0) return new SanitizedEntry(null, "empty name");

        var full = Path.GetFullPath(Path.Combine(_root, final));
        if (!full.StartsWith(_root + "\\", StringComparison.OrdinalIgnoreCase) && !full.StartsWith(_root + "/", StringComparison.OrdinalIgnoreCase))
            return new SanitizedEntry(null, "resolves outside the extraction folder");
        if (full.Length > _max + 4) return new SanitizedEntry(null, "path too long even after shortening");

        if (!isDirectory) { _taken.Add(final); _files.Add(final); }
        return new SanitizedEntry(final, reasons.Count == 0 ? null : string.Join("; ", reasons.Distinct()));
    }

    private readonly HashSet<string> _dirSet = new(StringComparer.Ordinal);

    // A directory path already created with exactly this spelling is the same directory (only a different case is a duplicate).
    private bool IsSameDir(string candidate) => _dirSet.Contains(candidate);

    private string Unique(string prefix, string name)
    {
        var ext = Path.GetExtension(name);
        var stem = ext.Length > 0 && ext.Length < name.Length ? name[..^ext.Length] : name;
        if (ext.Length == name.Length) ext = "";
        for (int n = 2; ; n++)
        {
            var c = Join(prefix, $"{stem}~{n}{ext}");
            if (!_taken.Contains(c)) return c;
        }
    }

    private static string Join(string prefix, string comp) => prefix.Length == 0 ? comp : prefix + "\\" + comp;

    /// <summary>Truncates to <paramref name="max"/> characters keeping a "~" + 8-hex hash of the full original (and the extension when asked).</summary>
    internal static string Shorten(string name, int max, bool keepExtension)
    {
        var hash = Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(name)))[..8].ToLowerInvariant();
        var ext = keepExtension ? Path.GetExtension(name) : "";
        if (ext.Length > 16) ext = "";
        int stemLen = Math.Max(1, max - ext.Length - 9);
        var stem = keepExtension && ext.Length > 0 ? name[..^ext.Length] : name;
        if (stem.Length > stemLen) stem = stem[..stemLen];
        return stem.TrimEnd('.', ' ') + "~" + hash + ext;
    }
}
