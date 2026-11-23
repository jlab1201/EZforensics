using System.Runtime.InteropServices;
using System.Text;

namespace EzForensics.Core.Explore;

/// <summary>One NTFS alternate data stream (<paramref name="Name"/> without the <c>:$DATA</c> suffix, e.g. <c>Zone.Identifier</c>).</summary>
public sealed record AdsStream(string Name, long Size);

/// <summary>Mark-of-the-Web from a <c>Zone.Identifier</c> stream.</summary>
public sealed record ZoneIdentifier(int? ZoneId, string? HostUrl, string? ReferrerUrl, string? LastWriterPackageFamilyName, string Raw)
{
    public string ZoneName => ZoneId switch
    {
        0 => "Local machine",
        1 => "Local intranet",
        2 => "Trusted sites",
        3 => "Internet",
        4 => "Restricted sites",
        null => "Unknown",
        _ => $"Zone {ZoneId}",
    };

    /// <summary>Parses the INI text (<c>[ZoneTransfer]</c> ZoneId / HostUrl / ReferrerUrl); null when it has no recognisable key.</summary>
    public static ZoneIdentifier? Parse(string? text)
    {
        if (string.IsNullOrWhiteSpace(text)) return null;
        int? zone = null; string? host = null, referrer = null, pkg = null;
        foreach (var raw in text.Split('\n'))
        {
            var line = raw.Trim().TrimStart('\uFEFF');
            int eq = line.IndexOf('=');
            if (eq <= 0 || line.StartsWith('[') || line.StartsWith(';')) continue;
            var key = line[..eq].Trim();
            var val = line[(eq + 1)..].Trim();
            if (key.Equals("ZoneId", StringComparison.OrdinalIgnoreCase) && int.TryParse(val, out var z)) zone = z;
            else if (key.Equals("HostUrl", StringComparison.OrdinalIgnoreCase)) host = val;
            else if (key.Equals("ReferrerUrl", StringComparison.OrdinalIgnoreCase)) referrer = val;
            else if (key.Equals("LastWriterPackageFamilyName", StringComparison.OrdinalIgnoreCase)) pkg = val;
        }
        return zone == null && host == null && referrer == null ? null : new ZoneIdentifier(zone, host, referrer, pkg, text);
    }
}

/// <summary>Lists NTFS alternate data streams (FindFirstStreamW) and reads <c>Zone.Identifier</c>. Windows only; elsewhere empty.</summary>
public static class AlternateStreams
{
    public const string ZoneIdentifierStream = "Zone.Identifier";
    private const int MaxZoneBytes = 64 * 1024;

    /// <summary>Named data streams of <paramref name="path"/> (the unnamed main stream is left out). Empty when none or unsupported.</summary>
    public static IReadOnlyList<AdsStream> List(string path)
    {
        var list = new List<AdsStream>();
        if (!OperatingSystem.IsWindows()) return list;
        var h = FindFirstStreamW(LongPath(path), 0, out var data, 0);
        if (h == new IntPtr(-1)) return list;
        try
        {
            do
            {
                var n = data.cStreamName ?? "";
                if (n.StartsWith(':') && n.EndsWith(":$DATA", StringComparison.OrdinalIgnoreCase))
                {
                    var name = n[1..^6];
                    if (name.Length > 0) list.Add(new AdsStream(name, data.StreamSize));
                }
            } while (FindNextStreamW(h, out data));
        }
        finally { FindClose(h); }
        return list;
    }

    /// <summary>Reads a named stream as text (capped), read-only and shared; null when absent or unreadable.</summary>
    public static string? ReadText(string path, string stream, int maxBytes = MaxZoneBytes)
    {
        if (!OperatingSystem.IsWindows()) return null;
        try
        {
            using var fs = new FileStream(LongPath(path) + ":" + stream, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete);
            var buf = new byte[(int)Math.Min(maxBytes, Math.Max(0, fs.Length))];
            int n = 0, r;
            while (n < buf.Length && (r = fs.Read(buf, n, buf.Length - n)) > 0) n += r;
            if (n >= 2 && buf[0] == 0xFF && buf[1] == 0xFE) return Encoding.Unicode.GetString(buf, 2, n - 2);
            return Encoding.UTF8.GetString(buf, 0, n);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or NotSupportedException or ArgumentException) { return null; }
    }

    /// <summary>The parsed <c>Zone.Identifier</c> stream of <paramref name="path"/>, or null.</summary>
    public static ZoneIdentifier? ReadZoneIdentifier(string path) => ZoneIdentifier.Parse(ReadText(path, ZoneIdentifierStream));

    private static string LongPath(string p)
    {
        var full = Path.GetFullPath(p);
        if (full.StartsWith(@"\\?\", StringComparison.Ordinal) || full.Length < 248) return full;
        return full.StartsWith(@"\\", StringComparison.Ordinal) ? @"\\?\UNC\" + full[2..] : @"\\?\" + full;
    }

    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
    private struct WIN32_FIND_STREAM_DATA
    {
        public long StreamSize;
        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 296)] public string cStreamName;
    }

    [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    private static extern IntPtr FindFirstStreamW(string lpFileName, int infoLevel, out WIN32_FIND_STREAM_DATA lpFindStreamData, uint dwFlags);

    [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool FindNextStreamW(IntPtr hFindStream, out WIN32_FIND_STREAM_DATA lpFindStreamData);

    [DllImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool FindClose(IntPtr hFindFile);
}
