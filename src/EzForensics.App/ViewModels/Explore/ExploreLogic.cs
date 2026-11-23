using System.Globalization;
using System.Text;
using EzForensics.Core.Explore;

namespace EzForensics.App.ViewModels.Explore;

// Pure, UI-free helpers of the Explore page (unit tested).

// ------------------------------------------------------------------------------------------------ strings

public sealed record ExtractedString(long Offset, bool Utf16, string Text)
{
    public string Encoding => Utf16 ? "UTF-16" : "ASCII";
    public string OffsetHex => "0x" + Offset.ToString("X");
}

public sealed record StringsResult(IReadOnlyList<ExtractedString> Items, bool Truncated, long BytesScanned);

/// <summary>Managed ASCII and UTF-16LE string extraction from a (read-only) stream.</summary>
public static class StringsExtractor
{
    public const int DefaultMaxCount = 50_000;
    public const long DefaultMaxBytes = 256L * 1024 * 1024;
    public const int MaxStringLength = 4096;

    private sealed class Run
    {
        public long Start = -1;
        public readonly StringBuilder Sb = new();
        public char Pending;
        public bool HasPending;
    }

    public static bool IsPrintable(byte b) => b is >= 0x20 and <= 0x7E || b == 0x09;

    public static StringsResult Extract(Stream s, int minLength, int maxCount = DefaultMaxCount, long maxBytes = DefaultMaxBytes, CancellationToken ct = default)
    {
        if (minLength < 2) minLength = 2;
        var results = new List<ExtractedString>();
        bool truncated = false;
        var ascii = new Run();
        var wide = new[] { new Run(), new Run() };

        void Flush(Run r, bool utf16)
        {
            if (r.Start >= 0 && r.Sb.Length >= minLength)
            {
                if (results.Count >= maxCount) truncated = true;
                else results.Add(new ExtractedString(r.Start, utf16, r.Sb.ToString()));
            }
            r.Start = -1; r.Sb.Clear(); r.HasPending = false;
        }

        long pos = 0;
        var buf = new byte[81920];
        int n;
        while (!truncated && pos < maxBytes && (n = s.Read(buf, 0, (int)Math.Min(buf.Length, maxBytes - pos))) > 0)
        {
            ct.ThrowIfCancellationRequested();
            for (int i = 0; i < n; i++, pos++)
            {
                byte b = buf[i];
                if (IsPrintable(b))
                {
                    if (ascii.Start < 0) ascii.Start = pos;
                    ascii.Sb.Append((char)b);
                    if (ascii.Sb.Length >= MaxStringLength) Flush(ascii, false);
                }
                else Flush(ascii, false);

                for (int a = 0; a < 2; a++)
                {
                    var r = wide[a];
                    if ((pos & 1) == a)
                    {
                        if (IsPrintable(b)) { r.Pending = (char)b; r.HasPending = true; }
                        else Flush(r, true);
                    }
                    else if (r.HasPending && b == 0)
                    {
                        if (r.Start < 0) r.Start = pos - 1;
                        r.Sb.Append(r.Pending);
                        r.HasPending = false;
                        if (r.Sb.Length >= MaxStringLength) Flush(r, true);
                    }
                    else Flush(r, true);
                }
            }
        }
        Flush(ascii, false); Flush(wide[0], true); Flush(wide[1], true);
        results.Sort((x, y) => x.Offset.CompareTo(y.Offset));
        return new StringsResult(results, truncated, pos);
    }

    public static StringsResult Extract(byte[] data, int minLength, int maxCount = DefaultMaxCount, CancellationToken ct = default)
    {
        using var ms = new MemoryStream(data, writable: false);
        return Extract(ms, minLength, maxCount, DefaultMaxBytes, ct);
    }
}

// ------------------------------------------------------------------------------------------------ hex selection inspector

public sealed record InspectorValue(string Label, string Value);

public static class SelectionInspector
{
    private static readonly DateTime FileTimeMin = new(1601, 1, 1, 0, 0, 0, DateTimeKind.Utc);
    // FILETIME / Unix values are only shown as dates when they fall in a plausible range.
    private static readonly DateTime PlausibleMin = new(1970, 1, 1, 0, 0, 0, DateTimeKind.Utc);
    private static readonly DateTime PlausibleMax = new(2100, 1, 1, 0, 0, 0, DateTimeKind.Utc);

    public static IReadOnlyList<InspectorValue> Describe(ReadOnlySpan<byte> b)
    {
        var l = new List<InspectorValue>();
        if (b.Length == 0) return l;
        l.Add(new("int8", $"{(sbyte)b[0]}  /  {b[0]}"));
        if (b.Length >= 2)
        {
            var u16 = BitConverter.ToUInt16(b);
            l.Add(new("int16 LE", $"{(short)u16}  /  {u16}"));
        }
        if (b.Length >= 4)
        {
            var u32 = BitConverter.ToUInt32(b);
            l.Add(new("int32 LE", $"{(int)u32}  /  {u32}"));
            l.Add(new("Unix time (32-bit)", FormatUnix(u32)));
        }
        if (b.Length >= 8)
        {
            var u64 = BitConverter.ToUInt64(b);
            l.Add(new("int64 LE", $"{(long)u64}  /  {u64}"));
            l.Add(new("FILETIME", FormatFileTime(u64)));
        }
        return l;
    }

