using System.Text;

namespace EzForensics.Core.Memory;

/// <summary>Magic-byte detection of memory images. Reads headers only, plus a bounded 64 MB scan for raw images.</summary>
public static class MemoryImageDetector
{
    private const long RawMinSize = 256L << 20;
    private const long DmpMinSize = 64L << 20;
    private const int RawScanBytes = 64 << 20;

    private static readonly HashSet<string> RawExtensions = new(StringComparer.OrdinalIgnoreCase) { ".raw", ".mem", ".vmem", ".lime", ".dmp" };
    private static readonly HashSet<string> ProbeExtensions = new(StringComparer.OrdinalIgnoreCase) { ".raw", ".mem", ".vmem", ".lime", ".dmp", ".vmss", ".vmsn", ".sys", ".core", ".elf", ".dump" };

    /// <summary>True when the name is worth opening (the extension only decides this; the magic decides the format).</summary>
    public static bool IsCandidateName(string fileName)
    {
        if (string.IsNullOrEmpty(fileName)) return false;
        if (IsExcludedName(fileName)) return false;
        return ProbeExtensions.Contains(Path.GetExtension(fileName));
    }

    private static bool IsExcludedName(string fileName) =>
        fileName.Equals("pagefile.sys", StringComparison.OrdinalIgnoreCase) || fileName.Equals("swapfile.sys", StringComparison.OrdinalIgnoreCase);

    /// <summary>Null = not a memory image (or unreadable).</summary>
    public static MemoryImage? Detect(string path)
    {
        try
        {
            if (string.IsNullOrWhiteSpace(path) || !File.Exists(path)) return null;
            var name = Path.GetFileName(path);
            var ext = Path.GetExtension(name);
            if (IsExcludedName(name)) return null;
            if (ext.Equals(".bin", StringComparison.OrdinalIgnoreCase) || ext.Equals(".img", StringComparison.OrdinalIgnoreCase)) return null;
            var full = Path.GetFullPath(path);

            using var fs = new FileStream(full, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete, 1, FileOptions.None);
            long size = fs.Length;
            if (size < 64) return null;

            var head = new byte[4096];
            int n = ReadAt(fs, 0, head);
            var h = head.AsSpan(0, n);

            if (Starts(h, "PAGEDU64") || Starts(h, "PAGEDUMP"))
                return DetectCrashDump(fs, full, size, Starts(h, "PAGEDU64"));
            if (Starts(h, "MDMP")) return null;

            if (Starts(h, "hibr") || Starts(h, "HIBR") || Starts(h, "wake") || Starts(h, "RSTR"))
            {
                if (!name.Equals("hiberfil.sys", StringComparison.OrdinalIgnoreCase)) return null;
                return new MemoryImage(full, size, MemoryFormat.Hibernation, MemoryOs.Windows, null, null, "hibernation file");
            }

            if (Starts(h, "EMiL"))
                return new MemoryImage(full, size, MemoryFormat.LiME, MemoryOs.Linux, null, null, null);

            if (n >= 4 && (ext.Equals(".vmss", StringComparison.OrdinalIgnoreCase) || ext.Equals(".vmsn", StringComparison.OrdinalIgnoreCase)))
            {
                uint m = BitConverter.ToUInt32(h);
                if (m is 0xBED2BED2 or 0xBED3BED3 or 0xBAD1BAD1)
                {
                    var vmem = Path.ChangeExtension(full, ".vmem");
                    if (!File.Exists(vmem)) return null; // state without the memory file is not analysable
                    long vsize = new FileInfo(vmem).Length;
                    return new MemoryImage(full, vsize, MemoryFormat.VMware, MemoryOs.Unknown, null, null, "VMware snapshot (" + ext + " + .vmem)") { CompanionPath = vmem };
                }
                return null;
            }

            if (n >= 20 && h[0] == 0x7F && h[1] == (byte)'E' && h[2] == (byte)'L' && h[3] == (byte)'F')
            {
                bool le = h[5] != 2;
                int eType = le ? h[16] | h[17] << 8 : h[17] | h[16] << 8;
                if (eType == 4) return new MemoryImage(full, size, MemoryFormat.ElfCore, MemoryOs.Unknown, null, null, "ELF core dump (VirtualBox/QEMU)");
                return null;
            }

            if (!RawExtensions.Contains(ext)) return null;
            if (ext.Equals(".dmp", StringComparison.OrdinalIgnoreCase) && size < DmpMinSize) return null;
            return DetectRaw(fs, full, size);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or ArgumentException or NotSupportedException or System.Security.SecurityException)
        {
            return null;
        }
    }

    /// <summary>LIVE_SYSTEM_DUMP: a live kernel dump taken while Windows keeps running (Task Manager, livekd -ml, WER).</summary>
    public const uint LiveSystemDumpBugCheck = 0x161;

    /// <summary>
    /// True when a crash dump with this bug check code was written from a running system: 0 is never a real bug check (the header was made by an
    /// acquisition tool such as DumpIt / WinPmem / LiveKD), 0x161 is a Windows live dump. Any other code means Windows wrote the dump at a bug check
    /// with every processor halted, so the image is internally consistent (no page smear).
    /// </summary>
    public static bool IsLiveDumpBugCheck(uint code) => code == 0 || code == LiveSystemDumpBugCheck;

