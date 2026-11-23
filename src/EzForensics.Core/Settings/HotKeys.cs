namespace EzForensics.Core.Settings;

/// <summary>A parsed shortcut: modifiers plus a key token (the WPF <c>Key</c> name, e.g. "F", "F2", "Enter", "Esc").</summary>
public readonly record struct HotKeyGesture(bool Ctrl, bool Alt, bool Shift, string Key)
{
    /// <summary>Canonical text, e.g. "Ctrl+Shift+F".</summary>
    public override string ToString() => HotKeys.Format(this);
}

/// <summary>One editable shortcut: stable id, display name, group and default gesture.</summary>
public sealed record HotKeyAction(string Id, string Name, string Group, string Default);

/// <summary>A built-in shortcut that is listed but not editable. <see cref="Gestures"/> are reserved for conflict checks.</summary>
public sealed record FixedHotKey(string Display, string Name, string Group, params string[] Gestures);

/// <summary>Analyze shortcut table, gesture parse/format, conflict detection and override resolution (pure, no UI types).</summary>
public static class HotKeys
{
    public const string FilterContains = "Analyze.FilterContains";
    public const string FilterNotContains = "Analyze.FilterNotContains";
    public const string FilterEquals = "Analyze.FilterEquals";
    public const string ClearFilters = "Analyze.ClearFilters";
    public const string ViewValue = "Analyze.ViewValue";
    public const string CopyRow = "Analyze.CopyRow";
    public const string ExportFiltered = "Analyze.ExportFiltered";
    public const string ShowInExplorer = "Analyze.ShowInExplorer";
    public const string Back = "App.Back";
    public const string ToggleNav = "App.ToggleNav";

    public static IReadOnlyList<HotKeyAction> Actions { get; } = new HotKeyAction[]
    {
        new(ViewValue, "View full value", "Cell", "Enter"),
        new(FilterContains, "Filter: contains this value", "Filter", "Ctrl+Shift+F"),
        new(FilterNotContains, "Filter: does not contain this value", "Filter", "Ctrl+Shift+X"),
        new(FilterEquals, "Filter: equals this value", "Filter", "Ctrl+Shift+E"),
        new(ClearFilters, "Clear all filters", "Filter", "Ctrl+Shift+L"),
        new(CopyRow, "Copy row", "Copy", "Ctrl+Shift+C"),
        new(ExportFiltered, "Export filtered rows", "File", "Ctrl+Shift+S"),
        new(ShowInExplorer, "Show file in Explorer", "File", "Ctrl+Shift+O"),
        new(Back, "Go back", "Navigation", "Alt+Left"),
        new(ToggleNav, "Toggle sidebar (pin / collapse)", "Navigation", "Ctrl+B"),
    };

    /// <summary>Keys that stay hard-coded (listed read-only in Options and the shortcuts dialog).</summary>
    public static IReadOnlyList<FixedHotKey> Fixed { get; } = new FixedHotKey[]
    {
        new("Space / Ctrl+F2", "Flag selected rows; press again to rotate colours, then clear", "Fixed", "Space", "Ctrl+F2"),
        new("Ctrl+1 .. Ctrl+9", "Set the Nth flag", "Fixed", "Ctrl+1", "Ctrl+2", "Ctrl+3", "Ctrl+4", "Ctrl+5", "Ctrl+6", "Ctrl+7", "Ctrl+8", "Ctrl+9"),
        new("Ctrl+0", "Clear flag", "Fixed", "Ctrl+0"),
        new("Ctrl+C", "Copy selection", "Fixed", "Ctrl+C"),
        new("Esc", "Clear all filters", "Fixed", "Esc"),
        new("F11", "Focus mode", "Fixed", "F11"),
    };

    public static HotKeyAction? Find(string id) => Actions.FirstOrDefault(a => a.Id == id);

    private static string KeyAlias(string k) => k.ToLowerInvariant() switch
    {
        "escape" => "Esc",
        "esc" => "Esc",
        "return" => "Enter",
        "del" => "Delete",
        "spacebar" => "Space",
        _ => k.Length == 1 ? k.ToUpperInvariant() : char.ToUpperInvariant(k[0]) + k[1..],
    };

