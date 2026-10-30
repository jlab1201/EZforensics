using System.Diagnostics;
using System.Text.Json;
using EzForensics.Core.Execution;
using EzForensics.Core.Runtimes;

namespace EzForensics.Core.Zeek;

/// <summary>One install/update/uninstall step, run as a JobQueue job (logged, on the Jobs page, in the run manifest).</summary>
public sealed record ZeekSetupStep(string ToolId, string Exe, IReadOnlyList<string> Args, string Label, TimeSpan Timeout,
    IReadOnlyDictionary<string, string>? Environment = null)
{
    public JobSpec ToJobSpec(string outputDir) =>
        new(ToolId, Path.GetFileName(Exe), Args, outputDir, Label, "Maintenance", ExePath: Exe, Timeout: Timeout, Environment: Environment);

    public string Display => RuntimeText.Display(Exe, Args);
}

/// <summary>What EZ Forensics itself set up (so Uninstall removes only that). Stored in <c>&lt;ManagedRoot&gt;\state.json</c>.</summary>
public sealed class ZeekSetupState
{
    /// <summary>The Docker image EZ Forensics pulled (null = none, or it was already there).</summary>
    public string? DockerImagePulled { get; set; }
    /// <summary>WSL distros where EZ Forensics added the official repo and installed Zeek.</summary>
    public List<string> WslDistros { get; set; } = new();
    public string? UpdatedUtc { get; set; }

    public bool IsEmpty => DockerImagePulled == null && WslDistros.Count == 0;

    private static readonly JsonSerializerOptions Json = new() { WriteIndented = true, PropertyNamingPolicy = JsonNamingPolicy.CamelCase };

    public static string PathIn(string root) => System.IO.Path.Combine(root, "state.json");

    public static ZeekSetupState Load(string? root = null)
    {
        try
        {
            var p = PathIn(root ?? ZeekLocator.ManagedRoot);
            return File.Exists(p) ? JsonSerializer.Deserialize<ZeekSetupState>(File.ReadAllText(p), Json) ?? new() : new();
        }
        catch (Exception ex) when (ex is IOException or JsonException or UnauthorizedAccessException) { return new(); }
    }

    public void Save(string? root = null)
    {
        root ??= ZeekLocator.ManagedRoot;
        Directory.CreateDirectory(root);
        UpdatedUtc = DateTime.UtcNow.ToString("o");
        var p = PathIn(root);
        File.WriteAllText(p + ".tmp", JsonSerializer.Serialize(this, Json));
        File.Move(p + ".tmp", p, overwrite: true);
    }
}

/// <summary>
/// Install / Update / Uninstall of Zeek through an existing runtime. Docker: <c>docker pull zeek/zeek:lts</c> (≈180 MB).
/// WSL (Ubuntu/Debian): add the official openSUSE Build Service repo <c>security:zeek</c> with its signing key and
/// <c>apt-get install zeek</c>, as root inside the distro (no Windows admin rights). Docker Desktop and WSL themselves are
/// never installed (they need admin rights and the user's consent).
/// </summary>
public static class ZeekSetup
{
    public const string ToolIdDockerPull = "zeek-docker-pull", ToolIdDockerRemove = "zeek-docker-rm",
        ToolIdWslInstall = "zeek-wsl-install", ToolIdWslUpdate = "zeek-wsl-update", ToolIdWslRemove = "zeek-wsl-remove";

    /// <summary>Compressed size of zeek/zeek:9.0.0 for amd64 on Docker Hub (178 MB), rounded.</summary>
    public const int DockerDownloadMB = 180;
    /// <summary>Download of the WSL packages (zeek + dependencies + apt lists) and their disk use: measured on Ubuntu 24.04 with Zeek 9.0.0
    /// (apt: "Need to get 107 MB", "493 MB of additional disk space", plus ≈35 MB of package lists).</summary>
    public const int WslDownloadMB = 140, WslDiskMB = 500;

    public static ZeekSetupStep DockerPullStep(string docker, string image = ZeekLocator.DockerImage) =>
        new(ToolIdDockerPull, docker, DockerRuntime.PullArgs(image), $"docker pull {image}", TimeSpan.FromMinutes(60));

    public static ZeekSetupStep DockerRemoveStep(string docker, string image = ZeekLocator.DockerImage) =>
        new(ToolIdDockerRemove, docker, DockerRuntime.RemoveImageArgs(image), $"docker image rm {image}", TimeSpan.FromMinutes(5));

