using System.Reflection;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace EzForensics.Core.Memory;

/// <summary>One Volatility 3 plugin run by the Ingest page (<c>Core/Resources/memory/plugins.json</c>).</summary>
/// <param name="Id">Short id (<c>pslist</c>); the artifact id is <c>memory-&lt;id&gt;</c>.</param>
/// <param name="Plugin">Volatility plugin name (<c>windows.pslist</c>, <c>windows.registry.printkey</c>, <c>timeliner</c>).</param>
/// <param name="Variant">PascalCase output variant: <c>Memory_&lt;Variant&gt;[_&lt;imagestem&gt;].csv</c>.</param>
/// <param name="Os"><c>windows</c> or <c>linux</c>.</param>
/// <param name="Args">Plugin arguments placed after the plugin name.</param>
/// <param name="NeedsUserPages">The plugin reads user-mode pages (results are incomplete on a kernel dump).</param>
public sealed record MemoryPluginDef(string Id, string Plugin, string Label, string Variant, string ArtifactId, bool DefaultSelected, string Os,
    IReadOnlyList<string> Args, bool NeedsUserPages, int TimeoutBaseMin, int TimeoutPerGbMin)
{
    /// <summary>The <c>windows.info</c> plugin: runs first, warms the symbol cache and opens the image's <see cref="MemorySymbolGate"/>.</summary>
    public bool IsInfo => Plugin.Equals(MemoryPlugins.InfoPlugin, StringComparison.OrdinalIgnoreCase);
}

/// <summary>Loads the embedded plugin catalog.</summary>
public static class MemoryPlugins
{
    public const string InfoPlugin = "windows.info";

    private static readonly Lazy<IReadOnlyList<MemoryPluginDef>> Embedded = new(LoadEmbeddedCore);

    /// <summary>The embedded catalog (cached).</summary>
    public static IReadOnlyList<MemoryPluginDef> All => Embedded.Value;

    public static MemoryPluginDef? Find(string id) => All.FirstOrDefault(p => p.Id.Equals(id, StringComparison.OrdinalIgnoreCase));

    /// <summary>Plugins for one OS (<c>windows</c> / <c>linux</c>), catalog order.</summary>
    public static IReadOnlyList<MemoryPluginDef> ForOs(MemoryOs os)
    {
        var key = os == MemoryOs.Linux ? "linux" : os == MemoryOs.Windows ? "windows" : null;
        return key == null ? Array.Empty<MemoryPluginDef>() : All.Where(p => p.Os.Equals(key, StringComparison.OrdinalIgnoreCase)).ToList();
    }

    /// <summary>
    /// Plugin ids Ingest plans for an image of <paramref name="os"/>: the configured set (Tools, <c>AppSettings.VolatilityPlugins</c>;
    /// null = the catalog defaults), catalog order; <c>windows.info</c> is always included (the other plugins wait for it).
    /// </summary>
    public static IReadOnlyList<string> SelectedFor(MemoryOs os, IEnumerable<string>? configured)
    {
        var set = configured == null ? null : new HashSet<string>(configured, StringComparer.OrdinalIgnoreCase);
        return ForOs(os).Where(p => p.IsInfo || (set == null ? p.DefaultSelected : set.Contains(p.Id))).Select(p => p.Id).ToList();
    }

    public static IReadOnlyList<MemoryPluginDef> Load(Stream json)
    {
        var file = JsonSerializer.Deserialize<PluginFile>(json, new JsonSerializerOptions { PropertyNameCaseInsensitive = true, ReadCommentHandling = JsonCommentHandling.Skip, AllowTrailingCommas = true })
                   ?? throw new InvalidDataException("plugins.json is empty");
        var list = new List<MemoryPluginDef>();
        foreach (var p in file.Plugins)
        {
            if (string.IsNullOrWhiteSpace(p.Id) || string.IsNullOrWhiteSpace(p.Plugin) || string.IsNullOrWhiteSpace(p.Variant))
                throw new InvalidDataException("plugins.json: every plugin needs id, plugin and variant");
            list.Add(new MemoryPluginDef(p.Id, p.Plugin, p.Label ?? p.Plugin, p.Variant, p.ArtifactId ?? "memory-" + p.Id, p.DefaultSelected,
                p.Os ?? "windows", p.Args ?? new List<string>(), p.NeedsUserPages, Math.Max(1, p.TimeoutBaseMin), Math.Max(0, p.TimeoutPerGbMin)));
        }
        var dup = list.GroupBy(p => p.Variant, StringComparer.OrdinalIgnoreCase).FirstOrDefault(g => g.Count() > 1);
        if (dup != null) throw new InvalidDataException("plugins.json: duplicate variant " + dup.Key);
        return list;
    }

    private static IReadOnlyList<MemoryPluginDef> LoadEmbeddedCore()
    {
        var asm = typeof(MemoryPlugins).Assembly;
        var name = asm.GetManifestResourceNames().First(n => n.EndsWith("memory.plugins.json", StringComparison.OrdinalIgnoreCase));
        using var s = asm.GetManifestResourceStream(name)!;
        return Load(s);
    }

    private sealed class PluginFile
    {
        public int Version { get; set; }
        public List<PluginEntry> Plugins { get; set; } = new();
    }

    private sealed class PluginEntry
    {
        public string Id { get; set; } = "";
        public string Plugin { get; set; } = "";
        public string? Label { get; set; }
        public string Variant { get; set; } = "";
        public string? ArtifactId { get; set; }
        public bool DefaultSelected { get; set; }
        public string? Os { get; set; }
        public List<string>? Args { get; set; }
        public bool NeedsUserPages { get; set; }
        public int TimeoutBaseMin { get; set; } = 10;
        public int TimeoutPerGbMin { get; set; } = 2;
    }
}
