using System.Text.RegularExpressions;
using EzForensics.Core.Runtimes;

namespace EzForensics.Core.Zeek;

/// <summary>
/// Finds Zeek. Order: the configured zeek.exe, zeek.exe on PATH / in Program Files (native), EZ Forensics' private WSL distro
/// (<see cref="EzDistro.Name"/>), the user's own WSL distros (<c>/opt/zeek/bin/zeek</c> from the official packages, or
/// <c>zeek</c> on the distro's PATH), then the Docker image <c>zeek/zeek:lts</c> when Docker is running. The first candidate
/// whose <c>zeek --version</c> answers wins.
/// When nothing is found it also works out what one click on Install should do (<see cref="ZeekSetupOption"/>).
/// </summary>
public sealed class ZeekLocator
{
    /// <summary>Official image, LTS tag (the run manifest records the exact digest).</summary>
    public const string DockerImage = "zeek/zeek:lts";
    public const string WslZeekPath = "/opt/zeek/bin/zeek";

    private static readonly Regex VersionRx = new(@"version\s+(?<v>\d+\.\d+(\.\d+)?[0-9A-Za-z.\-]*)", RegexOptions.IgnoreCase | RegexOptions.Compiled);

    private readonly IRuntimeProbe _probe;
    private readonly TimeSpan _versionTimeout;

    public ZeekLocator(IRuntimeProbe? probe = null, TimeSpan? versionTimeout = null)
    {
        _probe = probe ?? SystemRuntimeProbe.Instance;
        _versionTimeout = versionTimeout ?? TimeSpan.FromSeconds(60);
    }

    /// <summary><c>%LOCALAPPDATA%\EzForensicsData\zeek</c> (setup state); <c>EZFORENSICS_ZEEK_ROOT</c> (or <c>EZHUB_ZEEK_ROOT</c>) overrides it.
    /// EZ Hub's <c>%LOCALAPPDATA%\EzHub\zeek\state.json</c> keeps being used while it exists (so Uninstall still knows what was set up).</summary>
    public static string ManagedRoot => EzForensics.Core.Settings.AppPaths.ComponentRoot("zeek", "ZEEK_ROOT",
        legacy => File.Exists(Path.Combine(legacy, "state.json")));

