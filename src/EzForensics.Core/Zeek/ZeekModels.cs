using EzForensics.Core.Runtimes;

namespace EzForensics.Core.Zeek;

/// <summary>How Zeek is started.</summary>
public enum ZeekEngine
{
    /// <summary>A Windows zeek.exe (experimental source build or a configured path).</summary>
    Native,
    /// <summary><c>docker run zeek/zeek:lts zeek ...</c> (Docker Desktop).</summary>
    Docker,
    /// <summary><c>wsl.exe -d &lt;distro&gt; -e /opt/zeek/bin/zeek ...</c> (official OBS packages inside a WSL distro).</summary>
    Wsl,
}

/// <summary>Where the install was found (discovery order).</summary>
public enum ZeekOrigin { Configured, Path, ProgramFiles, Docker, Wsl }

/// <summary>A usable Zeek.</summary>
/// <param name="Exe">The process to start: zeek.exe, docker.exe or wsl.exe.</param>
/// <param name="ZeekPath">Path of the zeek binary as the engine sees it (Windows path, <c>zeek</c> in the image, <c>/opt/zeek/bin/zeek</c> in WSL).</param>
/// <param name="HasLocalScript">site/local.zeek exists, so <c>local</c> can be loaded (the recommended site policy).</param>
public sealed record ZeekInstall(
    ZeekEngine Engine,
    ZeekOrigin Origin,
    string Exe,
    string ZeekPath,
    string? Version,
    string Location,
    bool HasLocalScript,
    string? DockerImage = null,
    DockerImageInfo? Image = null,
    string? WslDistro = null,
    string WslMountRoot = "/mnt/")
{
    public string EngineLabel => Engine switch
    {
        ZeekEngine.Native => "Native (zeek.exe)",
        ZeekEngine.Docker => "Docker",
        _ => "WSL",
    };

    public string OriginLabel => Origin switch
    {
        ZeekOrigin.Configured => "configured path",
        ZeekOrigin.Path => "PATH",
        ZeekOrigin.ProgramFiles => "Program Files",
        ZeekOrigin.Docker => "image " + DockerImage,
        _ => IsEzDistro ? "EZ Forensics distro" : "distro " + WslDistro,
    };

    /// <summary>Runs in EZ Forensics' private WSL distro (<see cref="EzDistro.Name"/>).</summary>
    public bool IsEzDistro => Engine == ZeekEngine.Wsl && EzDistro.Name.Equals(WslDistro, StringComparison.OrdinalIgnoreCase);

    public string Summary => $"Zeek {Version ?? "(unknown version)"} · {EngineLabel} · {OriginLabel}";

    /// <summary>Key/value facts written to the run manifest of every Zeek job.</summary>
    public IReadOnlyDictionary<string, string> ManifestDetails()
    {
        var d = new Dictionary<string, string>
        {
            ["zeek.version"] = Version ?? "unknown",
            ["zeek.engine"] = Engine.ToString().ToLowerInvariant(),
            ["zeek.binary"] = ZeekPath,
            ["zeek.location"] = Location,
        };
        if (DockerImage != null) d["docker.image"] = DockerImage;
        if (Image != null)
        {
            d["docker.imageId"] = Image.Id;
            if (Image.Digest != null) d["docker.repoDigest"] = Image.RepoDigests.FirstOrDefault() ?? Image.Digest;
        }
        if (WslDistro != null) d["wsl.distro"] = WslDistro;
        return d;
    }
}

/// <summary>What one click on Install does when Zeek was not found.</summary>
public enum ZeekSetupOption
{
    /// <summary>Zeek is ready.</summary>
    None,
    /// <summary>Docker is running: <c>docker pull zeek/zeek:lts</c>.</summary>
    DockerPull,
    /// <summary>Docker Desktop is installed but stopped: start it (no admin), wait for the engine, then pull.</summary>
    StartDockerThenPull,
    /// <summary>An Ubuntu/Debian WSL distro exists: add the official OBS repo and <c>apt-get install zeek</c> inside it (as root, no Windows admin).</summary>
    WslApt,
    /// <summary>WSL is enabled: create EZ Forensics' private distro (<see cref="EzDistro"/>, no admin) when needed, then install the official packages in it. The default.</summary>
    EzDistroApt,
    /// <summary>Nothing usable: explain how to get Docker Desktop or WSL (both need admin, so EZ Forensics never installs them).</summary>
    Guidance,
}

/// <summary>Result of <see cref="ZeekLocator.Discover"/>.</summary>
public sealed record ZeekDiscovery(
    ZeekInstall? Selected,
    IReadOnlyList<string> Notes,
    DockerState Docker,
    DockerImageInfo? DockerImage,
    string? WslExe,
    IReadOnlyList<WslDistro> Distros,
    string? WslCandidate,
    WslOsRelease? WslCandidateOs,
    ZeekSetupOption Setup)
{
    public bool IsAvailable => Selected != null;
    public static readonly ZeekDiscovery Empty = new(null, Array.Empty<string>(), DockerState.None, null, null, Array.Empty<WslDistro>(), null, null, ZeekSetupOption.Guidance);
}

/// <summary>One process to start for a Zeek run.</summary>
public sealed record ZeekInvocation(string Exe, IReadOnlyList<string> Args, string WorkingDir, string? ContainerName,
    IReadOnlyList<string>? CancelCommand, IReadOnlyDictionary<string, string>? Environment)
{
    public string Display => RuntimeText.Display(Exe, Args);
}

/// <summary>Options of one Zeek run over one capture.</summary>
public sealed class ZeekRunOptions
{
    public string Pcap { get; set; } = "";
    /// <summary>Folder Zeek writes its logs into (it is Zeek's working directory).</summary>
    public string OutputDir { get; set; } = "";
    /// <summary>Load <c>local</c> (site policy: extra logs such as software, known hosts/services, x509 hashes) when available.</summary>
    public bool IncludeLocal { get; set; } = true;
    /// <summary>Extra scripts / redefs appended to the command line (advanced).</summary>
    public List<string> ExtraScripts { get; set; } = new();
    /// <summary>The user's additional arguments (Tools page, <see cref="Commands.ExtraArgsStore.Zeek"/>), placed before <see cref="ExtraScripts"/>.</summary>
    public IReadOnlyList<string> ExtraArgs { get; set; } = Array.Empty<string>();
    /// <summary><see cref="ExtraArgs"/> then <see cref="ExtraScripts"/>: the <c>extra</c> of <see cref="ZeekCommandBuilder.ZeekArgs"/>.</summary>
    public IEnumerable<string> AllExtra => ExtraArgs.Concat(ExtraScripts);
}

/// <summary>What the Tools card shows.</summary>
public enum ZeekToolStatus { Detecting, Installed, UpdateAvailable, NotInstalled, NeedsRuntime }
