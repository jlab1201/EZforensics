namespace EzForensics.Core.SystemInfo;

/// <summary>One fact about a system: where it belongs, what it is, its value ("-" when unknown) and where it was read (hive\key\value).</summary>
public sealed record DetailField(string Category, string Field, string Value, string Source);

/// <summary>Details of one Windows installation (one system root of the source).</summary>
public sealed class SystemDetails
{
    /// <summary>The system root (the folder that holds <c>Windows\System32\config</c>).</summary>
    public string SystemRoot { get; set; } = "";
    /// <summary>Computer name, or the system root's folder name when unknown.</summary>
    public string Title { get; set; } = "";
    /// <summary>Case host folder this system belongs to (layout v2: the report lives in <c>&lt;case&gt;/&lt;Host&gt;/SystemInfo</c>); empty for legacy reports.</summary>
    public string Host { get; set; } = "";
    /// <summary>One line: <c>HOSTNAME · Windows 10 Pro 22H2 (19045) · 10.0.0.5</c>.</summary>
    public string Summary { get; set; } = "";
    public List<DetailField> Fields { get; set; } = new();
    public List<HiveInfo> Hives { get; set; } = new();
    public List<string> Notes { get; set; } = new();
}

/// <summary><c>&lt;case&gt;\SystemInfo\ComputerDetails.json</c>.</summary>
public sealed class ComputerDetailsReport
{
    public const int CurrentSchemaVersion = 1;
    public int SchemaVersion { get; set; } = CurrentSchemaVersion;
    /// <summary>Triage source (raw evidence) the hives were read from.</summary>
    public string Source { get; set; } = "";
    public DateTime GeneratedUtc { get; set; } = DateTime.UtcNow;
    public string Generator { get; set; } = "EZ Forensics (Registry library by Eric Zimmerman, offline, read-only)";
    public List<SystemDetails> Systems { get; set; } = new();
    public List<string> Notes { get; set; } = new();
}

/// <summary>
/// Extension point for more sources of facts (e.g. a future Volatility step that reads installed memory from a memory image).
/// Called once per system after the registry fields were extracted; may add or replace fields.
/// </summary>
public interface IComputerDetailsProvider
{
    void Contribute(SystemDetails system, string systemRoot, CancellationToken ct);
}

/// <summary>Facts read from the file system of a system root (outside the registry).</summary>
public sealed class FileFacts
{
    public long? HiberfilSize { get; set; }
    public long? PagefileSize { get; set; }
    public long? SwapfileSize { get; set; }
    /// <summary>NTFS volume serial from the root's <c>$Boot</c> (16 hex digits), when present.</summary>
    public string? VolumeSerial { get; set; }
    public string? BootPath { get; set; }

    public static FileFacts Read(string systemRoot)
    {
        var f = new FileFacts();
        long? Size(string name)
        {
            try { var fi = new FileInfo(Path.Combine(systemRoot, name)); return fi.Exists ? fi.Length : null; }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { return null; }
        }
        f.HiberfilSize = Size("hiberfil.sys");
        f.PagefileSize = Size("pagefile.sys");
        f.SwapfileSize = Size("swapfile.sys");
        var boot = Path.Combine(systemRoot, "$Boot");
        try
        {
            if (File.Exists(boot))
            {
                using var fs = new FileStream(boot, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete);
                var b = new byte[512];
                if (fs.Read(b, 0, b.Length) == b.Length) f.VolumeSerial = NtfsSerial(b);
                if (f.VolumeSerial != null) f.BootPath = boot;
            }
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { }
        return f;
    }

    /// <summary>NTFS boot sector: OEM id "NTFS    " at 3, 64-bit volume serial at 0x48. Shown as XXXX-XXXX (low 32 bits, like <c>vol</c>) plus the full value.</summary>
    public static string? NtfsSerial(byte[] sector)
    {
        if (sector.Length < 0x50) return null;
        if (System.Text.Encoding.ASCII.GetString(sector, 3, 8) != "NTFS    ") return null;
        ulong serial = BitConverter.ToUInt64(sector, 0x48);
        uint low = (uint)serial;
        return $"{low >> 16:X4}-{low & 0xFFFF:X4} (full {serial:X16})";
    }
}
