using System.Buffers.Binary;
using System.Text;

namespace EzForensics.Core.Archives;

/// <summary>One central-directory record of a zip, with the parts SharpCompress does not expose (NTFS / extended timestamps, encryption kind, unix mode).</summary>
public sealed record ZipCentralEntry(int Index, string Name, ushort Flags, ushort Method, uint Crc, long CompressedSize, long Size, byte HostSystem,
    uint ExternalAttributes, DateTime? DosTime, DateTime? NtfsModifiedUtc, DateTime? NtfsAccessedUtc, DateTime? NtfsCreatedUtc,
    DateTime? UnixModifiedUtc, DateTime? UnixAccessedUtc, int? AesStrength)
{
    public bool IsEncrypted => (Flags & 0x0001) != 0;
    public bool IsAes => Method == 99 || AesStrength != null;
    /// <summary>Unix host with S_IFLNK in the high word of the external attributes.</summary>
    public bool IsSymlink => HostSystem == 3 && ((ExternalAttributes >> 16) & 0xF000) == 0xA000;

    public string? EncryptionMethod => !IsEncrypted ? null
        : IsAes ? AesStrength switch { 1 => "AES-128", 2 => "AES-192", _ => "AES-256" }
        : (Flags & 0x0040) != 0 ? "PKWARE strong encryption" : "ZipCrypto";

    /// <summary>Best times this record carries: NTFS 0x000a, else 0x5455, else the DOS time (local, unknown zone, 2 s).</summary>
    public (DateTime? M, DateTime? C, DateTime? A, EntryTimeSource Source) BestTimes()
    {
        if (NtfsModifiedUtc != null) return (NtfsModifiedUtc, NtfsCreatedUtc, NtfsAccessedUtc, EntryTimeSource.NtfsExtra);
        if (UnixModifiedUtc != null) return (UnixModifiedUtc, null, UnixAccessedUtc, EntryTimeSource.ExtendedTimestamp);
        if (DosTime is { } d) return (DateTime.SpecifyKind(d, DateTimeKind.Local).ToUniversalTime(), null, null, EntryTimeSource.DosLocal);
        return (null, null, null, EntryTimeSource.None);
    }
}

