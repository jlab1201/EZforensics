namespace EzForensics.Core.Triage;

public sealed record SourceLayout(IReadOnlyList<string> SystemRoots, bool IsLiveVolume, IReadOnlyList<string> Notes);

public static class SourceLayoutDetector
{
    public static SourceLayout Detect(string source, Func<string, bool>? isRunningOsRoot = null)
    {
        var notes = new List<string>();
        var src = source.Length > 3 ? source.TrimEnd('\\', '/') : source;
        var live = IsLiveVolumeRoot(source, isRunningOsRoot);
        if (live) notes.Add("Live NTFS volume: $MFT is read through raw access and needs Administrator.");

        var candidates = new List<string> { src };
        try
        {
            candidates.Add(Path.Combine(src, "C"));
            candidates.Add(Path.Combine(src, "[root]"));
            if (Directory.Exists(src))
            {
                foreach (var d in Directory.EnumerateDirectories(src))
                {
                    var n = Path.GetFileName(d);
                    if (n.Length == 1 && char.IsLetter(n[0])) candidates.Add(d);
                }
            }
        }
        catch (Exception ex) when (ex is UnauthorizedAccessException or IOException) { }

        var roots = new List<string>();
        foreach (var c in candidates.Distinct(StringComparer.OrdinalIgnoreCase))
        {
            if (!Directory.Exists(c)) continue;
            var score = Score(c);
            if (score > 0)
            {
                roots.Add(c);
                if (!string.Equals(c, src, StringComparison.OrdinalIgnoreCase))
                    notes.Add($"Found system root '{c}' (score {score}).");
            }
        }

        if (roots.Count == 0)
        {
            roots.Add(src);
            notes.Add("No Windows artifacts recognised at the source; scanning it as-is.");
        }
        return new SourceLayout(roots, live, notes);
    }

    public static int Score(string dir)
    {
        int s = 0;
        try
        {
            if (Directory.Exists(Path.Combine(dir, "Windows", "System32", "config"))) s++;
            if (Directory.Exists(Path.Combine(dir, "Users"))) s++;
            if (Directory.Exists(Path.Combine(dir, "$Recycle.Bin"))) s++;
            if (File.Exists(Path.Combine(dir, "$MFT"))) s++;
        }
        catch (Exception ex) when (ex is UnauthorizedAccessException or IOException) { }
        return s;
    }

    /// <summary>
    /// True when <paramref name="source"/> is the root of the volume the running Windows boots from (<see cref="IsRunningOsRoot"/>; raw $MFT
    /// access needs Administrator). Any other drive root (a mounted E01, a USB disk reporting "Fixed", a KAPE drive) is evidence, not the
    /// live system: it is walked and classified like a folder. <paramref name="isRunningOsRoot"/> replaces the test (unit tests).
    /// </summary>
    public static bool IsLiveVolumeRoot(string source, Func<string, bool>? isRunningOsRoot = null)
    {
        if (string.IsNullOrWhiteSpace(source)) return false;
        try { return (isRunningOsRoot ?? IsRunningOsRoot)(source); }
        catch (Exception) { return false; }
    }

    /// <summary>
    /// True when <paramref name="path"/> is the root of the running OS volume (<c>Path.GetPathRoot(Environment.SystemDirectory)</c>, else
    /// <c>%SystemDrive%</c>) and that volume is a ready local NTFS drive.
    /// </summary>
    public static bool IsRunningOsRoot(string path)
    {
        try
        {
            if (!OperatingSystem.IsWindows()) return false;
            var full = Path.GetFullPath(path).TrimEnd('\\', '/');
            string? sys = null;
            try { sys = Path.GetPathRoot(Environment.SystemDirectory); } catch (Exception) { }
            if (string.IsNullOrEmpty(sys)) sys = Environment.GetEnvironmentVariable("SystemDrive");
            if (string.IsNullOrEmpty(sys) || !string.Equals(full, sys.TrimEnd('\\', '/'), StringComparison.OrdinalIgnoreCase)) return false;
            return IsLiveNtfsRoot(full + "\\");
        }
        catch (Exception) { return false; }
    }

    /// <summary>True when <paramref name="path"/> is a volume / share root (<c>E:\</c>, <c>\\server\share</c>).</summary>
    public static bool IsVolumeRoot(string path)
    {
        try
        {
            var full = Path.GetFullPath(path);
            var root = Path.GetPathRoot(full);
            return root != null && string.Equals(full.TrimEnd('\\', '/'), root.TrimEnd('\\', '/'), StringComparison.OrdinalIgnoreCase);
        }
        catch (Exception) { return false; }
    }

    /// <summary>
    /// Stricter test than <see cref="Score"/> > 0 used by host discovery: the folder holds <c>Windows\System32\config</c> or <c>$MFT</c>,
    /// or both <c>Users</c> and <c>$Recycle.Bin</c>; a lone <c>Users</c> folder counts only when the folder is named like a volume
    /// (one letter or <c>[root]</c>), so an arbitrary folder that happens to contain "Users" is not taken for a system root.
    /// </summary>
    /// <param name="ignoreMft">Do not count <c>$MFT</c> (an NTFS volume root always "has" one, so it says nothing about a drive root).</param>
    public static bool LooksLikeSystemRoot(string dir, bool ignoreMft = false)
    {
        try
        {
            if (Directory.Exists(Path.Combine(dir, "Windows", "System32", "config"))) return true;
            if (!ignoreMft && File.Exists(Path.Combine(dir, "$MFT"))) return true;
            bool users = Directory.Exists(Path.Combine(dir, "Users"));
            if (users && Directory.Exists(Path.Combine(dir, "$Recycle.Bin"))) return true;
            if (users)
            {
                var n = Path.GetFileName(dir.TrimEnd('\\', '/'));
                if ((n.Length == 1 && char.IsLetter(n[0])) || string.Equals(n, "[root]", StringComparison.OrdinalIgnoreCase)) return true;
            }
        }
        catch (Exception ex) when (ex is UnauthorizedAccessException or IOException or ArgumentException) { }
        return false;
    }

    private static bool IsLiveNtfsRoot(string source)
    {
        try
        {
            if (!OperatingSystem.IsWindows()) return false;
            var full = Path.GetFullPath(source);
            var root = Path.GetPathRoot(full);
            if (root == null || !string.Equals(full.TrimEnd('\\', '/'), root.TrimEnd('\\', '/'), StringComparison.OrdinalIgnoreCase)) return false;
            if (root.StartsWith(@"\\")) return false;
            var di = new DriveInfo(root);
            return di.IsReady && di.DriveType == DriveType.Fixed && string.Equals(di.DriveFormat, "NTFS", StringComparison.OrdinalIgnoreCase);
        }
        catch { return false; }
    }
}