    public static string FormatFileTime(ulong ticks100ns)
    {
        if (ticks100ns == 0) return "0 (not set)";
        if (ticks100ns > (ulong)(DateTime.MaxValue.Ticks - FileTimeMin.Ticks)) return "out of range";
        var t = FileTimeMin.AddTicks((long)ticks100ns);
        return t < PlausibleMin || t > PlausibleMax ? $"{Fmt.Utc(t)} (implausible)" : Fmt.Utc(t);
    }

    public static string FormatUnix(uint seconds)
    {
        if (seconds == 0) return "0 (not set)";
        var t = DateTime.UnixEpoch.AddSeconds(seconds);
        return t > PlausibleMax ? $"{Fmt.Utc(t)} (implausible)" : Fmt.Utc(t);
    }
}

// ------------------------------------------------------------------------------------------------ verdict chips

public enum ChipKind { Info, Warn, Bad }

public sealed record VerdictChip(string Text, ChipKind Kind);

public static class VerdictChips
{
    public static IReadOnlyList<VerdictChip> From(FileFacts f, DieResult? die = null)
    {
        var chips = new List<VerdictChip>();
        if (die != null && die.IsPacked)
        {
            var names = die.Detections.Where(d => d.IsPacker || d.IsProtector).Select(d => d.Name).Distinct().Take(3);
            chips.Add(new VerdictChip("Packed: " + string.Join(", ", names), ChipKind.Warn));
        }
        if (f.IsPe && (f.Signature == null || !f.Signature.Valid))
            chips.Add(new VerdictChip("Unsigned", ChipKind.Warn));
        if (f.ExtensionMismatch)
            chips.Add(new VerdictChip("Ext mismatch", ChipKind.Warn));
        if (f.Name.DoubleExtension)
            chips.Add(new VerdictChip("Double ext", ChipKind.Bad));
        if (f.Name.HasBidiOverride)
            chips.Add(new VerdictChip("RTLO", ChipKind.Bad));
        if (f.Zone is { ZoneId: >= 3 })
            chips.Add(new VerdictChip("From internet", ChipKind.Info));
        return chips;
    }
}

// ------------------------------------------------------------------------------------------------ navigation

/// <summary>Back/forward history of visited folder paths.</summary>
public sealed class NavHistory
{
    private readonly List<string> _items = new();
    private int _index = -1;

    public string? Current => _index >= 0 ? _items[_index] : null;
    public bool CanBack => _index > 0;
    public bool CanForward => _index >= 0 && _index < _items.Count - 1;

    /// <summary>Visits <paramref name="path"/> (drops the forward stack; the same path twice in a row is ignored).</summary>
    public void Push(string path)
    {
        if (_index >= 0 && string.Equals(_items[_index], path, StringComparison.OrdinalIgnoreCase)) return;
        if (_index < _items.Count - 1) _items.RemoveRange(_index + 1, _items.Count - _index - 1);
        _items.Add(path);
        _index = _items.Count - 1;
    }

    public string? Back() => CanBack ? _items[--_index] : null;
    public string? Forward() => CanForward ? _items[++_index] : null;
    public void Clear() { _items.Clear(); _index = -1; }
}

public sealed record BreadcrumbSegment(string Label, string Path);

public static class Breadcrumbs
{
    /// <summary>Segments from <paramref name="rootPath"/> (labelled <paramref name="rootLabel"/>) down to <paramref name="path"/>.</summary>
    public static IReadOnlyList<BreadcrumbSegment> Build(string path, string rootPath, string rootLabel)
    {
        var list = new List<BreadcrumbSegment> { new(rootLabel, rootPath) };
        var root = rootPath.TrimEnd('\\', '/');
        var full = path.TrimEnd('\\', '/');
        if (full.Length <= root.Length || !full.StartsWith(root + "\\", StringComparison.OrdinalIgnoreCase)) return list;
        var cur = root;
        foreach (var part in full[(root.Length + 1)..].Split('\\', StringSplitOptions.RemoveEmptyEntries))
        {
            cur += "\\" + part;
            list.Add(new BreadcrumbSegment(part, cur));
        }
        return list;
    }

    /// <summary>The parent folder, or null when <paramref name="path"/> is (or is above) <paramref name="rootPath"/>.</summary>
    public static string? Parent(string path, string rootPath)
    {
        var root = rootPath.TrimEnd('\\', '/');
        var full = path.TrimEnd('\\', '/');
        if (full.Length <= root.Length) return null;
        var p = Path.GetDirectoryName(full);
        return string.IsNullOrEmpty(p) ? null : p;
    }
}

// ------------------------------------------------------------------------------------------------ formatting

public static class Fmt
{
    public static string Size(long bytes)
    {
        if (bytes < 1024) return bytes.ToString(CultureInfo.InvariantCulture) + " B";
        double v = bytes; string[] u = { "KB", "MB", "GB", "TB" }; int i = -1;
        do { v /= 1024; i++; } while (v >= 1024 && i < u.Length - 1);
        return v.ToString(v >= 100 ? "0" : "0.#", CultureInfo.InvariantCulture) + " " + u[i];
    }

    public static string Utc(DateTime t) => t.ToUniversalTime().ToString("yyyy-MM-dd HH:mm:ss", CultureInfo.InvariantCulture) + " UTC";

    /// <summary>Hex dump of bytes as a string (for the CyberChef/clipboard helpers): "AA BB CC".</summary>
    public static string HexBytes(ReadOnlySpan<byte> b) => Convert.ToHexString(b);
}
