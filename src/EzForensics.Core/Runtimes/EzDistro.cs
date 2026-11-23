using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Security.Cryptography;
using System.Text;
using EzForensics.Core.Execution;
using EzForensics.Core.Settings;

namespace EzForensics.Core.Runtimes;

/// <summary>A pinned root file system for the private distro (official Ubuntu Base tarball, SHA-256 from Canonical's SHA256SUMS).</summary>
public sealed record EzRootfsPin(string Os, string Version, string Architecture, string Url, string Sha256, long Bytes)
{
    public string FileName => Path.GetFileName(new Uri(Url).AbsolutePath);
    public string Display => $"{Os} {Version} ({Architecture})";
}

/// <summary>One maintenance process run as a JobQueue job (logged, in the Jobs list on Ingest). Generic counterpart of ZeekSetupStep.</summary>
public sealed record RuntimeStep(string ToolId, string Exe, IReadOnlyList<string> Args, string Label, TimeSpan Timeout,
    IReadOnlyDictionary<string, string>? Environment = null)
{
    public JobSpec ToJobSpec(string outputDir) =>
        new(ToolId, Path.GetFileName(Exe), Args, outputDir, Label, "Maintenance", ExePath: Exe, Timeout: Timeout, Environment: Environment);

    public string Display => RuntimeText.Display(Exe, Args);
}

/// <summary>State of the private distro (<see cref="EzDistro.Inspect"/>).</summary>
/// <param name="WslAvailable">wsl.exe exists and <c>wsl --status</c> answers (WSL enabled).</param>
/// <param name="Exists">Registered (<c>wsl -l -v</c> lists it).</param>
/// <param name="Provisioned">First-boot configuration finished (marker file inside the distro); null when not checked.</param>
/// <param name="BaseVersion">Content of the marker: "ubuntu-base 24.04.5 / schema 1".</param>
/// <param name="DiskBytes">Size of its virtual disk (ext4.vhdx), or null.</param>
public sealed record EzDistroInfo(bool WslAvailable, bool Exists, int WslVersion, string? State, bool? Provisioned, string? BaseVersion,
    string InstallDir, long? DiskBytes, string? WslExe)
{
    public bool Ready => Exists && Provisioned == true;
    public static readonly EzDistroInfo None = new(false, false, 0, null, null, null, EzDistro.InstallDir, null, null);

    public string Summary => !WslAvailable ? "WSL is not enabled"
        : !Exists ? "Not installed"
        : Provisioned == false ? "Installed, first-boot setup not finished"
        : $"Installed ({BaseVersion ?? "Ubuntu"}{(WslVersion is 1 or 2 ? $", WSL {WslVersion}" : "")}{(DiskBytes is { } b ? ", " + EzDistro.FormatBytes(b) : "")})";
}

/// <summary>Result of one <c>wsl --import</c> attempt (<see cref="EzDistro.ImportWithFallbackAsync"/>).</summary>
public sealed record WslImportAttempt(bool Ok, string Detail, bool Cancelled = false);

/// <summary>Outcome of the import: the WSL version the distro was created with (0 on failure) and a plain message.</summary>
public sealed record WslImportOutcome(bool Ok, int WslVersion, string Message);

/// <summary>Output of <see cref="EzDistro.RunAsync"/>.</summary>
public sealed record WslRunResult(int ExitCode, string Output, bool Cancelled)
{
    public bool Ok => ExitCode == 0 && !Cancelled;
}