    /// <summary>The <c>BugCheckCode</c> of a Windows crash dump header (<c>PAGEDU64</c> at 0x38, <c>PAGEDUMP</c> at 0x28); null when the file is not one.</summary>
    public static uint? ReadBugCheckCode(string path)
    {
        try
        {
            using var fs = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete, 1, FileOptions.None);
            var h = new byte[0x40];
            if (ReadAt(fs, 0, h) < h.Length) return null;
            if (Starts(h, "PAGEDU64")) return BitConverter.ToUInt32(h, 0x38);
            if (Starts(h, "PAGEDUMP")) return BitConverter.ToUInt32(h, 0x28);
            return null;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or ArgumentException or NotSupportedException or System.Security.SecurityException)
        {
            return null;
        }
    }

    private static MemoryImage? DetectCrashDump(FileStream fs, string full, long size, bool is64)
    {
        var buf = new byte[0x2010];
        int n = ReadAt(fs, 0, buf);
        int typeOff = is64 ? 0xF98 : 0xF88;
        if (n < typeOff + 4) return null;
        uint dt = BitConverter.ToUInt32(buf, typeOff);
        DumpKind? kind = dt switch { 1 => DumpKind.Full, 2 => DumpKind.Kernel, 5 => DumpKind.Bitmap, 6 => DumpKind.Active, _ => null };
        if (kind == DumpKind.Bitmap)
        {
            int bmOff = is64 ? 0x2000 : 0x1000;
            if (n >= bmOff + 4)
            {
                var sig = Encoding.ASCII.GetString(buf, bmOff, 4);
                if (sig == "SDMP") kind = DumpKind.BitmapKernel;
                else if (sig == "FDMP") kind = DumpKind.BitmapFull;
            }
        }
        string? note = kind is DumpKind.Kernel or DumpKind.BitmapKernel ? "user-mode pages missing" : null;
        int bcOff = is64 ? 0x38 : 0x28;
        if (n >= bcOff + 4 && IsLiveDumpBugCheck(BitConverter.ToUInt32(buf, bcOff)))
        {
            var live = $"written from a running system (bug check code 0x{BitConverter.ToUInt32(buf, bcOff):X}), so structures may be internally inconsistent";
            note = note == null ? live : note + "; " + live;
        }

        long? pages = null;
        if (is64 && n >= 0x98)
        {
            // DUMP_HEADER64: PhysicalMemoryBlock at 0x88 { u32 NumberOfRuns; u32 pad; u64 NumberOfPages; runs... }
            long p = BitConverter.ToInt64(buf, 0x90);
            if (p > 0 && p < (1L << 40)) pages = p;
        }
        else if (!is64 && n >= 0x6C)
        {
            // DUMP_HEADER32: PhysicalMemoryBlock at 0x64 { u32 NumberOfRuns; u32 NumberOfPages; runs... }
            long p = BitConverter.ToUInt32(buf, 0x68);
            if (p > 0) pages = p;
        }
        return new MemoryImage(full, size, MemoryFormat.WindowsCrashDump, MemoryOs.Windows, kind, pages, note);
    }

    private static MemoryImage? DetectRaw(FileStream fs, string full, long size)
    {
        if (size < RawMinSize || size % 4096 != 0) return null;
        var os = ScanForOs(fs, out var note);
        if (os == MemoryOs.Unknown) return null;
        return new MemoryImage(full, size, MemoryFormat.Raw, os, null, size / 4096, note);
    }

    private static readonly byte[][] WindowsMarkers = { Encoding.ASCII.GetBytes("KDBG"), Encoding.ASCII.GetBytes("ntoskrnl.exe") };
    private static readonly byte[] LinuxMarker = Encoding.ASCII.GetBytes("Linux version ");
    private static readonly byte[] DarwinMarker = Encoding.ASCII.GetBytes("Darwin Kernel Version");

    private static MemoryOs ScanForOs(FileStream fs, out string? note)
    {
        note = null;
        const int chunk = 4 << 20;
        const int overlap = 32;
        var buf = new byte[chunk + overlap];
        fs.Position = 0;
        long total = 0;
        int carry = 0;
        bool win = false, linux = false, mac = false;
        while (total < RawScanBytes)
        {
            int want = (int)Math.Min(chunk, RawScanBytes - total);
            int got = 0;
            while (got < want)
            {
                int r = fs.Read(buf, carry + got, want - got);
                if (r <= 0) break;
                got += r;
            }
            if (got == 0) break;
            var span = buf.AsSpan(0, carry + got);
            foreach (var m in WindowsMarkers) if (span.IndexOf(m) >= 0) { win = true; break; }
            if (win) break;
            if (!linux && span.IndexOf(LinuxMarker) >= 0) linux = true;
            if (!mac && span.IndexOf(DarwinMarker) >= 0) mac = true;
            total += got;
            carry = Math.Min(overlap, span.Length);
            span[^carry..].CopyTo(buf);
            if (got < want) break;
        }
        if (win) return MemoryOs.Windows;
        if (linux) { note = "Linux: needs a matching ISF symbol table"; return MemoryOs.Linux; }
        if (mac) { note = "macOS: not supported (flagged only)"; return MemoryOs.Mac; }
        return MemoryOs.Unknown;
    }

    private static bool Starts(ReadOnlySpan<byte> h, string ascii)
    {
        if (h.Length < ascii.Length) return false;
        for (int i = 0; i < ascii.Length; i++) if (h[i] != (byte)ascii[i]) return false;
        return true;
    }

    private static int ReadAt(FileStream fs, long pos, byte[] buf)
    {
        fs.Position = pos;
        int n = 0;
        while (n < buf.Length)
        {
            int r = fs.Read(buf, n, buf.Length - n);
            if (r <= 0) break;
            n += r;
        }
        return n;
    }
}
