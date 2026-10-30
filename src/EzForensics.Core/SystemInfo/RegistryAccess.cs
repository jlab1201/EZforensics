using System.Globalization;
using System.Security.Cryptography;
using System.Text;

namespace EzForensics.Core.SystemInfo;

/// <summary>Registry value types (REG_*).</summary>
public static class RegType
{
    public const uint None = 0, Sz = 1, ExpandSz = 2, Binary = 3, Dword = 4, DwordBigEndian = 5, Link = 6, MultiSz = 7, Qword = 11;
}

/// <summary>One registry value: name, raw type and raw data (decoded on demand).</summary>
public sealed class RegValue
{
    public RegValue(string name, uint type, byte[] raw)
    {
        Name = name ?? "";
        Type = type;
        Raw = raw ?? Array.Empty<byte>();
    }

    public string Name { get; }
    /// <summary>Raw data type (REG_* or, e.g. for SAM Names keys, an arbitrary number such as the RID).</summary>
    public uint Type { get; }
    public byte[] Raw { get; }

    public static RegValue Sz(string name, string s, uint type = RegType.Sz) => new(name, type, Encoding.Unicode.GetBytes(s + "\0"));
    public static RegValue Multi(string name, params string[] items) =>
        new(name, RegType.MultiSz, Encoding.Unicode.GetBytes(string.Concat(items.Select(i => i + "\0")) + "\0"));
    public static RegValue Dword(string name, uint v) => new(name, RegType.Dword, BitConverter.GetBytes(v));
    public static RegValue Qword(string name, ulong v) => new(name, RegType.Qword, BitConverter.GetBytes(v));
    public static RegValue Bin(string name, byte[] b) => new(name, RegType.Binary, b);

    /// <summary>String form of REG_SZ / REG_EXPAND_SZ (MULTI_SZ items joined with ", "; numbers in decimal); null for binary.</summary>
    public string? AsString()
    {
        switch (Type)
        {
            case RegType.Sz:
            case RegType.ExpandSz:
            case RegType.Link:
                return DecodeUtf16(Raw).TrimEnd('\0');
            case RegType.MultiSz:
                return string.Join(", ", AsMulti());
            case RegType.Dword:
            case RegType.DwordBigEndian:
            case RegType.Qword:
                return AsNumber()?.ToString(CultureInfo.InvariantCulture);
            default:
                return null;
        }
    }

    public string[] AsMulti()
    {
        if (Type is RegType.Sz or RegType.ExpandSz) { var s = AsString(); return string.IsNullOrEmpty(s) ? Array.Empty<string>() : new[] { s }; }
        if (Type != RegType.MultiSz) return Array.Empty<string>();
        return DecodeUtf16(Raw).Split('\0', StringSplitOptions.RemoveEmptyEntries).Select(x => x.Trim()).Where(x => x.Length > 0).ToArray();
    }

    /// <summary>DWORD / QWORD (or a 4/8-byte binary) as a number.</summary>
    public long? AsNumber()
    {
        if (Type == RegType.DwordBigEndian && Raw.Length >= 4) return (uint)(Raw[0] << 24 | Raw[1] << 16 | Raw[2] << 8 | Raw[3]);
        if ((Type is RegType.Dword or RegType.Binary) && Raw.Length == 4) return BitConverter.ToUInt32(Raw, 0);
        if ((Type is RegType.Qword or RegType.Binary) && Raw.Length == 8) return BitConverter.ToInt64(Raw, 0);
        if (Type == RegType.Dword && Raw.Length > 4) return BitConverter.ToUInt32(Raw, 0);
        if (Type is RegType.Sz or RegType.ExpandSz && long.TryParse(AsString(), NumberStyles.Integer, CultureInfo.InvariantCulture, out var n)) return n;
        return null;
    }

    public string Hex() => Convert.ToHexString(Raw);

    private static string DecodeUtf16(byte[] b) => b.Length < 2 ? "" : Encoding.Unicode.GetString(b, 0, b.Length & ~1);
}

/// <summary>A registry key of an offline hive (read-only view).</summary>
public abstract class RegKey
{
    public abstract string Name { get; }
    public abstract DateTime? LastWriteUtc { get; }
    public abstract IReadOnlyList<RegKey> SubKeys { get; }
    public abstract IReadOnlyList<RegValue> Values { get; }