/// <summary>
/// The private EZ Forensics WSL distro (<see cref="Name"/> = "EzForensics"): a minimal Ubuntu Base 24.04 root file system,
/// pinned by URL + SHA-256, imported per user with <c>wsl --import</c> (no admin rights) into
/// <c>%LOCALAPPDATA%\EzForensicsData\wsl\EzForensics</c>. Zeek (and RITA later) are installed inside it, so the user's own
/// distros are never touched. Enabling WSL itself (admin + restart) stays the setup's <c>EnableWsl</c> step.
/// Everything here only ever names <see cref="Name"/>: it never runs, changes or unregisters another distro.
/// </summary>
public static class EzDistro
{
    public const string Name = "EzForensics";
    /// <summary>Bump when <see cref="ProvisionScript"/> changes in a way that must re-run on existing installs.</summary>
    public const int SchemaVersion = 1;
    public const string MarkerPath = "/etc/ezforensics/provisioned";
    /// <summary>Packages added on first boot: TLS + downloads (curl, gnupg, CA certificates) and procps (pkill, used to cancel jobs).</summary>
    public static readonly IReadOnlyList<string> BasePackages = new[] { "ca-certificates", "curl", "gnupg", "procps" };

    /// <summary>Ubuntu Base 24.04.5 (cdimage.ubuntu.com keeps every point release under its own folder).</summary>
    public static readonly EzRootfsPin UbuntuBaseAmd64 = new("Ubuntu Base", "24.04.5", "amd64",
        "https://cdimage.ubuntu.com/ubuntu-base/releases/24.04.5/release/ubuntu-base-24.04.5-base-amd64.tar.gz",
        "e77b6f10c2590cef872b33ee9f635a0e3fd1f57fb074c0e52b5c7f56147a0c86", 30_028_293);

    public static readonly EzRootfsPin UbuntuBaseArm64 = new("Ubuntu Base", "24.04.5", "arm64",
        "https://cdimage.ubuntu.com/ubuntu-base/releases/24.04.5/release/ubuntu-base-24.04.5-base-arm64.tar.gz",
        "a91d5a93010193712d346d761372b7c9db6dfcf093893161c64ca107f05914f2", 0);

    public static EzRootfsPin Rootfs => RuntimeInformation.OSArchitecture == Architecture.Arm64 ? UbuntuBaseArm64 : UbuntuBaseAmd64;

    /// <summary>Measured: 30 MB rootfs + 35 MB apt lists + 8 MB base packages; the ext4.vhdx is ≈200 MB after first boot (before Zeek).</summary>
    public const int DownloadMB = 75, DiskMB = 250;

    public const string ToolIdImport = "ezdistro-import", ToolIdProvision = "ezdistro-provision", ToolIdRemove = "ezdistro-unregister";

    /// <summary><c>&lt;DataRoot&gt;\wsl\EzForensics</c> (the distro's ext4.vhdx); <c>EZFORENSICS_WSL_ROOT</c> overrides the <c>wsl</c> folder.</summary>
    public static string WslRoot => AppPaths.Env("WSL_ROOT") is { Length: > 0 } r ? r : Path.Combine(AppPaths.DataRoot, "wsl");
    public static string InstallDir => Path.Combine(WslRoot, Name);
    public static string DownloadDir => Path.Combine(WslRoot, "downloads");
    public static string VhdxPath(string? installDir = null) => Path.Combine(installDir ?? InstallDir, "ext4.vhdx");

    private static readonly TimeSpan ProbeTimeout = TimeSpan.FromSeconds(60);

    // ------------------------------------------------------------------ command lines (pure)

    /// <summary>
    /// <c>wsl --import EzForensics &lt;dir&gt; &lt;rootfs&gt; --version 2</c> (per user, no admin). <paramref name="wslVersion"/> 1 is the
    /// fallback when WSL 2 cannot run (no hardware virtualization, e.g. a VM without nested virtualization).
    /// </summary>
    public static IReadOnlyList<string> ImportArgs(string installDir, string rootfsTar, int wslVersion = 2) =>
        new[] { "--import", Name, installDir, rootfsTar, "--version", wslVersion == 1 ? "1" : "2" };

    public static IReadOnlyList<string> UnregisterArgs() => new[] { "--unregister", Name };
    public static IReadOnlyList<string> TerminateArgs() => new[] { "--terminate", Name };

    /// <summary><c>-d EzForensics -u root -e sh -c &lt;script&gt;</c>, the script with LF line ends only.</summary>
    public static IReadOnlyList<string> ScriptArgs(string script) => WslRuntime.Args(Name, new[] { "sh", "-c", Lf(script) }, asRoot: true);

