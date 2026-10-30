namespace EzForensics.Core.Catalog;

/// <summary>The cards of the Tools page. Catalog (KAPE-style) categories fold into a few groups; the tools that are not in the
/// catalog (plaso, Zeek, RITA, Volatility) each belong to one group too.</summary>
public enum ToolGroup { Disk, Memory, Network, Timeline, Gui }

public sealed record ToolGroupInfo(ToolGroup Group, string Title, string Description, string Glyph);

public static class ToolGroups
{
    public static IReadOnlyList<ToolGroupInfo> All { get; } = new ToolGroupInfo[]
    {
        new(ToolGroup.Disk, "Disk", "Windows artifacts on disk: event logs, registry, file system, program execution, browser and app databases, autostart entries.", ""),
        new(ToolGroup.Memory, "Memory", "Volatility 3 for memory images.", ""),
        new(ToolGroup.Network, "Network", "Packet captures to Zeek logs, and RITA analysis of those logs (beacons, long connections, DNS tunneling).", ""),
        new(ToolGroup.Timeline, "Timeline", "log2timeline / plaso super timelines.", ""),
    }.Append(new ToolGroupInfo(ToolGroup.Gui, "GUI tools", "Desktop viewers and explorers to open evidence and results interactively.", "")).ToArray();

    public static ToolGroupInfo Info(ToolGroup g) => All.First(i => i.Group == g);

    /// <summary>Group of a catalog tool, from its catalog category. Unknown categories land in Disk.</summary>
    public static ToolGroup OfCatalogCategory(string? category) => (category ?? "").Trim().ToLowerInvariant() switch
    {
        "network" or "networking" => ToolGroup.Network,
        "memory" => ToolGroup.Memory,
        _ => ToolGroup.Disk,
    };

    /// <summary>Gui-kind tools all live in the GUI group; CLI tools fold by catalog category.</summary>
    public static ToolGroup Of(ToolDefinition def) => def.Kind == ToolKind.Gui ? ToolGroup.Gui : OfCatalogCategory(def.Category);
}
