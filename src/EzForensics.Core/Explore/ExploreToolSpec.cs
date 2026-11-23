namespace EzForensics.Core.Explore;

/// <summary>External static analyzers the Explore page wraps (phase 1).</summary>
public enum ExploreTool
{
    Die,
    ExifTool,
    CyberChef,
}

/// <summary>
/// One pinned release of an Explore tool: the official download URL, its SHA-256 and size (computed from the downloaded asset),
/// how the zip is laid out and which file is the entry point.
/// </summary>
/// <param name="Folder">Install folder name under <c>&lt;data&gt;\Tools\Explore\</c>.</param>
/// <param name="Sha256">Upper-case hex SHA-256 of the release asset.</param>
/// <param name="StripPrefix">Leading folder in the zip that is removed on extraction (e.g. <c>die/</c>); empty = none.</param>
/// <param name="EntryRelPath">Entry file relative to the install folder (diec.exe, exiftool.exe, CyberChef's html).</param>
/// <param name="Renames">Zip entry (after <paramref name="StripPrefix"/>) to install name, e.g. <c>exiftool(-k).exe</c> to <c>exiftool.exe</c>.</param>
public sealed record ExploreToolSpec(
    ExploreTool Tool,
    string Name,
    string Folder,
    string Version,
    string Url,
    string Sha256,
    long Size,
    string StripPrefix,
    string EntryRelPath,
    string License,
    IReadOnlyDictionary<string, string>? Renames = null)
{
    public string FileName => $"{Folder}-{Version}.zip";
}

/// <summary>The pinned phase-1 Explore tools.</summary>
public static class ExploreToolReleases
{
    // Pinned 2026-10-30. Sizes and SHA-256 computed from the downloaded assets; they match GitHub's asset digests (DIE, CyberChef)
    // and exiftool.org's checksums-13.60.txt (ExifTool).
    //   Detect It Easy 3.21  (2026-04-21) https://github.com/horsicq/DIE-engine/releases/tag/3.21
    //   ExifTool 13.60                    https://exiftool.org/ (the Windows zip is hosted on SourceForge, which keeps every version)
    //   CyberChef v11.5.0    (2026-09-18) https://github.com/gchq/CyberChef/releases/tag/v11.5.0

    public static readonly ExploreToolSpec Die = new(
        ExploreTool.Die, "Detect It Easy", "die", "3.21",
        "https://github.com/horsicq/DIE-engine/releases/download/3.21/die_win64_portable_3.21_x64.zip",
        "078F2934F267392247F9C7B759A1C2457A48BC2000B25B80C2F129955EE4A3B9", 25_197_854,
        "die/", "diec.exe", "MIT");

    public static readonly ExploreToolSpec ExifTool = new(
        ExploreTool.ExifTool, "ExifTool", "exiftool", "13.60",
        "https://sourceforge.net/projects/exiftool/files/exiftool-13.60_64.zip/download",
        "548B1F4E0B713CD2A26824B34080C35DB4E2D2EDD166468EB38B1D55ADA90C2E", 11_198_064,
        "exiftool-13.60_64/", "exiftool.exe", "Perl Artistic / GPL",
        new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase) { ["exiftool(-k).exe"] = "exiftool.exe" });

    public static readonly ExploreToolSpec CyberChef = new(
        ExploreTool.CyberChef, "CyberChef", "cyberchef", "11.5.0",
        "https://github.com/gchq/CyberChef/releases/download/v11.5.0/CyberChef_8cd426dd4f40f1423912d5fad91b578a86a65112.zip",
        "F6478925D3EAA16EC08626A85F5B85EED10D531A5E18700615FED7ECB024CCCF", 83_125_922,
        "", "CyberChef_v11.5.0.html", "Apache-2.0");

    public static IReadOnlyList<ExploreToolSpec> All { get; } = new[] { Die, ExifTool, CyberChef };

    public static ExploreToolSpec For(ExploreTool tool) => tool switch
    {
        ExploreTool.Die => Die,
        ExploreTool.ExifTool => ExifTool,
        ExploreTool.CyberChef => CyberChef,
        _ => throw new ArgumentOutOfRangeException(nameof(tool)),
    };

    private static readonly string[] OfficialHosts =
    {
        "github.com", "objects.githubusercontent.com", "release-assets.githubusercontent.com",
        "exiftool.org", "sourceforge.net", "downloads.sourceforge.net",
    };

    /// <summary>Only https downloads from GitHub, exiftool.org or SourceForge are accepted.</summary>
    public static bool IsOfficialUrl(string url) =>
        Uri.TryCreate(url, UriKind.Absolute, out var u) && u.Scheme == Uri.UriSchemeHttps &&
        OfficialHosts.Any(h => u.Host.Equals(h, StringComparison.OrdinalIgnoreCase));
}