    public RegValue? Value(string name) =>
        Values.FirstOrDefault(v => string.Equals(v.Name, name, StringComparison.OrdinalIgnoreCase));

    /// <summary>The default (unnamed) value.</summary>
    public RegValue? DefaultValue => Values.FirstOrDefault(v => v.Name.Length == 0 || v.Name == "(default)");

    public RegKey? SubKey(string relativePath)
    {
        RegKey? k = this;
        foreach (var part in relativePath.Split('\\', StringSplitOptions.RemoveEmptyEntries))
        {
            k = k.SubKeys.FirstOrDefault(s => string.Equals(s.Name, part, StringComparison.OrdinalIgnoreCase));
            if (k == null) return null;
        }
        return k;
    }
}

/// <summary>Where a hive came from and how it was read (for the report's custody section).</summary>
public sealed class HiveInfo
{
    public string Name { get; set; } = "";
    public string Path { get; set; } = "";
    public long Size { get; set; }
    public string? Sha256 { get; set; }
    /// <summary>Primary and secondary sequence numbers differ (unsaved changes live in the .LOG1/.LOG2 files).</summary>
    public bool Dirty { get; set; }
    public List<string> TransactionLogs { get; set; } = new();
    /// <summary>The transaction logs were replayed (in memory, on temporary copies).</summary>
    public bool LogsReplayed { get; set; }
    public string? Note { get; set; }
}

/// <summary>An opened offline hive.</summary>
public interface IRegistryHive : IDisposable
{
    HiveInfo Info { get; }
    /// <summary>Key by path relative to the hive root (e.g. <c>Select</c>, <c>ControlSet001\Control</c>); null when missing.</summary>
    RegKey? GetKey(string path);
}

/// <summary>Opens offline hives (the real implementation uses Eric Zimmerman's Registry library; tests use <see cref="InMemoryHive"/>).</summary>
public interface IHiveOpener
{
    /// <summary>Opens <paramref name="hivePath"/> read-only. Never writes to the evidence. Throws when the file is not a hive.</summary>
    IRegistryHive Open(string hivePath, string name);
}

/// <summary>A hive built in memory (unit tests).</summary>
public sealed class InMemoryHive : IRegistryHive
{
    public InMemoryHive(string name, MemKey? root = null)
    {
        Root = root ?? new MemKey("ROOT");
        Info = new HiveInfo { Name = name, Path = name };
    }

    public MemKey Root { get; }
    public HiveInfo Info { get; }
    public RegKey? GetKey(string path) => path.Length == 0 ? Root : Root.SubKey(path);
    public void Dispose() { }

    /// <summary>Creates (or returns) the key at <paramref name="path"/>.</summary>
    public MemKey Key(string path)
    {
        var k = Root;
        foreach (var part in path.Split('\\', StringSplitOptions.RemoveEmptyEntries))
        {
            var next = k.Children.FirstOrDefault(c => string.Equals(c.Name, part, StringComparison.OrdinalIgnoreCase));
            if (next == null) { next = new MemKey(part); k.Children.Add(next); }
            k = next;
        }
        return k;
    }
}

public sealed class MemKey : RegKey
{
    public MemKey(string name, DateTime? lastWriteUtc = null) { _name = name; LastWrite = lastWriteUtc; }
    private readonly string _name;
    public DateTime? LastWrite { get; set; }
    public List<MemKey> Children { get; } = new();
    public List<RegValue> Vals { get; } = new();
    public override string Name => _name;
    public override DateTime? LastWriteUtc => LastWrite;
    public override IReadOnlyList<RegKey> SubKeys => Children;
    public override IReadOnlyList<RegValue> Values => Vals;
    public MemKey With(params RegValue[] values) { Vals.AddRange(values); return this; }
}