/// <summary>
/// Minimal zip central-directory parser (incl. Zip64) for the extra fields SharpCompress drops: NTFS 0x000a (mtime/atime/ctime as FILETIME),
/// Info-ZIP extended timestamp 0x5455 (mtime, plus atime from the local header; its "ctime" is never used as Created) and WinZip AES 0x9901.
/// </summary>
public static class ZipExtraFieldReader
{
    public static IReadOnlyList<ZipCentralEntry> Read(string path)
    {
        using var fs = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read);
        return Read(fs);
    }

    public static IReadOnlyList<ZipCentralEntry> Read(Stream fs)
    {
        var (cdOffset, cdSize, count) = FindCentralDirectory(fs);
        var list = new List<ZipCentralEntry>();
        // The end record is untrusted: a central directory that does not fit in the file is never allocated (a 22-byte file could
        // otherwise ask for a 2 GB buffer) nor sought to.
        if (cdSize <= 0 || cdSize > int.MaxValue || cdOffset < 0 || cdOffset > fs.Length || cdSize > fs.Length - cdOffset) return list;
        fs.Seek(cdOffset, SeekOrigin.Begin);
        var cd = new byte[cdSize];
        if (ArchiveFormatDetector.ReadFull(fs, cd) != cd.Length) return list;
        int p = 0;
        for (int i = 0; p + 46 <= cd.Length && BinaryPrimitives.ReadUInt32LittleEndian(cd.AsSpan(p)) == 0x02014b50; i++)
        {
            var s = cd.AsSpan(p);
            byte host = s[5];
            ushort flags = U16(s, 8), method = U16(s, 10), time = U16(s, 12), date = U16(s, 14);
            uint crc = U32(s, 16);
            long csize = U32(s, 20), usize = U32(s, 24);
            int nameLen = U16(s, 28), extraLen = U16(s, 30), commentLen = U16(s, 32);
            uint ext = U32(s, 38);
            long localOffset = U32(s, 42);
            if (p + 46 + nameLen + extraLen > cd.Length) break;
            var nameBytes = s.Slice(46, nameLen);
            var name = (flags & 0x0800) != 0 ? Encoding.UTF8.GetString(nameBytes) : Encoding.Latin1.GetString(nameBytes);
            var extra = s.Slice(46 + nameLen, extraLen);

            DateTime? nm = null, na = null, nc = null, um = null, ua = null;
            int? aes = null;
            bool unixHasAtime = false;
            foreach (var (id, data) in Fields(extra))
            {
                switch (id)
                {
                    case 0x0001:   // Zip64: usize, csize, offset when their 32-bit fields are saturated
                        int q = 0;
                        if (usize == 0xFFFFFFFF && q + 8 <= data.Length) { usize = (long)BinaryPrimitives.ReadUInt64LittleEndian(data[q..]); q += 8; }
                        if (csize == 0xFFFFFFFF && q + 8 <= data.Length) { csize = (long)BinaryPrimitives.ReadUInt64LittleEndian(data[q..]); q += 8; }
                        if (localOffset == 0xFFFFFFFF && q + 8 <= data.Length) { localOffset = (long)BinaryPrimitives.ReadUInt64LittleEndian(data[q..]); }
                        break;
                    case 0x000a:
                        ParseNtfs(data, ref nm, ref na, ref nc);
                        break;
                    case 0x5455:
                        if (data.Length >= 1)
                        {
                            unixHasAtime = (data[0] & 0x02) != 0;
                            if ((data[0] & 0x01) != 0 && data.Length >= 5) um = Unix(BinaryPrimitives.ReadInt32LittleEndian(data[1..]));
                        }
                        break;
                    case 0x9901:
                        if (data.Length >= 5) aes = data[4];
                        break;
                }
            }
            if (unixHasAtime && um != null) ua = LocalHeaderAtime(fs, localOffset);

            list.Add(new ZipCentralEntry(i, name, flags, method, crc, csize, usize, host, ext, Dos(date, time), nm, na, nc, um, ua, aes));
            p += 46 + nameLen + extraLen + commentLen;
            if (list.Count >= count && count > 0) break;
        }
        return list;
    }

    private static DateTime? LocalHeaderAtime(Stream fs, long offset)
    {
        try
        {
            var h = new byte[30];
            fs.Seek(offset, SeekOrigin.Begin);
            if (ArchiveFormatDetector.ReadFull(fs, h) != 30 || BinaryPrimitives.ReadUInt32LittleEndian(h) != 0x04034b50) return null;
            int nameLen = U16(h, 26), extraLen = U16(h, 28);
            fs.Seek(nameLen, SeekOrigin.Current);
            var extra = new byte[extraLen];
            if (ArchiveFormatDetector.ReadFull(fs, extra) != extraLen) return null;
            foreach (var (id, data) in Fields(extra))
            {
                if (id != 0x5455 || data.Length < 1) continue;
                int q = 1;
                if ((data[0] & 0x01) != 0) q += 4;
                if ((data[0] & 0x02) != 0 && q + 4 <= data.Length) return Unix(BinaryPrimitives.ReadInt32LittleEndian(data[q..]));
            }
        }
        catch (IOException) { }
        return null;
    }

    private static void ParseNtfs(ReadOnlySpan<byte> data, ref DateTime? m, ref DateTime? a, ref DateTime? c)
    {
        int q = 4;   // reserved
        while (q + 4 <= data.Length)
        {
            int tag = U16(data, q), size = U16(data, q + 2);
            q += 4;
            if (q + size > data.Length) break;
            if (tag == 1 && size >= 24)
            {
                m = FileTime(BinaryPrimitives.ReadInt64LittleEndian(data[q..]));
                a = FileTime(BinaryPrimitives.ReadInt64LittleEndian(data[(q + 8)..]));
                c = FileTime(BinaryPrimitives.ReadInt64LittleEndian(data[(q + 16)..]));
            }
            q += size;
        }
    }

    private static List<(int Id, byte[] Data)> FieldsList(ReadOnlySpan<byte> extra)
    {
        var r = new List<(int, byte[])>();
        int q = 0;
        while (q + 4 <= extra.Length)
        {
            int id = U16(extra, q), size = U16(extra, q + 2);
            q += 4;
            if (q + size > extra.Length) break;
            r.Add((id, extra.Slice(q, size).ToArray()));
            q += size;
        }
        return r;
    }

    private static IEnumerable<(int Id, byte[] Data)> Fields(ReadOnlySpan<byte> extra) => FieldsList(extra);

    private static (long Offset, long Size, long Count) FindCentralDirectory(Stream fs)
    {
        long len = fs.Length;
        int tail = (int)Math.Min(len, 65557);
        var buf = new byte[tail];
        fs.Seek(len - tail, SeekOrigin.Begin);
        ArchiveFormatDetector.ReadFull(fs, buf);
        for (int i = tail - 22; i >= 0; i--)
        {
            if (BinaryPrimitives.ReadUInt32LittleEndian(buf.AsSpan(i)) != 0x06054b50) continue;
            long count = U16(buf, i + 10), size = U32(buf, i + 12), offset = U32(buf, i + 16);
            // Zip64 end-of-central-directory locator just before the EOCD.
            if (i >= 20 && BinaryPrimitives.ReadUInt32LittleEndian(buf.AsSpan(i - 20)) == 0x07064b50)
            {
                long z64 = (long)BinaryPrimitives.ReadUInt64LittleEndian(buf.AsSpan(i - 12));
                var rec = new byte[56];
                if (z64 >= 0 && z64 <= len - rec.Length && Seek(fs, z64)
                    && ArchiveFormatDetector.ReadFull(fs, rec) == 56 && BinaryPrimitives.ReadUInt32LittleEndian(rec) == 0x06064b50)
                {
                    count = (long)BinaryPrimitives.ReadUInt64LittleEndian(rec.AsSpan(32));
                    size = (long)BinaryPrimitives.ReadUInt64LittleEndian(rec.AsSpan(40));
                    offset = (long)BinaryPrimitives.ReadUInt64LittleEndian(rec.AsSpan(48));
                }
            }
            return (offset, size, count);
        }
        return (0, 0, 0);
    }

    private static bool Seek(Stream fs, long offset) { fs.Seek(offset, SeekOrigin.Begin); return true; }

    private static DateTime? Dos(ushort date, ushort time)
    {
        int y = 1980 + (date >> 9), mo = (date >> 5) & 15, d = date & 31, h = time >> 11, mi = (time >> 5) & 63, s = (time & 31) * 2;
        if (mo < 1 || mo > 12 || d < 1 || d > DateTime.DaysInMonth(y, mo) || h > 23 || mi > 59 || s > 59) return null;
        return new DateTime(y, mo, d, h, mi, s, DateTimeKind.Unspecified);
    }

    private static DateTime? FileTime(long ft)
    {
        if (ft <= 0) return null;
        try { return DateTime.FromFileTimeUtc(ft); } catch (ArgumentOutOfRangeException) { return null; }
    }

    private static DateTime? Unix(int secs) => secs == 0 ? null : DateTimeOffset.FromUnixTimeSeconds((uint)secs).UtcDateTime;

    private static ushort U16(ReadOnlySpan<byte> s, int o) => BinaryPrimitives.ReadUInt16LittleEndian(s[o..]);
    private static uint U32(ReadOnlySpan<byte> s, int o) => BinaryPrimitives.ReadUInt32LittleEndian(s[o..]);
}
