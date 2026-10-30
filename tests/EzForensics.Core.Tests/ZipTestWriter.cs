using System.IO.Compression;
using System.Text;
using EzForensics.Core.Archives;

namespace EzForensics.Core.Tests;

/// <summary>
/// Raw zip writer for tests: arbitrary (hostile) names, custom extra fields (NTFS 0x000a), lying sizes, unix symlinks and ZipCrypto,
/// none of which System.IO.Compression.ZipArchive lets us produce.
/// </summary>
internal sealed class ZipTestWriter
{
    internal sealed class Entry
    {
        public string Name = "";
        public byte[] Data = Array.Empty<byte>();
        public bool Deflate = true;
        public long? DeclaredSize;
        public byte[]? Extra;
        public string? ZipCryptoPassword;
        public bool UnixSymlink;
        public bool IsDirectory;
        public DateTime DosTime = new(2020, 1, 2, 3, 4, 6);
    }

    private readonly List<Entry> _entries = new();

    public ZipTestWriter Add(string name, string text, Action<Entry>? configure = null) => Add(name, Encoding.UTF8.GetBytes(text), configure);

    public ZipTestWriter Add(string name, byte[] data, Action<Entry>? configure = null)
    {
        var e = new Entry { Name = name, Data = data };
        configure?.Invoke(e);
        _entries.Add(e);
        return this;
    }

    public ZipTestWriter AddDirectory(string name) { _entries.Add(new Entry { Name = name.EndsWith('/') ? name : name + "/", IsDirectory = true, Deflate = false }); return this; }

    public void Save(string path) => File.WriteAllBytes(path, ToArray());

    public byte[] ToArray()
    {
        using var ms = new MemoryStream();
        var central = new MemoryStream();
        foreach (var e in _entries)
        {
            var name = Encoding.UTF8.GetBytes(e.Name);
            uint crc = Crc32.Compute(e.Data);
            byte[] payload = e.Deflate ? DeflateBytes(e.Data) : e.Data;
            ushort flags = 0x0800;
            if (e.ZipCryptoPassword != null)
            {
                flags |= 0x0001;
                payload = ZipCrypto(payload, e.ZipCryptoPassword, crc);
            }
            ushort method = (ushort)(e.Deflate ? 8 : 0);
            var (dtime, ddate) = Dos(e.DosTime);
            uint usize = (uint)(e.DeclaredSize ?? e.Data.Length);
            var extra = e.Extra ?? Array.Empty<byte>();
            long localOffset = ms.Position;

            var w = new BinaryWriter(ms, Encoding.UTF8, leaveOpen: true);
            w.Write(0x04034b50u); w.Write((ushort)20); w.Write(flags); w.Write(method); w.Write(dtime); w.Write(ddate);
            w.Write(crc); w.Write((uint)payload.Length); w.Write(usize); w.Write((ushort)name.Length); w.Write((ushort)extra.Length);
            w.Write(name); w.Write(extra); w.Write(payload);
            w.Flush();

            uint ext = e.IsDirectory ? 0x10u : 0x20u;
            ushort madeBy = 20;
            if (e.UnixSymlink) { madeBy = (3 << 8) | 20; ext = 0xA1FFu << 16; }
            var c = new BinaryWriter(central, Encoding.UTF8, leaveOpen: true);
            c.Write(0x02014b50u); c.Write(madeBy); c.Write((ushort)20); c.Write(flags); c.Write(method); c.Write(dtime); c.Write(ddate);
            c.Write(crc); c.Write((uint)payload.Length); c.Write(usize); c.Write((ushort)name.Length); c.Write((ushort)extra.Length);
            c.Write((ushort)0); c.Write((ushort)0); c.Write((ushort)0); c.Write(ext); c.Write((uint)localOffset);
            c.Write(name); c.Write(extra);
            c.Flush();
        }
        long cdOffset = ms.Position;
        central.Position = 0;
        central.CopyTo(ms);
        var t = new BinaryWriter(ms, Encoding.UTF8, leaveOpen: true);
        t.Write(0x06054b50u); t.Write((ushort)0); t.Write((ushort)0); t.Write((ushort)_entries.Count); t.Write((ushort)_entries.Count);
        t.Write((uint)central.Length); t.Write((uint)cdOffset); t.Write((ushort)0);
        t.Flush();
        return ms.ToArray();
    }

    /// <summary>NTFS extra field 0x000a with mtime / atime / ctime.</summary>
    public static byte[] NtfsExtra(DateTime mUtc, DateTime aUtc, DateTime cUtc)
    {
        using var ms = new MemoryStream();
        var w = new BinaryWriter(ms);
        w.Write((ushort)0x000a); w.Write((ushort)32); w.Write(0u);
        w.Write((ushort)1); w.Write((ushort)24);
        w.Write(mUtc.ToFileTimeUtc()); w.Write(aUtc.ToFileTimeUtc()); w.Write(cUtc.ToFileTimeUtc());
        w.Flush();
        return ms.ToArray();
    }

    public static byte[] DeflateBytes(byte[] data)
    {
        using var ms = new MemoryStream();
        using (var d = new DeflateStream(ms, CompressionLevel.Optimal, leaveOpen: true)) d.Write(data);
        return ms.ToArray();
    }

    private static (ushort Time, ushort Date) Dos(DateTime t) =>
        ((ushort)((t.Hour << 11) | (t.Minute << 5) | (t.Second / 2)), (ushort)(((t.Year - 1980) << 9) | (t.Month << 5) | t.Day));

    // Traditional PKWARE encryption (ZipCrypto).
    private static byte[] ZipCrypto(byte[] data, string password, uint crc)
    {
        uint k0 = 0x12345678, k1 = 0x23456789, k2 = 0x34567890;
        void Update(byte b)
        {
            k0 = CrcByte(k0, b);
            k1 = (k1 + (k0 & 0xFF)) * 134775813 + 1;
            k2 = CrcByte(k2, (byte)(k1 >> 24));
        }
        byte Next() { uint temp = (k2 | 2) & 0xFFFF; return (byte)((temp * (temp ^ 1)) >> 8); }
        foreach (var b in Encoding.UTF8.GetBytes(password)) Update(b);
        var header = new byte[12];
        new Random(42).NextBytes(header);
        header[11] = (byte)(crc >> 24);
        var output = new byte[12 + data.Length];
        for (int i = 0; i < 12; i++) { var p = header[i]; output[i] = (byte)(p ^ Next()); Update(p); }
        for (int i = 0; i < data.Length; i++) { var p = data[i]; output[12 + i] = (byte)(p ^ Next()); Update(p); }
        return output;
    }

    private static uint CrcByte(uint crc, byte b)
    {
        uint c = (crc ^ b) & 0xFF;
        for (int k = 0; k < 8; k++) c = (c & 1) != 0 ? 0xEDB88320 ^ (c >> 1) : c >> 1;
        return c ^ (crc >> 8);
    }
}
