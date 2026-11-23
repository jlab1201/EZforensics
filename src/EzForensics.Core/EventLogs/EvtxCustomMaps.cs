using EzForensics.Core.Settings;

namespace EzForensics.Core.EventLogs;

/// <summary>Result of copying the custom maps into a tool's Maps folder.</summary>
public sealed record EvtxMapSyncResult(int Copied, int Unchanged, int Removed, IReadOnlyList<string> Errors);

/// <summary>
/// The analyst's own EvtxECmd maps, kept outside the tool (<c>%LocalAppData%\EzForensicsData\evtx-maps</c>, named by the EvtxECmd convention).
/// <para>
/// Precedence: EvtxECmd loads the maps of its Maps folder in file name order and keeps the first map for each EventId + Channel + Provider;
/// a later map with the same key is skipped ("An item with the same key has already been added"). The Maps README says to prefix a
/// custom copy with <c>1_</c> for this reason. Before each EvtxECmd run (and after <c>--sync</c>) the custom maps are copied into the Maps
/// folder as <see cref="Prefix"/> + name, which sorts before every stock map and before user <c>1_</c> copies. Stock maps are never edited.
/// </para>
/// </summary>
public sealed class EvtxCustomMaps
{
    /// <summary>Prefix of our copies in the tool's Maps folder ('0' sorts before letters and before the README's <c>1_</c> convention).</summary>
    public const string Prefix = "0_EzForensics_";
    private static readonly object CopyGate = new();

    public EvtxCustomMaps(string? folder = null) => Folder = folder ?? DefaultFolder;

    public static string DefaultFolder => Path.Combine(AppPaths.DataRoot, "evtx-maps");

    public string Folder { get; }

    public string PathFor(EvtxEventKey key) => Path.Combine(Folder, EvtxMapFiles.FileName(key));

    /// <summary>Keys of all custom maps (from the file headers).</summary>
    public IReadOnlyList<EvtxEventKey> Keys()
    {
        if (!Directory.Exists(Folder)) return Array.Empty<EvtxEventKey>();
        try
        {
            return Directory.EnumerateFiles(Folder, "*.map").Select(EvtxMapFiles.ReadKey).Where(k => k != null).Select(k => k!.Value).ToList();
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { return Array.Empty<EvtxEventKey>(); }
    }

    /// <summary>The custom map file for an event (an empty provider matches any), or null.</summary>
    public string? Find(EvtxEventKey key) => EvtxMapFiles.Find(Folder, key);

    public bool Has(EvtxEventKey key) => Find(key) != null;

    /// <summary>Writes the map (replacing any custom map with the same key) and returns its path.</summary>
    public string Save(EvtxMap map, string? comment = null)
    {
        Directory.CreateDirectory(Folder);
        var path = PathFor(map.Key);
        if (Find(map.Key) is { } old && !string.Equals(old, path, StringComparison.OrdinalIgnoreCase)) File.Delete(old);
        var tmp = path + ".tmp";
        File.WriteAllText(tmp, EvtxMapYaml.Write(map, comment));
        File.Move(tmp, path, overwrite: true);
        return path;
    }

    /// <summary>Deletes the custom map of an event; <paramref name="toolMapsDir"/> (when given) loses its copy too. True when a map was removed.</summary>
    public bool Delete(EvtxEventKey key, string? toolMapsDir = null)
    {
        var path = Find(key);
        if (path == null) return false;
        File.Delete(path);
        if (toolMapsDir != null)
        {
            var copy = Path.Combine(toolMapsDir, Prefix + Path.GetFileName(path));
            try { if (File.Exists(copy)) File.Delete(copy); } catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { }
        }
        return true;
    }

    /// <summary>
    /// Makes <paramref name="mapsDir"/> hold exactly the custom maps of <paramref name="customFolder"/> as <see cref="Prefix"/> copies:
    /// new / changed ones are copied, copies whose source is gone are removed. Other files are never touched. Serialized across threads.
    /// </summary>
    public static EvtxMapSyncResult SyncInto(string customFolder, string mapsDir)
    {
        int copied = 0, same = 0, removed = 0;
        var errors = new List<string>();
        lock (CopyGate)
        {
            var sources = Directory.Exists(customFolder) ? Directory.EnumerateFiles(customFolder, "*.map").ToList() : new List<string>();
            var wanted = new HashSet<string>(sources.Select(s => Prefix + Path.GetFileName(s)), StringComparer.OrdinalIgnoreCase);
            if (sources.Count == 0 && !Directory.Exists(mapsDir)) return new EvtxMapSyncResult(0, 0, 0, errors);
            try { Directory.CreateDirectory(mapsDir); }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { errors.Add(ex.Message); return new EvtxMapSyncResult(0, 0, 0, errors); }
            foreach (var src in sources)
            {
                var dst = Path.Combine(mapsDir, Prefix + Path.GetFileName(src));
                try
                {
                    var bytes = File.ReadAllBytes(src);
                    if (File.Exists(dst) && File.ReadAllBytes(dst).AsSpan().SequenceEqual(bytes)) { same++; continue; }
                    File.WriteAllBytes(dst, bytes);
                    copied++;
                }
                catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { errors.Add($"{Path.GetFileName(src)}: {ex.Message}"); }
            }
            foreach (var f in Directory.EnumerateFiles(mapsDir, Prefix + "*.map").ToList())
            {
                if (wanted.Contains(Path.GetFileName(f))) continue;
                try { File.Delete(f); removed++; }
                catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { errors.Add($"{Path.GetFileName(f)}: {ex.Message}"); }
            }
        }
        return new EvtxMapSyncResult(copied, same, removed, errors);
    }

    /// <summary>
    /// Job hook for an EvtxECmd run: the Maps folder the run uses (<c>--maps</c> value, else <c>Maps</c> next to the exe) gets the custom maps.
    /// A <c>--maps</c> folder is only written when it is the tool's own Maps folder. Returns log lines.
    /// </summary>
    public static IReadOnlyList<string> ApplyForRun(IReadOnlyList<string> args, string toolDir, string customFolder)
    {
        var lines = new List<string>();
        var own = Path.Combine(toolDir, "Maps");
        var target = own;
        for (int i = 0; i < args.Count - 1; i++)
            if (string.Equals(args[i], "--maps", StringComparison.OrdinalIgnoreCase)) target = args[i + 1];
        if (!SamePath(target, own))
        {
            if (Directory.Exists(customFolder) && Directory.EnumerateFiles(customFolder, "*.map").Any())
                lines.Add($"# Custom event maps not copied: --maps points to {target}, not the tool's Maps folder.");
            return lines;
        }
        var r = SyncInto(customFolder, own);
        if (r.Copied + r.Unchanged + r.Removed > 0)
            lines.Add($"# Custom event maps: {r.Copied + r.Unchanged} in use ({r.Copied} copied), {r.Removed} removed, as {Prefix}*.map in {own}. The stock map for the same event is then skipped by EvtxECmd (\"same key\" message).");
        foreach (var e in r.Errors) lines.Add("# Custom event map not copied: " + e);
        return lines;
    }

    private static bool SamePath(string a, string b)
    {
        try { return string.Equals(Path.GetFullPath(a).TrimEnd('\\', '/'), Path.GetFullPath(b).TrimEnd('\\', '/'), StringComparison.OrdinalIgnoreCase); }
        catch (Exception) { return false; }
    }
}
