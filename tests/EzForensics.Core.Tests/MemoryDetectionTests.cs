using System.Text;
using EzForensics.Core.Memory;
using FluentAssertions;

namespace EzForensics.Core.Tests;

public sealed class MemoryDetectionTests : IDisposable
{
    private readonly string _dir = Path.Combine(Path.GetTempPath(), "ezmem_" + Guid.NewGuid().ToString("N"));

    public MemoryDetectionTests() => Directory.CreateDirectory(_dir);

    public void Dispose()
    {
        try { Directory.Delete(_dir, true); } catch { }
    }

    private string Make(string name, long length, params (long Offset, byte[] Data)[] writes)
    {
        var p = Path.Combine(_dir, name);
        Directory.CreateDirectory(Path.GetDirectoryName(p)!);
        using var fs = new FileStream(p, FileMode.Create, FileAccess.Write);
        fs.SetLength(length);
        foreach (var (off, data) in writes) { fs.Position = off; fs.Write(data); }
        return p;
    }

    private static byte[] A(string s) => Encoding.ASCII.GetBytes(s);
    private static byte[] U32(uint v) => BitConverter.GetBytes(v);
    private static byte[] U64(ulong v) => BitConverter.GetBytes(v);

    /// <param name="bugCheck">Default 0xE2 (MANUALLY_INITIATED_CRASH): a dump Windows wrote at a real bug check.</param>
    private string CrashDump(string name, bool is64, uint dumpType, string? bitmapSig = null, long pages = 0x80000, uint bugCheck = 0xE2)
    {
        var w = new List<(long, byte[])> { (0, A(is64 ? "PAGEDU64" : "PAGEDUMP")), (is64 ? 0xF98 : 0xF88, U32(dumpType)), (is64 ? 0x38 : 0x28, U32(bugCheck)) };
        if (is64) w.Add((0x90, U64((ulong)pages)));
        else w.Add((0x68, U32((uint)pages)));
        if (bitmapSig != null) w.Add((is64 ? 0x2000 : 0x1000, A(bitmapSig)));
        return Make(name, 0x4000, w.ToArray());
    }

    [Theory]
    [InlineData(1u, null, DumpKind.Full, null)]
    [InlineData(2u, null, DumpKind.Kernel, "user-mode pages missing")]
    [InlineData(5u, null, DumpKind.Bitmap, null)]
    [InlineData(6u, null, DumpKind.Active, null)]
    [InlineData(5u, "SDMP", DumpKind.BitmapKernel, "user-mode pages missing")]
    [InlineData(5u, "FDMP", DumpKind.BitmapFull, null)]
    public void CrashDump64(uint type, string? sig, DumpKind kind, string? note)
    {
        var m = MemoryImageDetector.Detect(CrashDump("a.dmp", true, type, sig));
        m.Should().NotBeNull();
        m!.Format.Should().Be(MemoryFormat.WindowsCrashDump);
        m.Os.Should().Be(MemoryOs.Windows);
        m.DumpKind.Should().Be(kind);
        m.Note.Should().Be(note);
        m.PhysicalPages.Should().Be(0x80000);
    }

    [Theory]
    [InlineData(true, 0u, true)]
    [InlineData(true, 0x161u, true)]
    [InlineData(true, 0x80u, false)]   // NMI_HARDWARE_FAILURE (samples\C\memory.dmp: NMI injected into a VM)
    [InlineData(true, 0xE2u, false)]
    [InlineData(false, 0u, true)]
    [InlineData(false, 0xD1u, false)]
    public void CrashDump_bug_check_tells_live_from_halted(bool is64, uint bugCheck, bool live)
    {
        var path = CrashDump("live.dmp", is64, 1, bugCheck: bugCheck);
        MemoryImageDetector.ReadBugCheckCode(path).Should().Be(bugCheck);
        MemoryImageDetector.IsLiveDumpBugCheck(bugCheck).Should().Be(live);
        var m = MemoryImageDetector.Detect(path)!;
        if (live) m.Note.Should().Contain("written from a running system").And.Contain($"0x{bugCheck:X}");
        else m.Note.Should().BeNull();
    }

    [Fact]
    public void Kernel_dump_written_live_keeps_both_notes()
    {
        var m = MemoryImageDetector.Detect(CrashDump("k.dmp", true, 5, "SDMP", bugCheck: 0x161))!;
        m.Note.Should().StartWith("user-mode pages missing; written from a running system");
    }

