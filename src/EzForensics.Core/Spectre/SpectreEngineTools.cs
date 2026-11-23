using EzForensics.Core.Runtimes;

namespace EzForensics.Core.Spectre;

/// <summary>How a Spectre tool gets into the private distro.</summary>
public enum SpectreInstallKind
{
    /// <summary>Ubuntu packages (one apt-get run for all of them).</summary>
    Apt,
    /// <summary>Spectre itself: python venv under <see cref="SpectreEngineTools.VenvDir"/>, pip install from source. Brings volatility3 along.</summary>
    SpectreVenv,
    /// <summary>pip extra of Spectre (<c>spectre[plaso]</c>) in the same venv.</summary>
    VenvExtra,
    /// <summary>GitHub release of ufrisk/MemProcFS (linux x64 tarball) into <see cref="SpectreEngineTools.MemProcFsDir"/>.</summary>
    MemProcFsRelease,
    /// <summary>Zeek: reuses <see cref="Zeek.ZeekSetup"/> (official OBS repo).</summary>
    Zeek,
}

/// <summary>One external tool Spectre shells out to. <paramref name="Binaries"/> are the names Spectre looks for (module.yaml tools, config.py _which_or).</summary>
/// <param name="Modules">Spectre modules that use it ("all" for Spectre itself); empty for tools only config.py names.</param>
/// <param name="Probe">sh snippet that prints the version (or anything non-empty) when the tool works, nothing when it does not.</param>
/// <param name="PostInstall">sh lines run after the apt install of this tool.</param>
public sealed record SpectreEngineTool(string Id, string Name, string Description, IReadOnlyList<string> Binaries, IReadOnlyList<string> Modules,
    bool Required, SpectreInstallKind Kind, IReadOnlyList<string> Packages, string Probe, string? PostInstall = null)
{
    public string UsedBy => Modules.Count == 0 ? "-" : string.Join(", ", Modules);
    public string Importance => Required ? "Required" : "Optional";
}

/// <summary>Result of the probe for one tool.</summary>
public sealed record SpectreToolStatus(string Id, bool Installed, string? Version)
{
    public string Text => !Installed ? "Not installed" : string.IsNullOrEmpty(Version) ? "Installed" : "Installed, version " + Version;
}

/// <summary>
/// The one list of tools the Spectre engine needs (kept in step with ../Spectre by SpectreEngineToolsDriftTests): what each is, who uses it,
/// how it is installed, and how its version is probed inside the EzForensics distro.
/// </summary>
public static class SpectreEngineTools
{
    public const string VenvDir = "/opt/ezforensics/spectre/venv";
    public const string PipPackage = "spectre-forensic";
    public const string MemProcFsDir = "/opt/memprocfs";
    public const string ZeekExe = "/opt/zeek/bin/zeek";

    private static string[] A(params string[] a) => a;

    /// <summary>Probe for an apt package: every binary on PATH, version from dpkg.</summary>
    private static string AptProbe(string package, params string[] bins) =>
        "if " + string.Join(" && ", bins.Select(b => $"command -v {b} >/dev/null 2>&1")) +
        $"; then dpkg-query -W -f='${{Version}}' {package} 2>/dev/null || echo installed; fi";

