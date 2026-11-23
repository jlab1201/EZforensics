using System.Globalization;
using EzForensics.Core.Settings;

namespace EzForensics.Core.Results;

/// <summary>
/// One row flag: a stable <see cref="Id"/> (what is stored in flag sidecars), a display <see cref="Label"/> and a <see cref="Color"/>
/// ("#RRGGBB"). Renaming the label or changing the colour never touches stored flags.
/// </summary>
public sealed class FlagDefinition
{
    public FlagDefinition() { }
    public FlagDefinition(string id, string label, string color) { Id = id; Label = label; Color = color; }

    public string Id { get; set; } = "";
    public string Label { get; set; } = "";
    public string Color { get; set; } = FlagCatalog.FallbackColor;

    /// <summary>One of the five standard flags (can be renamed and recoloured, restored via Reset to defaults).</summary>
    public bool IsBuiltIn => FlagCatalog.IsBuiltInId(Id);

    public FlagDefinition Clone() => new(Id, Label, Color);
}

/// <summary>Standard flags (ordered by investigation progression), legacy colour migration and helpers over an ordered flag list.</summary>
public static class FlagCatalog
{
    public const string Informational = "informational";
    public const string Interesting = "interesting";
    public const string Suspicious = "suspicious";
    public const string Malicious = "malicious";
    public const string Benign = "benign";

    /// <summary>Flag used for a plain "flag" (Space / Ctrl+F2) until the user picks another one.</summary>
    public const string DefaultId = Suspicious;
    /// <summary>Flag of a plain flagged row in version 1 sidecars (was Red).</summary>
    public const string LegacyDefaultId = Malicious;
    public const string FallbackColor = "#9CA3AF";
    /// <summary>Id of the "Spectre evidence" flag older builds added to the examiner's list from Findings; removed on load (Findings evidence rows are a transient filter now).</summary>
    public const string RetiredSpectreEvidenceId = "spectre-evidence";

    /// <summary>Removes the retired Spectre evidence flag from <paramref name="settings"/>; true when it was there.</summary>
    public static bool RemoveRetiredSpectreEvidence(AppSettings settings)
    {
        if (!settings.Flags.Any(f => f.Id == RetiredSpectreEvidenceId)) return false;
        settings.Flags = settings.Flags.Where(f => f.Id != RetiredSpectreEvidenceId).ToList();
        return true;
    }

    /// <summary>The standard flags, in order: Informational, Interesting, Suspicious, Malicious, Benign / Ruled out.</summary>
    public static List<FlagDefinition> Defaults() => new()
    {
        new(Informational, "Informational", "#3B82F6"),
        new(Interesting, "Interesting", "#A855F7"),
        new(Suspicious, "Suspicious", "#F59E0B"),
        new(Malicious, "Malicious", "#EF4444"),
        new(Benign, "Benign / Ruled out", "#22C55E"),
    };

    private static readonly string[] BuiltInIds = { Informational, Interesting, Suspicious, Malicious, Benign };

    public static bool IsBuiltInId(string? id) => id != null && BuiltInIds.Contains(id, StringComparer.Ordinal);

    /// <summary>Flag-colour names used before flags had ids (the old colour palette), with the number the old enum stored.</summary>
    private static readonly (string Name, int Number, string Id)[] Legacy =
    {
        ("red", 1, Malicious), ("orange", 2, Suspicious), ("yellow", 3, Suspicious),
        ("green", 4, Benign), ("blue", 5, Informational), ("purple", 6, Interesting),
    };

    /// <summary>
    /// Maps a legacy colour name (red, orange, yellow, green, blue, purple; or the old enum number) or a built-in id to a built-in flag id.
    /// Returns null for anything else.
    /// </summary>
    public static string? MigrateLegacy(string? key)
    {
        if (string.IsNullOrWhiteSpace(key)) return null;
        var k = key.Trim().ToLowerInvariant();
        if (IsBuiltInId(k)) return k;
        foreach (var l in Legacy) if (l.Name == k) return l.Id;
        if (int.TryParse(k, NumberStyles.None, CultureInfo.InvariantCulture, out var n))
            foreach (var l in Legacy) if (l.Number == n) return l.Id;
        return null;
    }

