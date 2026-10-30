using System.Text.RegularExpressions;
using EzForensics.Core.Plaso;

namespace EzForensics.Core.Memory;

/// <summary>
/// Finds Volatility 3 (<c>vol.exe</c>). Order: the configured path (vol.exe, or a folder / venv that holds it), EZ Forensics' own venv
/// (<c>%LOCALAPPDATA%\EzForensicsData\volatility\venv</c>), then PATH. A candidate counts only when its Python answers
/// <c>importlib.metadata.version('volatility3')</c>.
/// </summary>
public sealed class VolatilityLocator
{
    public const string ExeName = "vol.exe";
    private static readonly Regex VersionRx = new(@"^\d+(\.\d+)+([A-Za-z0-9.\-+]*)$", RegexOptions.Compiled);

    private readonly IPlasoProbe _probe;

    public VolatilityLocator(IPlasoProbe? probe = null, string? root = null)
    {
        _probe = probe ?? SystemPlasoProbe.Instance;
        Root = root ?? ManagedRoot;
    }

    /// <summary><c>%LOCALAPPDATA%\EzForensicsData\volatility</c>; <c>EZFORENSICS_VOLATILITY_ROOT</c> (or <c>EZHUB_VOLATILITY_ROOT</c>) overrides it.</summary>
    public static string ManagedRoot => EzForensics.Core.Settings.AppPaths.ComponentRoot("volatility", "VOLATILITY_ROOT");

    public string Root { get; }
    public string ManagedVenv => Path.Combine(Root, "venv");
    public string ManagedExe => Path.Combine(ManagedVenv, "Scripts", ExeName);
    public string ManagedPython => Path.Combine(ManagedVenv, "Scripts", "python.exe");
    public string SymbolsDir => Path.Combine(Root, "symbols");
    public string CacheDir => Path.Combine(Root, "cache");

    /// <summary>Configured path -> managed venv -> PATH. Blocking (starts python processes): call from a background thread.</summary>
    public VolatilityInstall? Discover(string? configuredPath)
    {
        foreach (var c in Candidates(configuredPath))
        {
            var v = ProbeVersion(c);
            if (v != null) return c with { Version = v };
        }
        return null;
    }

    /// <summary>Candidates in discovery order; no processes started. <see cref="VolatilityInstall.Version"/> is null.</summary>
    public IReadOnlyList<VolatilityInstall> Candidates(string? configuredPath)
    {
        var list = new List<VolatilityInstall>();
        void Add(string? exe)
        {
            if (exe == null) return;
            var full = Path.GetFullPath(exe);
            if (list.Any(l => l.Exe.Equals(full, StringComparison.OrdinalIgnoreCase))) return;
            list.Add(Make(full));
        }

        if (!string.IsNullOrWhiteSpace(configuredPath)) Add(FromConfigured(configuredPath.Trim().Trim('"')));
        Add(_probe.FileExists(ManagedExe) ? ManagedExe : null);
        Add(_probe.FindOnPath(ExeName));
        return list;
    }

    private string? FromConfigured(string p)
    {
        if (_probe.FileExists(p))
            return Path.GetFileName(p).StartsWith("vol", StringComparison.OrdinalIgnoreCase) && p.EndsWith(".exe", StringComparison.OrdinalIgnoreCase) ? p : null;
        if (!_probe.DirectoryExists(p)) return null;
        foreach (var d in new[] { p, Path.Combine(p, "Scripts"), Path.Combine(p, "venv", "Scripts") })
        {
            var exe = Path.Combine(d, ExeName);
            if (_probe.FileExists(exe)) return exe;
        }
        return null;
    }

    private VolatilityInstall Make(string exe)
    {
        // pip puts vol.exe and python.exe in the same Scripts folder of the venv.
        var py = Path.Combine(Path.GetDirectoryName(exe) ?? "", "python.exe");
        return new VolatilityInstall(exe, py, null, Root, SymbolsDir, CacheDir);
    }

    /// <summary><c>python -c "import importlib.metadata as m;print(m.version('volatility3'))"</c>; the version or null.</summary>
    public string? ProbeVersion(VolatilityInstall install)
    {
        if (!_probe.FileExists(install.Python)) return null;
        var o = _probe.Run(install.Python, VolatilitySetup.VersionProbeArgs, TimeSpan.FromSeconds(60));
        return ParseVersion(o);
    }

    /// <summary>The last line that looks like a version number.</summary>
    public static string? ParseVersion(string? output)
    {
        if (string.IsNullOrWhiteSpace(output)) return null;
        foreach (var line in output.Split('\n', '\r').Reverse())
        {
            var t = line.Trim();
            if (t.Length > 0 && VersionRx.IsMatch(t)) return t;
        }
        return null;
    }

    /// <summary>True when the managed venv exists (it still needs the private Python that plaso's folder holds).</summary>
    public static bool ManagedVenvExists(string? root = null) =>
        Directory.Exists(Path.Combine(root ?? ManagedRoot, "venv"));
}
