namespace EzForensics.Core.Explore;

/// <summary>Deceptive-name findings for one file name.</summary>
/// <param name="HasBidiOverride">The name holds a Unicode direction control (RTLO U+202E and friends) that can hide the real extension.</param>
/// <param name="DoubleExtension">A decoy extension precedes an executable/script one, e.g. <c>invoice.pdf.exe</c>.</param>
/// <param name="DecoyExtension">The decoy extension (<c>pdf</c>) when <paramref name="DoubleExtension"/> is set.</param>
/// <param name="RealExtension">The extension Windows uses (the last one), lower case without the dot.</param>
/// <param name="PaddedBeforeExtension">Runs of spaces before the real extension (<c>photo.jpg          .exe</c>).</param>
public sealed record NameFlags(bool HasBidiOverride, bool DoubleExtension, string? DecoyExtension, string RealExtension, bool PaddedBeforeExtension)
{
    public bool Any => HasBidiOverride || DoubleExtension || PaddedBeforeExtension;
}

/// <summary>Double extension and RTLO detection on file names.</summary>
public static class NameChecks
{
    /// <summary>Extensions that run code when opened.</summary>
    public static readonly HashSet<string> RunnableExtensions = new(StringComparer.OrdinalIgnoreCase)
    {
        "exe", "scr", "com", "pif", "cpl", "msi", "msp", "bat", "cmd", "ps1", "psm1", "vbs", "vbe", "js", "jse", "wsf", "wsh", "hta",
        "lnk", "jar", "reg", "dll", "sys", "ocx", "inf", "application", "appref-ms", "gadget", "msc", "url", "iso", "img", "vhd", "vhdx",
        "chm", "xll", "sct", "docm", "xlsm", "pptm", "one", "library-ms", "searchConnector-ms", "settingcontent-ms",
    };

    /// <summary>Extensions users trust (documents, media, archives) and that make a good decoy.</summary>
    public static readonly HashSet<string> DecoyExtensions = new(StringComparer.OrdinalIgnoreCase)
    {
        "pdf", "doc", "docx", "xls", "xlsx", "ppt", "pptx", "rtf", "txt", "csv", "odt", "jpg", "jpeg", "png", "gif", "bmp", "tif", "tiff",
        "mp3", "mp4", "avi", "mov", "wav", "wmv", "zip", "rar", "7z", "htm", "html", "xml", "eml", "msg", "ics", "svg", "webp",
    };

    private static readonly char[] Bidi = { '‪', '‫', '‬', '‭', '‮', '⁦', '⁧', '⁨', '⁩', '‎', '‏', '؜' };

    public static NameFlags Analyze(string fileName)
    {
        var name = Path.GetFileName(fileName);
        bool bidi = name.IndexOfAny(Bidi) >= 0;
        var clean = new string(name.Where(c => Array.IndexOf(Bidi, c) < 0).ToArray());
        int lastDot = clean.LastIndexOf('.');
        string real = lastDot >= 0 ? clean[(lastDot + 1)..].Trim().ToLowerInvariant() : "";
        string stem = lastDot > 0 ? clean[..lastDot] : "";
        bool padded = stem.Length >= 3 && stem.EndsWith("   ", StringComparison.Ordinal) || stem.EndsWith("　", StringComparison.Ordinal);
        string? decoy = null;
        var stemTrim = stem.TrimEnd(' ', '　', '\t');
        int prevDot = stemTrim.LastIndexOf('.');
        if (prevDot >= 0 && RunnableExtensions.Contains(real))
        {
            var prev = stemTrim[(prevDot + 1)..].Trim().ToLowerInvariant();
            if (DecoyExtensions.Contains(prev)) decoy = prev;
        }
        return new NameFlags(bidi, decoy != null, decoy, real, padded && RunnableExtensions.Contains(real));
    }
}
