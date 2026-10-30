using System.Diagnostics;
using System.Reflection;

namespace EzForensics.Core.Tools;

public enum BuildFlavor { Net9, Net4 }

public sealed record InstalledTool(string ExeName, string Path, BuildFlavor Flavor, string? Version);

public interface IToolLocator
{
    void Rescan(string toolsRoot);
    InstalledTool? Resolve(string exeName, BuildFlavor? preferred = null);
    IReadOnlyList<InstalledTool> All { get; }
}

public sealed class ToolLocator : IToolLocator
{
    private const int MaxDepth = 5;
    private static readonly HashSet<string> SkipDirs = new(StringComparer.OrdinalIgnoreCase)
    {
        "Maps", "BatchExamples", "runtimes", "ref", "obj", ".git", "node_modules", "Plugins", "Reports",
    };
    private static readonly string[] SkipExePrefixes = { "unins", "vc_redist", "vcredist", "createdump", "dotnet", "setup" };

    private volatile IReadOnlyList<InstalledTool> _all = Array.Empty<InstalledTool>();

    public IReadOnlyList<InstalledTool> All => _all;

    public void Rescan(string toolsRoot)
    {
        var found = new List<InstalledTool>();
        if (Directory.Exists(toolsRoot)) Walk(toolsRoot, 0, found);
        _all = found.OrderBy(t => t.ExeName, StringComparer.OrdinalIgnoreCase).ThenBy(t => t.Path, StringComparer.OrdinalIgnoreCase).ToList();
    }

    private static void Walk(string dir, int depth, List<InstalledTool> found)
    {
        try
        {
            foreach (var f in Directory.EnumerateFiles(dir, "*.exe"))
            {
                var name = System.IO.Path.GetFileName(f);
                if (SkipExePrefixes.Any(p => name.StartsWith(p, StringComparison.OrdinalIgnoreCase))) continue;
                string? ver = null;
                try { ver = FileVersionInfo.GetVersionInfo(f).FileVersion; } catch { }
                found.Add(new InstalledTool(name, f, Classify(f), ver));
            }
            if (depth >= MaxDepth) return;
            foreach (var d in Directory.EnumerateDirectories(dir))
            {
                var dn = System.IO.Path.GetFileName(d);
                if (SkipDirs.Contains(dn)) continue;
                Walk(d, depth + 1, found);
            }
        }
        catch (UnauthorizedAccessException) { }
        catch (IOException) { }
    }

    public static BuildFlavor Classify(string exePath)
    {
        var segs = exePath.Split(System.IO.Path.DirectorySeparatorChar, System.IO.Path.AltDirectorySeparatorChar);
        if (segs.Take(segs.Length - 1).Any(s => s.Equals("net9", StringComparison.OrdinalIgnoreCase)))
            return BuildFlavor.Net9;
        try
        {
            AssemblyName.GetAssemblyName(exePath);
            return BuildFlavor.Net4;
        }
        catch
        {
            return BuildFlavor.Net9;
        }
    }

    public InstalledTool? Resolve(string exeName, BuildFlavor? preferred = null)
    {
        if (!exeName.EndsWith(".exe", StringComparison.OrdinalIgnoreCase)) exeName += ".exe";
        var matches = _all.Where(t => t.ExeName.Equals(exeName, StringComparison.OrdinalIgnoreCase)).ToList();
        if (matches.Count == 0) return null;

        var want = preferred ?? BuildFlavor.Net9;
        if (want == BuildFlavor.Net9 && !RuntimeProbe.HasNet9())
        {
            var net4 = matches.FirstOrDefault(m => m.Flavor == BuildFlavor.Net4);
            if (net4 != null) return net4;
        }
        return matches.FirstOrDefault(m => m.Flavor == want) ?? matches[0];
    }
}
