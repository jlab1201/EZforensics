using System.Net.Http;
using System.Text.Json;
using System.Text.RegularExpressions;

namespace EzForensics.Core.Runtimes;

/// <summary>State of Docker on this machine (Docker Desktop on Windows).</summary>
/// <param name="Cli">docker.exe (PATH or the Docker Desktop folder), or null.</param>
/// <param name="EngineRunning">The daemon answered <c>docker version</c>.</param>
/// <param name="DesktopExe">Docker Desktop.exe when installed (can be started without admin rights).</param>
/// <param name="ServerVersion">Engine version when running.</param>
public sealed record DockerState(string? Cli, bool EngineRunning, string? DesktopExe, string? ServerVersion)
{
    public bool CliFound => Cli != null;
    public bool DesktopInstalled => DesktopExe != null;
    public static readonly DockerState None = new(null, false, null, null);
}

/// <summary>A local image (<c>docker image inspect</c>).</summary>
/// <param name="Id">Image id (sha256:...).</param>
/// <param name="RepoDigests">e.g. <c>zeek/zeek@sha256:...</c> (the registry manifest digest: what Docker Hub shows for the tag).</param>
public sealed record DockerImageInfo(string Id, IReadOnlyList<string> RepoDigests, long? Size)
{
    /// <summary>The <c>sha256:...</c> part of the first repo digest (null for a locally built image).</summary>
    public string? Digest => RepoDigests.Select(d => d.Contains('@') ? d[(d.IndexOf('@') + 1)..] : null).FirstOrDefault(d => d != null);
}

/// <summary>
/// Generic Docker helper for engines run from an official image (Zeek now, Volatility later): find the CLI, check the
/// engine, inspect / pull / remove an image, bind-mount specs, Docker Hub tag lookups. Never installs Docker (that needs admin).
/// </summary>
public static class DockerRuntime
{
    public const string DesktopDownload = "https://www.docker.com/products/docker-desktop/";
    public const string DesktopInstallDocs = "https://docs.docker.com/desktop/setup/install/windows-install/";
    private static readonly TimeSpan ProbeTimeout = TimeSpan.FromSeconds(20);

