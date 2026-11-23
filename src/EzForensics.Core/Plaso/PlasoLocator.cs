using System.Diagnostics;
using System.Text;
using System.Text.RegularExpressions;

namespace EzForensics.Core.Plaso;

/// <summary>File system / process access used by discovery (replaced by a fake in tests).</summary>
public interface IPlasoProbe
{
    bool FileExists(string path);
    bool DirectoryExists(string path);
    /// <summary>Full path of <paramref name="fileName"/> on PATH, or null.</summary>
    string? FindOnPath(string fileName);
    /// <summary>Runs a process; stdout+stderr text, or null when it could not start, failed (non-zero exit) or timed out.</summary>
    string? Run(string exe, IReadOnlyList<string> args, TimeSpan timeout);
}

/// <summary>Real probe: PATH lookup and short-lived processes (argv via ArgumentList).</summary>
public sealed class SystemPlasoProbe : IPlasoProbe
{
    public static readonly SystemPlasoProbe Instance = new();

    public bool FileExists(string path) => File.Exists(path);
    public bool DirectoryExists(string path) => Directory.Exists(path);

    public string? FindOnPath(string fileName)
    {
        var path = Environment.GetEnvironmentVariable("PATH") ?? "";
        foreach (var dir in path.Split(Path.PathSeparator, StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
        {
            try
            {
                var p = Path.Combine(dir.Trim('"'), fileName);
                if (File.Exists(p)) return p;
            }
            catch (ArgumentException) { }
        }
        return null;
    }

    public string? Run(string exe, IReadOnlyList<string> args, TimeSpan timeout)
    {
        try
        {
            var psi = new ProcessStartInfo(exe)
            {
                UseShellExecute = false,
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                RedirectStandardInput = true,
                StandardOutputEncoding = Encoding.UTF8,
                StandardErrorEncoding = Encoding.UTF8,
                CreateNoWindow = true,
            };
            foreach (var a in args) psi.ArgumentList.Add(a);
            using var p = Process.Start(psi);
            if (p == null) return null;
            try { p.StandardInput.Close(); } catch { }
            var o = p.StandardOutput.ReadToEndAsync();
            var e = p.StandardError.ReadToEndAsync();
            if (!p.WaitForExit((int)timeout.TotalMilliseconds))
            {
                try { p.Kill(entireProcessTree: true); } catch { }
                return null;
            }
            Task.WaitAll(new Task[] { o, e }, 2000);
            return p.ExitCode == 0 ? o.Result + e.Result : null;
        }
        catch (Exception) { return null; }
    }
}

/// <summary>
/// Finds plaso (log2timeline + psort). Order: the configured path (folder with the executables/scripts, a venv, or a
/// python.exe), the app-managed venv (<c>%LOCALAPPDATA%\EzForensicsData\plaso\venv</c>), PATH, then Docker (<c>log2timeline/plaso</c>).
/// The first candidate whose <c>--version</c> answers wins.
/// </summary>
public sealed class PlasoLocator
{
    public const string DockerImage = "log2timeline/plaso";
    public static readonly Version MinPython = new(3, 10);
    /// <summary>Newest Python with Windows wheels for every plaso dependency (yara-python has no 3.14 wheel yet).</summary>
    public static readonly Version MaxPython = new(3, 13, int.MaxValue);
    private static readonly Regex VersionRx = new(@"version\s+(?<v>[0-9][0-9A-Za-z.\-]*)", RegexOptions.IgnoreCase | RegexOptions.Compiled);
    private static readonly Regex PythonVersionRx = new(@"Python\s+(?<v>\d+\.\d+(\.\d+)?)", RegexOptions.IgnoreCase | RegexOptions.Compiled);

    private readonly IPlasoProbe _probe;
    private readonly TimeSpan _versionTimeout;

    public PlasoLocator(IPlasoProbe? probe = null, string? managedVenv = null, TimeSpan? versionTimeout = null)
    {
        _probe = probe ?? SystemPlasoProbe.Instance;
        ManagedVenv = managedVenv ?? DefaultManagedVenv;
        _versionTimeout = versionTimeout ?? TimeSpan.FromSeconds(60);
    }

    /// <summary>
    /// <c>%LOCALAPPDATA%\EzForensicsData\plaso</c> (venv + private Python); <c>EZFORENSICS_PLASO_ROOT</c> (or <c>EZHUB_PLASO_ROOT</c>)
    /// overrides it. A venv made by EZ Hub in <c>%LOCALAPPDATA%\EzHub\plaso</c> keeps being used there (venvs are not relocatable).
    /// </summary>
    public static string ManagedRoot => EzForensics.Core.Settings.AppPaths.ComponentRoot("plaso", "PLASO_ROOT",
        legacy => Directory.Exists(Path.Combine(legacy, "venv")) || Directory.Exists(Path.Combine(legacy, "python")));
    public static string DefaultManagedVenv => Path.Combine(ManagedRoot, "venv");

    public string ManagedVenv { get; }
    public string ManagedPython => Path.Combine(ManagedVenv, "Scripts", "python.exe");
    /// <summary>EZ Forensics' private Python (python.org NuGet package), next to the managed venv.</summary>
    public string PrivatePythonDir => Path.Combine(Path.GetDirectoryName(Path.GetFullPath(ManagedVenv)) ?? ManagedVenv, "python");
    public string PrivatePythonExe => PrivatePython.PythonExe(PrivatePythonDir);

    /// <summary>Runs discovery (blocking: it starts <c>--version</c> processes). Use from a background thread.</summary>
    public PlasoDiscovery Discover(string? configuredPath, bool includeDocker = true)
    {
        var notes = new List<string>();
        foreach (var c in Candidates(configuredPath, notes))
        {
            var v = ProbeVersion(c);
            if (v != null) return new PlasoDiscovery(c with { Version = v }, notes, c.Kind == PlasoEngineKind.Docker, c.Kind == PlasoEngineKind.Docker);
            notes.Add($"{c.Location}: '{Path.GetFileName(c.Log2Timeline.Exe)} --version' did not answer; skipped.");
        }

        bool dockerAvailable = false, imagePresent = false;
        if (includeDocker)
        {
            var docker = _probe.FindOnPath("docker.exe") ?? _probe.FindOnPath("docker");
            if (docker != null)
            {
                dockerAvailable = _probe.Run(docker, new[] { "version", "--format", "{{.Server.Version}}" }, TimeSpan.FromSeconds(20)) != null;
                if (!dockerAvailable) notes.Add("Docker CLI found but the Docker engine is not running.");
                else
                {
                    imagePresent = _probe.Run(docker, new[] { "image", "inspect", "--format", "{{.Id}}", DockerImage }, TimeSpan.FromSeconds(20)) != null;
                    if (imagePresent)
                    {
                        var c = new PlasoInstall(PlasoOrigin.Docker, PlasoEngineKind.Docker, PlasoCommand.Of(docker), PlasoCommand.Of(docker), null, DockerImage);
                        var v = ProbeVersion(c);
                        if (v != null) return new PlasoDiscovery(c with { Version = v }, notes, true, true);
                        notes.Add($"Docker image {DockerImage} did not answer to 'log2timeline --version'.");
                    }
                    else notes.Add($"Docker is running but the {DockerImage} image is not pulled (docker pull {DockerImage}).");
                }
            }
        }
        return new PlasoDiscovery(null, notes, dockerAvailable, imagePresent);
    }

    /// <summary>Non-Docker candidates in discovery order (no processes started).</summary>
    public IReadOnlyList<PlasoInstall> Candidates(string? configuredPath, List<string>? notes = null)
    {
        var list = new List<PlasoInstall>();
        if (!string.IsNullOrWhiteSpace(configuredPath))
        {
            var c = FromConfigured(configuredPath.Trim().Trim('"'));
            if (c != null) list.Add(c);
            else notes?.Add($"Configured plaso path has no log2timeline/psort or python.exe: {configuredPath}");
        }

        var venv = FromFolder(ManagedVenv, PlasoOrigin.ManagedVenv);
        if (venv != null) list.Add(venv);

        var l2tExe = _probe.FindOnPath("log2timeline.exe");
        var psortExe = _probe.FindOnPath("psort.exe");
        if (l2tExe != null && psortExe != null)
            list.Add(new PlasoInstall(PlasoOrigin.Path, PlasoEngineKind.Executable, PlasoCommand.Of(l2tExe), PlasoCommand.Of(psortExe), null, Path.GetDirectoryName(l2tExe)!));
        else
        {
            var l2tPy = _probe.FindOnPath("log2timeline.py");
            var psortPy = _probe.FindOnPath("psort.py");
            var py = FindPythonOnPath();
            if (l2tPy != null && psortPy != null && py != null)
                list.Add(new PlasoInstall(PlasoOrigin.Path, PlasoEngineKind.Python, PlasoCommand.Of(py, l2tPy), PlasoCommand.Of(py, psortPy), null, Path.GetDirectoryName(l2tPy)!));
        }
        return list;
    }

    private PlasoInstall? FromConfigured(string p)
    {
        if (_probe.FileExists(p))
        {
            var name = Path.GetFileName(p).ToLowerInvariant();
            var dir = Path.GetDirectoryName(p) ?? "";
            if (name.StartsWith("python", StringComparison.Ordinal) && name.EndsWith(".exe", StringComparison.Ordinal))
                return PythonModule(p, PlasoOrigin.Configured, p);
            if (name.StartsWith("log2timeline", StringComparison.Ordinal) || name.StartsWith("psort", StringComparison.Ordinal))
                return FromFolder(dir, PlasoOrigin.Configured);
            return null;
        }
        return _probe.DirectoryExists(p) ? FromFolder(p, PlasoOrigin.Configured) : null;
    }

    /// <summary>A folder with log2timeline.exe+psort.exe, a venv (Scripts\...), or log2timeline.py+psort.py.</summary>
    private PlasoInstall? FromFolder(string dir, PlasoOrigin origin)
    {
        foreach (var d in new[] { dir, Path.Combine(dir, "Scripts") })
        {
            string l2t = Path.Combine(d, "log2timeline.exe"), ps = Path.Combine(d, "psort.exe");
            if (_probe.FileExists(l2t) && _probe.FileExists(ps))
                return new PlasoInstall(origin, PlasoEngineKind.Executable, PlasoCommand.Of(l2t), PlasoCommand.Of(ps), null, d);
        }
        foreach (var d in new[] { dir, Path.Combine(dir, "Scripts") })
        {
            var py = Path.Combine(d, "python.exe");
            if (_probe.FileExists(py)) return PythonModule(py, origin, dir);
        }
        string l2tPy = Path.Combine(dir, "log2timeline.py"), psPy = Path.Combine(dir, "psort.py");
        if (_probe.FileExists(l2tPy) && _probe.FileExists(psPy))
        {
            var python = FindPythonOnPath();
            if (python != null)
                return new PlasoInstall(origin, PlasoEngineKind.Python, PlasoCommand.Of(python, l2tPy), PlasoCommand.Of(python, psPy), null, dir);
        }
        return null;
    }

    /// <summary><c>python -m plaso.scripts.log2timeline</c> (plaso's console scripts live in plaso.scripts since 2021).</summary>
    private static PlasoInstall PythonModule(string python, PlasoOrigin origin, string location) =>
        new(origin, PlasoEngineKind.Python,
            PlasoCommand.Of(python, "-m", "plaso.scripts.log2timeline"),
            PlasoCommand.Of(python, "-m", "plaso.scripts.psort"), null, location);

    private string? FindPythonOnPath()
    {
        var p = _probe.FindOnPath("python.exe");
        return p != null && !IsStoreStub(p) ? p : null;
    }

    private static bool IsStoreStub(string p) => p.Contains(@"\WindowsApps\", StringComparison.OrdinalIgnoreCase);

    /// <summary>Runs <c>log2timeline --version</c>; the parsed version, or null.</summary>
    public string? ProbeVersion(PlasoInstall c)
    {
        IReadOnlyList<string> args = c.Kind == PlasoEngineKind.Docker
            ? new[] { "run", "--rm", c.DockerImage, "log2timeline", "--version" }
            : c.Log2Timeline.PrefixArgs.Concat(new[] { "--version" }).ToList();
        var outText = _probe.Run(c.Log2Timeline.Exe, args, _versionTimeout);
        return outText == null ? null : ParseVersion(outText) ?? "unknown";
    }

    /// <summary>"plaso - log2timeline version 20260720" -> "20260720".</summary>
    public static string? ParseVersion(string? output)
    {
        if (string.IsNullOrWhiteSpace(output)) return null;
        var m = VersionRx.Match(output);
        return m.Success ? m.Groups["v"].Value.TrimEnd('.') : null;
    }

    public static long VersionNumber(string? v)
    {
        if (string.IsNullOrEmpty(v)) return 0;
        var digits = new string(v.TakeWhile(char.IsAsciiDigit).ToArray());
        return digits.Length >= 8 && long.TryParse(digits[..8], System.Globalization.NumberStyles.None, System.Globalization.CultureInfo.InvariantCulture, out var n) ? n : 0;
    }

    // ------------------------------------------------------------------ Python (for "Install plaso")

    /// <summary>
    /// Python interpreters: EZ Forensics' private Python first, then <c>py -3</c>, then python.exe on PATH (the Microsoft
    /// Store stub is ignored).
    /// </summary>
    public IReadOnlyList<PythonInfo> FindPythons()
    {
        var found = new List<PythonInfo>();
        if (_probe.FileExists(PrivatePythonExe))
        {
            var v = PythonVersion(PrivatePythonExe);
            if (v != null) found.Add(new PythonInfo(PrivatePythonExe, v, PythonInfo.PrivateOrigin));
        }
        var launcher = _probe.FindOnPath("py.exe");
        if (launcher != null)
        {
            var exe = _probe.Run(launcher, new[] { "-3", "-c", "import sys; print(sys.executable)" }, TimeSpan.FromSeconds(20))?.Trim();
            if (!string.IsNullOrEmpty(exe))
            {
                exe = exe.Split('\n').Last().Trim();
                var v = PythonVersion(exe);
                if (v != null) found.Add(new PythonInfo(exe, v, "py -3"));
            }
        }
        var onPath = FindPythonOnPath();
        if (onPath != null && !found.Any(f => string.Equals(f.Exe, onPath, StringComparison.OrdinalIgnoreCase)))
        {
            var v = PythonVersion(onPath);
            if (v != null) found.Add(new PythonInfo(onPath, v, "PATH"));
        }
        return found;
    }

    /// <summary>First Python 3.10 - 3.13 (null when none).</summary>
    public PythonInfo? FindSupportedPython() => FindPythons().FirstOrDefault(p => p.IsSupported);

    private Version? PythonVersion(string exe)
    {
        var o = _probe.Run(exe, new[] { "--version" }, TimeSpan.FromSeconds(20));
        return ParsePythonVersion(o);
    }

    public static Version? ParsePythonVersion(string? output)
    {
        if (output == null) return null;
        var m = PythonVersionRx.Match(output);
        return m.Success && Version.TryParse(m.Groups["v"].Value, out var v) ? v : null;
    }

    /// <summary>
    /// The two jobs' argv of "Install plaso": <c>python -m venv &lt;venv&gt;</c>, then
    /// <c>&lt;venv&gt;\Scripts\python.exe -m pip install --upgrade plaso==&lt;PlasoSetup.PinnedVersion&gt;</c>.
    /// </summary>
    public (IReadOnlyList<string> CreateVenv, string VenvPython, IReadOnlyList<string> PipInstall) InstallCommands() =>
        (new[] { "-m", "venv", ManagedVenv }, ManagedPython, PipArgs(PlasoSetup.PinnedVersion));

    /// <summary>
    /// "Update": <c>&lt;venv&gt;\Scripts\python.exe -m pip install --upgrade plaso==&lt;version&gt;</c>. <paramref name="version"/> is the
    /// exact release the user chose to update to (the PyPI latest shown on the button); null or not a plain version = the pinned one.
    /// </summary>
    public (string VenvPython, IReadOnlyList<string> PipUpgrade) UpdateCommand(string? version = null) =>
        (ManagedPython, PipArgs(version != null && PlainVersionRx.IsMatch(version) ? version : PlasoSetup.PinnedVersion));

    private static readonly Regex PlainVersionRx = new(@"^[0-9]+(\.[0-9A-Za-z]+)*$", RegexOptions.Compiled);

    private static string[] PipArgs(string version) =>
        new[] { "-m", "pip", "install", "--upgrade", "--disable-pip-version-check", "plaso==" + version };

    /// <summary>True when <paramref name="install"/> lives in the managed venv (so pip can update it).</summary>
    public bool IsManaged(PlasoInstall? install) =>
        install != null && (install.Origin == PlasoOrigin.ManagedVenv || IsUnder(install.Location, ManagedVenv)
                            || IsUnder(install.Log2Timeline.Exe, ManagedVenv));

    /// <summary>Folders "Uninstall" removes: the managed venv and the private Python, nothing else.</summary>
    public IReadOnlyList<string> UninstallTargets() => new[] { ManagedVenv, PrivatePythonDir };

    private static bool IsUnder(string? path, string dir)
    {
        if (string.IsNullOrEmpty(path)) return false;
        try
        {
            var p = Path.GetFullPath(path).TrimEnd('\\', '/');
            var d = Path.GetFullPath(dir).TrimEnd('\\', '/');
            return p.Equals(d, StringComparison.OrdinalIgnoreCase) || p.StartsWith(d + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase);
        }
        catch (Exception) { return false; }
    }

    // ------------------------------------------------------------------ psort / pinfo versions

    /// <summary>plaso's pinfo for the same install (sibling pinfo.exe / pinfo.py, or the plaso.scripts.pinfo module).</summary>
    public static PlasoCommand? PinfoCommand(PlasoInstall i)
    {
        var l2t = i.Log2Timeline;
        if (i.Kind == PlasoEngineKind.Docker) return null;
        if (l2t.PrefixArgs.Count >= 2 && l2t.PrefixArgs[0] == "-m")
            return new PlasoCommand(l2t.Exe, l2t.PrefixArgs.Take(l2t.PrefixArgs.Count - 1).Append(l2t.PrefixArgs[^1].Replace("log2timeline", "pinfo")).ToList());
        if (i.Kind == PlasoEngineKind.Python && l2t.PrefixArgs.Count >= 1)
            return new PlasoCommand(l2t.Exe, l2t.PrefixArgs.Take(l2t.PrefixArgs.Count - 1)
                .Append(Path.Combine(Path.GetDirectoryName(l2t.PrefixArgs[^1]) ?? "", "pinfo.py")).ToList());
        if (l2t.PrefixArgs.Count > 0) return null; // e.g. the test fake: no pinfo
        return PlasoCommand.Of(Path.Combine(Path.GetDirectoryName(l2t.Exe) ?? "", "pinfo" + Path.GetExtension(l2t.Exe)));
    }

    /// <summary><c>&lt;tool&gt; --version</c> for any plaso command (not Docker); the parsed version or null.</summary>
    public string? ProbeVersion(PlasoCommand cmd)
    {
        if (cmd.PrefixArgs.Count == 0 && Path.IsPathRooted(cmd.Exe) && !_probe.FileExists(cmd.Exe)) return null;
        var o = _probe.Run(cmd.Exe, cmd.PrefixArgs.Concat(new[] { "--version" }).ToList(), _versionTimeout);
        return ParseVersion(o);
    }
}