    /// <summary>Parses "Ctrl+Shift+F" (case-insensitive, any modifier order, "Control" accepted). Needs exactly one non-modifier key. "Ctrl++" is the plus key.</summary>
    public static bool TryParse(string? text, out HotKeyGesture gesture)
    {
        gesture = default;
        if (string.IsNullOrWhiteSpace(text)) return false;
        text = text.Trim();
        string? key = null;
        if (text.EndsWith("++", StringComparison.Ordinal) || text == "+")
        {
            key = "+";
            text = text == "+" ? "" : text[..^2];
        }
        bool ctrl = false, alt = false, shift = false;
        var parts = text.Length == 0 ? Array.Empty<string>() : text.Split('+');
        foreach (var raw in parts)
        {
            var p = raw.Trim();
            if (p.Length == 0) return false;
            switch (p.ToLowerInvariant())
            {
                case "ctrl" or "control": if (ctrl) return false; ctrl = true; break;
                case "alt": if (alt) return false; alt = true; break;
                case "shift": if (shift) return false; shift = true; break;
                default:
                    if (key != null) return false;
                    key = KeyAlias(p);
                    break;
            }
        }
        if (key == null) return false;
        gesture = new HotKeyGesture(ctrl, alt, shift, key);
        return true;
    }

    public static string Format(HotKeyGesture g)
    {
        var sb = new System.Text.StringBuilder();
        if (g.Ctrl) sb.Append("Ctrl+");
        if (g.Alt) sb.Append("Alt+");
        if (g.Shift) sb.Append("Shift+");
        sb.Append(g.Key);
        return sb.ToString();
    }

    /// <summary>Canonical text for a gesture, or null when it does not parse.</summary>
    public static string? Normalize(string? text) => TryParse(text, out var g) ? Format(g) : null;

    /// <summary>
    /// Effective gesture (canonical text) per action id: the override when present and valid, else the default.
    /// An override that is empty/blank means the action is unassigned (""). An unparsable override falls back to the default.
    /// </summary>
    public static Dictionary<string, string> Resolve(IReadOnlyDictionary<string, string>? overrides)
    {
        var map = new Dictionary<string, string>();
        foreach (var a in Actions)
        {
            string g = Normalize(a.Default) ?? "";
            if (overrides != null && overrides.TryGetValue(a.Id, out var o))
            {
                if (string.IsNullOrWhiteSpace(o)) g = "";
                else if (Normalize(o) is { } n) g = n;
            }
            map[a.Id] = g;
        }
        return map;
    }

    /// <summary>Overrides to store for the given effective gestures: only the ones that differ from the default ("" = unassigned).</summary>
    public static Dictionary<string, string> ToOverrides(IReadOnlyDictionary<string, string> effective)
    {
        var o = new Dictionary<string, string>();
        foreach (var a in Actions)
        {
            if (!effective.TryGetValue(a.Id, out var g)) continue;
            if (!string.Equals(g, Normalize(a.Default) ?? "", StringComparison.OrdinalIgnoreCase)) o[a.Id] = g;
        }
        return o;
    }

    /// <summary>Gestures claimed by more than one action (editable or fixed). Unassigned actions never conflict.</summary>
    public static IReadOnlyList<(string Gesture, IReadOnlyList<string> Names)> Conflicts(IReadOnlyDictionary<string, string> effective)
    {
        var by = new Dictionary<string, List<string>>(StringComparer.OrdinalIgnoreCase);
        foreach (var a in Actions)
            if (effective.TryGetValue(a.Id, out var g) && g.Length > 0) Add(by, g, a.Name);
        foreach (var f in Fixed)
            foreach (var g in f.Gestures) Add(by, Normalize(g) ?? g, f.Name);
        return by.Where(kv => kv.Value.Count > 1).Select(kv => (kv.Key, (IReadOnlyList<string>)kv.Value)).ToList();
    }

    private static void Add(Dictionary<string, List<string>> by, string g, string name)
    {
        if (!by.TryGetValue(g, out var l)) by[g] = l = new List<string>();
        l.Add(name);
    }

    /// <summary>Name of another action (or fixed key) that already uses <paramref name="gesture"/>, or null when it is free for <paramref name="actionId"/>.</summary>
    public static string? FindConflict(IReadOnlyDictionary<string, string> effective, string actionId, string gesture)
    {
        var n = Normalize(gesture);
        if (n == null) return null;
        foreach (var a in Actions)
            if (a.Id != actionId && effective.TryGetValue(a.Id, out var g) && string.Equals(g, n, StringComparison.OrdinalIgnoreCase)) return a.Name;
        foreach (var f in Fixed)
            if (f.Gestures.Any(x => string.Equals(Normalize(x), n, StringComparison.OrdinalIgnoreCase))) return f.Name;
        return null;
    }

    /// <summary>Action id bound to the gesture, or null.</summary>
    public static string? ActionFor(IReadOnlyDictionary<string, string> effective, HotKeyGesture g)
    {
        var n = Format(g);
        foreach (var a in Actions)
            if (effective.TryGetValue(a.Id, out var e) && e.Length > 0 && string.Equals(e, n, StringComparison.OrdinalIgnoreCase)) return a.Id;
        return null;
    }
}