    /// <summary>Default install folder of Docker Desktop (<c>%ProgramFiles%\Docker\Docker</c>).</summary>
    public static string DefaultDesktopDir =>
        Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles), "Docker", "Docker");

    public static string? FindCli(IRuntimeProbe probe)
    {
        var p = probe.FindOnPath("docker.exe") ?? probe.FindOnPath("docker");
        if (p != null) return p;
        var bundled = Path.Combine(DefaultDesktopDir, "resources", "bin", "docker.exe");
        return probe.FileExists(bundled) ? bundled : null;
    }

    public static string? FindDesktopExe(IRuntimeProbe probe)
    {
        var exe = Path.Combine(DefaultDesktopDir, "Docker Desktop.exe");
        return probe.FileExists(exe) ? exe : null;
    }

    /// <summary>Blocking (starts <c>docker version</c>); call from a background thread.</summary>
    public static DockerState Detect(IRuntimeProbe probe)
    {
        var cli = FindCli(probe);
        var desktop = FindDesktopExe(probe);
        if (cli == null) return new DockerState(null, false, desktop, null);
        var v = probe.Run(cli, new[] { "version", "--format", "{{.Server.Version}}" }, ProbeTimeout);
        var ver = RuntimeText.Clean(v).Trim();
        return new DockerState(cli, v != null && ver.Length > 0, desktop, ver.Length > 0 ? ver : null);
    }

    /// <summary><c>docker image inspect</c>; null when the image is not present locally.</summary>
    public static DockerImageInfo? InspectImage(IRuntimeProbe probe, string cli, string image)
    {
        var o = probe.Run(cli, new[] { "image", "inspect", "--format", "{{.Id}}|{{join .RepoDigests \",\"}}|{{.Size}}", image }, ProbeTimeout);
        return ParseInspect(o);
    }

    /// <summary>Parses <c>{{.Id}}|{{join .RepoDigests ","}}|{{.Size}}</c>.</summary>
    public static DockerImageInfo? ParseInspect(string? output)
    {
        var line = RuntimeText.Clean(output).Split('\n', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries).FirstOrDefault();
        if (string.IsNullOrEmpty(line)) return null;
        var parts = line.Split('|');
        if (parts[0].Length == 0) return null;
        var digests = parts.Length > 1 ? parts[1].Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries) : Array.Empty<string>();
        long? size = parts.Length > 2 && long.TryParse(parts[2], out var s) ? s : null;
        return new DockerImageInfo(parts[0], digests, size);
    }

    public static IReadOnlyList<string> PullArgs(string image) => new[] { "pull", image };
    public static IReadOnlyList<string> RemoveImageArgs(string image) => new[] { "image", "rm", image };
    public static IReadOnlyList<string> StopArgs(string container) => new[] { "stop", "--time", "5", container };

    /// <summary><c>type=bind,source=...,target=...[,readonly]</c>; a source with a comma or quote is CSV-quoted.</summary>
    public static string MountSpec(string hostPath, string target, bool readOnly)
    {
        var src = "source=" + hostPath;
        if (src.IndexOfAny(new[] { ',', '"' }) >= 0) src = "\"" + src.Replace("\"", "\"\"") + "\"";
        return $"type=bind,{src},target={target}" + (readOnly ? ",readonly" : "");
    }

    /// <summary>A container name docker accepts: <c>ezforensics-&lt;tool&gt;-&lt;8 hex&gt;</c>.</summary>
    public static string NewContainerName(string tool) =>
        "ezforensics-" + Regex.Replace(tool.ToLowerInvariant(), "[^a-z0-9_.-]", "-") + "-" + Guid.NewGuid().ToString("N")[..8];

    // ------------------------------------------------------------------ Docker Hub (latest tag / digest)

    /// <summary>One tag of a Docker Hub repository.</summary>
    public sealed record HubTag(string Name, string? Digest, DateTime? LastUpdatedUtc);

    /// <summary><c>https://hub.docker.com/v2/repositories/&lt;repo&gt;/tags?page_size=100&amp;ordering=last_updated</c>.</summary>
    public static string HubTagsUrl(string repo) => $"https://hub.docker.com/v2/repositories/{repo}/tags?page_size=100&ordering=last_updated";

    /// <summary>Parses the Docker Hub v2 tags list (<c>results[].name / digest / last_updated</c>).</summary>
    public static IReadOnlyList<HubTag> ParseHubTags(string json)
    {
        var list = new List<HubTag>();
        try
        {
            using var doc = JsonDocument.Parse(json);
            if (!doc.RootElement.TryGetProperty("results", out var results) || results.ValueKind != JsonValueKind.Array) return list;
            foreach (var t in results.EnumerateArray())
            {
                var name = t.TryGetProperty("name", out var n) && n.ValueKind == JsonValueKind.String ? n.GetString() : null;
                if (string.IsNullOrEmpty(name)) continue;
                var digest = t.TryGetProperty("digest", out var d) && d.ValueKind == JsonValueKind.String ? d.GetString() : null;
                DateTime? updated = t.TryGetProperty("last_updated", out var u) && u.ValueKind == JsonValueKind.String
                                    && DateTime.TryParse(u.GetString(), System.Globalization.CultureInfo.InvariantCulture,
                                        System.Globalization.DateTimeStyles.AdjustToUniversal | System.Globalization.DateTimeStyles.AssumeUniversal, out var dt)
                    ? dt : null;
                list.Add(new HubTag(name!, digest, updated));
            }
        }
        catch (JsonException) { }
        return list;
    }

    public static async Task<IReadOnlyList<HubTag>> GetHubTagsAsync(HttpClient http, string repo, TimeSpan timeout, CancellationToken ct = default)
    {
        using var cts = CancellationTokenSource.CreateLinkedTokenSource(ct);
        cts.CancelAfter(timeout);
        try
        {
            using var resp = await http.GetAsync(HubTagsUrl(repo), cts.Token).ConfigureAwait(false);
            resp.EnsureSuccessStatusCode();
            return ParseHubTags(await resp.Content.ReadAsStringAsync(cts.Token).ConfigureAwait(false));
        }
        catch (OperationCanceledException) when (!ct.IsCancellationRequested)
        {
            throw new TimeoutException($"Docker Hub did not answer within {timeout.TotalSeconds:0} s.");
        }
    }

    /// <summary>
    /// The release version a moving tag (<c>lts</c>, <c>latest</c>) currently points to: the <c>x.y.z</c> tag with the
    /// same manifest digest. Returns (version or null, digest or null).
    /// </summary>
    public static (string? Version, string? Digest) ResolveMovingTag(IReadOnlyList<HubTag> tags, string movingTag)
    {
        var moving = tags.FirstOrDefault(t => t.Name.Equals(movingTag, StringComparison.OrdinalIgnoreCase));
        if (moving?.Digest == null) return (null, null);
        var ver = tags.Where(t => t.Digest == moving.Digest && Regex.IsMatch(t.Name, @"^\d+\.\d+\.\d+$"))
            .Select(t => t.Name).OrderByDescending(n => Version.TryParse(n, out var v) ? v : new Version(0, 0)).FirstOrDefault();
        return (ver, moving.Digest);
    }
}
