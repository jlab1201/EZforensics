using System.Collections.Concurrent;
using System.Text;
using System.Text.RegularExpressions;
using EzForensics.Core.Plaso;

namespace EzForensics.Core.Memory;

/// <summary>
/// Which plugins an installed Volatility 3 offers, read from <c>vol -h</c> (its PLUGIN list: <c>windows.pslist.PsList</c>, <c>windows.malware.malfind.Malfind</c>, ...).
/// The result is cached per vol path and version, in memory and in <c>&lt;cache&gt;\ezforensics-plugins.txt</c>, so <c>vol -h</c> (a few seconds: it imports every
/// plugin) runs once per install. Fails open: when vol cannot be run or its help lists nothing, <see cref="Get"/> returns null and every plugin counts as available.
/// </summary>
public static class VolatilityPluginProbe
{
    public const string CacheFileName = "ezforensics-plugins.txt";
    public static readonly IReadOnlyList<string> HelpArgs = new[] { "-h" };

    // "    windows.pslist.PsList   Lists the processes ..." : 4 spaces, a dotted name whose last part is the class name
    private static readonly Regex PluginLineRx = new(@"^\s{2,}(?<n>[a-z][a-z0-9_]*(?:\.[A-Za-z0-9_]+)+)(?:\s|$)", RegexOptions.CultureInvariant | RegexOptions.Compiled);

    private static readonly ConcurrentDictionary<string, IReadOnlySet<string>?> Cache = new(StringComparer.OrdinalIgnoreCase);

    /// <summary>
    /// Plugin names of the PLUGIN section of <c>vol -h</c>, each both with and without its class name (<c>windows.pslist</c> and <c>windows.pslist.PsList</c>),
    /// so a catalog entry matches either way. Empty when the text has no plugin list.
    /// </summary>
    public static HashSet<string> ParseHelp(string? help)
    {
        var set = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        if (string.IsNullOrEmpty(help)) return set;
        bool inPlugins = false;
        foreach (var raw in help.Split('\n'))
        {
            var line = raw.TrimEnd('\r');
            var t = line.Trim();
            if (!inPlugins)
            {
                // argparse prints the subparser group as "  PLUGIN" (older builds: "Plugins:")
                if (t.Equals("PLUGIN", StringComparison.Ordinal) || t.StartsWith("Plugins", StringComparison.OrdinalIgnoreCase)) inPlugins = true;
                continue;
            }
            var m = PluginLineRx.Match(line);
            if (!m.Success) continue;
            var name = m.Groups["n"].Value;
            set.Add(name);
            int dot = name.LastIndexOf('.');
            if (dot > 0 && char.IsUpper(name[dot + 1])) set.Add(name[..dot]);
        }
        return set;
    }

    /// <summary>True when <paramref name="plugin"/> is offered, or when nothing is known (<paramref name="available"/> null: fail open).</summary>
    public static bool IsListed(MemoryPluginDef plugin, IReadOnlySet<string>? available) =>
        available == null || plugin.IsInfo || available.Contains(plugin.Plugin);

    /// <summary>Catalog plugins the install does not list (empty when unknown).</summary>
    public static IReadOnlyList<MemoryPluginDef> Missing(IEnumerable<MemoryPluginDef> plugins, IReadOnlySet<string>? available) =>
        available == null ? Array.Empty<MemoryPluginDef>() : plugins.Where(p => !IsListed(p, available)).ToList();

    private static string KeyOf(VolatilityInstall install) => install.Exe + "|" + (install.Version ?? "");

    /// <summary>
    /// The plugin names <paramref name="install"/> offers, or null when they could not be read (then everything counts as available). Blocking on the first call per
    /// install (runs <c>vol -h</c>, at most <paramref name="timeout"/>); later calls are served from the cache. Call from a background thread.
    /// </summary>
    public static IReadOnlySet<string>? Get(VolatilityInstall install, IPlasoProbe? probe = null, TimeSpan? timeout = null)
    {
        var key = KeyOf(install);
        if (Cache.TryGetValue(key, out var hit)) return hit;
        var cacheFile = string.IsNullOrWhiteSpace(install.CacheDir) ? null : Path.Combine(install.CacheDir, CacheFileName);
        var set = ReadCacheFile(cacheFile, key);
        if (set == null)
        {
            var p = probe ?? SystemPlasoProbe.Instance;
            string? output = null;
            try { output = p.FileExists(install.Exe) ? p.Run(install.Exe, HelpArgs, timeout ?? TimeSpan.FromSeconds(120)) : null; }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or InvalidOperationException or System.ComponentModel.Win32Exception) { output = null; }
            var parsed = ParseHelp(output);
            if (parsed.Count > 0)
            {
                set = parsed;
                WriteCacheFile(cacheFile, key, parsed);
            }
        }
        // a failed probe is not cached on disk (the next start tries again), only for this session
        Cache[key] = set;
        return set;
    }

    /// <summary>Forgets the cached lists (after an install, update or a new configured path).</summary>
    public static void Reset(VolatilityInstall? install = null)
    {
        if (install == null) { Cache.Clear(); return; }
        Cache.TryRemove(KeyOf(install), out _);
        if (string.IsNullOrWhiteSpace(install.CacheDir)) return;
        try { File.Delete(Path.Combine(install.CacheDir, CacheFileName)); } catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { }
    }

    private static HashSet<string>? ReadCacheFile(string? path, string key)
    {
        if (path == null) return null;
        try
        {
            if (!File.Exists(path)) return null;
            var lines = File.ReadAllLines(path, Encoding.UTF8);
            if (lines.Length < 2 || !lines[0].Equals(key, StringComparison.OrdinalIgnoreCase)) return null;
            var set = new HashSet<string>(lines.Skip(1).Select(l => l.Trim()).Where(l => l.Length > 0), StringComparer.OrdinalIgnoreCase);
            return set.Count > 0 ? set : null;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { return null; }
    }

    private static void WriteCacheFile(string? path, string key, IEnumerable<string> names)
    {
        if (path == null) return;
        try
        {
            Directory.CreateDirectory(Path.GetDirectoryName(path)!);
            File.WriteAllLines(path, new[] { key }.Concat(names.OrderBy(n => n, StringComparer.OrdinalIgnoreCase)), new UTF8Encoding(false));
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { }
    }
}