    [Fact]
    public void ReadBugCheckCode_is_null_for_other_files()
    {
        var p = Path.Combine(_dir, "x.raw");
        File.WriteAllBytes(p, new byte[0x100]);
        MemoryImageDetector.ReadBugCheckCode(p).Should().BeNull();
        MemoryImageDetector.ReadBugCheckCode(Path.Combine(_dir, "missing.dmp")).Should().BeNull();
    }

    [Fact]
    public void CrashDump32()
    {
        var m = MemoryImageDetector.Detect(CrashDump("b.dmp", false, 1, pages: 1234));
        m!.DumpKind.Should().Be(DumpKind.Full);
        m.PhysicalPages.Should().Be(1234);
    }

    [Fact]
    public void CrashDump_SmallFileStillDetectedByMagic()
    {
        MemoryImageDetector.Detect(CrashDump("MEMORY.DMP", true, 1)).Should().NotBeNull();
    }

    [Fact]
    public void Minidump_IsRejected()
    {
        MemoryImageDetector.Detect(Make("mini.dmp", 200L << 20, (0, A("MDMP")))).Should().BeNull();
    }

    [Fact]
    public void Hiberfil_RequiresName()
    {
        MemoryImageDetector.Detect(Make("hiberfil.sys", 1 << 20, (0, A("hibr")))).Should().NotBeNull().And.Subject.As<MemoryImage>().Format.Should().Be(MemoryFormat.Hibernation);
        foreach (var magic in new[] { "HIBR", "wake", "RSTR" })
            MemoryImageDetector.Detect(Make("sub" + magic + "/hiberfil.sys", 1 << 20, (0, A(magic)))).Should().NotBeNull();
        MemoryImageDetector.Detect(Make("other.sys", 1 << 20, (0, A("hibr")))).Should().BeNull();
    }

    [Fact]
    public void Lime()
    {
        var m = MemoryImageDetector.Detect(Make("x.lime", 1 << 20, (0, A("EMiL"))));
        m!.Format.Should().Be(MemoryFormat.LiME);
        m.Os.Should().Be(MemoryOs.Linux);
    }

    [Fact]
    public void VmwareState_NeedsVmem()
    {
        var vmss = Make("vm.vmss", 4096, (0, U32(0xBED2BED2)));
        MemoryImageDetector.Detect(vmss).Should().BeNull();
        var vmem = Make("vm.vmem", 8192);
        var m = MemoryImageDetector.Detect(vmss);
        m!.Format.Should().Be(MemoryFormat.VMware);
        m.CompanionPath.Should().Be(vmem);
        MemoryImageDetector.Detect(Make("vm2.vmsn", 4096, (0, U32(0x12345678)))).Should().BeNull();
    }

    [Fact]
    public void ElfCore_OnlyType4()
    {
        var core = new byte[20]; core[0] = 0x7F; core[1] = (byte)'E'; core[2] = (byte)'L'; core[3] = (byte)'F'; core[4] = 2; core[5] = 1; core[16] = 4;
        MemoryImageDetector.Detect(Make("vb.elf", 1 << 20, (0, core)))!.Format.Should().Be(MemoryFormat.ElfCore);
        core[16] = 2;
        MemoryImageDetector.Detect(Make("exe.elf", 1 << 20, (0, core))).Should().BeNull();
    }

    [Fact]
    public void Raw_WindowsMarker()
    {
        var m = MemoryImageDetector.Detect(Make("w.raw", 256L << 20, (1 << 20, A("KDBG"))));
        m!.Format.Should().Be(MemoryFormat.Raw);
        m.Os.Should().Be(MemoryOs.Windows);
        m.PhysicalPages.Should().Be(65536);
    }

    [Fact]
    public void Raw_LinuxAndMacMarkers()
    {
        MemoryImageDetector.Detect(Make("l.mem", 256L << 20, (5 << 20, A("Linux version 5.15"))))!.Os.Should().Be(MemoryOs.Linux);
        var mac = MemoryImageDetector.Detect(Make("m.mem", 256L << 20, (5 << 20, A("Darwin Kernel Version 21"))));
        mac!.Os.Should().Be(MemoryOs.Mac);
        mac.Note.Should().NotBeNull();
    }

