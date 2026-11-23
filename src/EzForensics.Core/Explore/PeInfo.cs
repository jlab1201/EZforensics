using PeNet;

namespace EzForensics.Core.Explore;

/// <summary>One PE section.</summary>
/// <param name="WriteExecute">Both writable and executable (self-modifying code, common in packers and shellcode loaders).</param>
public sealed record PeSection(string Name, uint VirtualAddress, uint VirtualSize, uint RawOffset, uint RawSize, uint Characteristics,
    double Entropy, bool Executable, bool Writable, bool WriteExecute);

/// <summary>Functions imported from one DLL (by-ordinal imports appear as <c>#&lt;ordinal&gt;</c>).</summary>
public sealed record PeImport(string Dll, IReadOnlyList<string> Functions);

public sealed record PeExport(string? Name, ushort Ordinal, uint Address, string? ForwardName);

/// <summary>Static PE facts from PeNet (Apache-2.0). Signature checks are not done here (see <see cref="FileFactsReader"/>).</summary>
/// <param name="TimestampUtc">COFF TimeDateStamp; null when zero.</param>
/// <param name="TimestampZero">TimeDateStamp is 0 (stripped or tampered).</param>
/// <param name="TimestampInFuture">TimeDateStamp is after the time of analysis (tampered, or a reproducible-build hash).</param>
/// <param name="OverlayOffset">Start of data after the last section; null when there is none.</param>
/// <param name="OverlaySize">Overlay bytes excluding an Authenticode certificate table at the end.</param>
public sealed record PeInfo(
    string Machine,
    bool Is64Bit,
    bool IsDll,
    bool IsDriver,
    bool IsDotNet,
    uint TimeDateStamp,
    DateTime? TimestampUtc,
    bool TimestampZero,
    bool TimestampInFuture,
    string Subsystem,
    ulong ImageBase,
    uint EntryPoint,
    string? ImpHash,
    IReadOnlyList<PeSection> Sections,
    IReadOnlyList<PeImport> Imports,
    IReadOnlyList<PeExport> Exports,
    long? OverlayOffset,
    long OverlaySize,
    long CertificateSize,
    long FileSize)
{
    public bool HasWriteExecuteSection => Sections.Any(s => s.WriteExecute);
    public int ImportedFunctionCount => Imports.Sum(i => i.Functions.Count);

    private const uint MemExecute = 0x20000000, MemWrite = 0x80000000;
    private const long MaxSectionBytesForEntropy = 256L * 1024 * 1024;

    /// <summary>Parses <paramref name="path"/> (opened read-only, shared, never through a reparse point); null when it is not a PE file.</summary>
    public static PeInfo? Read(string path, DateTime? nowUtc = null)
    {
        using var fs = FileFactsReader.OpenEvidence(path);
        return Read(fs, nowUtc);
    }

    /// <summary>Parses a seekable stream; null when it is not a PE file. The stream is left open.</summary>
    public static PeInfo? Read(Stream s, DateTime? nowUtc = null)
    {
        if (!s.CanSeek) throw new ArgumentException("A seekable stream is required.", nameof(s));
        s.Position = 0;
        if (!PeFile.IsPeFile(s)) return null;
        s.Position = 0;
        if (!PeFile.TryParse(s, out var pe) || pe?.ImageNtHeaders == null) return null;

        var fh = pe.ImageNtHeaders.FileHeader;
        var oh = pe.ImageNtHeaders.OptionalHeader;
        long fileSize = s.Length;

        uint ts = fh.TimeDateStamp;
        DateTime? tsUtc = ts == 0 ? null : DateTime.UnixEpoch.AddSeconds(ts);
        var now = nowUtc ?? DateTime.UtcNow;

        var sections = new List<PeSection>();
        long lastEnd = oh.SizeOfHeaders;
        foreach (var h in pe.ImageSectionHeaders ?? Array.Empty<PeNet.Header.Pe.ImageSectionHeader>())
        {
            uint ch = (uint)h.Characteristics;
            bool x = (ch & MemExecute) != 0, w = (ch & MemWrite) != 0;
            double ent = SectionEntropy(s, h.PointerToRawData, h.SizeOfRawData);
            sections.Add(new PeSection(h.Name ?? "", h.VirtualAddress, h.VirtualSize, h.PointerToRawData, h.SizeOfRawData, ch, ent, x, w, x && w));
            if (h.SizeOfRawData > 0) lastEnd = Math.Max(lastEnd, (long)h.PointerToRawData + h.SizeOfRawData);
        }

        // The Authenticode certificate table (data directory 4) holds a file offset, and sits at the end of signed files.
        long certOff = 0, certSize = 0;
        try
        {
            var dd = oh.DataDirectory;
            if (dd != null && dd.Length > 4 && dd[4].Size > 0) { certOff = dd[4].VirtualAddress; certSize = dd[4].Size; }
        }
        catch (Exception) { }
        long? overlayOff = null;
        long overlaySize = 0;
        if (lastEnd < fileSize)
        {
            overlaySize = fileSize - lastEnd;
            if (certSize > 0 && certOff >= lastEnd && certOff + certSize <= fileSize) overlaySize -= certSize;
            if (overlaySize > 0) overlayOff = lastEnd; else overlaySize = 0;
        }

        var imports = (pe.ImportedFunctions ?? Array.Empty<PeNet.Header.Pe.ImportFunction>())
            .Where(f => !string.IsNullOrEmpty(f.DLL))
            .GroupBy(f => f.DLL!, StringComparer.OrdinalIgnoreCase)
            .Select(g => new PeImport(g.Key, g.Select(f => string.IsNullOrEmpty(f.Name) ? $"#{f.Hint}" : f.Name!).ToList()))
            .ToList();
        var exports = (pe.ExportedFunctions ?? Array.Empty<PeNet.Header.Pe.ExportFunction>())
            .Select(e => new PeExport(e.HasName ? e.Name : null, e.Ordinal, e.Address, e.HasForward ? e.ForwardName : null))
            .ToList();

        string? imphash = null;
        try { imphash = pe.ImpHash; } catch (Exception) { }

        return new PeInfo(
            fh.MachineResolved ?? fh.Machine.ToString(),
            pe.Is64Bit, pe.IsDll, pe.IsDriver, pe.IsDotNet,
            ts, tsUtc, ts == 0, tsUtc != null && tsUtc > now.AddDays(1),
            oh.SubsystemResolved ?? oh.Subsystem.ToString(),
            oh.ImageBase, oh.AddressOfEntryPoint, string.IsNullOrEmpty(imphash) ? null : imphash,
            sections, imports, exports, overlayOff, overlaySize, certSize, fileSize);
    }

    private static double SectionEntropy(Stream s, uint offset, uint size)
    {
        if (size == 0 || offset >= s.Length) return 0;
        long len = Math.Min(Math.Min(size, s.Length - offset), MaxSectionBytesForEntropy);
        var hist = new long[256];
        var buf = new byte[81920];
        s.Position = offset;
        long left = len, total = 0;
        while (left > 0)
        {
            int r = s.Read(buf, 0, (int)Math.Min(buf.Length, left));
            if (r <= 0) break;
            for (int i = 0; i < r; i++) hist[buf[i]]++;
            left -= r; total += r;
        }
        return Entropy.FromHistogram(hist, total);
    }
}