    /// <summary>Default native install folders checked after PATH.</summary>
    public static IReadOnlyList<string> NativeFolders => new[]
    {
        Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles), "Zeek", "bin"),
        Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles), "zeek", "bin"),
        @"C:\zeek\bin",
    };

    /// <summary>Runs discovery (blocking: starts --version processes). Use from a background thread.</summary>
    public ZeekDiscovery Discover(string? configuredPath, bool includeDocker = true, bool includeWsl = true)
    {
        var notes = new List<string>();

        foreach (var c in NativeCandidates(configuredPath, notes))
        {
            var v = ProbeVersion(c);
            if (v != null) return Finish(c with { Version = v }, notes, DockerState.None, null, null, Array.Empty<WslDistro>());
            notes.Add($"{c.Exe}: 'zeek --version' did not answer; skipped.");
        }

        // WSL: EZ Forensics' private distro first (started even when stopped: it is ours), then the user's own distros.
        string? wsl = null;
        IReadOnlyList<WslDistro> distros = Array.Empty<WslDistro>();
        bool wslEnabled = false;
        if (includeWsl)
        {
            wsl = WslRuntime.FindWslExe(_probe);
            if (wsl != null)
            {
                distros = WslRuntime.ListDistros(_probe, wsl);
                wslEnabled = distros.Count > 0 || WslRuntime.IsEnabled(_probe, wsl);
                foreach (var d in WslProbeOrder(distros))
                {
                    var c = ProbeWsl(wsl, d.Name);
                    // Docker state is still reported (the Tools card shows it; Uninstall may need it for an image pulled earlier).
                    if (c != null) return Finish(c, notes, includeDocker ? DockerRuntime.Detect(_probe) : DockerState.None, null, wsl, distros);
                }
                if (!wslEnabled) notes.Add("WSL is not enabled.");
                else if (!distros.Any(IsEzDistro)) notes.Add($"The EZ Forensics WSL distro ({EzDistro.Name}) is not installed yet.");
                else notes.Add($"No Zeek in the WSL distro(s): {string.Join(", ", distros.Select(d => d.Name))}.");
            }
            else notes.Add("WSL is not installed.");
        }

        var docker = includeDocker ? DockerRuntime.Detect(_probe) : DockerState.None;
        DockerImageInfo? image = null;
        if (includeDocker)
        {
            if (docker.Cli == null) notes.Add(docker.DesktopInstalled ? "Docker Desktop is installed but docker.exe was not found." : "Docker is not installed.");
            else if (!docker.EngineRunning) notes.Add("Docker CLI found but the Docker engine is not running (start Docker Desktop).");
            else
            {
                image = DockerRuntime.InspectImage(_probe, docker.Cli, DockerImage);
                if (image != null)
                {
                    var c = new ZeekInstall(ZeekEngine.Docker, ZeekOrigin.Docker, docker.Cli, "zeek", null, DockerImage, true, DockerImage, image);
                    var v = ProbeVersion(c);
                    if (v != null) return Finish(c with { Version = v }, notes, docker, image, wsl, distros);
                    notes.Add($"Docker image {DockerImage} did not answer to 'zeek --version'.");
                }
                else notes.Add($"Docker is running but the {DockerImage} image is not pulled yet.");
            }
        }

        // Nothing found: decide what one click on Install does. WSL enabled = the private distro (default, free, no admin).
        ZeekSetupOption setup = wslEnabled ? ZeekSetupOption.EzDistroApt
            : docker.EngineRunning ? ZeekSetupOption.DockerPull
            : docker.DesktopInstalled && docker.Cli != null ? ZeekSetupOption.StartDockerThenPull
            : ZeekSetupOption.Guidance;
        return new ZeekDiscovery(null, notes, docker, image, wsl, distros, setup == ZeekSetupOption.EzDistroApt ? EzDistro.Name : null, null, setup);
    }

    public static bool IsEzDistro(WslDistro d) => d.Name.Equals(EzDistro.Name, StringComparison.OrdinalIgnoreCase);

    /// <summary>Distros probed for Zeek, in preference order: the private EZ Forensics distro, then the user's (<see cref="ProbeOrder"/>).</summary>
    public static IEnumerable<WslDistro> WslProbeOrder(IReadOnlyList<WslDistro> distros) =>
        distros.Where(IsEzDistro).Concat(ProbeOrder(distros.Where(d => !IsEzDistro(d)).ToList()));

    private static ZeekDiscovery Finish(ZeekInstall c, List<string> notes, DockerState docker, DockerImageInfo? image, string? wsl, IReadOnlyList<WslDistro> distros) =>
        new(c, notes, docker, image, wsl, distros, c.WslDistro, null, ZeekSetupOption.None);

    /// <summary>Distros worth starting a process in: running ones and the default; all of them when none is running.</summary>
    public static IEnumerable<WslDistro> ProbeOrder(IReadOnlyList<WslDistro> distros)
    {
        var usable = OrderDistros(distros).ToList();
        var live = usable.Where(d => d.IsRunning || d.IsDefault).ToList();
        return live.Count > 0 ? live : usable;
    }

    /// <summary>Running distros first, then the default one, then by name.</summary>
    public static IEnumerable<WslDistro> OrderDistros(IEnumerable<WslDistro> distros) =>
        distros.Where(d => !d.IsDockerInternal).OrderByDescending(d => d.IsRunning).ThenByDescending(d => d.IsDefault).ThenBy(d => d.Name, StringComparer.OrdinalIgnoreCase);

    /// <summary>Native candidates in discovery order (no processes started).</summary>
    public IReadOnlyList<ZeekInstall> NativeCandidates(string? configuredPath, List<string>? notes = null)
    {
        var list = new List<ZeekInstall>();
        if (!string.IsNullOrWhiteSpace(configuredPath))
        {
            var p = configuredPath.Trim().Trim('"');
            string? exe = _probe.FileExists(p) ? p
                : _probe.FileExists(Path.Combine(p, "zeek.exe")) ? Path.Combine(p, "zeek.exe")
                : _probe.FileExists(Path.Combine(p, "bin", "zeek.exe")) ? Path.Combine(p, "bin", "zeek.exe") : null;
            if (exe != null) list.Add(Native(exe, ZeekOrigin.Configured));
            else notes?.Add($"Configured Zeek path has no zeek.exe: {configuredPath}");
        }
        var onPath = _probe.FindOnPath("zeek.exe");
        if (onPath != null && !list.Any(c => c.Exe.Equals(onPath, StringComparison.OrdinalIgnoreCase))) list.Add(Native(onPath, ZeekOrigin.Path));
        foreach (var dir in NativeFolders)
        {
            var exe = Path.Combine(dir, "zeek.exe");
            if (_probe.FileExists(exe) && !list.Any(c => c.Exe.Equals(exe, StringComparison.OrdinalIgnoreCase))) list.Add(Native(exe, ZeekOrigin.ProgramFiles));
        }
        return list;
    }

    private ZeekInstall Native(string exe, ZeekOrigin origin)
    {
        var bin = Path.GetDirectoryName(exe) ?? "";
        var prefix = Path.GetDirectoryName(bin) ?? bin;
        bool local = _probe.FileExists(Path.Combine(prefix, "share", "zeek", "site", "local.zeek"));
        return new ZeekInstall(ZeekEngine.Native, origin, exe, exe, null, bin, local);
    }

    /// <summary>Shell snippet run in a distro: prints ZEEK=&lt;path&gt;, the version and LOCAL=1; exit 3 when there is no Zeek.</summary>
    public const string WslProbeScript =
        "z=" + WslZeekPath + "; [ -x \"$z\" ] || z=$(command -v zeek 2>/dev/null); [ -n \"$z\" ] || exit 3; " +
        "echo \"ZEEK=$z\"; \"$z\" --version || exit 4; p=$(dirname \"$(dirname \"$z\")\"); " +
        "if [ -f \"$p/share/zeek/site/local.zeek\" ]; then echo LOCAL=1; fi; true";

    /// <summary>Zeek inside one distro, or null.</summary>
    public ZeekInstall? ProbeWsl(string wslExe, string distro)
    {
        var o = _probe.Run(wslExe, WslRuntime.Args(distro, new[] { "sh", "-c", WslProbeScript }), _versionTimeout, WslRuntime.Utf8Env);
        return ParseWslProbe(o, wslExe, distro);
    }

    public static ZeekInstall? ParseWslProbe(string? output, string wslExe, string distro)
    {
        var text = RuntimeText.Clean(output);
        var zeek = text.Split('\n').Select(l => l.Trim()).FirstOrDefault(l => l.StartsWith("ZEEK=", StringComparison.Ordinal))?[5..];
        if (string.IsNullOrEmpty(zeek)) return null;
        var v = ParseVersion(text);
        bool local = text.Split('\n').Any(l => l.Trim() == "LOCAL=1");
        return new ZeekInstall(ZeekEngine.Wsl, ZeekOrigin.Wsl, wslExe, zeek, v ?? "unknown", $"WSL {distro}: {zeek}", local, WslDistro: distro);
    }

    /// <summary><c>zeek --version</c> for a native or Docker candidate; the parsed version ("unknown" when unparsable), or null.</summary>
    public string? ProbeVersion(ZeekInstall c)
    {
        IReadOnlyList<string> args = c.Engine switch
        {
            ZeekEngine.Docker => new[] { "run", "--rm", "--network", "none", c.DockerImage ?? DockerImage, "zeek", "--version" },
            ZeekEngine.Wsl => WslRuntime.Args(c.WslDistro!, new[] { c.ZeekPath, "--version" }),
            _ => new[] { "--version" },
        };
        var o = _probe.Run(c.Exe, args, _versionTimeout, c.Engine == ZeekEngine.Wsl ? WslRuntime.Utf8Env : null);
        return o == null ? null : ParseVersion(o) ?? "unknown";
    }

    /// <summary>"zeek version 9.0.0" -> "9.0.0".</summary>
    public static string? ParseVersion(string? output)
    {
        if (string.IsNullOrWhiteSpace(output)) return null;
        var m = VersionRx.Match(output);
        return m.Success ? m.Groups["v"].Value.TrimEnd('.') : null;
    }
}
