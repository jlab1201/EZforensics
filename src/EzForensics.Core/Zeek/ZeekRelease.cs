using System.Net.Http;
using System.Text.Json;
using EzForensics.Core.Runtimes;

namespace EzForensics.Core.Zeek;

/// <summary>Latest Zeek release (GitHub) and what the <c>lts</c> image on Docker Hub points to.</summary>
/// <param name="Version">Latest release from GitHub (e.g. 9.0.0); null when GitHub did not answer.</param>
/// <param name="ImageVersion">Release the <c>zeek/zeek:lts</c> tag currently points to.</param>
/// <param name="ImageDigest">Manifest digest of <c>zeek/zeek:lts</c> on Docker Hub.</param>
public sealed record ZeekRelease(string? Version, DateTime? PublishedUtc, string? ImageVersion, string? ImageDigest, string? Error);

public static class ZeekReleaseCheck
{
    public static readonly TimeSpan DefaultTimeout = TimeSpan.FromSeconds(15);

    /// <summary>Parses the GitHub "latest release" JSON: (version without the leading v, published time).</summary>
    public static (string? Version, DateTime? PublishedUtc) ParseGitHubLatest(string json)
    {
        try
        {
            using var doc = JsonDocument.Parse(json);
            var root = doc.RootElement;
            if (root.ValueKind != JsonValueKind.Object) return (null, null);
            if (root.TryGetProperty("draft", out var d) && d.ValueKind == JsonValueKind.True) return (null, null);
            var tag = root.TryGetProperty("tag_name", out var t) && t.ValueKind == JsonValueKind.String ? t.GetString() : null;
            if (string.IsNullOrWhiteSpace(tag)) return (null, null);
            DateTime? pub = root.TryGetProperty("published_at", out var p) && p.ValueKind == JsonValueKind.String
                            && DateTime.TryParse(p.GetString(), System.Globalization.CultureInfo.InvariantCulture,
                                System.Globalization.DateTimeStyles.AdjustToUniversal | System.Globalization.DateTimeStyles.AssumeUniversal, out var dt)
                ? dt : null;
            return (Normalize(tag), pub);
        }
        catch (JsonException) { return (null, null); }
    }

    /// <summary>GitHub latest release + Docker Hub <c>lts</c> tag, in parallel, each with a timeout; never throws for network errors.</summary>
    public static async Task<ZeekRelease> GetLatestAsync(HttpClient http, TimeSpan? timeout = null, CancellationToken ct = default)
    {
        var to = timeout ?? DefaultTimeout;
        var gh = GetGitHubAsync(http, to, ct);
        var hub = DockerRuntime.GetHubTagsAsync(http, ZeekLinks.DockerRepo, to, ct);
        string? error = null;
        (string? Version, DateTime? Published) g = (null, null);
        (string? Version, string? Digest) h = (null, null);
        try { g = await gh.ConfigureAwait(false); }
        catch (Exception ex) when (ex is HttpRequestException or TimeoutException or InvalidDataException) { error = "GitHub: " + ex.Message; }
        try
        {
            var tag = ZeekLocator.DockerImage.Split(':') is { Length: 2 } parts ? parts[1] : "lts";
            h = DockerRuntime.ResolveMovingTag(await hub.ConfigureAwait(false), tag);
        }
        catch (Exception ex) when (ex is HttpRequestException or TimeoutException) { error = (error == null ? "" : error + "; ") + "Docker Hub: " + ex.Message; }
        return new ZeekRelease(g.Version, g.Published, h.Version, h.Digest, g.Version == null && h.Version == null ? error ?? "no answer" : null);
    }

