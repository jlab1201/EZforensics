using EzForensics.Core.Catalog;
using EzForensics.Core.Explore;
using EzForensics.Core.Memory;
using EzForensics.Core.Plaso;
using EzForensics.Core.Runtimes;
using EzForensics.Core.Rita;
using EzForensics.Core.Zeek;

namespace EzForensics.Core.Setup;

/// <summary>Ids of everything the setup can install (stable: used by the UI, the future CLI and saved selections).</summary>
public static class SetupIds
{
    /// <summary>Bundles.</summary>
    public const string BundleArtifacts = "artifacts", BundleTimeline = "timeline", BundleNetwork = "network", BundleMemory = "memory", BundleExplore = "explore", BundleAi = "ai";

    // Retired: "ez-timeline-explorer", "ez-gui-viewers" (the GUI viewers are no longer installed by setup; a saved selection that still
    // names them is fine: the planner drops unknown ids).
    public const string EzCli = "ez-cli", EzSync = "ez-sync",
        PersistenceBuiltIn = "persistence-builtin", ComputerDetails = "computer-details", Dotnet9 = "dotnet9",
        Plaso = "plaso", EzDistro = "ez-wsl-distro", Zeek = "zeek", Rita = "rita", Volatility = "volatility3",
        VolatilitySymbols = "volatility3-symbols",
        ExploreDie = "explore-die", ExploreExifTool = "explore-exiftool", ExploreCyberChef = "explore-cyberchef", LocalAi = "local-ai";

    /// <summary>Licence the user must accept in the UI before the item is installed (never accepted automatically).</summary>
}

/// <summary>Preselection. <see cref="Custom"/> = the user's own ticks (the wizard then selects no profile radio).</summary>
public enum SetupProfile { Minimal, Everything, Custom }

/// <summary>One installable thing (shown as a check box under its bundle).</summary>
public sealed class SetupItem
{
    public required string Id { get; init; }
    public required string Title { get; init; }
    public required string Description { get; init; }
    public bool DefaultOn { get; init; }
    /// <summary>Part of EZ Forensics itself: always available, nothing to download.</summary>
    public bool BuiltIn { get; init; }
    /// <summary>Shown greyed out ("coming soon").</summary>
    public bool ComingSoon { get; init; }
    /// <summary>Optional extra: Minimal and a bundle tick never select it; only Everything or the user's own tick does.</summary>
    public bool OptIn { get; init; }
    /// <summary>
    /// Not shown in the wizard and never selected by a profile: it comes in only through <see cref="Requires"/> (the EZ Forensics
    /// WSL distro, added by the planner whenever Zeek or RITA needs it). A saved selection or the CLI may still name it.
    /// </summary>
    public bool Hidden { get; init; }
    /// <summary>Approximate download / disk use in MB (refined at plan time where the real size is known).</summary>
    public int DownloadMB { get; init; }
    public int DiskMB { get; init; }
    /// <summary>Needs the .NET 9 runtime (net9-only EZ builds): selecting it on a machine without .NET 9 also selects <see cref="SetupIds.Dotnet9"/>.</summary>
    public bool RequiresNet9 { get; init; }
    /// <summary>Other items this one needs (selected automatically).</summary>
    public IReadOnlyList<string> Requires { get; init; } = Array.Empty<string>();
    /// <summary>Licence id that must be accepted first.</summary>
    public string? License { get; init; }
    /// <summary>EZ tool zips (as named on the EZ download index) installed by this item.</summary>
    public IReadOnlyList<string> Zips { get; init; } = Array.Empty<string>();

    public bool Selectable => !BuiltIn && !ComingSoon;
}

/// <summary>A group of items with one check box (expandable to the items).</summary>
public sealed class SetupBundle
{
    public required string Id { get; init; }
    public required string Title { get; init; }
    public required string Description { get; init; }
    public bool DefaultOn { get; init; }
    public bool ComingSoon { get; init; }
    public required IReadOnlyList<SetupItem> Items { get; init; }
}

/// <summary>
/// What the setup can install, grouped into bundles. Pure data (no I/O): shared by the WPF wizard, the future
/// <c>ezforensics setup --profile minimal</c> CLI and the tests.
/// </summary>
public sealed class SetupCatalog
{
    public SetupCatalog(IReadOnlyList<SetupBundle> bundles, IEnumerable<string>? net9OnlyZips = null)
    {
        Bundles = bundles;
        Net9OnlyZips = new HashSet<string>(net9OnlyZips ?? Array.Empty<string>(), StringComparer.OrdinalIgnoreCase);
    }