    /// <summary>
    /// Root shell script for Ubuntu/Debian: official OBS repo + key (signed-by), then <c>apt-get install zeek</c>. Exit 3 =
    /// unsupported distro, 4 = no official packages for this release.
    /// </summary>
    // The apt source / keyring keep their EZ Hub era names (ezhub-security-zeek) on purpose: a second, differently named entry for the
    // same repository next to an existing one makes apt fail with "Conflicting values set for option Signed-By".
    public static readonly string WslInstallScript = Lf("""
        set -e
        export DEBIAN_FRONTEND=noninteractive
        . /etc/os-release
        case "$ID" in
          ubuntu) R="xUbuntu_$VERSION_ID" ;;
          debian) R="Debian_$VERSION_ID" ;;
          *) echo "EZFORENSICS: $PRETTY_NAME is not supported (Ubuntu or Debian only)." >&2; exit 3 ;;
        esac
        URL="https://download.opensuse.org/repositories/security:/zeek/$R"
        echo "EZFORENSICS: distro $PRETTY_NAME, repository $URL"
        apt-get update
        apt-get install -y --no-install-recommends curl gnupg ca-certificates
        if ! curl -fsSL -o /dev/null "$URL/Release"; then echo "EZFORENSICS: no official Zeek packages for $PRETTY_NAME ($URL)." >&2; exit 4; fi
        curl -fsSL "$URL/Release.key" | gpg --dearmor --yes -o /usr/share/keyrings/ezhub-security-zeek.gpg
        echo "deb [signed-by=/usr/share/keyrings/ezhub-security-zeek.gpg] $URL/ /" > /etc/apt/sources.list.d/ezhub-security-zeek.list
        apt-get update
        apt-get install -y --no-install-recommends zeek
        /opt/zeek/bin/zeek --version
        """);

    public static readonly string WslUpdateScript = Lf("""
        set -e
        export DEBIAN_FRONTEND=noninteractive
        apt-get update
        apt-get install -y --only-upgrade --no-install-recommends zeek
        /opt/zeek/bin/zeek --version
        """);

    /// <summary>Removes the zeek packages and the repo / key files EZ Forensics created (curl / gnupg stay: they may be used by other software).</summary>
    public static readonly string WslRemoveScript = Lf("""
        set -e
        export DEBIAN_FRONTEND=noninteractive
        P=$(dpkg-query -W -f='${binary:Package}\n' 'zeek*' 'zeekctl*' 'spicy*' 'libbroker*' 2>/dev/null | tr '\n' ' ')
        if [ -n "$P" ]; then apt-get purge -y $P; fi
        rm -f /etc/apt/sources.list.d/ezhub-security-zeek.list /usr/share/keyrings/ezhub-security-zeek.gpg
        apt-get update || true
        """);

    /// <summary>The scripts run under sh: never let a CRLF source file leak CR characters into them.</summary>
    private static string Lf(string s) => s.Replace("\r\n", "\n").Replace('\r', '\n');

    public static ZeekSetupStep WslInstallStep(string wslExe, string distro) =>
        new(ToolIdWslInstall, wslExe, WslRuntime.Args(distro, new[] { "sh", "-c", WslInstallScript }, asRoot: true),
            $"Install Zeek in WSL {distro} (apt, official OBS repo)", TimeSpan.FromMinutes(45), WslRuntime.Utf8Env);

    public static ZeekSetupStep WslUpdateStep(string wslExe, string distro) =>
        new(ToolIdWslUpdate, wslExe, WslRuntime.Args(distro, new[] { "sh", "-c", WslUpdateScript }, asRoot: true),
            $"Update Zeek in WSL {distro} (apt)", TimeSpan.FromMinutes(45), WslRuntime.Utf8Env);

    public static ZeekSetupStep WslRemoveStep(string wslExe, string distro) =>
        new(ToolIdWslRemove, wslExe, WslRuntime.Args(distro, new[] { "sh", "-c", WslRemoveScript }, asRoot: true),
            $"Remove Zeek from WSL {distro} (apt purge)", TimeSpan.FromMinutes(20), WslRuntime.Utf8Env);

    /// <summary>Enqueues the step and waits for it; <paramref name="ct"/> cancels the job (process tree killed).</summary>
    public static async Task<Job> RunStepAsync(JobQueue queue, ZeekSetupStep step, string outputDir, Action<Job>? onJob = null, CancellationToken ct = default)
    {
        Directory.CreateDirectory(outputDir);
        var job = queue.Enqueue(step.ToJobSpec(outputDir), outputDir);
        onJob?.Invoke(job);
        using (ct.Register(job.Cancel))
            await Plaso.PlasoTimelineRunner.WaitAsync(job).ConfigureAwait(false);
        return job;
    }

    /// <summary>
    /// Starts Docker Desktop (a normal user-level app start, no admin) and waits until the engine answers.
    /// Returns the new state (EngineRunning false on timeout).
    /// </summary>
    public static async Task<DockerState> StartDockerDesktopAsync(string desktopExe, IRuntimeProbe? probe = null, TimeSpan? timeout = null,
        Action<string>? log = null, CancellationToken ct = default)
    {
        probe ??= SystemRuntimeProbe.Instance;
        log?.Invoke($"Starting Docker Desktop: {desktopExe}");
        Process.Start(new ProcessStartInfo(desktopExe) { UseShellExecute = true })?.Dispose();
        var deadline = DateTime.UtcNow + (timeout ?? TimeSpan.FromMinutes(4));
        var state = DockerState.None;
        int n = 0;
        while (DateTime.UtcNow < deadline)
        {
            await Task.Delay(TimeSpan.FromSeconds(3), ct).ConfigureAwait(false);
            state = await Task.Run(() => DockerRuntime.Detect(probe), ct).ConfigureAwait(false);
            if (state.EngineRunning) { log?.Invoke($"Docker engine {state.ServerVersion} is running."); return state; }
            if (++n % 5 == 0) log?.Invoke("Waiting for the Docker engine to start…");
        }
        log?.Invoke("The Docker engine did not start in time. Open Docker Desktop, finish its first-run steps, then click Install again.");
        return state;
    }
}
