using System.IO.Enumeration;

namespace EzForensics.Core.Platforms;

/// <summary>
/// A file found inside an evidence system root. <see cref="RelativePath"/> is the logical path below the root ('/' separated, on-disk
/// spelling), <see cref="FullPath"/> the file actually read (null when it is a link that dangles or points at something not in the
/// evidence, e.g. <c>.bash_history -&gt; /dev/null</c>), <see cref="LinkTarget"/> the raw link text when the entry is a symbolic link.
/// </summary>
public sealed record EvidenceFile(string RelativePath, string? FullPath, string? LinkTarget)
{
    public bool IsLink => LinkTarget != null;
}

/// <summary>
/// Path lookups inside an evidence root that work the same on Windows (case-insensitive NTFS) and Linux (case-sensitive ext4 / mounted
/// images / extracted tarballs):
/// <list type="bullet">
/// <item>every segment is matched exactly first, then case-insensitively (so <c>etc/os-release</c> finds <c>ETC/OS-RELEASE</c> on a
///   case-sensitive copy, and an exact <c>etc</c> wins over a sibling <c>Etc</c>);</item>
/// <item>symbolic links are resolved <b>inside the root</b> (chroot-style): an absolute target such as <c>/usr/lib/os-release</c> is
///   re-rooted to <c>&lt;root&gt;/usr/lib/os-release</c> and <c>..</c> never climbs above the root, so a mounted image can never make
///   EZ Hub read the analyst's own <c>/etc</c>;</item>
/// <item>hidden and system entries are not skipped.</item>
/// </list>
/// </summary>
public static class EvidencePath
{
    private const int MaxLinkHops = 16;
    private const int MaxDepth = 32;

    private static readonly EnumerationOptions ListOptions = new()
    {
        RecurseSubdirectories = false,
        IgnoreInaccessible = true,
        AttributesToSkip = 0,
        ReturnSpecialDirectories = false,
    };

    /// <summary>Splits a relative or absolute path on '/' and '\'; drops empty segments, "\\?\" style prefixes and drive letters.</summary>
    public static IReadOnlyList<string> Split(string path)
    {
        var parts = new List<string>();
        foreach (var raw in path.Split('/', '\\'))
        {
            if (raw.Length == 0 || raw == "?" || raw == "??") continue;
            if (parts.Count == 0 && raw.Length == 2 && raw[1] == ':' && char.IsLetter(raw[0])) continue;   // C:
            parts.Add(raw);
        }
        return parts;
    }

    public static bool IsRooted(string linkTarget) =>
        linkTarget.StartsWith('/') || linkTarget.StartsWith('\\') ||
        (linkTarget.Length >= 2 && linkTarget[1] == ':' && char.IsLetter(linkTarget[0]));

    /// <summary>Resolves <paramref name="relative"/> below <paramref name="root"/>; null when any segment is missing.</summary>
    public static string? Resolve(string root, string relative) => ResolveCore(root, Split(relative));

    /// <summary>The resolved path when it is an existing file, else null.</summary>
    public static string? ResolveFile(string root, string relative)
    {
        var p = Resolve(root, relative);
        return p != null && File.Exists(p) ? p : null;
    }

    /// <summary>The resolved path when it is an existing directory, else null.</summary>
    public static string? ResolveDirectory(string root, string relative)
    {
        var p = Resolve(root, relative);
        return p != null && Directory.Exists(p) ? p : null;
    }

