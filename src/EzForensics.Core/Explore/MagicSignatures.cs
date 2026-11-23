using System.Text;
using System.Text.RegularExpressions;

namespace EzForensics.Core.Explore;

/// <summary>A file type detected from content.</summary>
/// <param name="Id">Stable id (<c>pe</c>, <c>zip</c>, <c>ooxml-word</c>, <c>script-powershell</c>, <c>text</c>, <c>unknown</c>...).</param>
/// <param name="ExpectedExtensions">Lower-case extensions without the dot that fit the type ("" = no extension).</param>
/// <param name="Strong">Detected from a magic number. Weak types (text heuristics) only count as a mismatch when the extension belongs
/// to a strong type (e.g. a PowerShell script named <c>.jpg</c>).</param>
public sealed record MagicType(string Id, string Description, IReadOnlyList<string> ExpectedExtensions, bool Strong)
{
    public bool IsExecutable => Id is "pe" or "pe-dll" or "pe-driver" or "elf";
    public bool IsScript => Id.StartsWith("script-", StringComparison.Ordinal);

    /// <summary>True when <paramref name="extension"/> (with or without the dot) does not fit this type.</summary>
    public bool IsMismatch(string? extension)
    {
        var e = MagicSignatures.NormExt(extension);
        if (Id is "unknown" or "text" or "empty") return false;
        if (ExpectedExtensions.Contains(e)) return false;
        return Strong || MagicSignatures.StrongExtensions.Contains(e);
    }
}

/// <summary>Small embedded magic-number table plus text heuristics for scripts. Reads only the bytes it is given.</summary>
public static class MagicSignatures
{
    /// <summary>Bytes of the file head the detector looks at.</summary>
    public const int HeadSize = 64 * 1024;

    private static string[] E(params string[] x) => x;

    private static readonly string[] PeExt = E("exe", "dll", "sys", "scr", "cpl", "ocx", "drv", "efi", "mui", "com", "ax", "acm", "node", "pyd",
        "winmd", "mun", "tsp", "xll", "ime", "rll", "bpl", "dpl", "fon", "iec", "tlb", "vxd", "wpx", "api", "flt", "ppi", "ds", "lex");
    private static readonly string[] ZipExt = E("zip", "jar", "apk", "xpi", "crx", "kmz", "nupkg", "vsix", "whl", "appx", "msix", "appxbundle",
        "msixbundle", "epub", "odt", "ods", "odp", "odg", "3mf", "ipa", "aar", "war", "ear", "xps", "oxps", "zipx", "cbz", "sketch", "pbix", "egg");
    private static readonly string[] WordExt = E("docx", "docm", "dotx", "dotm");
    private static readonly string[] ExcelExt = E("xlsx", "xlsm", "xltx", "xltm", "xlam");
    private static readonly string[] PptExt = E("pptx", "pptm", "potx", "potm", "ppsx", "ppsm", "ppam");
    private static readonly string[] OleExt = E("doc", "dot", "xls", "xlt", "xla", "ppt", "pot", "pps", "msg", "msi", "msp", "mst", "vsd", "pub",
        "db", "ole", "mpp", "wps", "xlsb", "oft", "ade", "adp", "sdb", "hwp", "suo", "dat");

    private sealed record Sig(string Id, string Description, byte[] Magic, int Offset, string[] Ext);

    private static readonly Sig[] Table =
    {
        new("png", "PNG image", new byte[] { 0x89, 0x50, 0x4E, 0x47, 0x0D, 0x0A, 0x1A, 0x0A }, 0, E("png")),
        new("jpeg", "JPEG image", new byte[] { 0xFF, 0xD8, 0xFF }, 0, E("jpg", "jpeg", "jpe", "jfif")),
        new("gif", "GIF image", "GIF87a"u8.ToArray(), 0, E("gif")),
        new("gif", "GIF image", "GIF89a"u8.ToArray(), 0, E("gif")),
        new("7z", "7-Zip archive", new byte[] { 0x37, 0x7A, 0xBC, 0xAF, 0x27, 0x1C }, 0, E("7z")),
        new("rar", "RAR archive", "Rar!\x1A\x07"u8.ToArray(), 0, E("rar", "r00", "cbr")),
        new("gzip", "GZIP archive", new byte[] { 0x1F, 0x8B }, 0, E("gz", "tgz", "gzip", "svgz")),
        new("ole", "OLE compound file (legacy Office, MSI, MSG)", new byte[] { 0xD0, 0xCF, 0x11, 0xE0, 0xA1, 0xB1, 0x1A, 0xE1 }, 0, OleExt),
        new("rtf", "Rich Text Format", "{\\rtf"u8.ToArray(), 0, E("rtf")),
        new("lnk", "Windows shortcut (LNK)", new byte[] { 0x4C, 0x00, 0x00, 0x00, 0x01, 0x14, 0x02, 0x00 }, 0, E("lnk")),
        new("evtx", "Windows event log (EVTX)", "ElfFile\0"u8.ToArray(), 0, E("evtx")),
        new("hive", "Windows registry hive", "regf"u8.ToArray(), 0, E("", "dat", "hve", "hiv", "sav", "log", "log1", "log2", "bak", "old")),
        new("sqlite", "SQLite database", "SQLite format 3\0"u8.ToArray(), 0, E("", "db", "sqlite", "sqlite3", "db3", "sqlitedb", "s3db", "sl3")),
        new("elf", "ELF executable", new byte[] { 0x7F, 0x45, 0x4C, 0x46 }, 0, E("", "so", "elf", "o", "ko", "bin", "axf", "out", "run")),
        new("pdf", "PDF document", "%PDF-"u8.ToArray(), 0, E("pdf")),
    };