    /// <summary>Flag id for a key found in a sidecar. Version 3+ keys are already ids; older keys are colour names (unknown names are kept lower-cased).</summary>
    public static string IdFromStored(string key, bool legacyFormat)
    {
        var k = key.Trim();
        return legacyFormat ? MigrateLegacy(k) ?? k.ToLowerInvariant() : k;
    }

    public static bool TryParseColor(string? s, out string normalized)
    {
        normalized = FallbackColor;
        if (string.IsNullOrWhiteSpace(s)) return false;
        var t = s.Trim();
        if (t.Length != 7 || t[0] != '#') return false;
        if (!int.TryParse(t.AsSpan(1), NumberStyles.AllowHexSpecifier, CultureInfo.InvariantCulture, out _)) return false;
        normalized = t.ToUpperInvariant();
        return true;
    }

    /// <summary>
    /// Cleans a user-edited list: drops entries without id, removes duplicate ids (first wins), repairs bad colours and empty labels.
    /// Never returns an empty list (falls back to <see cref="Defaults"/>). Order is kept.
    /// </summary>
    public static List<FlagDefinition> Normalize(IEnumerable<FlagDefinition>? flags)
    {
        var result = new List<FlagDefinition>();
        var seen = new HashSet<string>(StringComparer.Ordinal);
        var defaults = Defaults().ToDictionary(d => d.Id);
        foreach (var f in flags ?? Enumerable.Empty<FlagDefinition>())
        {
            if (f == null || string.IsNullOrWhiteSpace(f.Id)) continue;
            var id = f.Id.Trim();
            if (!seen.Add(id)) continue;
            defaults.TryGetValue(id, out var d);
            var label = string.IsNullOrWhiteSpace(f.Label) ? (d?.Label ?? "Flag") : f.Label.Trim();
            var color = TryParseColor(f.Color, out var c) ? c : (d?.Color ?? FallbackColor);
            result.Add(new FlagDefinition(id, label, color));
        }
        return result.Count == 0 ? Defaults() : result;
    }

    /// <summary>A new unique id for a custom flag.</summary>
    public static string NewCustomId() => "custom-" + Guid.NewGuid().ToString("N")[..8];

    /// <summary>The definition for <paramref name="id"/>; an id that is no longer in the list resolves to a grey placeholder labelled with the id.</summary>
    public static FlagDefinition Resolve(IReadOnlyList<FlagDefinition> flags, string? id)
    {
        foreach (var f in flags) if (string.Equals(f.Id, id, StringComparison.Ordinal)) return f;
        return new FlagDefinition(id ?? "", string.IsNullOrEmpty(id) ? "Flag" : id, FallbackColor);
    }

    public static string LabelOf(IReadOnlyList<FlagDefinition> flags, string id) => Resolve(flags, id).Label;

    public static int IndexOf(IReadOnlyList<FlagDefinition> flags, string? id)
    {
        for (int i = 0; i < flags.Count; i++) if (string.Equals(flags[i].Id, id, StringComparison.Ordinal)) return i;
        return -1;
    }

    /// <summary>Click-cycle order: none, then the flags in list order, then none again. An id outside the list starts at the first flag.</summary>
    public static string? Next(IReadOnlyList<FlagDefinition> flags, string? current)
    {
        if (flags.Count == 0) return null;
        int i = IndexOf(flags, current);
        if (i < 0) return flags[0].Id;
        return i + 1 < flags.Count ? flags[i + 1].Id : null;
    }

    /// <summary>
    /// Space rotation, starting from the last used flag <paramref name="last"/>: an unflagged row gets <paramref name="last"/>; a flagged row
    /// moves to the next flag in list order (wrapping), and once every flag has been shown (the next one would be <paramref name="last"/>
    /// again) the flag is cleared. An id outside the list moves to <paramref name="last"/>.
    /// </summary>
    public static string? RotateNext(IReadOnlyList<FlagDefinition> flags, string? current, string last)
    {
        if (flags.Count == 0) return null;
        int start = Math.Max(0, IndexOf(flags, last));
        if (current == null) return flags[start].Id;
        int i = IndexOf(flags, current);
        if (i < 0) return flags[start].Id;
        int next = (i + 1) % flags.Count;
        return next == start ? null : flags[next].Id;
    }

    /// <summary>Position of an id in the list (ids not in the list sort last).</summary>
    public static int Order(IReadOnlyList<FlagDefinition> flags, string id)
    {
        var i = IndexOf(flags, id);
        return i < 0 ? int.MaxValue : i;
    }
}
