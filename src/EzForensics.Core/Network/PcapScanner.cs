using System.Text.RegularExpressions;

namespace EzForensics.Core.Network;

/// <summary>Packet capture container format, from the file's magic number.</summary>
public enum PcapFormat
{
    /// <summary>libpcap, microsecond timestamps (<c>d4 c3 b2 a1</c> little-endian / <c>a1 b2 c3 d4</c> big-endian).</summary>
    Pcap,
    /// <summary>libpcap, nanosecond timestamps (<c>4d 3c b2 a1</c> / <c>a1 b2 3c 4d</c>).</summary>
    PcapNanosecond,
    /// <summary>pcapng (Section Header Block <c>0a 0d 0d 0a</c> followed by the byte-order magic <c>1a2b3c4d</c>).</summary>
    PcapNg,
}

/// <summary>A packet capture found in a source.</summary>
public sealed record PcapFile(string Path, long Size, PcapFormat Format)
{
    public string Name => System.IO.Path.GetFileName(Path);
    public string FormatLabel => Format switch { PcapFormat.PcapNg => "pcapng", PcapFormat.PcapNanosecond => "pcap (ns)", _ => "pcap" };
}

/// <summary>Magic-number detection of pcap / pcapng files (the extension alone is never trusted).</summary>
public static class PcapDetector
{
    /// <summary>Extensions that are probed (opened) during a scan; everything else is skipped without I/O.</summary>
    private static readonly HashSet<string> ProbeExtensions = new(StringComparer.OrdinalIgnoreCase)
    {
        ".pcap", ".pcapng", ".cap", ".dmp", ".ntar", ".trace", ".trc", ".out", ".raw", ".bin", ".dump", ".tcpdump", ".snoop",
    };

    /// <summary>Rotated captures: <c>x.pcap0</c>, <c>x.pcap12</c>, <c>x.pcap.1</c>, <c>x.pcapng.3</c>, <c>x.cap.2</c>.</summary>
    private static readonly Regex RotatedRx = new(@"\.(pcap|pcapng|cap)(\d+|\.\d+)$", RegexOptions.IgnoreCase | RegexOptions.Compiled | RegexOptions.CultureInvariant);

    /// <summary>
    /// True when a file name is worth opening: a capture extension (.pcap .pcapng .cap; .dmp and the other generic ones only
    /// count when the magic matches), a rotated name, a purely numeric extension or no extension at all.
    /// </summary>
    public static bool IsCandidateName(string fileName)
    {
        if (string.IsNullOrEmpty(fileName)) return false;
        if (RotatedRx.IsMatch(fileName)) return true;
        var ext = Path.GetExtension(fileName);
        if (ext.Length == 0) return true;
        if (ProbeExtensions.Contains(ext)) return true;
        return ext.Length > 1 && ext.Skip(1).All(char.IsDigit);
    }

    /// <summary>Format from the first bytes (at least 4; 12 to confirm pcapng's byte-order magic).</summary>
    public static PcapFormat? FromMagic(ReadOnlySpan<byte> head)
    {
        if (head.Length < 4) return null;
        uint le = (uint)(head[0] | head[1] << 8 | head[2] << 16 | head[3] << 24);
        switch (le)
        {
            case 0xA1B2C3D4: // bytes d4 c3 b2 a1 (little-endian file)
            case 0xD4C3B2A1: // bytes a1 b2 c3 d4 (big-endian file)
                return PcapFormat.Pcap;
            case 0xA1B23C4D: // bytes 4d 3c b2 a1
            case 0x4D3CB2A1: // bytes a1 b2 3c 4d
                return PcapFormat.PcapNanosecond;
            case 0x0A0D0D0A: // pcapng Section Header Block (same bytes in both byte orders)
                if (head.Length >= 12)
                {
                    uint bom = (uint)(head[8] | head[9] << 8 | head[10] << 16 | head[11] << 24);
                    if (bom != 0x1A2B3C4D && bom != 0x4D3C2B1A) return null;
                }
                return PcapFormat.PcapNg;
            default:
                return null;
        }
    }

    /// <summary>Opens the file and checks its magic; null when it is not a capture (or unreadable / too small).</summary>
    public static PcapFormat? Probe(string path)
    {
        try
        {
            using var fs = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete, 1, FileOptions.None);
            if (fs.Length < 24) return null; // a pcap global header is 24 bytes, a pcapng SHB at least 28
            Span<byte> buf = stackalloc byte[12];
            int n = 0;
            while (n < buf.Length)
            {
                int r = fs.Read(buf[n..]);
                if (r <= 0) break;
                n += r;
            }
            return FromMagic(buf[..n]);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or ArgumentException or NotSupportedException) { return null; }
    }
}

