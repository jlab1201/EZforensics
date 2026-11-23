namespace EzForensics.Core.Knowledge;

/// <summary>The fixed label vocabulary of the knowledge feed and the rules that normalize AI or user supplied values.</summary>
public static class KnowledgeLabels
{
    public const string None = "none", Labelled = "labelled", Failed = "failed";

    public const int MaxTags = 6;

    public static IReadOnlyList<string> Categories { get; } = new[]
    {
        "Windows artifacts", "Event logs", "Memory", "Disk & file system", "Network", "Malware",
        "Cloud & identity", "Threat intel", "Tools", "Methodology", "Reporting", "General",
    };

    public static IReadOnlyList<string> Types { get; } = new[] { "Learned by doing", "Reference", "Template / checklist" };

    /// <summary>The matching category (case-insensitive), or <c>General</c>.</summary>
    public static string Category(string? v) => Match(Categories, v) ?? "General";

    /// <summary>The matching type (case-insensitive), or <c>Reference</c>.</summary>
    public static string Type(string? v) => Match(Types, v) ?? "Reference";

    /// <summary>Trimmed, de-duplicated (case-insensitive, first seen wins) tags, at most 6.</summary>
    public static IReadOnlyList<string> Tags(IEnumerable<string?>? raw)
    {
        var list = new List<string>();
        if (raw == null) return list;
        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var t in raw)
        {
            var s = t?.Trim();
            if (string.IsNullOrEmpty(s) || !seen.Add(s)) continue;
            list.Add(s);
            if (list.Count == MaxTags) break;
        }
        return list;
    }

    private static string? Match(IReadOnlyList<string> list, string? v)
    {
        var s = v?.Trim();
        if (string.IsNullOrEmpty(s)) return null;
        foreach (var x in list) if (string.Equals(x, s, StringComparison.OrdinalIgnoreCase)) return x;
        return null;
    }
}
