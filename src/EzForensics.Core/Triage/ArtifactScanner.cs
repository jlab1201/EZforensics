using System.Text.RegularExpressions;
using EzForensics.Core.Platforms;
using EzForensics.Core.SystemInfo;

namespace EzForensics.Core.Triage;

/// <summary>A scan hit. First six members match CONTRACTS.md; the rest are additive.</summary>
/// <param name="System">The discovered system the hit belongs to (host, acquisition...); null when scanned without host discovery.</param>
public sealed record DetectedArtifact(
    string RuleId, string Label, string ToolId, string Category, string MatchedPath, bool PerMatch,
    int MatchCount = 1, string SystemRoot = "", bool RequiresAdmin = false, bool DefaultSelected = true,
    DiscoveredSystem? System = null)
{
    /// <summary>Host name of <see cref="System"/> ("" when unknown: the planner then derives it from the system root folder).</summary>
    public string Host => System?.HostName ?? "";
    public AcquisitionKind? Acquisition => System?.AcquisitionKind;
}

/// <summary>Result of <see cref="ArtifactScanner.ScanSource"/>: every system found under the source and the artifacts of each.</summary>
/// <param name="Layout">Compatibility view: <see cref="SourceLayout.SystemRoots"/> = the systems' roots.</param>
/// <param name="Systems">Every system (host) found, in discovery order. The UI lists them; all are planned by default.</param>
public sealed record TriageScanResult(string Source, SourceLayout Layout, IReadOnlyList<DiscoveredSystem> Systems, List<DetectedArtifact> Artifacts)
{
    /// <summary>Memory images found in the source (<see cref="Memory.MemoryImageScanner"/>), planned by <see cref="MemoryTriage.Plan"/>. Empty when none.</summary>
    public IReadOnlyList<Memory.MemoryImage> MemoryImages { get; init; } = Array.Empty<Memory.MemoryImage>();

    /// <summary>
    /// A single evidence file (e.g. a packet capture or a memory image): no system root. A file that is a memory image
    /// (<see cref="Memory.MemoryImageDetector"/>) is exposed in <see cref="MemoryImages"/>.
    /// </summary>
    public static TriageScanResult ForFile(string file, string note)
    {
        Memory.MemoryImage? image = null;
        try { image = Memory.MemoryImageDetector.Detect(file); }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { }
        return new(file, new SourceLayout(Array.Empty<string>(), false, new[] { note }), Array.Empty<DiscoveredSystem>(), new List<DetectedArtifact>())
        {
            MemoryImages = image == null ? Array.Empty<Memory.MemoryImage>() : new[] { image },
        };
    }
}

public static class ArtifactScanner
{
    /// <summary>Host name source used when a live system drive's host name is the running computer's name.</summary>
    public const string FromLiveSystem = "live system";

    /// <summary>
    /// Finds every system under <paramref name="source"/> (<see cref="HostDiscovery"/>: <c>src\HOST1\C</c>, <c>src\HOST2\C</c>, a mounted image, a
    /// live drive...) and scans each with the triage rules. When host discovery finds nothing, <see cref="SourceLayoutDetector"/> decides
    /// (the source itself is scanned as-is). Read-only.
    /// </summary>
    public static TriageScanResult ScanSource(string source, IReadOnlyList<TriageRule>? rules = null, IHiveOpener? opener = null,
        int maxDepth = 3, CancellationToken ct = default, Func<string, bool>? isRunningOsRoot = null)
    {
        var systems = DiscoverSystems(source, opener, maxDepth, out var notes, isRunningOsRoot);
        ct.ThrowIfCancellationRequested();
        var layout = new SourceLayout(systems.Select(s => s.RootPath).ToList(),
            systems.Any(s => s.AcquisitionKind == AcquisitionKind.LiveVolume), notes);
        var found = Scan(systems, rules ?? TriageRules.LoadEmbedded(), ct);
        return new TriageScanResult(source, layout, systems, found) { MemoryImages = ScanMemoryImages(new[] { source }, ct) };
    }