    /// <summary>Extensions of strong (magic-number) types, used to flag weak types renamed to them.</summary>
    public static readonly HashSet<string> StrongExtensions = new(
        Table.SelectMany(s => s.Ext).Concat(PeExt).Concat(ZipExt).Concat(WordExt).Concat(ExcelExt).Concat(PptExt).Where(e => e.Length > 0)
            .Except(new[] { "dat", "log", "bak", "old", "bin", "out", "run", "db" }),
        StringComparer.OrdinalIgnoreCase);

    public static readonly MagicType Unknown = new("unknown", "Unknown (binary)", Array.Empty<string>(), false);
    public static readonly MagicType Empty = new("empty", "Empty file", Array.Empty<string>(), false);
    public static readonly MagicType Text = new("text", "Text", Array.Empty<string>(), false);

    /// <summary>Lower-case extension without the dot ("" when none).</summary>
    public static string NormExt(string? ext)
    {
        if (string.IsNullOrEmpty(ext)) return "";
        return ext.TrimStart('.').Trim().ToLowerInvariant();
    }

    /// <summary>Type of the content in <paramref name="head"/> (the first bytes of the file, ideally <see cref="HeadSize"/>).</summary>
    public static MagicType Detect(ReadOnlySpan<byte> head)
    {
        if (head.Length == 0) return Empty;
        if (head.Length >= 2 && head[0] == 'M' && head[1] == 'Z') return DetectPe(head) ?? new("mz", "DOS executable (MZ)", E("exe", "com", "dll", "sys", "scr"), true);
        if (head.Length >= 4 && head[0] == 'P' && head[1] == 'K' && head[2] is 3 or 5 or 7 && head[3] is 4 or 6 or 8) return DetectZip(head);
        foreach (var s in Table)
            if (head.Length >= s.Offset + s.Magic.Length && head.Slice(s.Offset, s.Magic.Length).SequenceEqual(s.Magic))
                return new(s.Id, s.Description, s.Ext, true);
        // %PDF- may follow junk bytes (readers accept it within the first 1 KB).
        if (head[..Math.Min(head.Length, 1024)].IndexOf("%PDF-"u8) > 0) return new("pdf", "PDF document", E("pdf"), true);
        return DetectText(head) ?? Unknown;
    }

    private static MagicType? DetectPe(ReadOnlySpan<byte> h)
    {
        if (h.Length < 0x40) return null;
        int lfanew = BitConverter.ToInt32(h.Slice(0x3C, 4));
        if (lfanew < 0x40 || lfanew > h.Length - 24) return null;
        if (!h.Slice(lfanew, 4).SequenceEqual("PE\0\0"u8)) return null;
        ushort characteristics = BitConverter.ToUInt16(h.Slice(lfanew + 22, 2));
        int optOff = lfanew + 24;
        ushort subsystem = 0;
        if (h.Length >= optOff + 70)
        {
            ushort magic = BitConverter.ToUInt16(h.Slice(optOff, 2));
            if (magic is 0x10B or 0x20B) subsystem = BitConverter.ToUInt16(h.Slice(optOff + 68, 2));
        }
        if (subsystem == 1) return new("pe-driver", "PE driver (native)", PeExt, true);
        if ((characteristics & 0x2000) != 0) return new("pe-dll", "PE DLL", PeExt, true);
        return new("pe", "PE executable", PeExt, true);
    }

    private static MagicType DetectZip(ReadOnlySpan<byte> span)
    {
        var h = span.ToArray();
        bool Has(string s) => h.AsSpan().IndexOf(Encoding.ASCII.GetBytes(s)) >= 0;
        if (Has("[Content_Types].xml") || Has("_rels/.rels"))
        {
            if (Has("word/")) return new("ooxml-word", "Word document (OOXML)", WordExt, true);
            if (Has("xl/")) return new("ooxml-excel", "Excel workbook (OOXML)", ExcelExt, true);
            if (Has("ppt/")) return new("ooxml-powerpoint", "PowerPoint presentation (OOXML)", PptExt, true);
            return new("ooxml", "Office Open XML package", WordExt.Concat(ExcelExt).Concat(PptExt).Concat(E("vsdx", "xps", "oxps", "appx", "msix", "nupkg", "3mf")).ToArray(), true);
        }
        if (Has("AndroidManifest.xml")) return new("apk", "Android package (APK)", E("apk", "aab", "zip"), true);
        if (Has("META-INF/MANIFEST.MF")) return new("jar", "Java archive (JAR)", E("jar", "war", "ear", "zip", "apk", "xpi", "aar"), true);
        if (Has("mimetypeapplication/vnd.oasis.opendocument")) return new("odf", "OpenDocument file", E("odt", "ods", "odp", "odg", "ott", "ots", "otp"), true);
        if (Has("mimetypeapplication/epub+zip")) return new("epub", "EPUB e-book", E("epub"), true);
        return new("zip", "ZIP archive", ZipExt.Concat(WordExt).Concat(ExcelExt).Concat(PptExt).ToArray(), true);
    }

