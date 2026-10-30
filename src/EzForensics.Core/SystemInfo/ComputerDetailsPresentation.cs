namespace EzForensics.Core.SystemInfo;

/// <summary>
/// Which facts the Computer details panel shows: a compact key-facts list by default, a filtered detailed list on demand.
/// The saved report / CSV keeps every field; this only selects for display.
/// </summary>
public static class ComputerDetailsPresentation
{
    /// <summary>Fields hidden from the detailed view (category, field): install/UI trivia or duplicated by other fields.</summary>
    private static readonly HashSet<(string Category, string Field)> Hidden = new()
    {
        (ComputerDetailsCollector.CatOs, "Product ID"),
        (ComputerDetailsCollector.CatOs, "Registered organization"),
        (ComputerDetailsCollector.CatOs, "Edition"),
        (ComputerDetailsCollector.CatOs, "NT version"),
        (ComputerDetailsCollector.CatOs, "Build lab"),
        (ComputerDetailsCollector.CatOs, "System root"),
        (ComputerDetailsCollector.CatSystem, "Current control set"),
    };

    public static bool IsUnknown(string? v) => string.IsNullOrWhiteSpace(v) || v.Trim() == ComputerDetailsCollector.Unknown;

    private static string? Get(SystemDetails d, string category, string field) =>
        d.Fields.FirstOrDefault(f => f.Category == category && f.Field == field && !IsUnknown(f.Value))?.Value;

    /// <summary>Forensically key facts, in display order; missing values are omitted.</summary>
    public static IReadOnlyList<(string Label, string Value)> KeyFacts(SystemDetails d)
    {
        var r = new List<(string, string)>();
        void Add(string label, string? v) { if (!IsUnknown(v)) r.Add((label, v!.Trim())); }

        Add("Computer name", Get(d, ComputerDetailsCollector.CatSystem, "Computer name")
            ?? Get(d, ComputerDetailsCollector.CatSystem, "Host name (TCP/IP)"));

        var os = Get(d, ComputerDetailsCollector.CatOs, "Operating system (derived)")
            ?? Get(d, ComputerDetailsCollector.CatOs, "Product name");
        var ver = Get(d, ComputerDetailsCollector.CatOs, "Version");
        var build = Get(d, ComputerDetailsCollector.CatOs, "Build");
        var osText = os;
        if (osText != null && ver != null && !osText.Contains(ver, StringComparison.OrdinalIgnoreCase)) osText += " " + ver;
        if (osText != null && build != null) osText += $" (build {build})";
        else if (osText == null && build != null) osText = "Build " + build;
        Add("OS", osText);

        var ips = d.Fields
            .Where(f => f.Category.StartsWith(ComputerDetailsCollector.CatNetwork, StringComparison.Ordinal)
                        && (f.Field == "IP address (static)" || f.Field == "IP address (DHCP)") && !IsUnknown(f.Value))
            .SelectMany(f => f.Value.Split(new[] { ',', ';', ' ' }, StringSplitOptions.RemoveEmptyEntries))
            .Distinct(StringComparer.OrdinalIgnoreCase).ToList();
        if (ips.Count > 0) Add(ips.Count == 1 ? "IP address" : "IP addresses", string.Join(", ", ips));

        Add("Time zone", Get(d, ComputerDetailsCollector.CatTime, "Time zone"));
        Add("Last logged-on user", Get(d, ComputerDetailsCollector.CatSystem, "Last logged-on user")
            ?? Get(d, ComputerDetailsCollector.CatSystem, "Default user name (Winlogon)"));
        Add("Domain / workgroup", Get(d, ComputerDetailsCollector.CatSystem, "Domain / workgroup (Winlogon)")
            ?? Get(d, ComputerDetailsCollector.CatSystem, "Domain (TCP/IP)"));
        return r;
    }

    /// <summary>Fields for the detailed view: report order, minus trivia, unknown values and duplicates.</summary>
    public static IReadOnlyList<DetailField> DetailedFields(SystemDetails d)
    {
        var computer = Get(d, ComputerDetailsCollector.CatSystem, "Computer name");
        var hasDerived = Get(d, ComputerDetailsCollector.CatOs, "Operating system (derived)") != null;
        return d.Fields.Where(f =>
        {
            if (IsUnknown(f.Value) || Hidden.Contains((f.Category, f.Field))) return false;
            if (f.Field == "Interface GUID" && f.Category.StartsWith(ComputerDetailsCollector.CatNetwork, StringComparison.Ordinal)) return false;
            if (hasDerived && f.Category == ComputerDetailsCollector.CatOs && f.Field == "Product name") return false;
            if (computer != null && f.Category == ComputerDetailsCollector.CatSystem && f.Field == "Host name (TCP/IP)"
                && string.Equals(f.Value, computer, StringComparison.OrdinalIgnoreCase)) return false;
            return true;
        }).ToList();
    }
}
