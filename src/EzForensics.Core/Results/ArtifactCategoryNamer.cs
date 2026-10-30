using System.Globalization;
using System.Text.RegularExpressions;
using EzForensics.Core.Advisories;
using EzForensics.Core.Memory;

namespace EzForensics.Core.Results;

/// <summary>Suggests a readable category name for a CSV layout (<c>Event logs</c>, <c>Memory: Netscan</c>, or the file name).</summary>
public static class ArtifactCategoryNamer
{
    private static readonly Lazy<ReliabilityAdvisor> DefaultAdvisor = new(() => new ReliabilityAdvisor());
    private static readonly Regex LeadStamp = new(@"^\d{8,14}[_\-\s]+", RegexOptions.Compiled);
    private static readonly Regex TrailQualifier = new(@"((^|[_\-\s]+)(Output|\d+))+$", RegexOptions.Compiled | RegexOptions.IgnoreCase);
    private static readonly Regex MemoryPrefix = new(@"^Memory_", RegexOptions.Compiled | RegexOptions.IgnoreCase);

    public static string Name(IReadOnlyList<string> headers, string? csvFileName, string? artifactId = null, ReliabilityAdvisor? advisor = null)
    {
        advisor ??= DefaultAdvisor.Value;
        var artifact = !string.IsNullOrWhiteSpace(artifactId)
            ? advisor.Rules.FindArtifact(artifactId)
            : advisor.Identify(headers, csvFileName);

        var stem = Stem(csvFileName);
        if (artifact != null)
        {
            var label = ShortLabel(artifact.Label);
            if (artifact.Id.StartsWith("memory-", StringComparison.OrdinalIgnoreCase))
            {
                var plugin = MemoryPluginName(stem);
                return plugin != null ? "Memory: " + plugin : label;
            }
            if (label.Length > 0) return label;
        }
        var fromFile = TitleCase(TrailQualifier.Replace(LeadStamp.Replace(stem, ""), ""));
        return fromFile.Length > 0 ? fromFile : "Custom layout";
    }

    private static string ShortLabel(string label)
    {
        var i = label.IndexOf(" (", StringComparison.Ordinal);
        return (i > 0 ? label[..i] : label).Trim();
    }

    private static string Stem(string? file)
    {
        if (string.IsNullOrWhiteSpace(file)) return "";
        try { return Path.GetFileNameWithoutExtension(file.Trim()); }
        catch (ArgumentException) { return ""; }
    }

    /// <summary>The Volatility plugin in a file stem (<c>20261020123456_Memory_NetScan_img</c>, <c>windows.netscan</c>, <c>netscan</c>), title-cased; null when unrecognised.</summary>
    private static string? MemoryPluginName(string stem)
    {
        if (stem.Length == 0) return null;
        var tokens = LeadStamp.Replace(stem, "").Split(new[] { '_', ' ', '-' }, StringSplitOptions.RemoveEmptyEntries);
        foreach (var t in tokens)
        {
            var last = t.Contains('.') ? t[(t.LastIndexOf('.') + 1)..] : t;
            var def = Match(last);
            if (def != null) return Cap(PluginTail(def));
        }
        return null;
    }

    private static MemoryPluginDef? Match(string token)
    {
        if (token.Length == 0) return null;
        return MemoryPlugins.All.FirstOrDefault(p =>
            p.Variant.Equals(token, StringComparison.OrdinalIgnoreCase)
            || p.Id.Equals(token, StringComparison.OrdinalIgnoreCase)
            || PluginTail(p).Equals(token, StringComparison.OrdinalIgnoreCase));
    }

    private static string PluginTail(MemoryPluginDef p)
    {
        var i = p.Plugin.LastIndexOf('.');
        return i >= 0 ? p.Plugin[(i + 1)..] : p.Plugin;
    }

    private static string Cap(string s) => s.Length == 0 ? s : char.ToUpperInvariant(s[0]) + s[1..].ToLowerInvariant();

    private static string TitleCase(string s)
    {
        s = MemoryPrefix.Replace(s, "").Replace('_', ' ').Replace('-', ' ').Trim();
        if (s.Length == 0) return "";
        return CultureInfo.InvariantCulture.TextInfo.ToTitleCase(s.ToLowerInvariant());
    }
}
