namespace EzForensics.Core.Archives;

/// <summary>Incremental CRC-32 (IEEE 802.3, the zip/7z/RAR checksum).</summary>
public sealed class Crc32
{
    private static readonly uint[] Table = BuildTable();
    private uint _crc = 0xFFFFFFFF;

    public uint Value => ~_crc;

    public void Append(ReadOnlySpan<byte> data)
    {
        uint c = _crc;
        foreach (var b in data) c = Table[(c ^ b) & 0xFF] ^ (c >> 8);
        _crc = c;
    }

    public static uint Compute(ReadOnlySpan<byte> data) { var c = new Crc32(); c.Append(data); return c.Value; }

    private static uint[] BuildTable()
    {
        var t = new uint[256];
        for (uint i = 0; i < 256; i++)
        {
            uint c = i;
            for (int k = 0; k < 8; k++) c = (c & 1) != 0 ? 0xEDB88320 ^ (c >> 1) : c >> 1;
            t[i] = c;
        }
        return t;
    }
}
