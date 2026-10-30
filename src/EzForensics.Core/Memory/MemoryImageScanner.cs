using EzForensics.Core.Network;

namespace EzForensics.Core.Memory;

/// <summary>Finds memory images under a folder (or probes a single file). Uses the PcapScanOptions limits and skip dirs.</summary>
public static class MemoryImageScanner
{
    public static IReadOnlyList<MemoryImage> Scan(string root, PcapScanOptions? options = null, CancellationToken ct = default)
    {
        var o = options ?? new PcapScanOptions();
        var found = new List<MemoryImage>();
        if (string.IsNullOrWhiteSpace(root)) return found;
        if (File.Exists(root))
        {
            var one = MemoryImageDetector.Detect(root);
            if (one != null) found.Add(one);
            return found;
        }
        if (!Directory.Exists(root)) return found;

        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        void Add(MemoryImage? m)
        {
            if (m != null && seen.Add(m.Path)) found.Add(m);
        }

        // Well-known locations, including Windows\ which is normally skipped.
        foreach (var rel in new[] { "hiberfil.sys", "MEMORY.DMP", Path.Combine("Windows", "MEMORY.DMP") })
        {
            ct.ThrowIfCancellationRequested();
            var p = Path.Combine(root, rel);
            if (File.Exists(p)) Add(MemoryImageDetector.Detect(p));
        }

        var enumOpts = new EnumerationOptions
        {
            IgnoreInaccessible = true,
            RecurseSubdirectories = false,
            AttributesToSkip = FileAttributes.ReparsePoint,
            ReturnSpecialDirectories = false,
        };
        int entries = 0, probes = 0;
        bool stop = false;
        var stack = new Stack<(string Dir, int Depth)>();
        stack.Push((root, 0));
        while (stack.Count > 0 && !stop)
        {
            ct.ThrowIfCancellationRequested();
            var (dir, depth) = stack.Pop();
            var subdirs = new List<string>();
            try
            {
                foreach (var entry in new DirectoryInfo(dir).EnumerateFileSystemInfos("*", enumOpts))
                {
                    if (++entries > o.MaxEntries) { stop = true; break; }
                    if (entry is DirectoryInfo di)
                    {
                        if (depth < o.MaxDepth && !o.SkipDirectories.Contains(di.Name)) subdirs.Add(di.FullName);
                        continue;
                    }
                    if (!MemoryImageDetector.IsCandidateName(entry.Name)) continue;
                    if (++probes > o.MaxProbes) { stop = true; break; }
                    ct.ThrowIfCancellationRequested();
                    Add(MemoryImageDetector.Detect(entry.FullName));
                    if (found.Count >= o.MaxResults) { stop = true; break; }
                }
            }
            catch (Exception ex) when (ex is UnauthorizedAccessException or IOException or System.Security.SecurityException) { }
            subdirs.Sort(StringComparer.OrdinalIgnoreCase);
            for (int i = subdirs.Count - 1; i >= 0; i--) stack.Push((subdirs[i], depth + 1));
        }
        found.Sort((a, b) => StringComparer.OrdinalIgnoreCase.Compare(a.Path, b.Path));
        return found;
    }
}
