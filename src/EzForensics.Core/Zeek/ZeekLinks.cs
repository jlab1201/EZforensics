namespace EzForensics.Core.Zeek;

/// <summary>
/// Official Zeek links (verified 2026-10-08). The Zeek project publishes source tarballs on GitHub (no Windows binaries or
/// MSI: Windows support is an experimental build-from-source option since 5.2), official container images on Docker Hub
/// (<c>zeek/zeek</c>: <c>latest</c>, <c>lts</c>, <c>x.y</c>, <c>x.y.z</c>) and Linux packages on the openSUSE Build Service
/// (<c>security:zeek</c>). Zeek is BSD-licensed.
/// </summary>
public static class ZeekLinks
{
    public const string Website = "https://zeek.org/";
    public const string Docs = "https://docs.zeek.org/en/current/";
    public const string InstallDocs = "https://docs.zeek.org/en/current/install.html";
    public const string LogsDocs = "https://docs.zeek.org/en/current/logs/index.html";
    public const string GitHub = "https://github.com/zeek/zeek";
    public const string Releases = "https://github.com/zeek/zeek/releases";
    /// <summary>GitHub API: <c>tag_name</c> (e.g. v9.0.0) and <c>published_at</c> of the latest release.</summary>
    public const string LatestReleaseApi = "https://api.github.com/repos/zeek/zeek/releases/latest";
    public const string License = "https://github.com/zeek/zeek/blob/master/COPYING";
    public const string DockerHub = "https://hub.docker.com/r/zeek/zeek";
    public const string DockerRepo = "zeek/zeek";
    public const string DockerDesktopDocs = Runtimes.DockerRuntime.DesktopInstallDocs;
    public const string WslInstall = Runtimes.WslRuntime.InstallDocs;
}
