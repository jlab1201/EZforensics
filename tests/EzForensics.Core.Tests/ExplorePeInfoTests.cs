using EzForensics.Core.Explore;
using FluentAssertions;

namespace EzForensics.Core.Tests;

public sealed class ExplorePeInfoTests : IDisposable
{
    private readonly TempDir _tmp = new();
    public void Dispose() => _tmp.Dispose();

    [Fact]
    public void Native_apphost_exe_has_headers_sections_and_imports()
    {
        var pe = PeInfo.Read(FakeToolPaths.Exe)!;
        pe.Should().NotBeNull();
        pe.IsDll.Should().BeFalse();
        pe.IsDotNet.Should().BeFalse();
        pe.Sections.Should().Contain(s => s.Name == ".text" && s.Executable && !s.Writable);
        pe.Sections.Should().OnlyContain(s => s.Entropy >= 0 && s.Entropy <= 8);
        pe.Imports.Should().Contain(i => i.Dll.Equals("KERNEL32.dll", StringComparison.OrdinalIgnoreCase) && i.Functions.Count > 0);
        pe.ImpHash.Should().MatchRegex("^[0-9a-f]{32}$");
        pe.Subsystem.Should().NotBeNullOrEmpty();
        pe.Machine.Should().NotBeNullOrEmpty();
        pe.HasWriteExecuteSection.Should().BeFalse();
    }

    [Fact]
    public void Managed_assembly_is_flagged_dotnet()
    {
        var pe = PeInfo.Read(FakeToolPaths.Dll)!;
        pe.IsDotNet.Should().BeTrue();
        PeInfo.Read(typeof(PeInfo).Assembly.Location)!.IsDll.Should().BeTrue();
    }

    [Fact]
    public void System_dll_has_exports()
    {
        if (!OperatingSystem.IsWindows()) return;
        var k32 = Path.Combine(Environment.SystemDirectory, "kernel32.dll");
        var pe = PeInfo.Read(k32)!;
        pe.IsDll.Should().BeTrue();
        pe.Is64Bit.Should().Be(Environment.Is64BitOperatingSystem && Environment.Is64BitProcess);
        pe.Exports.Should().Contain(e => e.Name == "CreateFileW");
    }

    [Fact]
    public void Zero_timestamp_wx_section_and_overlay_are_reported()
    {
        var bytes = File.ReadAllBytes(FakeToolPaths.Exe);
        int lfanew = BitConverter.ToInt32(bytes, 0x3C);
        BitConverter.GetBytes(0u).CopyTo(bytes, lfanew + 8);                      // TimeDateStamp
        ushort optSize = BitConverter.ToUInt16(bytes, lfanew + 20);
        int sec0 = lfanew + 24 + optSize;
        uint ch = BitConverter.ToUInt32(bytes, sec0 + 36);
        BitConverter.GetBytes(ch | 0x20000000u | 0x80000000u).CopyTo(bytes, sec0 + 36);
        // Drop any certificate table so the appended bytes are pure overlay.
        int certDir = lfanew + 24 + (BitConverter.ToUInt16(bytes, lfanew + 24) == 0x20B ? 112 : 96) + 4 * 8;
        BitConverter.GetBytes(0UL).CopyTo(bytes, certDir);
        var overlay = Enumerable.Repeat((byte)0xAB, 1234).ToArray();
        var p = Path.Combine(_tmp.Path, "patched.exe");
        var original = PeInfo.Read(FakeToolPaths.Exe)!;
        File.WriteAllBytes(p, bytes.Take((int)(original.OverlayOffset ?? bytes.Length)).Concat(overlay).ToArray());

        var pe = PeInfo.Read(p)!;
        pe.TimestampZero.Should().BeTrue();
        pe.TimestampUtc.Should().BeNull();
        pe.Sections[0].WriteExecute.Should().BeTrue();
        pe.HasWriteExecuteSection.Should().BeTrue();
        pe.OverlaySize.Should().Be(1234);
        pe.OverlayOffset.Should().Be(new FileInfo(p).Length - 1234);
    }

    [Fact]
    public void Future_timestamp_is_flagged()
    {
        var pe = PeInfo.Read(FakeToolPaths.Exe, nowUtc: new DateTime(1990, 1, 1, 0, 0, 0, DateTimeKind.Utc))!;
        if (pe.TimestampZero) return;
        pe.TimestampInFuture.Should().BeTrue();
    }

    [Fact]
    public void Non_pe_returns_null()
    {
        var p = Path.Combine(_tmp.Path, "a.txt");
        File.WriteAllText(p, "MZ but not really a PE file at all");
        PeInfo.Read(p).Should().BeNull();
    }
}
