using Velopack;
using Velopack.Sources;

namespace EzForensics.App.Services;

/// <summary>
/// Self-update of the installed app through Velopack. Off unless Settings has an update feed (a GitHub repository URL whose
/// releases carry the vpk output, or any https Velopack feed URL / a local folder). A portable or development copy never updates itself.
/// Plain http feeds are refused: the feed's release list carries the package hashes, so over http anyone on the network could
/// hand out their own package.
/// </summary>
public static class AppUpdater
{
    /// <summary>Why <paramref name="feed"/> cannot be used (null when it can): only https URLs and full local folder paths.</summary>
    internal static string? FeedProblem(string? feed)
    {
        if (string.IsNullOrWhiteSpace(feed)) return "no update feed is configured.";
        feed = feed.Trim();
        if (Uri.TryCreate(feed, UriKind.Absolute, out var u) && !u.IsFile)
            return u.Scheme == Uri.UriSchemeHttps ? null : $"the update feed must use https ({u.Scheme}:// is not accepted).";
        return Path.IsPathFullyQualified(feed) ? null : "the update feed must be an https URL or a full folder path.";
    }

    private static UpdateManager Create(string feed)
    {
        feed = feed.Trim();
        IUpdateSource source = feed.StartsWith("https://github.com/", StringComparison.OrdinalIgnoreCase)
            ? new GithubSource(feed.TrimEnd('/'), null, false, null)
            : Uri.TryCreate(feed, UriKind.Absolute, out var u) && !u.IsFile
                ? new SimpleWebSource(feed, null, 30)
                : new SimpleFileSource(new DirectoryInfo(feed));
        return new UpdateManager(source, null, null);
    }

    /// <summary>True when running from a Velopack install (not a dev build or portable unzip without Update.exe).</summary>
    public static bool IsInstalled
    {
        get
        {
            try { return new UpdateManager("https://localhost/none", null, null).IsInstalled; }
            catch (Exception) { return false; }
        }
    }

    /// <summary>Checks the feed; downloads and restarts into the new version when <paramref name="apply"/> is true. Returns a plain status line.</summary>
    public static async Task<string> CheckAsync(string? feed, bool apply, Action<int>? progress = null)
    {
        if (string.IsNullOrWhiteSpace(feed)) return "Updates are off: no update feed is configured.";
        if (FeedProblem(feed) is { } problem) return "Updates are off: " + problem;
        var mgr = Create(feed);
        if (!mgr.IsInstalled) return "This copy of EZ Forensics is not installed with the installer (development or portable copy), so it does not update itself.";
        var info = await mgr.CheckForUpdatesAsync();
        if (info == null) return $"EZ Forensics {mgr.CurrentVersion} is up to date.";
        if (!apply) return $"Version {info.TargetFullRelease.Version} is available (installed: {mgr.CurrentVersion}).";
        await mgr.DownloadUpdatesAsync(info, progress);
        AppLog.Info($"Applying update {info.TargetFullRelease.Version} and restarting");
        mgr.ApplyUpdatesAndRestart(info.TargetFullRelease);
        return "Restarting…";
    }
}
