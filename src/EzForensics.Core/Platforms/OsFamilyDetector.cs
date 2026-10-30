using System.Text.RegularExpressions;

namespace EzForensics.Core.Platforms;

/// <summary>Result of <see cref="OsFamilyDetector.Detect"/>.</summary>
public sealed record OsDetection
{
    public OsFamily Family { get; init; } = OsFamily.Unknown;
    /// <summary>Lower-case distribution id: os-release <c>ID</c> (<c>ubuntu</c>, <c>rhel</c>, <c>debian</c>...), <c>windows</c>, <c>macos</c>.</summary>
    public string? Distribution { get; init; }
    /// <summary>os-release <c>VERSION_ID</c>, lsb <c>DISTRIB_RELEASE</c>, the redhat-release number, debian_version, or macOS ProductVersion.</summary>
    public string? Version { get; init; }
    /// <summary>Human-readable name (os-release <c>PRETTY_NAME</c> / <c>NAME</c>, lsb description, macOS ProductName).</summary>
    public string? DisplayName { get; init; }
    /// <summary>Host name recorded in the evidence (<c>etc/hostname</c>), when present.</summary>
    public string? HostName { get; init; }
    /// <summary>What the decision was based on, e.g. <c>"etc/os-release: ID=ubuntu"</c>.</summary>
    public IReadOnlyList<string> Evidence { get; init; } = Array.Empty<string>();

    public static OsDetection Unknown(IReadOnlyList<string>? evidence = null) => new() { Evidence = evidence ?? Array.Empty<string>() };
}

/// <summary>
/// Decides which operating system a system root (the folder that plays the part of <c>C:\</c> or <c>/</c>) belongs to.
/// Pure file checks through <see cref="EvidencePath"/> (case-insensitive, links resolved inside the root); nothing is executed.
/// Order: Windows, macOS, Linux (macOS also has <c>etc/passwd</c>, so it must win over the Linux markers).
/// </summary>
public static class OsFamilyDetector
{
    private const int MaxReleaseFileBytes = 64 * 1024;

    public static OsDetection Detect(string root)
    {
        if (string.IsNullOrWhiteSpace(root) || !Directory.Exists(root)) return OsDetection.Unknown(new[] { "root folder not found" });

        var windows = DetectWindows(root);
        if (windows != null) return windows;
        var mac = DetectMac(root);
        if (mac != null) return mac;
        var linux = DetectLinux(root);
        if (linux != null) return linux;
        return OsDetection.Unknown();
    }

    // ------------------------------------------------------------------ Windows
    private static OsDetection? DetectWindows(string root)
    {
        if (EvidencePath.ResolveDirectory(root, "Windows/System32/config") == null) return null;
        var ev = new List<string> { "Windows/System32/config" };
        foreach (var hive in new[] { "SYSTEM", "SOFTWARE" })
            if (EvidencePath.ResolveFile(root, "Windows/System32/config/" + hive) != null) ev.Add("Windows/System32/config/" + hive);
        if (EvidencePath.ResolveFile(root, "etc/os-release") != null) ev.Add("note: etc/os-release also present (mixed root?)");
        return new OsDetection { Family = OsFamily.Windows, Distribution = "windows", Evidence = ev };
    }

    // ------------------------------------------------------------------ macOS
    private static OsDetection? DetectMac(string root)
    {
        const string plist = "System/Library/CoreServices/SystemVersion.plist";
        var ev = new List<string>();
        string? version = null, name = null;
        var file = EvidencePath.ResolveFile(root, plist);
        if (file != null)
        {
            ev.Add(plist);
            var text = EvidencePath.ReadText(file, MaxReleaseFileBytes);
            if (text != null && text.StartsWith("bplist", StringComparison.Ordinal))
                ev.Add("SystemVersion.plist is a binary plist (version not read)");
            else if (text != null)
            {
                version = PlistString(text, "ProductVersion");
                name = PlistString(text, "ProductName");
                if (version != null) ev.Add("ProductVersion=" + version);
            }
        }
        else if (EvidencePath.ResolveDirectory(root, "System/Library/CoreServices") != null &&
                 EvidencePath.ResolveDirectory(root, "private/var") != null)
        {
            ev.Add("System/Library/CoreServices");
            ev.Add("private/var");
        }
        else return null;

        return new OsDetection
        {
            Family = OsFamily.MacOS, Distribution = "macos", Version = version, DisplayName = name, Evidence = ev,
            HostName = FirstLine(root, "private/etc/hostname") ?? FirstLine(root, "etc/hostname"),
        };
    }