    /// <summary>Scripts run under sh: never let a CRLF source file leak CR characters into them.</summary>
    public static string Lf(string s) => s.Replace("\r\n", "\n").Replace('\r', '\n');

    /// <summary>
    /// /etc/wsl.conf: root is the default user (only root exists in Ubuntu Base), no systemd (nothing runs in the background),
    /// Windows drives under /mnt/ (captures and case folders are read / written there), generated hosts / resolv.conf (DNS
    /// follows Windows), interop off and no Windows PATH (nothing inside calls Windows programs).
    /// </summary>
    public static readonly string WslConf = Lf("""
        # Written by EZ Forensics. Changes are overwritten when EZ Forensics re-provisions this distro.
        [boot]
        systemd=false

        [user]
        default=root

        [automount]
        enabled=true
        root=/mnt/
        mountFsTab=false

        [network]
        generateHosts=true
        generateResolvConf=true
        hostname=ezforensics

        [interop]
        enabled=false
        appendWindowsPath=false
        """);

    /// <summary>First-boot script (root): wsl.conf, apt update, base packages, marker. Idempotent (safe to re-run after a failure).</summary>
    public static string ProvisionScript => Lf($$"""
        set -e
        export DEBIAN_FRONTEND=noninteractive
        mkdir -p /etc/ezforensics
        cat > /etc/wsl.conf <<'EZWSLCONF'
        {{WslConf.TrimEnd('\n')}}
        EZWSLCONF
        . /etc/os-release
        echo "EZFORENSICS: provisioning $PRETTY_NAME"
        apt-get update
        apt-get install -y --no-install-recommends {{string.Join(" ", BasePackages)}}
        apt-get clean
        rm -rf /var/lib/apt/lists/*
        echo "{{Rootfs.Os.ToLowerInvariant().Replace(' ', '-')}} {{Rootfs.Version}} / schema {{SchemaVersion}}" > {{MarkerPath}}
        echo "EZFORENSICS: ready"
        """);

    /// <summary>Prints EZDISTRO=&lt;marker&gt; when provisioned; exit 0 either way (exit 0 + no line = not provisioned).</summary>
    public static readonly string ProbeScript = $"if [ -f {MarkerPath} ]; then echo \"EZDISTRO=$(cat {MarkerPath})\"; fi; true";

    public static RuntimeStep ImportStep(string wslExe, string installDir, string rootfsTar, int wslVersion = 2) =>
        new(ToolIdImport, wslExe, ImportArgs(installDir, rootfsTar, wslVersion), $"Create the {Name} WSL distro (wsl --import, WSL {(wslVersion == 1 ? 1 : 2)}, {Rootfs.Display})",
            TimeSpan.FromMinutes(15), WslRuntime.Utf8Env);

    public static RuntimeStep ProvisionStep(string wslExe) =>
        new(ToolIdProvision, wslExe, ScriptArgs(ProvisionScript), $"Configure the {Name} WSL distro (wsl.conf, apt base packages)", TimeSpan.FromMinutes(30), WslRuntime.Utf8Env);

    public static RuntimeStep UnregisterStep(string wslExe) =>
        new(ToolIdRemove, wslExe, UnregisterArgs(), $"Remove the {Name} WSL distro (wsl --unregister {Name})", TimeSpan.FromMinutes(5), WslRuntime.Utf8Env);

    // ------------------------------------------------------------------ state

