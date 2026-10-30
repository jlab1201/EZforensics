using System.Runtime.InteropServices;
using EzForensics.Core.Memory;
using EzForensics.Core.Plaso;
using EzForensics.Core.Runtimes;
using EzForensics.Core.Settings;
using EzForensics.Core.Sysinternals;
using EzForensics.Core.Tools;
using EzForensics.Core.Rita;
using EzForensics.Core.Zeek;

namespace EzForensics.Core.Setup;

public enum WslStatus { Unknown, NotAvailable, NotEnabled, Enabled }

/// <summary>
/// Facts about this machine that the setup plan depends on. A plain record so the planner is pure and testable; the real
/// values come from <see cref="SystemCheck.RunAsync"/>. Unknown values are null.
/// </summary>
public sealed record SystemCheckResult
{
    public string OsName { get; init; } = "";
    public int OsBuild { get; init; }
    public bool Is64Bit { get; init; } = true;
    /// <summary>Free space on the drive of the data folder (bytes).</summary>
    public long? FreeDiskBytes { get; init; }
    public string DataRoot { get; init; } = "";
    public bool IsAdmin { get; init; }
    public WslStatus Wsl { get; init; }
    /// <summary>WSL distros usable for tools (Docker Desktop's own excluded).</summary>
    public IReadOnlyList<string> WslDistros { get; init; } = Array.Empty<string>();
    /// <summary>An Ubuntu / Debian distro of the user's (the old Zeek target; informational now that the private distro is the default), or null.</summary>
    public string? WslZeekDistro { get; init; }
    /// <summary>EZ Forensics' private WSL distro (<see cref="EzDistro"/>), or null when not checked.</summary>
    public EzDistroInfo? EzDistroState { get; init; }
    public bool EzDistroReady => EzDistroState?.Ready == true;
    public DockerState Docker { get; init; } = DockerState.None;
    /// <summary>.NET 9 runtime installed for the machine (Program Files\dotnet).</summary>
    public bool HasSystemNet9 { get; init; }
    /// <summary>EZ Forensics' private .NET 9 runtime (data folder) is present.</summary>
    public bool HasPrivateNet9 { get; init; }
    public bool HasNet9 => HasSystemNet9 || HasPrivateNet9;
    /// <summary>A Python plaso can use (3.10-3.13, including EZ Forensics' private one).</summary>
    public string? Python { get; init; }
    public bool? InternetReachable { get; init; }
    public string? InternetError { get; init; }

    // What is already set up (so the plan can skip it).
    public IReadOnlyCollection<string> InstalledZips { get; init; } = Array.Empty<string>();
    public bool EzToolsInstalled => InstalledZips.Count > 0;
    public string? PlasoVersion { get; init; }
    public string? ZeekSummary { get; init; }
    /// <summary>Installed volatility3 version (importlib.metadata), or null.</summary>
    public string? VolatilityVersion { get; init; }
    /// <summary>The offline Windows symbol pack was extracted into Volatility's symbols folder.</summary>
    public bool VolatilitySymbolsInstalled { get; init; }
    /// <summary>Installed RITA version in the EZ Forensics distro (RitaLocator.Discover), or null.</summary>
    public string? RitaVersion { get; init; }
    /// <summary>RITA is installed from an older pin / schema (<see cref="RitaVersion"/> set): the setup step re-runs the install.</summary>
    public bool RitaUpdateAvailable { get; init; }

    public bool DiskLow(int neededMB) => FreeDiskBytes is { } f && f < (long)(neededMB + 1024) * 1024 * 1024;
}

/// <summary>Collects a <see cref="SystemCheckResult"/> (blocking probes run off the calling thread).</summary>
public static class SystemCheck
{
    public static readonly string ReachabilityUrl = Tools.ToolUpdater.IndexUrl;

    /// <param name="toolsRoot">Tools folder, to tell which EZ tools are already there.</param>
    /// <param name="locator">A rescanned locator for <paramref name="toolsRoot"/> (null = scan here).</param>
    public static async Task<SystemCheckResult> RunAsync(HttpClient? http, AppSettings settings, IToolLocator? locator = null,
        IRuntimeProbe? probe = null, bool probeEngines = true, CancellationToken ct = default)
    {
        probe ??= SystemRuntimeProbe.Instance;
        var net = http == null ? Task.FromResult<(bool?, string?)>((null, null)) : ProbeInternetAsync(http, ct);
        var local = Task.Run(() => Collect(settings, locator, probe, probeEngines), ct);
        var r = await local.ConfigureAwait(false);
        var (ok, err) = await net.ConfigureAwait(false);
        return r with { InternetReachable = ok, InternetError = err };
    }