    /// <summary>EZ zips published only as net9 builds (no net4 build on the download index).</summary>
    public IReadOnlySet<string> Net9OnlyZips { get; }

    public bool IsNet9Only(string zip) => Net9OnlyZips.Contains(zip);

    public IReadOnlyList<SetupBundle> Bundles { get; }

    public IEnumerable<SetupItem> AllItems => Bundles.SelectMany(b => b.Items);

    public SetupItem? Find(string id) => AllItems.FirstOrDefault(i => i.Id.Equals(id, StringComparison.OrdinalIgnoreCase));

    public SetupBundle? BundleOf(string itemId) => Bundles.FirstOrDefault(b => b.Items.Any(i => i.Id.Equals(itemId, StringComparison.OrdinalIgnoreCase)));

    /// <summary>Approximate zip sizes on the EZ download index (net9 builds, MB), used until the live sizes are known.</summary>
    public static readonly IReadOnlyDictionary<string, double> EzZipMB = new Dictionary<string, double>(StringComparer.OrdinalIgnoreCase)
    {
        ["AmcacheParser.zip"] = 2.3, ["AppCompatCacheParser.zip"] = 2.3, ["EvtxECmd.zip"] = 3.9, ["JLECmd.zip"] = 3.0, ["LECmd.zip"] = 3.1,
        ["MFTECmd.zip"] = 2.8, ["PECmd.zip"] = 2.3, ["RBCmd.zip"] = 1.7, ["RECmd.zip"] = 4.7, ["RecentFileCacheParser.zip"] = 1.6,
        ["SBECmd.zip"] = 3.2, ["SQLECmd.zip"] = 7.4, ["SrumECmd.zip"] = 2.1, ["SumECmd.zip"] = 1.7, ["WxTCmd.zip"] = 6.1,
        ["iisGeolocate.zip"] = 30.8, ["rla.zip"] = 2.2,
    };

    /// <summary>Approximate download size of the .NET 9 Desktop Runtime zip (x64) and its size on disk.</summary>
    public const int Dotnet9DownloadMB = 120, Dotnet9DiskMB = 340;
    /// <summary>EvtxECmd / RECmd / SQLECmd --sync (maps and batch files from GitHub).</summary>
    public const int SyncDownloadMB = 8, SyncDiskMB = 20;

    public static int ZipsMB(IEnumerable<string> zips) => (int)Math.Ceiling(zips.Sum(z => EzZipMB.TryGetValue(z, out var mb) ? mb : 3.0));

    private static SetupCatalog? _default;

    /// <summary>The catalog built from the embedded EZ tool catalog (CLI tools only; the GUI viewers are not part of setup) plus the other engines.</summary>
    public static SetupCatalog Default => _default ??= Build(ToolCatalog.LoadEmbedded());

