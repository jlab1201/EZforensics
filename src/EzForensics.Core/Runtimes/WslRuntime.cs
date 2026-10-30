using System.Text.RegularExpressions;

namespace EzForensics.Core.Runtimes;

/// <summary>One installed WSL distribution (<c>wsl -l -v</c>).</summary>
public sealed record WslDistro(string Name, bool IsDefault, string State, int Version)
{
    public bool IsRunning => State.Equals("Running", StringComparison.OrdinalIgnoreCase);
    /// <summary>Docker Desktop's internal distros (never used for tools).</summary>
    public bool IsDockerInternal => WslRuntime.IsDockerInternal(Name);
}

/// <summary>Facts about a distro needed to install packages (<c>/etc/os-release</c>).</summary>
public sealed record WslOsRelease(string Id, string VersionId, string? PrettyName)
{
    public bool IsUbuntu => Id.Equals("ubuntu", StringComparison.OrdinalIgnoreCase);
}

/// <summary>
/// Generic WSL helper (Zeek now, Volatility later): list distros, read os-release, translate Windows paths to the distro's
/// view (<c>D:\x\y.pcap</c> -> <c>/mnt/d/x/y.pcap</c>) and build <c>wsl.exe -d &lt;distro&gt; ...</c> argv. Never installs
/// WSL (that needs admin and a reboot). The only distro EZ Forensics creates is its own private one (<see cref="EzDistro"/>).
/// </summary>
public static class WslRuntime
{
    public const string InstallDocs = "https://learn.microsoft.com/windows/wsl/install";
    /// <summary>Makes wsl.exe write UTF-8 instead of UTF-16 (wsl 0.64+); older builds are handled by stripping NULs.</summary>
    public static readonly IReadOnlyDictionary<string, string> Utf8Env = new Dictionary<string, string> { ["WSL_UTF8"] = "1" };
    private static readonly TimeSpan ProbeTimeout = TimeSpan.FromSeconds(30);

    public static string? FindWslExe(IRuntimeProbe probe)
    {
        var sys = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.System), "wsl.exe");
        if (probe.FileExists(sys)) return sys;
        return probe.FindOnPath("wsl.exe");
    }

    public static bool IsDockerInternal(string name) =>
        name.Equals("docker-desktop", StringComparison.OrdinalIgnoreCase) || name.Equals("docker-desktop-data", StringComparison.OrdinalIgnoreCase);

    /// <summary>Blocking: <c>wsl --status</c> succeeds only when the WSL platform is installed and enabled (the inbox wsl.exe stub exits non-zero).</summary>
    public static bool IsEnabled(IRuntimeProbe probe, string wslExe) => probe.Run(wslExe, new[] { "--status" }, TimeSpan.FromSeconds(20), Utf8Env) != null;

    /// <summary>Blocking; distros usable for tools (Docker Desktop's internal ones excluded).</summary>
    public static IReadOnlyList<WslDistro> ListDistros(IRuntimeProbe probe, string? wslExe = null)
    {
        wslExe ??= FindWslExe(probe);
        if (wslExe == null) return Array.Empty<WslDistro>();
        return ParseList(probe.Run(wslExe, new[] { "-l", "-v" }, ProbeTimeout, Utf8Env)).Where(d => !d.IsDockerInternal).ToList();
    }

    /// <summary>
    /// Parses <c>wsl -l -v</c>:
    /// <code>
    ///   NAME            STATE           VERSION
    /// * Ubuntu-24.04    Running         2
    ///   docker-desktop  Stopped         2
    /// </code>
    /// </summary>
    public static IReadOnlyList<WslDistro> ParseList(string? output)
    {
        var list = new List<WslDistro>();
        foreach (var raw in RuntimeText.Clean(output).Split('\n'))
        {
            var line = raw.TrimEnd();
            if (line.Trim().Length == 0) continue;
            bool def = line.TrimStart().StartsWith('*');
            var body = def ? line.TrimStart()[1..] : line;
            var cols = Regex.Split(body.Trim(), @"\s{2,}|\t").Where(c => c.Length > 0).ToArray();
            if (cols.Length < 3) cols = body.Trim().Split(' ', StringSplitOptions.RemoveEmptyEntries);
            if (cols.Length < 3) continue;
            if (cols[0].Equals("NAME", StringComparison.OrdinalIgnoreCase)) continue;
            if (!int.TryParse(cols[^1], out var ver)) continue;
            var name = string.Join(" ", cols[..^2]);
            list.Add(new WslDistro(name, def, cols[^2], ver));
        }
        return list;
    }

    /// <summary>Parses <c>/etc/os-release</c> text.</summary>
    public static WslOsRelease? ParseOsRelease(string? text)
    {
        var map = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        foreach (var line in RuntimeText.Clean(text).Split('\n'))
        {
            int eq = line.IndexOf('=');
            if (eq <= 0) continue;
            map[line[..eq].Trim()] = line[(eq + 1)..].Trim().Trim('"', '\'');
        }
        return map.TryGetValue("ID", out var id) ? new WslOsRelease(id, map.GetValueOrDefault("VERSION_ID") ?? "", map.GetValueOrDefault("PRETTY_NAME")) : null;
    }

    /// <summary>
    /// Windows path -> path inside the distro using the default automount root (<c>C:\a b\c</c> -> <c>/mnt/c/a b/c</c>).
    /// Null for paths WSL cannot see this way (UNC shares, relative paths).
    /// </summary>
    public static string? ToWslPath(string windowsPath, string mountRoot = "/mnt/")
    {
        if (string.IsNullOrWhiteSpace(windowsPath)) return null;
        string full;
        try { full = Path.GetFullPath(windowsPath); } catch (Exception) { return null; }
        if (full.Length < 2 || full[1] != ':' || !char.IsLetter(full[0])) return null;
        var root = mountRoot.EndsWith('/') ? mountRoot : mountRoot + "/";
        var rest = full.Length > 2 ? full[2..].Replace('\\', '/') : "/";
        if (!rest.StartsWith('/')) rest = "/" + rest;
        var result = root + char.ToLowerInvariant(full[0]) + rest;
        return result.Length > root.Length + 2 ? result.TrimEnd('/') : result;
    }

    /// <summary>
    /// <c>wsl.exe -d &lt;distro&gt; [-u root] [--cd &lt;linuxDir&gt;] -e &lt;command...&gt;</c>. <c>-e</c> (--exec) runs the command
    /// without the login shell, so every argv element arrives unchanged (spaces, $, quotes); <c>--</c> would re-parse it.
    /// </summary>
    public static IReadOnlyList<string> Args(string distro, IEnumerable<string> command, string? linuxWorkDir = null, bool asRoot = false)
    {
        var a = new List<string> { "-d", distro };
        if (asRoot) { a.Add("-u"); a.Add("root"); }
        if (!string.IsNullOrEmpty(linuxWorkDir)) { a.Add("--cd"); a.Add(linuxWorkDir); }
        a.Add("-e");
        a.AddRange(command);
        return a;
    }

    /// <summary>Single-quotes a value for <c>sh -c</c>.</summary>
    public static string ShQuote(string s) => "'" + s.Replace("'", "'\"'\"'") + "'";
}