    public static string? PlistString(string xml, string key)
    {
        var m = Regex.Match(xml, @"<key>\s*" + Regex.Escape(key) + @"\s*</key>\s*<string>([^<]*)</string>", RegexOptions.CultureInvariant);
        if (!m.Success) return null;
        var v = System.Net.WebUtility.HtmlDecode(m.Groups[1].Value).Trim();
        return v.Length == 0 ? null : v;
    }

    // ------------------------------------------------------------------ Linux
    private static OsDetection? DetectLinux(string root)
    {
        var ev = new List<string>();
        string? distro = null, version = null, display = null;

        foreach (var rel in new[] { "etc/os-release", "usr/lib/os-release" })
        {
            var f = EvidencePath.ResolveFile(root, rel);
            if (f == null) continue;
            var kv = ParseOsRelease(EvidencePath.ReadText(f, MaxReleaseFileBytes) ?? "");
            if (kv.Count == 0) continue;
            distro = Lower(Get(kv, "ID"));
            version = Get(kv, "VERSION_ID");
            display = Get(kv, "PRETTY_NAME") ?? Get(kv, "NAME");
            ev.Add($"{rel}: ID={distro ?? "?"} NAME={Get(kv, "NAME") ?? "?"} VERSION_ID={version ?? "?"}");
            break;
        }

        if (distro == null)
        {
            var lsb = EvidencePath.ResolveFile(root, "etc/lsb-release");
            if (lsb != null)
            {
                var kv = ParseOsRelease(EvidencePath.ReadText(lsb, MaxReleaseFileBytes) ?? "");
                if (Get(kv, "DISTRIB_ID") is { } id)
                {
                    distro = Lower(id);
                    version = Get(kv, "DISTRIB_RELEASE");
                    display = Get(kv, "DISTRIB_DESCRIPTION") ?? id;
                    ev.Add($"etc/lsb-release: DISTRIB_ID={id} DISTRIB_RELEASE={version ?? "?"}");
                }
            }
        }

        if (distro == null)
        {
            foreach (var rel in new[] { "etc/redhat-release", "etc/centos-release", "etc/fedora-release", "etc/system-release" })
            {
                var f = EvidencePath.ResolveFile(root, rel);
                var line = f == null ? null : FirstLine(EvidencePath.ReadText(f, MaxReleaseFileBytes));
                if (line == null) continue;
                (distro, version) = ParseRedHatRelease(line);
                display = line;
                ev.Add($"{rel}: {line}");
                break;
            }
        }

        if (distro == null)
        {
            var f = EvidencePath.ResolveFile(root, "etc/debian_version");
            var line = f == null ? null : FirstLine(EvidencePath.ReadText(f, MaxReleaseFileBytes));
            if (line != null)
            {
                distro = "debian";
                version = line;
                display = "Debian " + line;
                ev.Add("etc/debian_version: " + line);
            }
        }

        bool passwd = EvidencePath.ResolveFile(root, "etc/passwd") != null;
        bool varLog = EvidencePath.ResolveDirectory(root, "var/log") != null;
        if (passwd) ev.Add("etc/passwd");
        if (varLog) ev.Add("var/log");

        // A release file is decisive. Without one (stripped collection) both etc/passwd and var/log are required:
        // either alone is too weak (a random folder can hold a "var/log", a Cygwin/MSYS tree an "etc/passwd").
        if (distro == null && !(passwd && varLog)) return null;

        return new OsDetection
        {
            Family = OsFamily.Linux, Distribution = distro, Version = version, DisplayName = display, Evidence = ev,
            HostName = FirstLine(root, "etc/hostname"),
        };
    }

    /// <summary>
    /// Parses os-release / lsb-release style <c>KEY=value</c> lines: comments and blank lines skipped, values may be double- or
    /// single-quoted, backslash escapes inside double quotes (<c>\" \\ \$ \`</c>) are honoured.
    /// </summary>
    public static IReadOnlyDictionary<string, string> ParseOsRelease(string text)
    {
        var d = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        foreach (var raw in text.Split('\n'))
        {
            var line = raw.Trim().TrimEnd('\r');
            if (line.Length == 0 || line[0] == '#') continue;
            int eq = line.IndexOf('=');
            if (eq <= 0) continue;
            var key = line[..eq].Trim();
            if (key.StartsWith("export ", StringComparison.Ordinal)) key = key[7..].Trim();
            d[key] = Unquote(line[(eq + 1)..].Trim());
        }
        return d;
    }