    /// <summary>Reads at most <paramref name="maxBytes"/> of a file as UTF-8 (invalid bytes replaced); null when unreadable.</summary>
    public static string? ReadText(string path, int maxBytes = 1 << 20)
    {
        try
        {
            using var fs = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete);
            var len = (int)Math.Min(fs.Length, maxBytes);
            var buf = new byte[len];
            int read = 0;
            while (read < len)
            {
                int n = fs.Read(buf, read, len - read);
                if (n <= 0) break;
                read += n;
            }
            return System.Text.Encoding.UTF8.GetString(buf, 0, read);
        }
        catch (IOException) { return null; }
        catch (UnauthorizedAccessException) { return null; }
    }

    private static string? ResolveCore(string root, IReadOnlyList<string> segments)
    {
        var resolved = new List<string>();
        var pending = new Stack<string>();
        for (int i = segments.Count - 1; i >= 0; i--) pending.Push(segments[i]);
        int hops = 0;

        while (pending.Count > 0)
        {
            var seg = pending.Pop();
            if (seg.Length == 0 || seg == ".") continue;
            if (seg == "..")
            {
                if (resolved.Count > 0) resolved.RemoveAt(resolved.Count - 1);
                continue;
            }

            var dir = Combine(root, resolved);
            var name = MatchEntry(dir, seg);
            if (name == null) return null;
            var full = Path.Combine(dir, name);

            var target = LinkTargetOf(full);
            if (target != null)
            {
                if (++hops > MaxLinkHops) return null;
                if (IsRooted(target)) resolved.Clear();
                var t = Split(target);
                for (int i = t.Count - 1; i >= 0; i--) pending.Push(t[i]);
                continue;
            }
            resolved.Add(name);
        }
        return Combine(root, resolved);
    }

    private static string Combine(string root, List<string> segments)
    {
        var p = root;
        foreach (var s in segments) p = Path.Combine(p, s);
        return p;
    }

    /// <summary>The on-disk name in <paramref name="dir"/> for <paramref name="segment"/>: exact match first, then case-insensitive.</summary>
    public static string? MatchEntry(string dir, string segment)
    {
        if (!Directory.Exists(dir)) return null;
        string? insensitive = null;
        try
        {
            foreach (var full in Directory.EnumerateFileSystemEntries(dir, "*", ListOptions))
            {
                var name = Path.GetFileName(full);
                if (string.Equals(name, segment, StringComparison.Ordinal)) return name;
                if (insensitive == null && string.Equals(name, segment, StringComparison.OrdinalIgnoreCase)) insensitive = name;
            }
        }
        catch (IOException) { return null; }
        catch (UnauthorizedAccessException) { return null; }
        return insensitive;
    }

    private static string? LinkTargetOf(string full)
    {
        try
        {
            FileSystemInfo info = Directory.Exists(full) ? new DirectoryInfo(full) : new FileInfo(full);
            return info.LinkTarget;
        }
        catch (IOException) { return null; }
        catch (UnauthorizedAccessException) { return null; }
    }

    private static bool HasWildcard(string segment) => segment == "**" || segment.IndexOfAny(new[] { '*', '?' }) >= 0;

    /// <summary>
    /// Files matching a glob relative to the root. Segments may use <c>*</c> and <c>?</c> (case-insensitive) and <c>**</c> (zero or more
    /// folders; links are not followed by <c>**</c>). The literal prefix is resolved with <see cref="Resolve"/>. Results are ordered by
    /// relative path and capped at <paramref name="maxResults"/>.
    /// </summary>
    public static IReadOnlyList<EvidenceFile> Glob(string root, string pattern, int maxResults = 10_000)
    {
        var segs = Split(pattern);
        if (segs.Count == 0) return Array.Empty<EvidenceFile>();
        var results = new SortedDictionary<string, EvidenceFile>(StringComparer.Ordinal);

        int firstWild = -1;
        for (int i = 0; i < segs.Count; i++) if (HasWildcard(segs[i])) { firstWild = i; break; }

        if (firstWild < 0)
        {
            AddFile(root, string.Join('/', segs), results);
            return results.Values.ToList();
        }

        var prefix = segs.Take(firstWild).ToList();
        var baseDir = prefix.Count == 0 ? root : ResolveDirectory(root, string.Join('/', prefix));
        if (baseDir == null) return Array.Empty<EvidenceFile>();
        var baseRel = string.Join('/', prefix);
        Walk(root, baseDir, baseRel, segs.Skip(firstWild).ToArray(), 0, results, maxResults);
        return results.Values.Take(maxResults).ToList();
    }

    private static void AddFile(string root, string rel, SortedDictionary<string, EvidenceFile> results)
    {
        var segs = Split(rel);
        if (segs.Count == 0) return;
        // Link text of the entry itself (not of a linked parent folder such as macOS etc -> private/etc).
        string? linkTarget = null;
        var parent = ResolveCore(root, segs.Take(segs.Count - 1).ToList());
        if (parent != null)
        {
            var name = MatchEntry(parent, segs[^1]);
            if (name != null) linkTarget = LinkTargetOf(Path.Combine(parent, name));
        }
        var resolved = ResolveCore(root, segs);
        if (resolved != null && File.Exists(resolved))
            results[rel] = new EvidenceFile(rel, resolved, linkTarget);
        else if (linkTarget != null)
            results[rel] = new EvidenceFile(rel, null, linkTarget);
    }

    private static void Walk(string root, string dirFull, string dirRel, string[] rest, int depth,
        SortedDictionary<string, EvidenceFile> results, int maxResults)
    {
        if (rest.Length == 0 || depth > MaxDepth || results.Count >= maxResults) return;
        var seg = rest[0];

        if (seg == "**")
        {
            var next = rest.Length == 1 ? new[] { "*" } : rest[1..];
            Walk(root, dirFull, dirRel, next, depth + 1, results, maxResults);
            foreach (var sub in Entries(dirFull))
            {
                if (!Directory.Exists(sub) || LinkTargetOf(sub) != null) continue;
                Walk(root, sub, Join(dirRel, Path.GetFileName(sub)), rest, depth + 1, results, maxResults);
            }
            return;
        }

        foreach (var entry in Entries(dirFull))
        {
            if (results.Count >= maxResults) return;
            var name = Path.GetFileName(entry);
            if (!FileSystemName.MatchesSimpleExpression(seg, name, ignoreCase: true)) continue;
            var rel = Join(dirRel, name);
            var link = LinkTargetOf(entry);

            if (rest.Length == 1)
            {
                if (link != null) { AddFile(root, rel, results); continue; }
                if (File.Exists(entry)) results[rel] = new EvidenceFile(rel, entry, null);
                continue;
            }

            if (link != null)
            {
                var target = ResolveDirectory(root, rel);
                if (target != null) Walk(root, target, rel, rest[1..], depth + 1, results, maxResults);
            }
            else if (Directory.Exists(entry))
            {
                Walk(root, entry, rel, rest[1..], depth + 1, results, maxResults);
            }
        }
    }

    private static string Join(string a, string b) => a.Length == 0 ? b : a + "/" + b;

    private static IEnumerable<string> Entries(string dir)
    {
        try { return Directory.EnumerateFileSystemEntries(dir, "*", ListOptions).OrderBy(e => e, StringComparer.Ordinal).ToList(); }
        catch (IOException) { return Array.Empty<string>(); }
        catch (UnauthorizedAccessException) { return Array.Empty<string>(); }
    }
}