    private static SystemCheckResult Collect(AppSettings settings, IToolLocator? locator, IRuntimeProbe probe, bool probeEngines)
    {
        var dataRoot = AppPaths.DataRoot;
        long? free = null;
        try
        {
            var root = Path.GetPathRoot(Path.GetFullPath(dataRoot));
            if (!string.IsNullOrEmpty(root)) free = new DriveInfo(root).AvailableFreeSpace;
        }
        catch (Exception ex) when (ex is IOException or ArgumentException or UnauthorizedAccessException) { }

        var (wsl, distros, zeekDistro) = probeEngines ? ProbeWsl(probe) : (WslStatus.Unknown, Array.Empty<string>(), null);
        EzDistroInfo? ezDistro = null;
        if (probeEngines && wsl == WslStatus.Enabled)
            try { ezDistro = EzDistro.Inspect(probe); } catch (Exception) { }
        var docker = probeEngines ? DockerRuntime.Detect(probe) : DockerState.None;

        IToolLocator loc = locator ?? new ToolLocator();
        if (locator == null) loc.Rescan(settings.ToolsRoot);
        var installedExes = new HashSet<string>(loc.All.Select(t => t.ExeName), StringComparer.OrdinalIgnoreCase);
        var zips = Catalog.ToolCatalog.LoadEmbedded().Where(t => installedExes.Contains(t.Exe)).Select(t => t.Zip).ToList();

        string? plaso = null, python = null, zeek = null, volatility = null, rita = null;
        bool ritaUpdate = false;
        bool volSymbols = false;
        if (probeEngines)
        {
            try
            {
                var pl = new PlasoLocator();
                python = pl.FindSupportedPython()?.Exe;
                plaso = pl.Discover(settings.PlasoPath, includeDocker: docker.EngineRunning).Selected?.Version;
            }
            catch (Exception) { }
            try
            {
                var vl = new VolatilityLocator();
                volatility = vl.Discover(settings.VolatilityPath)?.Version;
                volSymbols = VolatilitySetup.SymbolPackInstalled(vl.SymbolsDir);
            }
            catch (Exception) { }
            if (ezDistro?.Ready == true)
                try { var rd = RitaLocator.Discover(probe); rita = rd.Installed ? rd.RitaVersion ?? "installed" : null; ritaUpdate = rd.UpdateAvailable; } catch (Exception) { }
            try { zeek = new ZeekLocator(probe).Discover(settings.ZeekPath).Selected?.Summary; } catch (Exception) { }
        }

        return new SystemCheckResult
        {
            OsName = OsName(),
            OsBuild = Environment.OSVersion.Version.Build,
            Is64Bit = RuntimeInformation.OSArchitecture is Architecture.X64 or Architecture.Arm64,
            FreeDiskBytes = free,
            DataRoot = dataRoot,
            IsAdmin = ProcessElevation.IsElevated(),
            Wsl = wsl,
            WslDistros = distros,
            WslZeekDistro = zeekDistro,
            EzDistroState = ezDistro,
            Docker = docker,
            HasSystemNet9 = RuntimeProbe.HasSystemNet9(),
            HasPrivateNet9 = RuntimeProbe.HasPrivateNet9(),
            Python = python,
            InstalledZips = zips,
            PlasoVersion = plaso,
            ZeekSummary = zeek,
            VolatilityVersion = volatility,
            VolatilitySymbolsInstalled = volSymbols,
            RitaVersion = rita,
            RitaUpdateAvailable = ritaUpdate,
        };
    }

    private static (WslStatus, IReadOnlyList<string>, string?) ProbeWsl(IRuntimeProbe probe)
    {
        if (!OperatingSystem.IsWindows()) return (WslStatus.NotAvailable, Array.Empty<string>(), null);
        var exe = WslRuntime.FindWslExe(probe);
        if (exe == null) return (WslStatus.NotAvailable, Array.Empty<string>(), null);
        // "wsl --status" succeeds only when the WSL platform is installed and enabled (the inbox wsl.exe stub exits non-zero).
        if (!WslRuntime.IsEnabled(probe, exe)) return (WslStatus.NotEnabled, Array.Empty<string>(), null);
        // The user's own distros are only listed, never started: Zeek / RITA go into the private EZ Forensics distro.
        var distros = WslRuntime.ListDistros(probe, exe);
        return (WslStatus.Enabled, distros.Select(d => d.Name).ToList(), null);
    }

    private static string OsName()
    {
        var v = Environment.OSVersion.Version;
        string name = RuntimeInformation.OSDescription;
        if (OperatingSystem.IsWindows())
        {
            try
            {
                using var k = Microsoft.Win32.Registry.LocalMachine.OpenSubKey(@"SOFTWARE\Microsoft\Windows NT\CurrentVersion");
                var product = k?.GetValue("ProductName") as string;
                var display = k?.GetValue("DisplayVersion") as string;
                if (!string.IsNullOrWhiteSpace(product))
                {
                    // The registry still says "Windows 10" on Windows 11.
                    if (v.Build >= 22000) product = product.Replace("Windows 10", "Windows 11");
                    name = product + (string.IsNullOrWhiteSpace(display) ? "" : " " + display);
                }
            }
            catch (Exception) { }
        }
        return $"{name} (build {v.Build})";
    }

    /// <summary>HEAD request to the EZ tools index (8 s); (true, null), (false, reason).</summary>
    public static async Task<(bool?, string?)> ProbeInternetAsync(HttpClient http, CancellationToken ct = default)
    {
        try
        {
            using var cts = CancellationTokenSource.CreateLinkedTokenSource(ct);
            cts.CancelAfter(TimeSpan.FromSeconds(8));
            using var req = new HttpRequestMessage(HttpMethod.Head, ReachabilityUrl);
            using var resp = await http.SendAsync(req, HttpCompletionOption.ResponseHeadersRead, cts.Token).ConfigureAwait(false);
            return ((int)resp.StatusCode < 500, (int)resp.StatusCode < 500 ? null : $"HTTP {(int)resp.StatusCode}");
        }
        catch (OperationCanceledException) when (!ct.IsCancellationRequested) { return (false, "timed out (check the proxy in Settings)"); }
        catch (HttpRequestException ex) { return (false, ex.Message); }
    }
}