    [Fact]
    public void Raw_Rules()
    {
        MemoryImageDetector.Detect(Make("small.raw", 255L << 20, (1 << 20, A("KDBG")))).Should().BeNull();
        MemoryImageDetector.Detect(Make("odd.raw", (256L << 20) + 1, (1 << 20, A("KDBG")))).Should().BeNull();
        MemoryImageDetector.Detect(Make("nomarker.raw", 256L << 20)).Should().BeNull();
        MemoryImageDetector.Detect(Make("late.raw", 256L << 20, (100L << 20, A("KDBG")))).Should().BeNull(); // beyond the 64 MB scan
        MemoryImageDetector.Detect(Make("x.bin", 256L << 20, (1 << 20, A("KDBG")))).Should().BeNull();
        MemoryImageDetector.Detect(Make("x.img", 256L << 20, (1 << 20, A("KDBG")))).Should().BeNull();
        MemoryImageDetector.Detect(Make("pagefile.sys", 256L << 20, (1 << 20, A("KDBG")))).Should().BeNull();
        MemoryImageDetector.Detect(Make("swapfile.sys", 256L << 20, (1 << 20, A("KDBG")))).Should().BeNull();
        MemoryImageDetector.Detect(Make("tiny.dmp", 10L << 20, (1 << 20, A("KDBG")))).Should().BeNull();
        MemoryImageDetector.Detect(Path.Combine(_dir, "missing.raw")).Should().BeNull();
    }

    [Fact]
    public void Raw_MarkerAcrossChunkBoundary()
    {
        // 4 MB scan chunk boundary: put "ntoskrnl.exe" straddling it.
        MemoryImageDetector.Detect(Make("edge.raw", 256L << 20, ((4L << 20) - 5, A("ntoskrnl.exe")))).Should().NotBeNull();
    }

    [Fact]
    public void Scanner_FindsImages_SkipsDirsAndCovers_WindowsMemoryDmp()
    {
        CrashDump("case/MEMORY.DMP", true, 1);
        CrashDump("case/Windows/MEMORY.DMP", true, 2);
        CrashDump("case/Users/u/Desktop/dump.dmp", true, 1);
        CrashDump("case/Windows/System32/hidden.dmp", true, 1); // skipped dir
        Make("case/hiberfil.sys", 1 << 20, (0, A("hibr")));
        Make("case/pagefile.sys", 1 << 20);
        Make("case/x.bin", 1 << 20, (0, A("EMiL")));
        var found = MemoryImageScanner.Scan(Path.Combine(_dir, "case"));
        found.Select(f => Path.GetFileName(f.Path)).Should().BeEquivalentTo("MEMORY.DMP", "MEMORY.DMP", "dump.dmp", "hiberfil.sys");
    }

    [Fact]
    public void Scanner_FilePath_And_Cancel()
    {
        var p = CrashDump("one.dmp", true, 1);
        MemoryImageScanner.Scan(p).Should().ContainSingle();
        var cts = new CancellationTokenSource(); cts.Cancel();
        Assert.Throws<OperationCanceledException>(() => MemoryImageScanner.Scan(_dir, null, cts.Token));
    }

    [Fact]
    public void HostHint_MajorityVote()
    {
        static byte[] U(string s) => Encoding.Unicode.GetBytes(s);
        var p = Make("hint.raw", 20L << 20,
            (100, U("COMPUTERNAME=WRONG\0")),
            ((8L << 20) - 20, U("COMPUTERNAME=DESKTOP-ABC\0")), // straddles a chunk boundary
            (12L << 20, U("COMPUTERNAME=DESKTOP-ABC\0")),
            (15L << 20, U("COMPUTERNAME=DESKTOP-ABC\0")));
        MemoryHostHint.TryGetComputerName(p).Should().Be("DESKTOP-ABC");
        MemoryHostHint.TryGetComputerName(Make("none.raw", 1 << 20)).Should().BeNull();
        MemoryHostHint.TryGetComputerName(p, 1000).Should().Be("WRONG"); // byte cap honoured
        var cts = new CancellationTokenSource(); cts.Cancel();
        Assert.Throws<OperationCanceledException>(() => MemoryHostHint.TryGetComputerName(p, 4L << 30, cts.Token));
    }
}
