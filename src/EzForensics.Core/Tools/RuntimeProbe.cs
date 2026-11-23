using System.Diagnostics;

namespace EzForensics.Core.Tools;

/// <summary>
/// Are the .NET runtimes (every major in <see cref="RequiredMajors"/>, currently 9 and 10) available for the net9 EZ builds? Either the machine-wide one (Program Files\dotnet) or EZ Forensics'
/// private copy (<see cref="Settings.AppPaths.PrivateDotnetDir"/>, installed per user with Microsoft's dotnet-install script).
/// The private copy is used by the EZ tools' apphosts through <c>DOTNET_ROOT</c> (see <see cref="DotnetRootEnvironment"/>).
/// </summary>
public static class RuntimeProbe
{
    /// <summary>.NET majors the EZ tool builds need (some still 9, the 2026.11 builds need 10). "Net9" in member names means all of these.</summary>
    public static readonly int[] RequiredMajors = { 9, 10 };

    private static Lazy<bool> _net9 = new(Probe);
    private static Lazy<bool> _systemNet9 = new(ProbeSystem);
    private static Task<bool>? _task;

    public static bool HasNet9() => _net9.Value;

    /// <summary>Non-blocking probe (runs on the thread pool, result cached).</summary>
    public static Task<bool> HasNet9Async() => _task ??= Task.Run(() => _net9.Value);

    /// <summary>True once the probe has completed (via either entry point).</summary>
    public static bool IsKnown => _net9.IsValueCreated;

    /// <summary>Cached result, or null while the probe has not completed. Never blocks.</summary>
    public static bool? Cached => _net9.IsValueCreated ? _net9.Value : null;

    /// <summary>Forget the cached answers (after the private runtime was installed or removed).</summary>
    public static void Reset()
    {
        _net9 = new Lazy<bool>(Probe);
        _systemNet9 = new Lazy<bool>(ProbeSystem);
        _task = null;
    }

    /// <summary>Folder of the private runtime (overridable for tests).</summary>
    public static string PrivateDotnetRoot { get; set; } = Settings.AppPaths.PrivateDotnetDir;

    public static bool HasSystemNet9() => _systemNet9.Value;

    /// <summary>The private runtime holds Microsoft.NETCore.App for every required major (installed by the "dotnet" runtime of dotnet-install.ps1).</summary>
    public static bool HasPrivateNet9() => RequiredMajors.All(m => HasFramework(PrivateDotnetRoot, "Microsoft.NETCore.App", m));

    /// <summary>True when <paramref name="dotnetRoot"/>\shared\<paramref name="framework"/> has a <paramref name="major"/>.x folder.</summary>
    public static bool HasFramework(string dotnetRoot, string framework, int major)
    {
        try
        {
            var dir = Path.Combine(dotnetRoot, "shared", framework);
            return Directory.Exists(dir) && Directory.EnumerateDirectories(dir, major + ".*").Any();
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { return false; }
    }

    /// <summary>
    /// Environment for processes that should use the private runtime: <c>DOTNET_ROOT</c> / <c>DOTNET_ROOT_X64</c> = the private
    /// folder, but only when the machine lacks a required .NET major and the private copy exists (null otherwise). An apphost
    /// (MFTECmd.exe, ...) looks at DOTNET_ROOT before the machine-wide install location.
    /// </summary>
    public static IReadOnlyDictionary<string, string>? DotnetRootEnvironment()
    {
        if (!HasPrivateNet9() || HasSystemNet9()) return null; // the cheap folder check first: "dotnet --list-runtimes" blocks start-up
        var root = Path.GetFullPath(PrivateDotnetRoot);
        return new Dictionary<string, string> { ["DOTNET_ROOT"] = root, ["DOTNET_ROOT_X64"] = root };
    }

    /// <summary>
    /// Sets <see cref="DotnetRootEnvironment"/> on this process, so every child process (EZ tool jobs, GUI tools started from
    /// Tools) inherits it. Returns the private root when applied, else null. Call at startup and after installing it.
    /// </summary>
    public static string? ApplyToCurrentProcess()
    {
        var env = DotnetRootEnvironment();
        if (env == null) return null;
        foreach (var (k, v) in env) Environment.SetEnvironmentVariable(k, v);
        return env["DOTNET_ROOT"];
    }

    private static bool Probe() => ProbeSystem() || HasPrivateNet9();

    /// <summary>The first <c>dotnet.exe</c> in a fully qualified PATH folder (relative PATH entries and the current directory are ignored).</summary>
    public static string? FindDotnetOnPath(string? path = null)
    {
        path ??= Environment.GetEnvironmentVariable("PATH") ?? "";
        foreach (var dir in path.Split(Path.PathSeparator, StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
        {
            try
            {
                var d = dir.Trim('"');
                if (!Path.IsPathFullyQualified(d)) continue;
                var exe = Path.Combine(d, OperatingSystem.IsWindows() ? "dotnet.exe" : "dotnet");
                if (File.Exists(exe)) return exe;
            }
            catch (ArgumentException) { }
        }
        return null;
    }

    private static bool ProbeSystem()
    {
        try
        {
            // dotnet.exe from PATH only (full folders): a bare "dotnet" would also be looked up in the current directory.
            var dotnet = FindDotnetOnPath();
            if (dotnet == null) throw new FileNotFoundException("dotnet.exe is not on PATH.");
            // stderr is not redirected: nothing reads it, and a full pipe would block the probe until its timeout.
            var psi = new ProcessStartInfo(dotnet, "--list-runtimes")
            {
                UseShellExecute = false,
                RedirectStandardOutput = true,
                CreateNoWindow = true,
            };
            // Ask the machine-wide host only: a DOTNET_ROOT set for the private runtime must not count as "installed".
            psi.Environment.Remove("DOTNET_ROOT");
            psi.Environment.Remove("DOTNET_ROOT_X64");
            using var p = Process.Start(psi);
            if (p != null)
            {
                var outTask = p.StandardOutput.ReadToEndAsync();
                if (p.WaitForExit(10000))
                {
                    var text = outTask.GetAwaiter().GetResult();
                    if (RequiredMajors.All(m => text.Contains($"Microsoft.NETCore.App {m}.", StringComparison.Ordinal))) return true;
                }
                else { try { p.Kill(true); } catch { } }
            }
        }
        catch { }

        try
        {
            var pf = Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles);
            if (RequiredMajors.All(m => HasFramework(Path.Combine(pf, "dotnet"), "Microsoft.NETCore.App", m))) return true;
        }
        catch { }
        return false;
    }
}
