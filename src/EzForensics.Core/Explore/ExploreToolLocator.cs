using EzForensics.Core.Settings;

namespace EzForensics.Core.Explore;

/// <summary>State of one Explore tool on disk.</summary>
/// <param name="EntryPath">diec.exe, exiftool.exe or CyberChef's html file (may not exist when not installed).</param>
/// <param name="Version">Installed version from the marker; null when not installed or unknown.</param>
/// <param name="IsCurrent">Installed version equals the pinned one.</param>
public sealed record ExploreToolInstall(ExploreTool Tool, string Dir, string EntryPath, bool IsInstalled, string? Version, bool IsCurrent);

/// <summary>Finds the Explore tools under <c>&lt;data&gt;\Tools\Explore\&lt;tool&gt;\</c> (<c>EZFORENSICS_EXPLORE_TOOLS</c> overrides the root).</summary>
public sealed class ExploreToolLocator
{
    public ExploreToolLocator(string? root = null) => Root = Path.GetFullPath(root ?? ManagedRoot);

    public static string ManagedRoot => AppPaths.Env("EXPLORE_TOOLS") is { } r
        ? Path.GetFullPath(r)
        : Path.Combine(AppPaths.DataRoot, "Tools", "Explore");

    public string Root { get; }

    public string ToolDir(ExploreTool tool) => Path.Combine(Root, ExploreToolReleases.For(tool).Folder);

    public string EntryPath(ExploreTool tool) => Path.Combine(ToolDir(tool), ExploreToolReleases.For(tool).EntryRelPath);

    public bool IsInstalled(ExploreTool tool) => File.Exists(EntryPath(tool));

    public string DiecExe => EntryPath(ExploreTool.Die);
    public string ExifToolExe => EntryPath(ExploreTool.ExifTool);
    public string CyberChefFolder => ToolDir(ExploreTool.CyberChef);
    public string CyberChefHtml => EntryPath(ExploreTool.CyberChef);

    /// <summary>Installed version from the marker, else null.</summary>
    public string? Version(ExploreTool tool) =>
        IsInstalled(tool) ? ExploreToolSetup.ReadMarker(ToolDir(tool))?.Version : null;

    public ExploreToolInstall Get(ExploreTool tool)
    {
        var spec = ExploreToolReleases.For(tool);
        bool installed = IsInstalled(tool);
        var v = installed ? ExploreToolSetup.ReadMarker(ToolDir(tool))?.Version : null;
        return new ExploreToolInstall(tool, ToolDir(tool), EntryPath(tool), installed, v,
            installed && string.Equals(v, spec.Version, StringComparison.OrdinalIgnoreCase));
    }

    public IReadOnlyList<ExploreToolInstall> All() => ExploreToolReleases.All.Select(s => Get(s.Tool)).ToList();
}
