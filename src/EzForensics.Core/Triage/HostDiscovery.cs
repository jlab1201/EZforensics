using EzForensics.Core.Archives;
using EzForensics.Core.Platforms;
using EzForensics.Core.SystemInfo;

namespace EzForensics.Core.Triage;

/// <summary>How the evidence of a discovered system was acquired.</summary>
public enum AcquisitionKind
{
    /// <summary>Root of a live, local NTFS drive (raw access needs Administrator).</summary>
    LiveVolume,
    /// <summary>A complete volume: a mounted image or a raw copy (has <c>$MFT</c> and the OS files).</summary>
    MountedImageOrRawCopy,
    /// <summary>A triage collection (KAPE, CyLR, a folder copy...).</summary>
    CopiedCollection,
    /// <summary>Evidence extracted from an archive (zip, 7z, rar, tar...): file-system times are those restored from the archive.</summary>
    ExtractedArchive,
}

/// <summary>One system (Windows installation / volume root) found under a source.</summary>
public sealed record DiscoveredSystem
{
    /// <summary>The system root (the folder that holds <c>Windows</c>, <c>Users</c>...).</summary>
    public required string RootPath { get; init; }
    /// <summary>Unique within one discovery and safe as a folder name.</summary>
    public required string HostName { get; init; }
    /// <summary><see cref="HostDiscovery.FromSystemHive"/>, <see cref="HostDiscovery.FromFolderName"/> or <see cref="HostDiscovery.FromDefault"/>.</summary>
    public required string HostNameSource { get; init; }
    public OsFamily OsFamily { get; init; } = OsFamily.Unknown;
    public AcquisitionKind AcquisitionKind { get; init; }
    /// <summary>Archive this system was extracted from (null = not from an archive).</summary>
    public ArchiveOrigin? Archive { get; init; }
    /// <summary>IPv4 addresses of the system's interfaces (SYSTEM hive <c>Services\Tcpip\Parameters\Interfaces</c>, or the case's
    /// Computer details output for a host of an earlier ingest); empty when unknown.</summary>
    public IReadOnlyList<string> IpAddresses { get; init; } = Array.Empty<string>();
}

/// <summary>
/// Finds every system root under a source, e.g. <c>src\HOST1\C</c>, <c>src\HOST2\C</c> or <c>src\HOST\tout\C</c> (several KAPE
/// collections in one folder). Walks at most <c>maxDepth</c> folder levels below the source, never into reparse points, OS folders
/// (Windows, Users, Program Files...) or into a root already found. Read-only.
/// </summary>
public static class HostDiscovery
{
    public const string FromSystemHive = "SYSTEM hive ComputerName", FromFolderName = "folder name", FromDefault = "default";
    public const string DefaultHostName = "HOST";

    private static readonly HashSet<string> SkipDirs = new(StringComparer.OrdinalIgnoreCase)
    {
        "$Recycle.Bin", "RECYCLER", "$Extend", "System Volume Information", "Windows", "Users", "Documents and Settings",
        "Program Files", "Program Files (x86)", "ProgramData", "PerfLogs", "Recovery", "$WinREAgent", "$SysReset", "Config.Msi",
    };

    // Folder names that describe the collection, not the host.
    private static readonly HashSet<string> NonHostNames = new(StringComparer.OrdinalIgnoreCase)
    {
        "C", "[root]", "tout", "Targets", "mout", "Modules", "kape", "KapeOutput",
    };

    private static readonly HashSet<string> ReservedNames = new(StringComparer.OrdinalIgnoreCase)
    {
        "CON", "PRN", "AUX", "NUL", "COM1", "COM2", "COM3", "COM4", "COM5", "COM6", "COM7", "COM8", "COM9",
        "LPT1", "LPT2", "LPT3", "LPT4", "LPT5", "LPT6", "LPT7", "LPT8", "LPT9",
    };

