using System.Text;

namespace EzForensics.Core.Persistence;

/// <summary>Target of a Windows shortcut (.lnk): the fields a startup-folder review needs.</summary>
public sealed record LnkInfo(string? Target, string? Arguments, string? WorkingDirectory, string? RelativePath, string? Description,
    DateTime? TargetCreatedUtc, DateTime? TargetModifiedUtc);

/// <summary>
/// Minimal [MS-SHLLINK] reader: header, LinkInfo (local base path + common path suffix) and the StringData section. Enough to
/// show what a startup shortcut launches; for the full details run LECmd on the folder.
/// </summary>
public static class LnkTarget
{
    private const int HasIdList = 0x1, HasLinkInfo = 0x2, HasName = 0x4, HasRelPath = 0x8, HasWorkDir = 0x10, HasArgs = 0x20, HasIcon = 0x40, IsUnicode = 0x80;

    public static LnkInfo? Read(string path)
    {
        try
        {
            using var fs = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete);
            if (fs.Length < 76 || fs.Length > 16 << 20) return null;
            var b = new byte[fs.Length];
            fs.ReadExactly(b);
            return Parse(b);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { return null; }
    }

    public static LnkInfo? Parse(byte[] b)
    {
        try
        {
            if (b.Length < 76 || BitConverter.ToUInt32(b, 0) != 0x4C) return null;
            int flags = BitConverter.ToInt32(b, 20);
            var created = RegistryAseps.FileTime(BitConverter.ToInt64(b, 28));
            var modified = RegistryAseps.FileTime(BitConverter.ToInt64(b, 44));
            int p = 76;
            if ((flags & HasIdList) != 0) p += 2 + BitConverter.ToUInt16(b, p);
            string? target = null;
            if ((flags & HasLinkInfo) != 0 && p + 28 <= b.Length)
            {
                int size = BitConverter.ToInt32(b, p);
                int headerSize = BitConverter.ToInt32(b, p + 4);
                int liFlags = BitConverter.ToInt32(b, p + 8);
                int baseOff = BitConverter.ToInt32(b, p + 16);
                int netOff = BitConverter.ToInt32(b, p + 20);
                int suffixOff = BitConverter.ToInt32(b, p + 24);
                string? basePath = null, suffix = null;
                if (headerSize >= 0x24 && p + 36 <= b.Length)
                {
                    int ubase = BitConverter.ToInt32(b, p + 28), usuffix = BitConverter.ToInt32(b, p + 32);
                    if ((liFlags & 1) != 0 && ubase > 0) basePath = UnicodeZ(b, p + ubase);
                    if (usuffix > 0) suffix = UnicodeZ(b, p + usuffix);
                }
                if (basePath == null && (liFlags & 1) != 0 && baseOff > 0) basePath = AnsiZ(b, p + baseOff);
                if (basePath == null && (liFlags & 2) != 0 && netOff > 0 && p + netOff + 20 <= b.Length)
                {
                    int netNameOff = BitConverter.ToInt32(b, p + netOff + 8);
                    basePath = AnsiZ(b, p + netOff + netNameOff);
                    if (basePath != null && !basePath.EndsWith('\\')) basePath += "\\";
                }
                suffix ??= suffixOff > 0 ? AnsiZ(b, p + suffixOff) : null;
                if (basePath != null) target = basePath + (suffix ?? "");
                p += size;
            }
            bool uni = (flags & IsUnicode) != 0;
            string? Next(int flag)
            {
                if ((flags & flag) == 0 || p + 2 > b.Length) return null;
                int count = BitConverter.ToUInt16(b, p);
                p += 2;
                int bytes = uni ? count * 2 : count;
                if (p + bytes > b.Length) { p = b.Length; return null; }
                var s = uni ? Encoding.Unicode.GetString(b, p, bytes) : Encoding.Latin1.GetString(b, p, bytes);
                p += bytes;
                return s;
            }
            var name = Next(HasName);
            var rel = Next(HasRelPath);
            var wd = Next(HasWorkDir);
            var args = Next(HasArgs);
            return new LnkInfo(target ?? rel, args, wd, rel, name, created, modified);
        }
        catch (Exception ex) when (ex is ArgumentException or IndexOutOfRangeException) { return null; }
    }

    private static string? UnicodeZ(byte[] b, int off)
    {
        if (off < 0 || off >= b.Length) return null;
        int end = off;
        while (end + 1 < b.Length && (b[end] != 0 || b[end + 1] != 0)) end += 2;
        return Encoding.Unicode.GetString(b, off, end - off);
    }

    private static string? AnsiZ(byte[] b, int off)
    {
        if (off < 0 || off >= b.Length) return null;
        int end = Array.IndexOf(b, (byte)0, off);
        if (end < 0) end = b.Length;
        return Encoding.Latin1.GetString(b, off, end - off);
    }
}
