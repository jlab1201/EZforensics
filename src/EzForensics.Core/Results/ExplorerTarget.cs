namespace EzForensics.Core.Results;

/// <summary>What "Show in Explorer" launches for a path: a file is selected in its folder, a folder is opened.</summary>
public static class ExplorerTarget
{
    /// <summary>
    /// File: <c>explorer.exe /select,"full path"</c> (the path quoted inside the one argument; a separate quoted argument is not understood
    /// by Explorer and it falls back to the default folder). Folder: <c>explorer.exe "folder"</c>. Null when the path is missing.
    /// </summary>
    public static (string Exe, string Arguments)? Resolve(string? path, Func<string, bool>? fileExists = null, Func<string, bool>? dirExists = null)
    {
        if (string.IsNullOrWhiteSpace(path)) return null;
        fileExists ??= File.Exists;
        dirExists ??= Directory.Exists;
        var full = path.Trim().Trim('"');
        try { full = Path.GetFullPath(full); } catch (Exception) { return null; }
        if (fileExists(full)) return ("explorer.exe", $"/select,\"{full}\"");
        if (dirExists(full)) return ("explorer.exe", $"\"{(Path.GetPathRoot(full) == full ? full : full.TrimEnd('\\'))}\"");
        return null;
    }
}