    public static IReadOnlyList<SpectreEngineTool> All { get; } = new SpectreEngineTool[]
    {
        new("spectre", "Spectre engine", "The Spectre analysis engine and its Python environment (includes Volatility 3 and PDF/Word report export).",
            A("spectre-mcp"), A("all"), true, SpectreInstallKind.SpectreVenv,
            A("python3", "python3-venv", "libmagic1t64", "libpango-1.0-0", "libpangoft2-1.0-0", "libharfbuzz-subset0", "fonts-dejavu-core"),
            $"{VenvDir}/bin/pip show {PipPackage} 2>/dev/null | sed -n 's/^Version: //p'"),
        new("volatility3", "Volatility 3 (vol)", "Memory image analysis. Installed with the Spectre engine, in its Python environment.",
            A("vol"), A("memory"), true, SpectreInstallKind.SpectreVenv, A(),
            $"[ -x {VenvDir}/bin/vol ] && {VenvDir}/bin/pip show volatility3 2>/dev/null | sed -n 's/^Version: //p'"),
        new("sleuthkit", "The Sleuth Kit (fls, mmls, icat)", "File system and partition listing for disk images.",
            A("fls", "mmls", "icat"), A("disk"), true, SpectreInstallKind.Apt, A("sleuthkit"), AptProbe("sleuthkit", "fls", "mmls", "icat")),
        new("libevtx-utils", "evtxexport (libevtx-utils)", "Exports Windows event logs (.evtx).",
            A("evtxexport"), A("evtx"), true, SpectreInstallKind.Apt, A("libevtx-utils"), AptProbe("libevtx-utils", "evtxexport")),
        new("regripper", "RegRipper (rip.pl)", "Registry hive plugins: Run keys, services, ShimCache, Amcache.",
            A("rip.pl"), A("registry_hive"), true, SpectreInstallKind.Apt, A("regripper"), AptProbe("regripper", "rip.pl"),
            // the package may only ship a "regripper" wrapper or a rip.pl under /usr/share: make rip.pl resolvable on PATH either way
            PostInstall: "if ! command -v rip.pl >/dev/null 2>&1; then R=$(command -v regripper || ls /usr/share/regripper/rip.pl /usr/lib/regripper/rip.pl 2>/dev/null | head -n1); " +
                         "if [ -n \"$R\" ]; then ln -sf \"$R\" /usr/local/bin/rip.pl; fi; fi"),
        new("tcpdump", "tcpdump", "Reads packet captures.",
            A("tcpdump"), A("pcap"), true, SpectreInstallKind.Apt, A("tcpdump"), AptProbe("tcpdump", "tcpdump")),
        new("tshark", "tshark (Wireshark CLI)", "Alternative packet capture engine.",
            A("tshark"), A("pcap"), false, SpectreInstallKind.Apt, A("tshark"), AptProbe("tshark", "tshark")),
        new("zeek", "Zeek", "Packet captures to Zeek logs (the same install as the Zeek entry of the Network group).",
            A("zeek"), A("pcap"), false, SpectreInstallKind.Zeek, A(),
            $"[ -x {ZeekExe} ] && {ZeekExe} --version 2>/dev/null | sed 's/^.*version //'"),
        new("memprocfs", "MemProcFS", "Alternative memory extraction engine (GitHub release, into /opt/memprocfs).",
            A("memprocfs"), A("memory"), false, SpectreInstallKind.MemProcFsRelease, A("libusb-1.0-0", "libfuse2t64", "liblz4-1"),
            $"[ -x {MemProcFsDir}/memprocfs ] && {{ cat {MemProcFsDir}/.ezf-version 2>/dev/null || echo installed; }}"),
        new("plaso", "log2timeline / plaso", "Super timelines. Installed into the Spectre Python environment (spectre[plaso]).",
            A("log2timeline.py", "psort.py"), A(), false, SpectreInstallKind.VenvExtra, A(),
            $"[ -x {VenvDir}/bin/log2timeline.py ] && {VenvDir}/bin/pip show plaso 2>/dev/null | sed -n 's/^Version: //p'"),
    };

    public static SpectreEngineTool? Find(string id) => All.FirstOrDefault(t => t.Id.Equals(id, StringComparison.OrdinalIgnoreCase));

    /// <summary>Every binary name of every tool (what the drift test checks Spectre's declarations against).</summary>
    public static IEnumerable<string> AllBinaries => All.SelectMany(t => t.Binaries);

    /// <summary>One sh script that prints <c>id=&lt;version or MISSING&gt;</c> per tool. Read-only, safe to run any time.</summary>
    public static string ProbeScript()
    {
        var sb = new System.Text.StringBuilder();
        foreach (var t in All)
            sb.Append("v=$( { ").Append(t.Probe).Append("; } 2>/dev/null | head -n1 ); echo \"").Append(t.Id).Append("=${v:-MISSING}\"\n");
        return sb.ToString();
    }

    public static IReadOnlyDictionary<string, SpectreToolStatus> ParseProbe(string? output)
    {
        var map = new Dictionary<string, SpectreToolStatus>(StringComparer.OrdinalIgnoreCase);
        foreach (var raw in (output ?? "").Split('\n'))
        {
            var line = raw.Trim();
            var eq = line.IndexOf('=');
            if (eq <= 0) continue;
            var id = line[..eq];
            if (Find(id) == null) continue;   // other output (wsl notices, warnings)
            var v = line[(eq + 1)..].Trim();
            map[id] = v is "" or "MISSING" ? new(id, false, null) : new(id, true, v == "installed" ? null : v);
        }
        foreach (var t in All) if (!map.ContainsKey(t.Id)) map[t.Id] = new(t.Id, false, null);
        return map;
    }

    /// <summary>Runs the probe in the distro. Throws when WSL cannot run it.</summary>
    public static async Task<IReadOnlyDictionary<string, SpectreToolStatus>> ProbeAsync(CancellationToken ct = default)
    {
        var r = await EzDistro.RunAsync(ProbeScript(), ct, timeout: TimeSpan.FromSeconds(60)).ConfigureAwait(false);
        return ParseProbe(r.Output);
    }
}