/// <summary>Limits for <see cref="PcapScanner.Scan"/>.</summary>
public sealed class PcapScanOptions
{
    /// <summary>Folder levels below the source that are searched (the source itself is level 0).</summary>
    public int MaxDepth { get; set; } = 8;
    /// <summary>Stop after this many directory entries (files + folders) have been looked at.</summary>
    public int MaxEntries { get; set; } = 250_000;
    /// <summary>Stop after this many files have been opened to check their magic.</summary>
    public int MaxProbes { get; set; } = 20_000;
    /// <summary>Stop after this many captures were found.</summary>
    public int MaxResults { get; set; } = 1_000;

    /// <summary>
    /// Folders that never hold evidence captures but are huge (Windows, Program Files, WinSxS, package caches...). Matched on
    /// the folder name, case-insensitive. User profiles, ProgramData, the Recycle Bin and everything else are searched.
    /// </summary>
    public ISet<string> SkipDirectories { get; set; } = new HashSet<string>(StringComparer.OrdinalIgnoreCase)
    {
        "Windows", "Program Files", "Program Files (x86)", "WinSxS", "System Volume Information", "$Extend", "$WinREAgent",
        "node_modules", ".git", ".svn", ".hg", "__pycache__", "site-packages", ".nuget", ".vs",
        "MSOCache", "Windows.old", "$Windows.~BT", "$Windows.~WS", "Recovery",
    };
}

/// <summary>Result of a capture scan.</summary>
public sealed record PcapScanResult(IReadOnlyList<PcapFile> Files, int EntriesSeen, int FilesProbed, bool Truncated, string? TruncatedReason)
{
    public long TotalBytes => Files.Sum(f => f.Size);
    public static readonly PcapScanResult Empty = new(Array.Empty<PcapFile>(), 0, 0, false, null);
}

/// <summary>
/// Finds packet captures in a triage source: a single file picked directly, a plain evidence folder, or a KAPE / system
/// root tree. Detection is by magic number; only candidate names (see <see cref="PcapDetector.IsCandidateName"/>) are opened.
/// Depth, entry and probe limits keep it fast on a live drive; huge irrelevant trees are skipped; junctions are not followed.
/// </summary>
public static class PcapScanner
{
    public static PcapScanResult Scan(string source, PcapScanOptions? options = null, CancellationToken ct = default)
    {
        var o = options ?? new PcapScanOptions();
        if (string.IsNullOrWhiteSpace(source)) return PcapScanResult.Empty;
        if (File.Exists(source))
        {
            var fmt = PcapDetector.Probe(source);
            return fmt == null
                ? new PcapScanResult(Array.Empty<PcapFile>(), 1, 1, false, null)
                : new PcapScanResult(new[] { new PcapFile(Path.GetFullPath(source), SafeLength(source), fmt.Value) }, 1, 1, false, null);
        }
        if (!Directory.Exists(source)) return PcapScanResult.Empty;

        var found = new List<PcapFile>();
        int entries = 0, probes = 0;
        string? truncated = null;
        var enumOpts = new EnumerationOptions
        {
            IgnoreInaccessible = true,
            RecurseSubdirectories = false,
            AttributesToSkip = FileAttributes.ReparsePoint, // never follow junctions / symlinks (loops, other volumes)
            ReturnSpecialDirectories = false,
        };

        var stack = new Stack<(string Dir, int Depth)>();
        stack.Push((source, 0));
        while (stack.Count > 0 && truncated == null)
        {
            ct.ThrowIfCancellationRequested();
            var (dir, depth) = stack.Pop();
            List<string> subdirs = new();
            try
            {
                foreach (var entry in new DirectoryInfo(dir).EnumerateFileSystemInfos("*", enumOpts))
                {
                    if (++entries > o.MaxEntries) { truncated = $"stopped after {o.MaxEntries:N0} entries"; break; }
                    if (entry is DirectoryInfo di)
                    {
                        if (depth < o.MaxDepth && !o.SkipDirectories.Contains(di.Name)) subdirs.Add(di.FullName);
                        continue;
                    }
                    if (!PcapDetector.IsCandidateName(entry.Name)) continue;
                    if (++probes > o.MaxProbes) { truncated = $"stopped after checking {o.MaxProbes:N0} files"; break; }
                    var fmt = PcapDetector.Probe(entry.FullName);
                    if (fmt == null) continue;
                    found.Add(new PcapFile(entry.FullName, ((FileInfo)entry).Length, fmt.Value));
                    if (found.Count >= o.MaxResults) { truncated = $"stopped after {o.MaxResults:N0} captures"; break; }
                }
            }
            catch (Exception ex) when (ex is UnauthorizedAccessException or IOException or System.Security.SecurityException) { }
            // Depth-first in name order (pushed in reverse so the first name is processed first).
            subdirs.Sort(StringComparer.OrdinalIgnoreCase);
            for (int i = subdirs.Count - 1; i >= 0; i--) stack.Push((subdirs[i], depth + 1));
        }
        found.Sort((a, b) => StringComparer.OrdinalIgnoreCase.Compare(a.Path, b.Path));
        return new PcapScanResult(found, entries, probes, truncated != null, truncated);
    }

    private static long SafeLength(string path)
    {
        try { return new FileInfo(path).Length; } catch (Exception) { return 0; }
    }
}