    private static string Unquote(string v)
    {
        if (v.Length >= 2 && v[0] == '\'' && v[^1] == '\'') return v[1..^1];
        if (v.Length >= 2 && v[0] == '"' && v[^1] == '"')
        {
            var inner = v[1..^1];
            var sb = new System.Text.StringBuilder(inner.Length);
            for (int i = 0; i < inner.Length; i++)
            {
                if (inner[i] == '\\' && i + 1 < inner.Length && "\"\\$`".IndexOf(inner[i + 1]) >= 0) { sb.Append(inner[i + 1]); i++; }
                else sb.Append(inner[i]);
            }
            return sb.ToString();
        }
        return v;
    }

    /// <summary>"CentOS Linux release 7.9.2009 (Core)" -> ("centos", "7.9.2009"); "Red Hat Enterprise Linux release 8.6 (Ootpa)" -> ("rhel", "8.6").</summary>
    public static (string Distribution, string? Version) ParseRedHatRelease(string line)
    {
        var m = Regex.Match(line, @"release\s+([0-9][0-9A-Za-z.\-]*)", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant);
        var version = m.Success ? m.Groups[1].Value : null;
        var l = line.ToLowerInvariant();
        string distro =
            l.StartsWith("red hat") ? "rhel" :
            l.StartsWith("centos stream") ? "centos" :
            l.StartsWith("centos") ? "centos" :
            l.StartsWith("fedora") ? "fedora" :
            l.StartsWith("rocky") ? "rocky" :
            l.StartsWith("almalinux") ? "almalinux" :
            l.StartsWith("oracle") ? "ol" :
            l.StartsWith("amazon") ? "amzn" :
            Regex.Match(l, @"^[a-z0-9]+").Value is { Length: > 0 } w ? w : "linux";
        return (distro, version);
    }

    private static string? Get(IReadOnlyDictionary<string, string> kv, string key) =>
        kv.TryGetValue(key, out var v) && !string.IsNullOrWhiteSpace(v) ? v.Trim() : null;

    private static string? Lower(string? s) => s?.ToLowerInvariant();

    private static string? FirstLine(string root, string rel)
    {
        var f = EvidencePath.ResolveFile(root, rel);
        return f == null ? null : FirstLine(EvidencePath.ReadText(f, 4096));
    }

    private static string? FirstLine(string? text)
    {
        if (text == null) return null;
        foreach (var raw in text.Split('\n'))
        {
            var line = raw.Trim();
            if (line.Length > 0 && line[0] != '#') return line;
        }
        return null;
    }

    // ------------------------------------------------------------------ roots
    /// <summary>
    /// Finds system roots of any family below <paramref name="source"/> (the source itself, then sub-folders up to
    /// <paramref name="maxDepth"/> levels: UAC's <c>[root]</c>, CyLR / KAPE style <c>&lt;host&gt;/...</c>, extracted tarballs). A folder
    /// that is a root is not searched further. Junctions / links are not followed.
    /// </summary>
    public static IReadOnlyList<(string Root, OsDetection Os)> FindSystemRoots(string source, int maxDepth = 3)
    {
        var found = new List<(string, OsDetection)>();
        if (!Directory.Exists(source)) return found;
        var queue = new Queue<(string Dir, int Depth)>();
        queue.Enqueue((source.TrimEnd('\\', '/') is { Length: > 0 } s ? s : source, 0));
        int visited = 0;
        while (queue.Count > 0 && visited++ < 5_000)
        {
            var (dir, depth) = queue.Dequeue();
            var os = Detect(dir);
            if (os.Family != OsFamily.Unknown) { found.Add((dir, os)); continue; }
            if (depth >= maxDepth) continue;
            IEnumerable<string> subs;
            try
            {
                subs = Directory.EnumerateDirectories(dir, "*", new EnumerationOptions { IgnoreInaccessible = true, AttributesToSkip = 0 })
                    .Where(d => new DirectoryInfo(d).LinkTarget == null).OrderBy(d => d, StringComparer.Ordinal).ToList();
            }
            catch (IOException) { continue; }
            catch (UnauthorizedAccessException) { continue; }
            foreach (var sub in subs) queue.Enqueue((sub, depth + 1));
        }
        return found;
    }
}
