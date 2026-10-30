namespace EzForensics.Core.Plaso;

/// <summary>
/// Official plaso / log2timeline links (verified 2026-10-08). The plaso project publishes source tarballs on GitHub,
/// wheels on PyPI and a Docker image; there are no prebuilt Windows binaries any more, so on Windows plaso is installed
/// with pip (EZ Forensics' managed venv) or run from the Docker image.
/// </summary>
public static class PlasoLinks
{
    public const string Docs = "https://plaso.readthedocs.io/en/latest/";
    public const string DockerDocs = "https://plaso.readthedocs.io/en/latest/sources/user/Installing-with-docker.html";
    public const string GitHub = "https://github.com/log2timeline/plaso";
    public const string Releases = "https://github.com/log2timeline/plaso/releases";
    public const string PyPI = "https://pypi.org/project/plaso/";
    /// <summary>PyPI JSON API: <c>info.version</c> is the latest non-pre-release version.</summary>
    public const string PyPIJson = "https://pypi.org/pypi/plaso/json";
    public const string DockerHub = "https://hub.docker.com/r/log2timeline/plaso";
    public const string PythonDownloads = "https://www.python.org/downloads/windows/";
    /// <summary>python.org's official NuGet package (full portable CPython with pip/venv; no installer, no admin, no PATH changes).</summary>
    public const string PythonNuGet = "https://www.nuget.org/packages/python";
}