    /// <summary>Blocking. Never starts the distro unless <paramref name="checkProvisioned"/> (one short <c>sh -c</c>).</summary>
    public static EzDistroInfo Inspect(IRuntimeProbe? probe = null, bool checkProvisioned = true)
    {
        probe ??= SystemRuntimeProbe.Instance;
        var wsl = WslRuntime.FindWslExe(probe);
        if (wsl == null || !WslRuntime.IsEnabled(probe, wsl)) return EzDistroInfo.None with { WslExe = wsl };
        var d = WslRuntime.ListDistros(probe, wsl).FirstOrDefault(x => x.Name.Equals(Name, StringComparison.OrdinalIgnoreCase));
        if (d == null) return new EzDistroInfo(true, false, 0, null, null, null, InstallDir, null, wsl);
        bool? prov = null;
        string? ver = null;
        if (checkProvisioned)
        {
            var o = probe.Run(wsl, WslRuntime.Args(Name, new[] { "sh", "-c", ProbeScript }, asRoot: true), ProbeTimeout, WslRuntime.Utf8Env);
            if (o != null)
            {
                ver = ParseProbe(o);
                // A marker from an older provisioning schema means the first-boot script must run again (SchemaVersion bump).
                prov = ver != null && MarkerSchema(ver) >= SchemaVersion;
            }
        }
        return new EzDistroInfo(true, true, d.Version, d.State, prov, ver, InstallDir, DiskBytes(), wsl);
    }

    public static string? ParseProbe(string? output) =>
        RuntimeText.Clean(output).Split('\n').Select(l => l.Trim()).FirstOrDefault(l => l.StartsWith("EZDISTRO=", StringComparison.Ordinal))?[9..].Trim() is { Length: > 0 } v ? v : null;

    /// <summary>"ubuntu-base 24.04.5 / schema 2" → 2; 0 when the marker names no schema.</summary>
    public static int MarkerSchema(string? marker)
    {
        var m = System.Text.RegularExpressions.Regex.Match(marker ?? "", @"(?:^|/)\s*schema\s+(\d{1,6})\s*(?:/|$)");
        return m.Success ? int.Parse(m.Groups[1].Value, System.Globalization.CultureInfo.InvariantCulture) : 0;
    }

