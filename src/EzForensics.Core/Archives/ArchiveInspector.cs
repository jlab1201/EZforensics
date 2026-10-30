using System.Buffers.Binary;

namespace EzForensics.Core.Archives;

/// <summary>
/// Inspection helpers: format-level facts read without SharpCompress (RAR header encryption flags, encryption method names),
/// plus a facade that handles virtual disks (no listing) before handing real archives to the engine.
/// </summary>
public static class ArchiveInspector
{
    /// <summary>Lists <paramref name="path"/>. Virtual disks and unknown files give an empty inspection (they are never extracted).</summary>
    public static ArchiveInspection Inspect(string path, IArchiveEngine? engine = null, string? password = null)
    {
        var fmt = ArchiveFormatDetector.Detect(path);
        if (!ArchiveFormatDetector.IsExtractable(fmt))
            return new ArchiveInspection(path, fmt, false, false, null, 0, 0, Array.Empty<ArchiveEntryInfo>(), false);
        return (engine ?? new SharpCompressArchiveEngine()).Inspect(path, password);
    }

    /// <summary>True when a RAR archive was made with -hp (encrypted headers): RAR4 main-header flag MHD_PASSWORD (0x80), RAR5 archive encryption header (type 4).</summary>
    public static bool RarHeadersEncrypted(string path)
    {
        try
        {
            using var fs = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read);
            var h = new byte[32];
            int n = ArchiveFormatDetector.ReadFull(fs, h);
            if (n < 13) return false;
            if (h[6] == 0x01)   // RAR5: 8-byte signature, then CRC32, vint size, vint type
            {
                int p = 8 + 4;
                ReadVInt(h, ref p);
                return ReadVInt(h, ref p) == 4;
            }
            // RAR4: 7-byte marker, main header: CRC(2) TYPE(1)=0x73 FLAGS(2)
            return h[9] == 0x73 && (BinaryPrimitives.ReadUInt16LittleEndian(h.AsSpan(10)) & 0x0080) != 0;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { return false; }
    }

    /// <summary>"AES-256" for RAR5, "AES-128" for RAR4.</summary>
    public static string RarEncryptionMethod(string path)
    {
        try
        {
            using var fs = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read);
            var h = new byte[7];
            ArchiveFormatDetector.ReadFull(fs, h);
            return h[6] == 0x01 ? "AES-256" : "AES-128";
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { return "AES"; }
    }

    private static long ReadVInt(byte[] b, ref int p)
    {
        long v = 0;
        for (int shift = 0; p < b.Length && shift < 63; shift += 7)
        {
            byte x = b[p++];
            v |= (long)(x & 0x7F) << shift;
            if ((x & 0x80) == 0) break;
        }
        return v;
    }
}
