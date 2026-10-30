using EzForensics.Core.Tools;

namespace EzForensics.Core.Tests;

internal static class FakeToolPaths
{
    /// <summary>Directory holding EzForensics.FakeTool.exe (same configuration as the test assembly).</summary>
    public static string Dir
    {
        get
        {
            var baseDir = new DirectoryInfo(AppContext.BaseDirectory.TrimEnd(Path.DirectorySeparatorChar));
            var tfm = baseDir.Name;                // net8.0
            var config = baseDir.Parent!.Name;     // Debug / Release
            var testProj = baseDir.Parent!.Parent!.Parent!; // tests/EzForensics.Core.Tests
            return Path.Combine(testProj.Parent!.FullName, "EzForensics.FakeTool", "bin", config, tfm);
        }
    }

    public static string Exe => Path.Combine(Dir, "EzForensics.FakeTool.exe");
    public static string Dll => Path.Combine(Dir, "EzForensics.FakeTool.dll");
}

internal sealed class TempDir : IDisposable
{
    public string Path { get; } = System.IO.Path.Combine(System.IO.Path.GetTempPath(), "ezforensics_" + Guid.NewGuid().ToString("N")[..8]);
    public TempDir() => Directory.CreateDirectory(Path);
    public void Dispose() { try { Directory.Delete(Path, true); } catch { } }
}

internal sealed class FakeLocator : IToolLocator
{
    private readonly string? _exe;
    public FakeLocator(string? exe) => _exe = exe;
    public void Rescan(string toolsRoot) { }
    public InstalledTool? Resolve(string exeName, BuildFlavor? preferred = null)
        => _exe == null ? null : new InstalledTool(Path.GetFileName(_exe), _exe, BuildFlavor.Net9, "1.0");
    public IReadOnlyList<InstalledTool> All => _exe == null ? Array.Empty<InstalledTool>() : new[] { Resolve("x")! };
}