    /// <summary>Size of the distro's virtual disk (it grows with use and does not shrink by itself).</summary>
    public static long? DiskBytes(string? installDir = null)
    {
        try { var f = new FileInfo(VhdxPath(installDir)); return f.Exists ? f.Length : null; }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or ArgumentException) { return null; }
    }

    public static string FormatBytes(long b) => b >= 1L << 30 ? $"{b / (double)(1L << 30):0.0} GB" : $"{Math.Max(1, b >> 20)} MB";

    // ------------------------------------------------------------------ rootfs download + verification

    public static bool IsOfficialUrl(string url) =>
        Uri.TryCreate(url, UriKind.Absolute, out var u) && u.Scheme == Uri.UriSchemeHttps && u.Host.Equals("cdimage.ubuntu.com", StringComparison.OrdinalIgnoreCase);

    /// <summary>https on ubuntu.com or one of its sub-domains (not "evilubuntu.com"): where a rootfs download may end up after redirects.</summary>
    public static bool IsUbuntuHttps(Uri? u) =>
        u != null && u.IsAbsoluteUri && u.Scheme == Uri.UriSchemeHttps
        && (u.Host.Equals("ubuntu.com", StringComparison.OrdinalIgnoreCase) || u.Host.EndsWith(".ubuntu.com", StringComparison.OrdinalIgnoreCase));

    public static string Sha256Of(string file)
    {
        using var s = File.OpenRead(file);
        return Convert.ToHexString(SHA256.HashData(s)).ToLowerInvariant();
    }

    /// <summary>True when <paramref name="file"/> matches the pin (SHA-256, and the size when the pin has one).</summary>
    public static bool Verify(string file, EzRootfsPin pin)
    {
        var f = new FileInfo(file);
        if (!f.Exists || (pin.Bytes > 0 && f.Length != pin.Bytes)) return false;
        return Sha256Of(file).Equals(pin.Sha256, StringComparison.OrdinalIgnoreCase);
    }

    /// <summary>
    /// Downloads the pinned rootfs into <paramref name="dir"/> (re-uses a verified copy), checks its SHA-256 and returns the
    /// path. A mismatch deletes the file and throws <see cref="InvalidDataException"/>.
    /// </summary>
    public static async Task<string> DownloadRootfsAsync(HttpClient http, EzRootfsPin pin, string dir, IProgress<double>? progress, Action<string>? log, CancellationToken ct)
    {
        if (!IsOfficialUrl(pin.Url)) throw new InvalidOperationException($"Refusing to download from {pin.Url}.");
        Directory.CreateDirectory(dir);
        var dest = Path.Combine(dir, pin.FileName);
        if (File.Exists(dest) && Verify(dest, pin)) { log?.Invoke($"Using the verified {pin.FileName} already downloaded."); return dest; }
        var part = dest + ".part";
        log?.Invoke($"Downloading {pin.Url}");
        using (var resp = await http.GetAsync(pin.Url, HttpCompletionOption.ResponseHeadersRead, ct).ConfigureAwait(false))
        {
            resp.EnsureSuccessStatusCode();
            var final = resp.RequestMessage?.RequestUri;
            if (!IsUbuntuHttps(final))
                throw new InvalidOperationException($"The download was redirected to an unexpected location ({final}); stopped.");
            long total = resp.Content.Headers.ContentLength ?? pin.Bytes, done = 0;
            try
            {
                await using (var src = await resp.Content.ReadAsStreamAsync(ct).ConfigureAwait(false))
                await using (var dst = new FileStream(part, FileMode.Create, FileAccess.Write, FileShare.None, 1 << 16, useAsync: true))
                {
                    var buf = new byte[1 << 17];
                    int n;
                    while ((n = await src.ReadAsync(buf, ct).ConfigureAwait(false)) > 0)
                    {
                        await dst.WriteAsync(buf.AsMemory(0, n), ct).ConfigureAwait(false);
                        done += n;
                        if (total > 0) progress?.Report(Math.Min(1, done / (double)total));
                    }
                }
            }
            catch { TryDelete(part); throw; }
        }
        var sha = Sha256Of(part);
        if (!sha.Equals(pin.Sha256, StringComparison.OrdinalIgnoreCase))
        {
            TryDelete(part);
            throw new InvalidDataException($"{pin.FileName}: SHA-256 {sha} does not match the pinned {pin.Sha256}; the file was deleted.");
        }
        File.Move(part, dest, overwrite: true);
        log?.Invoke($"SHA-256 verified: {sha}");
        return dest;
    }

    // ------------------------------------------------------------------ ensure / remove

    /// <summary>
    /// Makes sure the distro exists and is provisioned: download + verify rootfs, <c>wsl --import</c>, first-boot script,
    /// <c>wsl --terminate</c> (so wsl.conf applies). Each process runs through <paramref name="runStep"/> (a JobQueue job in
    /// the app; awaits resume on the caller's context, so <paramref name="runStep"/> may touch UI state). Never enables WSL (that is the setup's admin step): returns a failure when WSL is not enabled.
    /// </summary>
    public static async Task<(bool Ok, string Message)> EnsureAsync(HttpClient http, Func<RuntimeStep, CancellationToken, Task<Job>> runStep,
        Action<string> log, IProgress<double>? progress = null, IRuntimeProbe? probe = null, CancellationToken ct = default)
    {
        probe ??= SystemRuntimeProbe.Instance;
        var info = await Task.Run(() => Inspect(probe), ct).ConfigureAwait(true);
        if (info.WslExe == null) return (false, "WSL is not installed on this computer.");
        if (!info.WslAvailable) return (false, "WSL is not enabled. Run setup (Network) to enable it: it needs an administrator prompt and a restart, once.");
        if (info.Ready) return (true, $"The {Name} WSL distro is ready ({info.BaseVersion}).");
        var wsl = info.WslExe;
        if (!info.Exists)
        {
            var pin = Rootfs;
            if (Directory.Exists(InstallDir) && Directory.EnumerateFileSystemEntries(InstallDir).Any())
            {
                // 'wsl -l -v' failing or timing out also reads as "not registered": make sure before deleting a disk that may be in use
                // (WSL 2 keeps an ext4.vhdx in the folder, WSL 1 a rootfs folder).
                if (HasDistroFiles() && probe.Run(wsl, WslRuntime.Args(Name, new[] { "true" }, asRoot: true), ProbeTimeout, WslRuntime.Utf8Env) != null)
                    return (false, $"The {Name} WSL distro is registered but 'wsl -l -v' did not list it. Try again; if it keeps failing, restart WSL with 'wsl --shutdown'.");
                // A left-over folder of an earlier, unregistered copy: wsl --import refuses a folder with an ext4.vhdx in it.
                log($"Removing the left-over folder {InstallDir} (no distro is registered for it).");
                TryDeleteDir(InstallDir);
            }
            Directory.CreateDirectory(InstallDir);
            var tar = await DownloadRootfsAsync(http, pin, DownloadDir, new Progress<double>(p => progress?.Report(p * 0.4)), log, ct).ConfigureAwait(true);
            var import = await ImportWithFallbackAsync(async (version, t) =>
            {
                var job = await runStep(ImportStep(wsl, InstallDir, tar, version), t).ConfigureAwait(true);
                return new WslImportAttempt(job.State == JobState.Succeeded, DescribeFailure(job.StatusReason, ReadJobLog(job.LogPath)), job.State == JobState.Cancelled);
            }, () => CleanUpFailedImport(probe, wsl, log), log, ct).ConfigureAwait(true);
            if (!import.Ok) return (false, import.Message);
            TryDelete(tar); // the distro now holds everything; a re-install downloads it again (30 MB)
            progress?.Report(0.5);
        }
        var prov = await runStep(ProvisionStep(wsl), ct).ConfigureAwait(true);
        if (prov.State != JobState.Succeeded) return (false, $"Configuring the {Name} distro failed: {prov.StatusReason}. Check the internet connection (apt needs archive.ubuntu.com), then retry.");
        probe.Run(wsl, TerminateArgs(), ProbeTimeout, WslRuntime.Utf8Env);
        progress?.Report(1);
        var after = await Task.Run(() => Inspect(probe), ct).ConfigureAwait(true);
        return after.Ready ? (true, $"The {Name} WSL distro is ready: {after.Summary}.")
            : (false, $"The {Name} distro was created but does not report as configured. See the log, then retry.");
    }

    /// <summary>
    /// <c>wsl --import ... --version 2</c>, and when that fails (WSL 2 needs hardware virtualization: a VM without nested
    /// virtualization, or virtualization off in the firmware) the partial install is cleaned up and the import is retried once with
    /// <c>--version 1</c>, logging why. Nothing in the distro, Zeek or RITA set-up depends on WSL 2 (no systemd, no
    /// <c>wsl --set-version</c>). A cancelled attempt is not retried. <c>wsl --update</c> is never run here (it can prompt).
    /// </summary>
    /// <param name="import">Runs one import with the given WSL version (1 or 2).</param>
    /// <param name="cleanUp">Removes what a failed attempt left behind (registration, folder) before the retry.</param>
    public static async Task<WslImportOutcome> ImportWithFallbackAsync(Func<int, CancellationToken, Task<WslImportAttempt>> import, Action cleanUp,
        Action<string> log, CancellationToken ct = default)
    {
        var v2 = await import(2, ct).ConfigureAwait(true);
        if (v2.Ok) return new WslImportOutcome(true, 2, $"The {Name} WSL distro was created (WSL 2).");
        if (v2.Cancelled || ct.IsCancellationRequested) return new WslImportOutcome(false, 0, $"wsl --import was cancelled ({v2.Detail}).");
        log($"wsl --import with WSL 2 failed: {v2.Detail}");
        log("WSL 2 needs hardware virtualization (in a virtual machine: nested virtualization). Cleaning up and trying WSL 1 instead…");
        cleanUp();
        var v1 = await import(1, ct).ConfigureAwait(true);
        if (v1.Ok)
        {
            log($"The {Name} WSL distro uses WSL 1, because WSL 2 could not be used ({v2.Detail}).");
            return new WslImportOutcome(true, 1, $"The {Name} WSL distro was created with WSL 1 (WSL 2 is not available on this computer).");
        }
        if (!v1.Cancelled) cleanUp();
        var plain = NeedsWsl1Component(v1.Detail) ? $"WSL 2 cannot run on this computer ({NoVirtualizationReason}) and WSL 1 needs the 'Windows Subsystem for Linux' component. "
            : IsNoVirtualization(v2.Detail) ? $"WSL 2 cannot run on this computer ({NoVirtualizationReason}) and WSL 1 failed too. " : "";
        return new WslImportOutcome(false, 0, $"{plain}wsl --import failed with WSL 2 ({v2.Detail}) and with WSL 1 ({v1.Detail}).");
    }

    public const string NoVirtualizationReason = "hardware virtualization is off, or this is a virtual machine without nested virtualization";
    public const string NoVirtualizationHint = "To use WSL 2 instead, turn on virtualization (Intel VT-x / AMD-V) in the BIOS/UEFI, or nested virtualization for the VM.";

    /// <summary>wsl.exe refused WSL 1 because the classic Microsoft-Windows-Subsystem-Linux component is not enabled.</summary>
    public static bool NeedsWsl1Component(string? text) =>
        text != null && (text.Contains("WSL_E_WSL1_NOT_SUPPORTED", StringComparison.OrdinalIgnoreCase)
                         || text.Contains("optional component to use WSL1", StringComparison.OrdinalIgnoreCase));

    /// <summary>WSL 2 failed because Hyper-V / hardware virtualization is not available.</summary>
    public static bool IsNoVirtualization(string? text) =>
        text != null && (text.Contains("HCS_E_HYPERV_NOT_INSTALLED", StringComparison.OrdinalIgnoreCase)
                         || text.Contains("enablevirtualization", StringComparison.OrdinalIgnoreCase));

    /// <summary>"Exited with code -1" plus the last meaningful lines wsl.exe printed (when the reason does not already contain them).</summary>
    public static string DescribeFailure(string? statusReason, IEnumerable<string>? output)
    {
        var reason = string.IsNullOrWhiteSpace(statusReason) ? "it failed" : statusReason.Trim().TrimEnd('.');
        var tail = MeaningfulTail(output);
        if (tail != null && !reason.Contains(tail, StringComparison.OrdinalIgnoreCase)) reason += ". wsl.exe: " + tail;
        return reason;
    }

    /// <summary>The last (up to <paramref name="max"/>) non-empty output lines, without log metadata (#...), NULs or BOMs; null when none.</summary>
    public static string? MeaningfulTail(IEnumerable<string>? output, int max = 3)
    {
        if (output == null) return null;
        var lines = output.SelectMany(l => RuntimeText.Clean(l).Split('\n')).Select(l => l.Trim())
            .Where(l => l.Length > 0 && !l.StartsWith('#')).ToList();
        if (lines.Count == 0) return null;
        var tail = string.Join(" / ", lines.Skip(Math.Max(0, lines.Count - max)));
        return tail.Length > 400 ? tail[..400] + "…" : tail;
    }

    private static IEnumerable<string> ReadJobLog(string? path)
    {
        if (string.IsNullOrEmpty(path)) return Array.Empty<string>();
        try
        {
            using var fs = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete);
            using var r = new StreamReader(fs, Encoding.UTF8);
            return r.ReadToEnd().Split('\n');
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { return Array.Empty<string>(); }
    }

    /// <summary>A failed import may leave a registration or a half-written folder: both are removed (only ever <see cref="Name"/>).</summary>
    private static void CleanUpFailedImport(IRuntimeProbe probe, string wsl, Action<string> log)
    {
        if (WslRuntime.ListDistros(probe, wsl).Any(d => d.Name.Equals(Name, StringComparison.OrdinalIgnoreCase)))
        {
            log($"Unregistering the partly created {Name} distro.");
            probe.Run(wsl, UnregisterArgs(), ProbeTimeout, WslRuntime.Utf8Env);
        }
        TryDeleteDir(InstallDir);
        try { Directory.CreateDirectory(InstallDir); } catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { }
    }

    /// <summary>The install folder holds a distro: an ext4.vhdx (WSL 2) or a rootfs folder (WSL 1).</summary>
    private static bool HasDistroFiles() => File.Exists(VhdxPath()) || Directory.Exists(Path.Combine(InstallDir, "rootfs"));

    /// <summary><c>wsl --unregister EzForensics</c> (deletes its disk, so Zeek / RITA and their data inside it) and the leftover folder.</summary>
    public static async Task<(bool Ok, string Message)> RemoveAsync(Func<RuntimeStep, CancellationToken, Task<Job>> runStep, IRuntimeProbe? probe = null, CancellationToken ct = default)
    {
        probe ??= SystemRuntimeProbe.Instance;
        var info = await Task.Run(() => Inspect(probe, checkProvisioned: false), ct).ConfigureAwait(true);
        if (info.WslExe != null && info.Exists)
        {
            var job = await runStep(UnregisterStep(info.WslExe), ct).ConfigureAwait(true);
            if (job.State != JobState.Succeeded) return (false, $"wsl --unregister {Name} failed: {job.StatusReason}.");
        }
        TryDeleteDir(InstallDir);
        return (true, $"The {Name} WSL distro was removed.");
    }

    // ------------------------------------------------------------------ run a script (RITA and other engines)

    /// <summary>
    /// Runs <paramref name="script"/> as root in the distro (<c>wsl -d EzForensics -u root -e sh -c</c>, LF-only) and returns
    /// exit code + combined output. <paramref name="ct"/> kills wsl.exe; long-running Linux processes should be stopped by the
    /// caller's own cancel command (e.g. <c>pkill</c>), as killing wsl.exe does not always stop them.
    /// </summary>
    public static async Task<WslRunResult> RunAsync(string script, CancellationToken ct = default, Action<string>? onLine = null, string? wslExe = null, TimeSpan? timeout = null)
    {
        wslExe ??= WslRuntime.FindWslExe(SystemRuntimeProbe.Instance) ?? throw new InvalidOperationException("wsl.exe was not found.");
        WslWelcomeGuard.MarkOobeCompleteOnce(); // no "Welcome to WSL" window popping up over EZ Forensics
        var psi = new ProcessStartInfo(wslExe)
        {
            UseShellExecute = false, RedirectStandardOutput = true, RedirectStandardError = true, RedirectStandardInput = true,
            StandardOutputEncoding = WslOutputEncoding.Instance, StandardErrorEncoding = WslOutputEncoding.Instance, CreateNoWindow = true,
        };
        foreach (var a in ScriptArgs(script)) psi.ArgumentList.Add(a);
        foreach (var (k, v) in WslRuntime.Utf8Env) psi.Environment[k] = v;
        using var p = new Process { StartInfo = psi };
        var sb = new StringBuilder();
        void Line(string? l)
        {
            if (l == null) return;
            l = RuntimeText.Clean(l);
            lock (sb) sb.AppendLine(l);
            onLine?.Invoke(l);
        }
        p.OutputDataReceived += (_, e) => Line(e.Data);
        p.ErrorDataReceived += (_, e) => Line(e.Data);
        p.Start();
        try { p.StandardInput.Close(); } catch { }
        p.BeginOutputReadLine();
        p.BeginErrorReadLine();
        using var cts = CancellationTokenSource.CreateLinkedTokenSource(ct);
        if (timeout != null) cts.CancelAfter(timeout.Value);
        try { await p.WaitForExitAsync(cts.Token).ConfigureAwait(false); }
        catch (OperationCanceledException)
        {
            try { p.Kill(entireProcessTree: true); } catch { }
            try { p.WaitForExit(5000); } catch { }
            lock (sb) return new WslRunResult(-1, sb.ToString(), true);
        }
        p.WaitForExit(); // flush the async readers
        lock (sb) return new WslRunResult(p.ExitCode, sb.ToString(), false);
    }

    private static void TryDelete(string f) { try { if (File.Exists(f)) File.Delete(f); } catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { } }
    private static void TryDeleteDir(string d) { try { if (Directory.Exists(d)) Directory.Delete(d, true); } catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { } }
}
