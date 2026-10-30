using System.Diagnostics;
using EzForensics.Core.Memory;
using FluentAssertions;
using Xunit.Abstractions;

namespace EzForensics.Core.Tests;

/// <summary>Runs against the real samples\C\memory.dmp when present (skipped silently otherwise).</summary>
public class MemoryDetectionSampleTests
{
    private readonly ITestOutputHelper _out;
    public MemoryDetectionSampleTests(ITestOutputHelper o) => _out = o;

    private static string? FindSample()
    {
        var d = new DirectoryInfo(AppContext.BaseDirectory);
        while (d != null)
        {
            var p = Path.Combine(d.FullName, "samples", "C", "memory.dmp");
            if (File.Exists(p)) return p;
            var marker = Path.Combine(".claude", "worktrees");
            var idx = d.FullName.IndexOf(marker, StringComparison.OrdinalIgnoreCase);
            if (idx > 0)
            {
                var main = Path.Combine(d.FullName[..(idx - 1)], "samples", "C", "memory.dmp");
                if (File.Exists(main)) return main;
            }
            d = d.Parent;
        }
        return null;
    }

    [Fact]
    public void RealSample_DetectsAndHints()
    {
        var p = FindSample();
        if (p == null) return;
        var sw = Stopwatch.StartNew();
        var m = MemoryImageDetector.Detect(p);
        _out.WriteLine($"detect {sw.ElapsedMilliseconds} ms: {m}");
        m.Should().NotBeNull();
        m!.Format.Should().Be(MemoryFormat.WindowsCrashDump);
        m.DumpKind.Should().Be(DumpKind.Full);
        m.PhysicalPages.Should().Be(0x80000);
        sw.Restart();
        var hint = MemoryHostHint.TryGetComputerName(p);
        _out.WriteLine($"hint '{hint}' in {sw.Elapsed.TotalSeconds:F1} s");
    }
}
