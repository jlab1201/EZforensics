using System.IO.Compression;
using System.Text;

namespace EzForensics.Core.Archives;

/// <summary>Detects the archive format from magic bytes (the extension is only a hint for which files to open).</summary>
public static class ArchiveFormatDetector
{
    private static readonly string[] ArchiveExtensions = { ".zip", ".7z", ".rar", ".tar", ".gz", ".tgz", ".vhd", ".vhdx" };

    /// <summary>True when the extension suggests an archive worth opening (used when scanning folders).</summary>
    public static bool HasArchiveExtension(string path) =>
        ArchiveExtensions.Contains(Path.GetExtension(path), StringComparer.OrdinalIgnoreCase);

    public static ArchiveFormat Detect(string path)
    {
        try
        {
            using var fs = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read);
            var head = new byte[512];
            int n = ReadFull(fs, head);
            var h = head.AsSpan(0, n);
            if (StartsWith(h, "PK\x03\x04") || StartsWith(h, "PK\x05\x06")) return ArchiveFormat.Zip;
            if (h.Length >= 6 && h[0] == (byte)'7' && h[1] == (byte)'z' && h[2] == 0xBC && h[3] == 0xAF && h[4] == 0x27 && h[5] == 0x1C) return ArchiveFormat.SevenZip;
            if (StartsWith(h, "Rar!\x1A\x07\x00") || StartsWith(h, "Rar!\x1A\x07\x01")) return ArchiveFormat.Rar;
            if (IsTarHeader(h)) return ArchiveFormat.Tar;
            if (h.Length >= 2 && h[0] == 0x1F && h[1] == 0x8B) return GzipHoldsTar(fs) ? ArchiveFormat.TarGz : ArchiveFormat.GZip;
            if (StartsWith(h, "vhdxfile")) return ArchiveFormat.Vhdx;
            if (StartsWith(h, "conectix")) return ArchiveFormat.Vhd;
            if (fs.Length >= 512)
            {
                fs.Seek(-512, SeekOrigin.End);
                var foot = new byte[8];
                if (ReadFull(fs, foot) == 8 && Encoding.ASCII.GetString(foot) == "conectix") return ArchiveFormat.Vhd;   // fixed VHD: footer only
            }
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { }
        return ArchiveFormat.Unknown;
    }

    /// <summary>Extractable by the engine (not a virtual disk, not unknown).</summary>
    public static bool IsExtractable(ArchiveFormat f) => f is ArchiveFormat.Zip or ArchiveFormat.SevenZip or ArchiveFormat.Rar or ArchiveFormat.Tar or ArchiveFormat.GZip or ArchiveFormat.TarGz;

    /// <summary>"ustar" at offset 257.</summary>
    internal static bool IsTarHeader(ReadOnlySpan<byte> h) =>
        h.Length >= 262 && h[257] == (byte)'u' && h[258] == (byte)'s' && h[259] == (byte)'t' && h[260] == (byte)'a' && h[261] == (byte)'r';

    private static bool GzipHoldsTar(FileStream fs)
    {
        try
        {
            fs.Seek(0, SeekOrigin.Begin);
            using var gz = new GZipStream(fs, CompressionMode.Decompress, leaveOpen: true);
            var buf = new byte[512];
            return IsTarHeader(buf.AsSpan(0, ReadFull(gz, buf)));
        }
        catch (InvalidDataException) { return false; }
    }

    internal static int ReadFull(Stream s, byte[] buf)
    {
        int total = 0, n;
        while (total < buf.Length && (n = s.Read(buf, total, buf.Length - total)) > 0) total += n;
        return total;
    }

    private static bool StartsWith(ReadOnlySpan<byte> h, string ascii)
    {
        if (h.Length < ascii.Length) return false;
        for (int i = 0; i < ascii.Length; i++) if (h[i] != (byte)ascii[i]) return false;
        return true;
    }
}