    private static async Task<(string?, DateTime?)> GetGitHubAsync(HttpClient http, TimeSpan timeout, CancellationToken ct)
    {
        using var cts = CancellationTokenSource.CreateLinkedTokenSource(ct);
        cts.CancelAfter(timeout);
        try
        {
            using var req = new HttpRequestMessage(HttpMethod.Get, ZeekLinks.LatestReleaseApi);
            req.Headers.Accept.ParseAdd("application/vnd.github+json");
            if (req.Headers.UserAgent.Count == 0 && http.DefaultRequestHeaders.UserAgent.Count == 0) req.Headers.UserAgent.ParseAdd("EzForensics");
            using var resp = await http.SendAsync(req, cts.Token).ConfigureAwait(false);
            resp.EnsureSuccessStatusCode();
            var r = ParseGitHubLatest(await resp.Content.ReadAsStringAsync(cts.Token).ConfigureAwait(false));
            return r.Version == null ? throw new InvalidDataException("no usable release") : r;
        }
        catch (OperationCanceledException) when (!ct.IsCancellationRequested)
        {
            throw new TimeoutException($"no answer within {timeout.TotalSeconds:0} s");
        }
    }

    /// <summary>Dotted-number comparison ("9.0.10" &gt; "9.0.9"); a leading "v" is ignored; unknown sorts lowest.</summary>
    public static int Compare(string? a, string? b)
    {
        a = Normalize(a); b = Normalize(b);
        if (a == null || b == null) return (a == null ? 0 : 1) - (b == null ? 0 : 1);
        var pa = a.Split('.', '-'); var pb = b.Split('.', '-');
        for (int i = 0; i < Math.Max(pa.Length, pb.Length); i++)
        {
            long x = i < pa.Length && long.TryParse(pa[i], System.Globalization.NumberStyles.None, System.Globalization.CultureInfo.InvariantCulture, out var xi) ? xi : 0;
            long y = i < pb.Length && long.TryParse(pb[i], System.Globalization.NumberStyles.None, System.Globalization.CultureInfo.InvariantCulture, out var yi) ? yi : 0;
            if (x != y) return x.CompareTo(y);
        }
        return 0;
    }

    public static bool IsNewer(string? latest, string? installed) => Normalize(latest) != null && Normalize(installed) != null && Compare(latest, installed) > 0;

    private static string? Normalize(string? v)
    {
        if (string.IsNullOrWhiteSpace(v)) return null;
        v = v.Trim();
        if (v.StartsWith('v') || v.StartsWith('V')) v = v[1..];
        return v.Equals("unknown", StringComparison.OrdinalIgnoreCase) || v.Length == 0 || !char.IsDigit(v[0]) ? null : v;
    }
}

public static class ZeekToolState
{
    /// <summary>
    /// Installed / UpdateAvailable (Docker: the local <c>lts</c> image digest differs from Docker Hub's; native / WSL: GitHub
    /// has a newer release) / NotInstalled (one click installs it) / NeedsRuntime (neither Docker nor a usable WSL distro).
    /// </summary>
    public static ZeekToolStatus Evaluate(bool detecting, ZeekInstall? install, ZeekRelease? latest, ZeekSetupOption setup)
    {
        if (install == null) return detecting ? ZeekToolStatus.Detecting : setup == ZeekSetupOption.Guidance ? ZeekToolStatus.NeedsRuntime : ZeekToolStatus.NotInstalled;
        if (latest == null) return ZeekToolStatus.Installed;
        if (install.Engine == ZeekEngine.Docker)
        {
            var local = install.Image?.Digest;
            return local != null && latest.ImageDigest != null && !local.Equals(latest.ImageDigest, StringComparison.OrdinalIgnoreCase)
                ? ZeekToolStatus.UpdateAvailable : ZeekToolStatus.Installed;
        }
        return ZeekReleaseCheck.IsNewer(latest.Version, install.Version) ? ZeekToolStatus.UpdateAvailable : ZeekToolStatus.Installed;
    }

    public static string Label(ZeekToolStatus s) => s switch
    {
        ZeekToolStatus.Detecting => "Detecting…",
        ZeekToolStatus.Installed => "Installed",
        ZeekToolStatus.UpdateAvailable => "Update available",
        ZeekToolStatus.NotInstalled => "Not installed",
        _ => "Needs Docker or WSL",
    };
}