    /// <param name="source">Folder or drive root to search.</param>
    /// <param name="maxDepth">Folder levels below <paramref name="source"/> that are still examined (0 = the source only).</param>
    /// <param name="opener">Hive opener used to read the computer name (default <see cref="ZimmermanHiveOpener"/>).</param>
    /// <param name="isRunningOsRoot">"Is this the running OS volume root" test (default <see cref="SourceLayoutDetector.IsRunningOsRoot"/>; unit tests).</param>
    public static IReadOnlyList<DiscoveredSystem> Discover(string source, int maxDepth = 3, IHiveOpener? opener = null, Func<string, bool>? isRunningOsRoot = null)
    {
        if (string.IsNullOrWhiteSpace(source)) return Array.Empty<DiscoveredSystem>();
        string src;
        try { src = Path.GetFullPath(source); }
        catch (Exception ex) when (ex is ArgumentException or NotSupportedException or PathTooLongException) { return Array.Empty<DiscoveredSystem>(); }
        if (src.Length > 3) src = src.TrimEnd('\\', '/');
        if (!Directory.Exists(src)) return Array.Empty<DiscoveredSystem>();

        var roots = new List<string>();
        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        // Only the running OS volume is "live" (one system: never walk it). Any other drive root (mounted image, USB disk, KAPE drive) is walked.
        if (SourceLayoutDetector.IsLiveVolumeRoot(src, isRunningOsRoot)) roots.Add(src);
        else Walk(src, 0, Math.Max(0, maxDepth), roots, seen);

        opener ??= new ZimmermanHiveOpener();
        var used = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var result = new List<DiscoveredSystem>();
        foreach (var root in roots)
        {
            var (name, nameSource, ips) = ResolveIdentity(root, opener);
            name = Unique(Sanitize(name), used);
            result.Add(new DiscoveredSystem
            {
                RootPath = root,
                HostName = name,
                HostNameSource = nameSource,
                IpAddresses = ips,
                OsFamily = HasWindows(root) ? OsFamily.Windows : OsFamily.Unknown,
                AcquisitionKind = Kind(root, isRunningOsRoot),
            });
        }
        return result;
    }

    private static void Walk(string dir, int depth, int maxDepth, List<string> roots, HashSet<string> seen)
    {
        if (!seen.Add(dir)) return;
        // a volume root always has $MFT: at a drive root only the OS folders count (D:\HOST1\C on an evidence drive is still found)
        if (SourceLayoutDetector.LooksLikeSystemRoot(dir, ignoreMft: SourceLayoutDetector.IsVolumeRoot(dir))) { roots.Add(dir); return; }
        if (depth >= maxDepth) return;
        List<DirectoryInfo> children;
        try
        {
            children = new DirectoryInfo(dir).EnumerateDirectories()
                .Where(d => !d.Attributes.HasFlag(FileAttributes.ReparsePoint) && !SkipDirs.Contains(d.Name))
                .OrderBy(d => d.Name, StringComparer.OrdinalIgnoreCase).ToList();
        }
        catch (Exception ex) when (ex is UnauthorizedAccessException or IOException or System.Security.SecurityException) { return; }
        foreach (var c in children) Walk(c.FullName, depth + 1, maxDepth, roots, seen);
    }

    private static bool HasWindows(string root)
    {
        try { return Directory.Exists(Path.Combine(root, "Windows", "System32")) || Directory.Exists(Path.Combine(root, "Windows", "system32")); }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { return false; }
    }

