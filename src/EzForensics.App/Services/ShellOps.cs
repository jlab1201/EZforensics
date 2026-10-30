using System.Diagnostics;

namespace EzForensics.App.Services;

/// <summary>Explorer / shell helpers.</summary>
public static class ShellOps
{
    public static void OpenFolder(string? path)
    {
        if (string.IsNullOrWhiteSpace(path)) throw new InvalidOperationException("No folder to open.");
        var t = EzForensics.Core.Results.ExplorerTarget.Resolve(path);
        if (t == null) throw new DirectoryNotFoundException($"Folder does not exist (yet): {path}");
        Process.Start(new ProcessStartInfo(t.Value.Exe) { Arguments = t.Value.Arguments, UseShellExecute = false });
    }

    public static void OpenFile(string? path)
    {
        if (string.IsNullOrWhiteSpace(path) || !File.Exists(path)) throw new FileNotFoundException("File not found.", path);
        // Evidence files are untrusted: anything the shell would execute is revealed in Explorer instead of launched.
        if (IsExecutableLike(path)) { OpenFolder(path); return; }
        Process.Start(new ProcessStartInfo(path) { UseShellExecute = true })?.Dispose();
    }

    private static readonly HashSet<string> ExecutableExtensions = new(StringComparer.OrdinalIgnoreCase)
    {
        ".exe", ".com", ".scr", ".pif", ".bat", ".cmd", ".lnk", ".url", ".vbs", ".vbe", ".js", ".jse", ".wsf", ".wsh", ".ps1", ".psm1",
        ".msi", ".msp", ".msc", ".cpl", ".hta", ".jar", ".reg", ".dll", ".sys", ".appref-ms", ".application", ".gadget", ".inf", ".sct", ".chm", ".py", ".pyw"
    };

    internal static bool IsExecutableLike(string path)
    {
        var ext = Path.GetExtension(path.Trim().Trim('"'));
        return string.IsNullOrEmpty(ext) || ExecutableExtensions.Contains(ext);
    }
}
