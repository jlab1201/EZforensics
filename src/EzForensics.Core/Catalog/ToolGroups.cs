namespace EzForensics.Core.Catalog;

/// <summary>The cards of the Tools page. Catalog (KAPE-style) categories fold into a few groups; the tools that are not in the
/// catalog (plaso, Zeek, RITA, Volatility) each belong to one group too.</summary>
public enum ToolGroup { Disk, Memory, Network, Timeline, Explore, Ai, Spectre, Gui }

/// <summary>The top tabs of the Tools page. Each <see cref="ToolGroup"/> belongs to one page.</summary>
public enum ToolPage { Analyze, Explore, Ai, Gui }

public sealed record ToolPageInfo(ToolPage Page, string Title, string Description);

public sealed record ToolGroupInfo(ToolGroup Group, string Title, string Description, string Glyph);

public static class ToolGroups
{
    public static IReadOnlyList<ToolGroupInfo> All { get; } = new ToolGroupInfo[]
    {
        new(ToolGroup.Disk, "Disk", "Windows artifacts on disk: event logs, registry, file system, program execution, browser and app databases, autostart entries.", ""),
        new(ToolGroup.Memory, "Memory", "Volatility 3 for memory images.", ""),
        new(ToolGroup.Network, "Network", "Packet captures to Zeek logs, and RITA analysis of those logs (beacons, long connections, DNS tunneling).", ""),
        new(ToolGroup.Timeline, "Timeline", "log2timeline / plaso super timelines.", ""),
        new(ToolGroup.Explore, "Explore", "File type, metadata and CyberChef tools used by the Explore page.", ""),
        new(ToolGroup.Ai, "AI", "Documents for the assistant (PDF, Markdown, TXT, HTML, Obsidian vaults, .ezkb packs) and local AI setup.", ""),
        new(ToolGroup.Spectre, "Spectre (AI)", "The tools the Spectre analysis engine runs in the EZ Forensics Linux environment: install and update them here.", ""),
    }.Append(new ToolGroupInfo(ToolGroup.Gui, "GUI tools", "Desktop viewers and explorers to open evidence and results interactively.", "")).ToArray();

    public static IReadOnlyList<ToolPageInfo> Pages { get; } = new ToolPageInfo[]
    {
        new(ToolPage.Analyze, "Analyze", "Tools for evidence on disk, memory images, network captures and timelines."),
        new(ToolPage.Explore, "Explore", "File type, metadata and CyberChef tools used by the Explore page."),
        new(ToolPage.Ai, "AI", "Local AI, documents for the assistant, and the tools the Spectre engine runs."),
        new(ToolPage.Gui, "GUI", "Desktop viewers and explorers to open evidence and results interactively."),
    };

    public static ToolPageInfo PageInfo(ToolPage p) => Pages.First(i => i.Page == p);

    /// <summary>The top tab a group is shown on.</summary>
    public static ToolPage PageOf(ToolGroup g) => g switch
    {
        ToolGroup.Explore => ToolPage.Explore,
        ToolGroup.Ai or ToolGroup.Spectre => ToolPage.Ai,
        ToolGroup.Gui => ToolPage.Gui,
        _ => ToolPage.Analyze,
    };

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
