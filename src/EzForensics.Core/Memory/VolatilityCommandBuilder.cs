using System.Globalization;

namespace EzForensics.Core.Memory;

/// <summary>A Volatility 3 command: argv only (passed through <c>ProcessStartInfo.ArgumentList</c>) plus extra environment.</summary>
public sealed record VolatilityInvocation(string Exe, IReadOnlyList<string> Args, IReadOnlyDictionary<string, string> Environment);

/// <summary>
/// Builds one <c>vol.exe</c> run per plugin:
/// <c>-r csv -f &lt;image&gt; --symbol-dirs &lt;root&gt;\symbols --cache-path &lt;root&gt;\cache [--offline] [--single-swap-locations &lt;pagefile&gt;] &lt;plugin&gt; [args]</c>.
/// No <c>-q</c>: the <c>Progress:</c> lines on stderr feed the job output.
/// </summary>
public static class VolatilityCommandBuilder
{
    /// <summary>Environment for every run: no user site-packages, UTF-8 pipes, unbuffered stderr (live progress).</summary>
    public static readonly IReadOnlyDictionary<string, string> BaseEnvironment = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
    {
        ["PYTHONNOUSERSITE"] = "1",
        ["PYTHONIOENCODING"] = "utf-8",
        ["PYTHONUNBUFFERED"] = "1",
    };

    /// <param name="extraArgs">Extra plugin arguments typed by the user (split on whitespace, double quotes group).</param>
    /// <param name="includePagefile">Add <c>--single-swap-locations &lt;pagefile&gt;</c> when a pagefile is known.</param>
    /// <param name="pagefilePath">The pagefile.sys to add; null = look for <c>pagefile.sys</c> next to the image and in its parent folders.</param>
    public static VolatilityInvocation Build(VolatilityInstall install, string imagePath, MemoryPluginDef plugin, bool offline, string? extraArgs = null,
        bool includePagefile = false, string? pagefilePath = null)
    {
        ArgumentNullException.ThrowIfNull(install);
        ArgumentException.ThrowIfNullOrEmpty(imagePath);
        ArgumentNullException.ThrowIfNull(plugin);
        var args = new List<string>
        {
            "-r", "csv",
            "-f", imagePath,
            "--symbol-dirs", install.SymbolsDir,
            "--cache-path", install.CacheDir,
        };
        if (offline) args.Add("--offline");
        if (includePagefile)
        {
            var pf = pagefilePath ?? FindPagefile(imagePath);
            if (!string.IsNullOrEmpty(pf)) { args.Add("--single-swap-locations"); args.Add(pf); }
        }
        args.Add(plugin.Plugin);
        args.AddRange(plugin.Args);
        if (!string.IsNullOrWhiteSpace(extraArgs)) args.AddRange(SplitArgs(extraArgs));
        return new VolatilityInvocation(install.Exe, args, BaseEnvironment);
    }

    /// <summary>
    /// <c>pagefile.sys</c> next to the image, or in the system root above it when the image is <c>&lt;root&gt;\Windows\MEMORY.DMP</c>; null when none.
    /// The running system's own pagefile (root of the system drive) is never used.
    /// </summary>
    public static string? FindPagefile(string imagePath)
    {
        try
        {
            var dir = Path.GetDirectoryName(Path.GetFullPath(imagePath));
            if (string.IsNullOrEmpty(dir)) return null;
            var candidates = new List<string> { dir };
            if (Path.GetFileName(dir).Equals("Windows", StringComparison.OrdinalIgnoreCase) && Path.GetDirectoryName(dir) is { Length: > 0 } root)
                candidates.Add(root);
            var liveRoot = Path.GetPathRoot(Environment.SystemDirectory);
            foreach (var d in candidates)
            {
                if (!string.IsNullOrEmpty(liveRoot) && string.Equals(Path.TrimEndingDirectorySeparator(d), Path.TrimEndingDirectorySeparator(liveRoot),
                        StringComparison.OrdinalIgnoreCase))
                    continue;
                var p = Path.Combine(d, "pagefile.sys");
                if (File.Exists(p)) return p;
            }
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or ArgumentException or NotSupportedException) { }
        return null;
    }

    /// <summary>Splits user-typed plugin arguments (whitespace separated, <c>"..."</c> groups; <see cref="Commands.ExtraArgsStore.Split"/>).</summary>
    public static IReadOnlyList<string> SplitArgs(string s) => Commands.ExtraArgsStore.Split(s, out _);

    /// <summary>Display form of an invocation (exe + quoted args), for labels and logs.</summary>
    public static string ToDisplayString(VolatilityInvocation inv)
    {
        static string Q(string s) => s.Length == 0 || s.Any(c => char.IsWhiteSpace(c) || c == '"') ? "\"" + s.Replace("\"", "\\\"") + "\"" : s;
        return string.Join(" ", new[] { Q(inv.Exe) }.Concat(inv.Args.Select(Q)));
    }
}