    private static readonly Regex PsRx = new(
        @"(\$[A-Za-z_][\w:]*\s*=|\b(Invoke-\w+|Get-\w+|Set-\w+|New-Object|Write-Host|Start-Process|IEX|FromBase64String)\b|\[System\.|\bparam\s*\(|-enc(odedcommand)?\s)",
        RegexOptions.IgnoreCase | RegexOptions.CultureInvariant);
    private static readonly Regex VbsRx = new(
        @"(CreateObject\s*\(|\bWScript\.|\bDim\s+\w|\bEnd\s+(Sub|Function|If)\b|\bOn\s+Error\s+Resume\s+Next\b|\bSet\s+\w+\s*=\s*)",
        RegexOptions.IgnoreCase | RegexOptions.CultureInvariant);
    private static readonly Regex JsRx = new(
        @"(new\s+ActiveXObject\s*\(|\bfunction\s*\w*\s*\(|\bvar\s+\w+\s*=|\blet\s+\w+\s*=|\bconst\s+\w+\s*=|=>\s*\{|\beval\s*\()",
        RegexOptions.CultureInvariant);
    private static readonly Regex BatRx = new(
        @"(^\s*@?echo\s+off\b|^\s*(set|goto|call|if\s+exist|rem)\b|%~dp0|^\s*:\w+\s*$)",
        RegexOptions.IgnoreCase | RegexOptions.Multiline | RegexOptions.CultureInvariant);

    /// <summary>Text heuristics: null when the head does not look like text.</summary>
    private static MagicType? DetectText(ReadOnlySpan<byte> h)
    {
        var text = DecodeText(h[..Math.Min(h.Length, 8192)]);
        if (text == null) return null;
        var t = text.TrimStart('﻿', ' ', '\t', '\r', '\n');
        if (t.StartsWith("#!", StringComparison.Ordinal))
            return new("script-shebang", "Script (shebang)", E("", "sh", "bash", "py", "pl", "rb", "zsh", "ksh", "php"), false);
        if (t.Contains("<HTA:APPLICATION", StringComparison.OrdinalIgnoreCase))
            return new("script-hta", "HTML application (HTA)", E("hta"), false);
        if (t.StartsWith("<?xml", StringComparison.OrdinalIgnoreCase) && t.Contains("<job", StringComparison.OrdinalIgnoreCase) && t.Contains("<script", StringComparison.OrdinalIgnoreCase))
            return new("script-wsf", "Windows Script File (WSF)", E("wsf", "wsc", "sct"), false);
        if (t.StartsWith("<!DOCTYPE html", StringComparison.OrdinalIgnoreCase) || t.StartsWith("<html", StringComparison.OrdinalIgnoreCase))
            return new("html", "HTML document", E("html", "htm", "xhtml", "mht", "mhtml", "hta", "svg", "shtml"), false);
        int ps = PsRx.Matches(t).Count, vbs = VbsRx.Matches(t).Count, js = JsRx.Matches(t).Count, bat = BatRx.Matches(t).Count;
        int best = new[] { ps, vbs, js, bat }.Max();
        if (best >= 2)
        {
            if (best == bat) return new("script-batch", "Batch script", E("bat", "cmd", "btm"), false);
            if (best == ps) return new("script-powershell", "PowerShell script", E("ps1", "psm1", "psd1", "ps1xml", "pssc", "psc1"), false);
            if (best == vbs) return new("script-vbscript", "VBScript", E("vbs", "vbe", "vba", "bas", "cls", "frm", "wsf", "hta", "asp"), false);
            return new("script-javascript", "JavaScript / JScript", E("js", "jse", "mjs", "cjs", "wsf", "hta", "html", "htm", "ts"), false);
        }
        return Text;
    }

    /// <summary>UTF-8/ASCII or UTF-16 text with BOM when it is mostly printable; else null.</summary>
    private static string? DecodeText(ReadOnlySpan<byte> h)
    {
        string s;
        if (h.Length >= 2 && h[0] == 0xFF && h[1] == 0xFE) s = Encoding.Unicode.GetString(h[2..]);
        else if (h.Length >= 2 && h[0] == 0xFE && h[1] == 0xFF) s = Encoding.BigEndianUnicode.GetString(h[2..]);
        else
        {
            if (h.IndexOf((byte)0) >= 0) return null;
            s = Encoding.UTF8.GetString(h);
        }
        if (s.Length == 0) return null;
        int bad = 0;
        foreach (var c in s)
            if (c == '�' || (char.IsControl(c) && c is not ('\r' or '\n' or '\t' or '\f'))) bad++;
        return bad <= s.Length / 50 ? s : null;
    }
}