    /// <summary>Memory images under the sources (bounded scan, magic bytes decide); unreadable sources are skipped.</summary>
    public static IReadOnlyList<Memory.MemoryImage> ScanMemoryImages(IEnumerable<string> sources, CancellationToken ct = default)
    {
        var list = new List<Memory.MemoryImage>();
        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var s in sources)
        {
            ct.ThrowIfCancellationRequested();
            try { foreach (var m in Memory.MemoryImageScanner.Scan(s, null, ct)) if (seen.Add(m.Path)) list.Add(m); }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { }
        }
        return list;
    }

    /// <summary>
    /// <see cref="ScanSource"/> for several roots (e.g. several extracted archives), results merged; host names are kept unique across roots
    /// (a repeat gets " (2)", " (3)"...). <paramref name="origins"/> maps a source root to the archive it was extracted from: its systems are
    /// then <see cref="AcquisitionKind.ExtractedArchive"/> and carry <see cref="DiscoveredSystem.Archive"/>.
    /// </summary>
    public static TriageScanResult ScanSources(IReadOnlyList<string> sources, IReadOnlyList<TriageRule>? rules = null, IHiveOpener? opener = null,
        int maxDepth = 3, CancellationToken ct = default, Func<string, bool>? isRunningOsRoot = null,
        IReadOnlyDictionary<string, Archives.ArchiveOrigin>? origins = null)
    {
        if (sources.Count == 1 && origins == null) return ScanSource(sources[0], rules, opener, maxDepth, ct, isRunningOsRoot);
        var all = new List<DiscoveredSystem>();
        var notes = new List<string>();
        var used = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var src in sources)
        {
            ct.ThrowIfCancellationRequested();
            Archives.ArchiveOrigin? origin = null;
            if (origins != null && !origins.TryGetValue(src, out origin)) origin = null;
            var systems = DiscoverSystems(src, opener, maxDepth, out var n, isRunningOsRoot);
            notes.AddRange(n);
            foreach (var s in systems)
            {
                var name = s.HostName;
                for (int i = 2; !used.Add(name); i++) name = $"{s.HostName} ({i})";
                var t = s with { HostName = name };
                if (origin != null) t = t with { Archive = origin, AcquisitionKind = AcquisitionKind.ExtractedArchive };
                all.Add(t);
            }
        }
        var layout = new SourceLayout(all.Select(s => s.RootPath).ToList(), all.Any(s => s.AcquisitionKind == AcquisitionKind.LiveVolume), notes);
        var found = Scan(all, rules ?? TriageRules.LoadEmbedded(), ct);
        return new TriageScanResult(sources.Count > 0 ? sources[0] : "", layout, all, found) { MemoryImages = ScanMemoryImages(sources, ct) };
    }

    /// <summary>The systems of a source (see <see cref="ScanSource"/>), with plain-language notes.</summary>
    /// <param name="isRunningOsRoot">"Is the running OS volume root" test (default <see cref="SourceLayoutDetector.IsRunningOsRoot"/>; unit tests).</param>
    public static IReadOnlyList<DiscoveredSystem> DiscoverSystems(string source, IHiveOpener? opener, int maxDepth, out List<string> notes,
        Func<string, bool>? isRunningOsRoot = null)
    {
        notes = new List<string>();
        var systems = HostDiscovery.Discover(source, maxDepth, opener, isRunningOsRoot).ToList();
        if (systems.Count == 0)
        {
            var layout = SourceLayoutDetector.Detect(source, isRunningOsRoot);
            notes.AddRange(layout.Notes);
            var used = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            foreach (var root in layout.SystemRoots)
            {
                var s = SystemFor(root, opener, isRunningOsRoot: isRunningOsRoot);
                var name = s.HostName;
                for (int n = 2; !used.Add(name); n++) name = $"{s.HostName} ({n})";
                systems.Add(s with { HostName = name });
            }
        }
        else if (systems.Any(s => s.AcquisitionKind == AcquisitionKind.LiveVolume))
            notes.Add("Live NTFS volume: $MFT is read through raw access and needs Administrator.");

        for (int i = 0; i < systems.Count; i++)
        {
            var s = systems[i];
            // A live system drive: its SYSTEM hive is locked, so the running computer's name is the reliable host name.
            // (LiveVolume = the running OS volume only, see SourceLayoutDetector.IsLiveVolumeRoot.)
            if (s.AcquisitionKind == AcquisitionKind.LiveVolume && s.HostNameSource != HostDiscovery.FromSystemHive)
                systems[i] = s = s with { HostName = HostDiscovery.Sanitize(Environment.MachineName), HostNameSource = FromLiveSystem };
        }
        if (systems.Count > 1 || (systems.Count == 1 && systems[0].HostNameSource != HostDiscovery.FromDefault))
            foreach (var s in systems)
                notes.Add($"System '{s.HostName}' at {s.RootPath} ({Describe(s.AcquisitionKind)}; host name from {s.HostNameSource}).");
        return systems;
    }

    /// <summary>A system for a root found without host discovery (host from the SYSTEM hive or the folder name).</summary>
    public static DiscoveredSystem SystemFor(string root, IHiveOpener? opener = null, bool readHive = true, Func<string, bool>? isRunningOsRoot = null)
    {
        var (name, src) = readHive ? HostDiscovery.ResolveHostName(root, opener)
            : HostDiscovery.FolderHostName(root) is { } f ? (f, HostDiscovery.FromFolderName) : (HostDiscovery.DefaultHostName, HostDiscovery.FromDefault);
        bool windows;
        try { windows = Directory.Exists(Path.Combine(root, "Windows", "System32")); }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or ArgumentException) { windows = false; }
        return new DiscoveredSystem
        {
            RootPath = root,
            HostName = HostDiscovery.Sanitize(name),
            HostNameSource = src,
            OsFamily = windows ? OsFamily.Windows : OsFamily.Unknown,
            AcquisitionKind = HostDiscovery.Kind(root, isRunningOsRoot),
        };
    }

    public static string Describe(AcquisitionKind k) => k switch
    {
        AcquisitionKind.LiveVolume => "live volume",
        AcquisitionKind.MountedImageOrRawCopy => "mounted image or raw copy",
        AcquisitionKind.ExtractedArchive => "extracted archive",
        _ => "copied collection",
    };

    /// <summary>Scans each discovered system; every hit carries its system (host, root, acquisition).</summary>
    public static List<DetectedArtifact> Scan(IReadOnlyList<DiscoveredSystem> systems, IReadOnlyList<TriageRule> rules, CancellationToken ct = default)
    {
        var result = new List<DetectedArtifact>();
        foreach (var s in systems)
            result.AddRange(ScanRoot(s.RootPath, s.AcquisitionKind == AcquisitionKind.LiveVolume, rules, s, ct));
        return result;
    }

    private static readonly EnumerationOptions EnumOpts = new()
    {
        IgnoreInaccessible = true,
        AttributesToSkip = 0, // hives, $MFT etc. are Hidden|System
        MatchCasing = MatchCasing.CaseInsensitive,
        RecurseSubdirectories = false,
    };

    public static List<DetectedArtifact> Scan(SourceLayout layout, CancellationToken ct = default)
        => Scan(layout, TriageRules.LoadEmbedded(), ct);

    /// <summary>Scans the layout's roots without host discovery (hits carry no <see cref="DetectedArtifact.System"/>).</summary>
    public static List<DetectedArtifact> Scan(SourceLayout layout, IReadOnlyList<TriageRule> rules, CancellationToken ct = default)
    {
        var result = new List<DetectedArtifact>();
        foreach (var root in layout.SystemRoots)
            result.AddRange(ScanRoot(root, layout.IsLiveVolume, rules, null, ct));
        return result;
    }

    private static List<DetectedArtifact> ScanRoot(string root, bool live, IReadOnlyList<TriageRule> rules, DiscoveredSystem? system, CancellationToken ct)
    {
        var result = new List<DetectedArtifact>();
        foreach (var rule in rules)
        {
            ct.ThrowIfCancellationRequested();
            var matches = new List<string>();
            var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            foreach (var p in rule.Paths)
                foreach (var m in Resolve(root, p, rule, ct))
                    if (seen.Add(m)) matches.Add(m);

            var admin = false;
            if (matches.Count == 0 && rule.LiveVolumeMatch && live && rule.Paths.Count > 0)
            {
                matches.Add(Path.Combine(root, rule.Paths[0]));
                admin = rule.RequiresAdmin;
            }
            if (matches.Count == 0) continue;

            if (rule.PerMatch)
            {
                foreach (var m in matches)
                    result.Add(new DetectedArtifact(rule.Id, rule.Label, rule.ToolId, rule.Category, m, true, 1, root, admin, rule.DefaultSelected, system));
            }
            else
            {
                result.Add(new DetectedArtifact(rule.Id, rule.Label, rule.ToolId, rule.Category, matches[0], false, matches.Count, root, admin, rule.DefaultSelected, system));
            }
        }
        return result;
    }

    private static List<string> Resolve(string root, string relPath, TriageRule rule, CancellationToken ct)
    {
        var segs = relPath.Split(new[] { '\\', '/' }, StringSplitOptions.RemoveEmptyEntries);
        var results = new List<string>();
        if (segs.Length == 0) return results;
        try { Walk(root, segs, 0, rule, results, ct); }
        catch (Exception ex) when (ex is UnauthorizedAccessException or IOException) { }
        return results;
    }

    private static void Walk(string cur, string[] segs, int i, TriageRule rule, List<string> results, CancellationToken ct)
    {
        ct.ThrowIfCancellationRequested();
        bool last = i == segs.Length - 1;
        var seg = segs[i];

        IEnumerable<string> next;
        if (seg.IndexOfAny(new[] { '*', '?' }) < 0)
        {
            next = new[] { Path.Combine(cur, seg) };
        }
        else
        {
            var rx = GlobToRegex(seg);
            try
            {
                next = Directory.EnumerateFileSystemEntries(cur, "*", EnumOpts)
                    .Where(e => rx.IsMatch(Path.GetFileName(e))).ToList();
            }
            catch (Exception ex) when (ex is UnauthorizedAccessException or IOException) { return; }
        }

        foreach (var p in next)
        {
            if (!last)
            {
                if (Directory.Exists(p)) Walk(p, segs, i + 1, rule, results, ct);
                continue;
            }
            if (rule.Mode == RuleMode.File)
            {
                if (File.Exists(p)) results.Add(p);
            }
            else if (Directory.Exists(p) && DirMatches(p, rule))
            {
                results.Add(p);
            }
        }
    }

    private static bool DirMatches(string dir, TriageRule rule)
    {
        if (string.IsNullOrEmpty(rule.Mask)) return true;
        try
        {
            var opts = new EnumerationOptions
            {
                IgnoreInaccessible = true,
                AttributesToSkip = 0,
                MatchCasing = MatchCasing.CaseInsensitive,
                MatchType = MatchType.Win32,
                RecurseSubdirectories = rule.Recurse,
            };
            return Directory.EnumerateFiles(dir, rule.Mask, opts).Any();
        }
        catch (Exception ex) when (ex is UnauthorizedAccessException or IOException) { return false; }
    }

    private static Regex GlobToRegex(string glob)
        => new("^" + Regex.Escape(glob).Replace(@"\*", ".*").Replace(@"\?", ".") + "$",
               RegexOptions.IgnoreCase | RegexOptions.CultureInvariant);
}