    public static SetupCatalog Build(IReadOnlyList<ToolDefinition> tools)
    {
        var setupTools = tools.Where(t => t.Kind == ToolKind.Cli).ToList();
        var cli = setupTools.Select(t => t.Zip).Distinct(StringComparer.OrdinalIgnoreCase).OrderBy(z => z, StringComparer.OrdinalIgnoreCase).ToList();
        var (plasoDl, plasoDisk) = PlasoSetup.Estimate(needsPython: true);
        var (volDl, volDisk) = VolatilitySetup.Estimate(needsPython: true);
        var net9Only = setupTools.Where(t => t.Builds.Count > 0 && !t.Builds.Contains("net4", StringComparer.OrdinalIgnoreCase)).Select(t => t.Zip);

        return new SetupCatalog(new[]
        {
            new SetupBundle
            {
                Id = SetupIds.BundleArtifacts, Title = "Windows artifacts", DefaultOn = true,
                Description = "Parse a Windows system: event logs, registry, $MFT, prefetch, LNK, jump lists, SRUM, browser data and more, with Eric Zimmerman's tools. Includes the built-in persistence collector, and computer details.",
                Items = new[]
                {
                    new SetupItem
                    {
                        Id = SetupIds.EzCli, Title = "EZ command-line parsers", DefaultOn = true, Zips = cli,
                        Description = $"{cli.Count} tools (MFTECmd, EvtxECmd, RECmd, PECmd, LECmd, JLECmd, SrumECmd, AmcacheParser, ...) from download.ericzimmermanstools.com. Without .NET 9 the net4 builds are used (SQLECmd and WxTCmd exist only as net9 builds).",
                        DownloadMB = ZipsMB(cli), DiskMB = (int)(ZipsMB(cli) * 2.6),
                    },
                    new SetupItem
                    {
                        Id = SetupIds.EzSync, Title = "Maps and batch files (EvtxECmd, RECmd, SQLECmd --sync)", DefaultOn = true, Requires = new[] { SetupIds.EzCli },
                        Description = "Downloads the latest event log maps, RECmd batch files and SQLECmd maps from GitHub, so new artifacts are parsed.",
                        DownloadMB = SyncDownloadMB, DiskMB = SyncDiskMB,
                    },
                    new SetupItem
                    {
                        // Default on: Minimal selects it when the machine has no .NET 9 (with .NET 9 it is "already installed").
                        Id = SetupIds.Dotnet9, Title = "Microsoft .NET 9 and 10 Desktop Runtimes (private copy)", DefaultOn = true,
                        Description = "Only needed for the net9 EZ builds, which need .NET 9 and 10 (SQLECmd and WxTCmd exist only as net9 builds). Installed per user into EZ Forensics' data folder with Microsoft's official dotnet-install script: no admin rights, nothing else on the system changes. Without it, the net4 builds are used (they need only the .NET Framework that ships with Windows).",
                        DownloadMB = Dotnet9DownloadMB, DiskMB = Dotnet9DiskMB,
                    },
                    new SetupItem
                    {
                        Id = SetupIds.PersistenceBuiltIn, Title = "Persistence collector (built in)", BuiltIn = true, DefaultOn = true,
                        Description = "Reads autostart entries (Run keys, services, scheduled tasks, WMI, startup folders...) offline from the evidence. Part of EZ Forensics: nothing to install.",
                    },
                    new SetupItem
                    {
                        Id = SetupIds.ComputerDetails, Title = "Computer details (built in)", BuiltIn = true, DefaultOn = true,
                        Description = "Computer name, OS, time zone, IP addresses, disks and users from the evidence's registry. Part of EZ Forensics: nothing to install.",
                    },
                },
            },
            new SetupBundle
            {
                Id = SetupIds.BundleTimeline, Title = "Super timeline", DefaultOn = true,
                Description = "log2timeline / plaso builds one timeline from the raw evidence (disk images, folders).",
                Items = new[]
                {
                    new SetupItem
                    {
                        Id = SetupIds.Plaso, Title = "log2timeline / plaso", DefaultOn = true,
                        Description = $"Installed with pip into EZ Forensics' own Python environment. If no Python 3.10-3.13 is found, a private Python {PrivatePython.Default.Version} (python.org's official NuGet package, SHA-512 checked) is downloaded first. No admin rights, no PATH changes.",
                        DownloadMB = plasoDl, DiskMB = plasoDisk,
                    },
                },
            },
            new SetupBundle
            {
                Id = SetupIds.BundleNetwork, Title = "Network",
                Description = "Zeek turns packet captures (pcap / pcapng) into connection, DNS, HTTP and TLS logs. Zeek has no Windows build, so Zeek and RITA run in EZ Forensics' own Linux environment in WSL (added automatically).",
                Items = new[]
                {
                    new SetupItem
                    {
                        Id = SetupIds.EzDistro, Title = "EZ Forensics Linux environment (WSL)", DefaultOn = true, Hidden = true,
                        Description = $"A private, minimal WSL distro named {EzDistro.Name} ({EzDistro.Rootfs.Display}, official image from cdimage.ubuntu.com, SHA-256 checked), created per user with 'wsl --import' (no admin rights). Zeek and RITA run in it; your own WSL distros are never touched. Needs WSL: if it is not enabled, setup enables it once (administrator prompt + restart).",
                        DownloadMB = EzDistro.DownloadMB, DiskMB = EzDistro.DiskMB,
                    },
                    new SetupItem
                    {
                        Id = SetupIds.Zeek, Title = "Zeek", DefaultOn = true, Requires = new[] { SetupIds.EzDistro },
                        Description = "Official Zeek packages (openSUSE Build Service), installed in EZ Forensics' own Linux environment in WSL.",
                        DownloadMB = ZeekSetup.WslDownloadMB, DiskMB = ZeekSetup.WslDiskMB,
                    },
                    new SetupItem
                    {
                        Id = SetupIds.Rita, Title = "RITA (beacon detection)", Requires = new[] { SetupIds.EzDistro, SetupIds.Zeek },
                        Description = $"RITA {RitaRelease.Version} finds beaconing and long connections in Zeek logs. Licence: GPL-3.0. Downloaded from upstream at setup (the official ghcr.io image layer, plus ClickHouse {RitaRelease.ClickHouseVersion} from GitHub) and installed in the EZ Forensics Linux environment; nothing is bundled with EZ Forensics. Included in the Everything profile; tick it yourself for the Minimal one.",
                        DownloadMB = RitaRelease.DownloadMB, DiskMB = RitaRelease.DiskMB,
                    },
                },
            },
            new SetupBundle
            {
                Id = SetupIds.BundleMemory, Title = "Memory",
                Description = "Volatility 3 analyses memory images (crash dumps, hibernation files, raw and VM memory) natively on Windows, with no WSL or Docker.",
                Items = new[]
                {
                    new SetupItem
                    {
                        Id = SetupIds.Volatility, Title = "Volatility 3", DefaultOn = true,
                        Description = $"volatility3 {VolatilitySetup.PinnedVersion} installed with pip into its own environment, built on the same private Python as plaso (downloaded once if no Python 3.10-3.13 is found). No admin rights, no PATH changes. Windows symbols are fetched automatically when online.",
                        DownloadMB = volDl, DiskMB = volDisk,
                    },
                    new SetupItem
                    {
                        Id = SetupIds.VolatilitySymbols, Title = "Windows symbol pack (offline)", OptIn = true, Requires = new[] { SetupIds.Volatility },
                        Description = $"Official windows.zip from downloads.volatilityfoundation.org (≈{VolatilitySetup.SymbolPackDownloadMB} MB, ≈{SetupPlan.FormatMB(VolatilitySetup.SymbolPackDiskMB)} on disk) so memory images can be analysed without an internet connection. Optional: online runs fetch symbols on demand.",
                        DownloadMB = VolatilitySetup.SymbolPackDownloadMB, DiskMB = VolatilitySetup.SymbolPackDiskMB,
                    },
                },
            },
            new SetupBundle
            {
                Id = SetupIds.BundleExplore, Title = "File analysis (Explore)", DefaultOn = true,
                Description = "Small per-user downloads for the Explore page: file type and packer detection, file metadata and CyberChef. No admin rights, nothing is run on your files.",
                Items = new[]
                {
                    ExploreItem(SetupIds.ExploreDie, ExploreToolReleases.Die, "Detect It Easy (DIE)", "Identifies file types, packers and compilers (MIT)."),
                    ExploreItem(SetupIds.ExploreExifTool, ExploreToolReleases.ExifTool, "ExifTool", "Reads metadata from almost any file type (Perl Artistic License / GPL)."),
                    ExploreItem(SetupIds.ExploreCyberChef, ExploreToolReleases.CyberChef, "CyberChef", "Decodes and transforms data; runs offline inside EZ Forensics (Apache-2.0)."),
                },
            },
            new SetupBundle
            {
                Id = SetupIds.BundleAi, Title = "AI assistant (Spectre)", DefaultOn = true,
                Description = "Spectre can run on your own PC with Ollama, so nothing leaves the computer. Everything always ticks it (a slower PC gets a smaller model); Minimal ticks it only when your PC is up to it. You can also set up a cloud AI later in Options, AI.",
                Items = new[]
                {
                    new SetupItem
                    {
                        Id = SetupIds.LocalAi, Title = "Local AI (Ollama + model)", DefaultOn = false,
                        Description = "Installs Ollama (official per-user installer from ollama.com, no admin) and downloads a model that suits this PC, then points Spectre at it. Ollama is MIT licensed; the Qwen3 models are Apache-2.0.",
                        DownloadMB = 3800, DiskMB = 7000,
                    },
                },
            },
        }, net9Only);
    }

    private static SetupItem ExploreItem(string id, ExploreToolSpec spec, string title, string what) => new()
    {
        Id = id, Title = title, DefaultOn = true,
        Description = $"{what} {spec.Name} {spec.Version}, downloaded from upstream and checked against a pinned SHA-256.",
        DownloadMB = (int)Math.Max(1, spec.Size / (1024 * 1024)), DiskMB = (int)Math.Max(1, spec.Size * 3 / (1024 * 1024)),
    };
}