    /// <summary>
    /// Root of the running OS volume = live; else <c>$MFT</c> next to a complete OS (ntoskrnl.exe present) = mounted image / raw copy; else a
    /// copied collection.
    /// </summary>
    public static AcquisitionKind Kind(string root, Func<string, bool>? isRunningOsRoot = null)
    {
        if (SourceLayoutDetector.IsLiveVolumeRoot(root, isRunningOsRoot)) return AcquisitionKind.LiveVolume;
        try
        {
            if (File.Exists(Path.Combine(root, "$MFT")) && File.Exists(Path.Combine(root, "Windows", "System32", "ntoskrnl.exe")))
                return AcquisitionKind.MountedImageOrRawCopy;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { }
        return AcquisitionKind.CopiedCollection;
    }

    /// <summary>ComputerName from the root's SYSTEM hive; else the nearest meaningful folder name; else <see cref="DefaultHostName"/>.</summary>
    public static (string Name, string Source) ResolveHostName(string root, IHiveOpener? opener = null)
    {
        var (name, source, _) = ResolveIdentity(root, opener);
        return (name, source);
    }

    /// <summary><see cref="ResolveHostName"/> plus the interface IPs of the same SYSTEM hive (one hive open; empty without a readable hive).</summary>
    public static (string Name, string Source, IReadOnlyList<string> Ips) ResolveIdentity(string root, IHiveOpener? opener = null)
    {
        IReadOnlyList<string> ips = Array.Empty<string>();
        var hive = ComputerDetailsCollector.HivePath(root, "SYSTEM");
        if (hive != null)
        {
            try
            {
                using var h = (opener ?? new ZimmermanHiveOpener()).Open(hive, "SYSTEM");
                try { ips = InterfaceAddresses.AllIps(h); } catch (Exception) { /* keep the host name */ }
                var cs = ComputerDetailsCollector.ResolveControlSet(h);
                var name = cs == null ? null : h.GetKey(cs + @"\Control\ComputerName\ComputerName")?.Value("ComputerName")?.AsString()?.Trim();
                if (!string.IsNullOrWhiteSpace(name)) return (name, FromSystemHive, ips);
            }
            catch (Exception) { /* unreadable / not a hive: fall back to the folder name */ }
        }
        var folder = FolderHostName(root);
        return folder != null ? (folder, FromFolderName, ips) : (DefaultHostName, FromDefault, ips);
    }

    /// <summary>
    /// "Windows 10 Pro 22H2 (build 19045.3803)" from the root's SOFTWARE hive (Windows 11 derived from the build), or null when the hive is
    /// missing or unreadable. Read-only; reads the whole SOFTWARE hive, so call it off the UI thread.
    /// </summary>
    public static string? ReadOsVersion(string root, IHiveOpener? opener = null)
    {
        var hive = ComputerDetailsCollector.HivePath(root, "SOFTWARE");
        if (hive == null) return null;
        try
        {
            using var h = (opener ?? new ZimmermanHiveOpener()).Open(hive, "SOFTWARE");
            return OsVersionText(h);
        }
        catch (Exception) { return null; /* unreadable / not a hive */ }
    }

    /// <summary>One-line OS version of a SOFTWARE hive (<c>Microsoft\Windows NT\CurrentVersion</c>), or null without a product name.</summary>
    public static string? OsVersionText(IRegistryHive software)
    {
        var cv = software.GetKey(@"Microsoft\Windows NT\CurrentVersion");
        string? S(string n) => cv?.Value(n)?.AsString()?.Trim() is { Length: > 0 } v ? v : null;
        var product = ComputerDetailsCollector.DeriveOsName(S("ProductName"), S("CurrentBuild") ?? S("CurrentBuildNumber"));
        if (product == null) return null;
        var version = S("DisplayVersion") ?? S("ReleaseId");
        var build = S("CurrentBuild") ?? S("CurrentBuildNumber");
        var ubr = cv?.Value("UBR")?.AsNumber();
        var text = product + (version != null ? " " + version : "");
        if (build != null) text += $" (build {build}{(ubr != null ? "." + ubr.Value.ToString(System.Globalization.CultureInfo.InvariantCulture) : "")})";
        return text;
    }

    /// <summary>Nearest folder (the root itself, then its ancestors) whose name is not a drive letter or a collection folder (C, [root], tout, Targets...).</summary>
    public static string? FolderHostName(string root)
    {
        DirectoryInfo? d;
        try { d = new DirectoryInfo(root); }
        catch (Exception ex) when (ex is ArgumentException or PathTooLongException or NotSupportedException) { return null; }
        for (; d != null; d = d.Parent)
        {
            if (d.Parent == null) break;                       // the drive / share root itself
            var n = d.Name;
            if (n.Length == 0 || (n.Length == 1 && char.IsLetter(n[0])) || NonHostNames.Contains(n)) continue;
            return n;
        }
        return null;
    }

    /// <summary>Makes <paramref name="name"/> safe as a Windows folder name (invalid characters → '_', no reserved device names, max 64 chars).</summary>
    public static string Sanitize(string name)
    {
        var invalid = Path.GetInvalidFileNameChars().Concat(new[] { '<', '>', ':', '"', '/', '\\', '|', '?', '*' }).ToHashSet();
        var chars = name.Select(c => invalid.Contains(c) || char.IsControl(c) ? '_' : c).ToArray();
        var s = new string(chars).Trim().TrimEnd('.', ' ');
        if (s.Length > 64) s = s[..64].TrimEnd('.', ' ');
        if (s.Length == 0 || s.All(c => c == '_' || c == '.')) return DefaultHostName;
        var stem = s.Split('.')[0];
        if (ReservedNames.Contains(stem)) s = "_" + s;
        if (Case.CaseIndex.IsCaseWideFolder(s)) s = "Host" + s;   // "_Case" is the case-wide results folder, never a host
        return s;
    }

    private static string Unique(string name, HashSet<string> used)
    {
        if (used.Add(name)) return name;
        for (int i = 2; ; i++)
        {
            var candidate = $"{name} ({i})";
            if (used.Add(candidate)) return candidate;
        }
    }
}