/// <summary>
/// Opens hives with Eric Zimmerman's Registry library (MIT, NuGet "Registry"). The hive and its transaction logs are copied to a
/// private temporary folder first; dirty hives are replayed in memory from the copies, so the evidence is only ever read.
/// </summary>
public sealed class ZimmermanHiveOpener : IHiveOpener
{
    public IRegistryHive Open(string hivePath, string name)
    {
        var info = new HiveInfo { Name = name, Path = hivePath };
        var fi = new FileInfo(hivePath);
        info.Size = fi.Length;
        info.Sha256 = Sha256(hivePath);
        var tmp = Path.Combine(Path.GetTempPath(), "EzForensics_hive_" + Guid.NewGuid().ToString("N")[..12]);
        Directory.CreateDirectory(tmp);
        try
        {
            var copy = Path.Combine(tmp, Path.GetFileName(hivePath));
            CopyReadOnly(hivePath, copy);
            var bytes = File.ReadAllBytes(copy);
            var hive = new Registry.RegistryHive(bytes, copy);
            info.Dirty = hive.Header.PrimarySequenceNumber != hive.Header.SecondarySequenceNumber;
            var dir = Path.GetDirectoryName(hivePath) ?? ".";
            var logs = Directory.Exists(dir)
                ? new[] { ".LOG1", ".LOG2" }.Select(ext => Path.Combine(dir, Path.GetFileName(hivePath) + ext))
                    .Where(l => File.Exists(l) && new FileInfo(l).Length > 0).ToList()
                : new List<string>();
            info.TransactionLogs = logs.Select(Path.GetFileName).Where(n => n != null).Select(n => n!).ToList();
            if (info.Dirty)
            {
                if (logs.Count > 0)
                {
                    var copies = new List<string>();
                    foreach (var l in logs)
                    {
                        var c = Path.Combine(tmp, Path.GetFileName(l));
                        CopyReadOnly(l, c);
                        copies.Add(c);
                    }
                    try
                    {
                        hive.ProcessTransactionLogs(copies, true);
                        info.LogsReplayed = true;
                        info.Note = $"Dirty hive: {copies.Count} transaction log(s) replayed in memory (evidence not modified).";
                    }
                    catch (Exception ex)
                    {
                        info.Note = "Dirty hive: replaying the transaction logs failed (" + ex.Message + "); values are read as stored in the primary file.";
                    }
                }
                else info.Note = "Dirty hive and no .LOG1/.LOG2 found next to it: values are read as stored in the primary file and may miss the latest changes.";
            }
            hive.ParseHive();
            return new ZimmermanHive(hive, info);
        }
        finally
        {
            try { Directory.Delete(tmp, true); } catch { }
        }
    }

    private static void CopyReadOnly(string src, string dst)
    {
        using var i = new FileStream(src, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete);
        using var o = new FileStream(dst, FileMode.CreateNew, FileAccess.Write, FileShare.None);
        i.CopyTo(o);
    }

    public static string Sha256(string path)
    {
        using var fs = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete);
        return Convert.ToHexString(SHA256.HashData(fs));
    }

    private sealed class ZimmermanHive : IRegistryHive
    {
        private readonly Registry.RegistryHive _hive;
        public ZimmermanHive(Registry.RegistryHive hive, HiveInfo info) { _hive = hive; Info = info; }
        public HiveInfo Info { get; }
        public RegKey? GetKey(string path)
        {
            if (string.IsNullOrEmpty(path)) return new ZKey(_hive.Root);
            Registry.Abstractions.RegistryKey? k = _hive.Root;
            foreach (var part in path.Split('\\', StringSplitOptions.RemoveEmptyEntries))
            {
                k = k.SubKeys.FirstOrDefault(s => string.Equals(s.KeyName, part, StringComparison.OrdinalIgnoreCase));
                if (k == null) return null;
            }
            return new ZKey(k);
        }
        public void Dispose() { }
    }

    private sealed class ZKey : RegKey
    {
        private readonly Registry.Abstractions.RegistryKey _k;
        private IReadOnlyList<RegKey>? _subs;
        private IReadOnlyList<RegValue>? _vals;
        public ZKey(Registry.Abstractions.RegistryKey k) => _k = k;
        public override string Name => _k.KeyName;
        public override DateTime? LastWriteUtc => _k.LastWriteTime?.UtcDateTime;
        public override IReadOnlyList<RegKey> SubKeys => _subs ??= _k.SubKeys.Select(s => (RegKey)new ZKey(s)).ToList();
        public override IReadOnlyList<RegValue> Values => _vals ??= _k.Values.Select(v =>
            new RegValue(v.ValueName == "(default)" ? "" : v.ValueName, v.VkRecord?.DataTypeRaw ?? 0, v.ValueDataRaw ?? Array.Empty<byte>())).ToList();
    }
}
