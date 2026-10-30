namespace EzForensics.Core.Memory;

public enum MemoryFormat { Unknown, WindowsCrashDump, Hibernation, LiME, VMware, ElfCore, Raw }

public enum MemoryOs { Unknown, Windows, Linux, Mac }

/// <summary>Crash dump flavour (DumpType 1 full, 2 kernel, 5 bitmap; SDMP = kernel, FDMP = full).</summary>
public enum DumpKind { Full, Kernel, Bitmap, BitmapFull, BitmapKernel, Active }

/// <summary>A physical memory image found by <see cref="MemoryImageDetector"/>.</summary>
public sealed record MemoryImage(string Path, long Size, MemoryFormat Format, MemoryOs Os, DumpKind? DumpKind, long? PhysicalPages, string? Note)
{
    /// <summary>The .vmem next to a .vmss/.vmsn.</summary>
    public string? CompanionPath { get; init; }

    public string Name => System.IO.Path.GetFileName(Path);
}
