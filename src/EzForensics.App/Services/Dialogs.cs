using System.Windows;
using EzForensics.App.Views;
using Microsoft.Win32;

namespace EzForensics.App.Services;

public static class Dialogs
{
    /// <summary>Ingest page Browse filter: packet captures, memory images, archives (extracted, then scanned) and virtual disks (detected; mount them).</summary>
    public const string EvidenceFileFilter =
        "Evidence files|*.pcap;*.pcapng;*.cap;*.pcap*;*.dmp;*.raw;*.mem;*.vmem;*.vmss;*.vmsn;*.lime;hiberfil.sys;*.zip;*.7z;*.rar;*.tar;*.tgz;*.tar.gz;*.gz;*.vhd;*.vhdx"
        + "|Archives (zip, 7z, rar, tar, gz)|*.zip;*.7z;*.rar;*.tar;*.tgz;*.tar.gz;*.gz"
        + "|Packet captures|*.pcap;*.pcapng;*.cap;*.dmp;*.pcap*"
        + "|Memory images|*.dmp;*.raw;*.mem;*.vmem;*.vmss;*.vmsn;*.lime;hiberfil.sys"
        + "|Virtual disks (mount them, then pick the drive)|*.vhd;*.vhdx"
        + "|All files|*.*";

    // ---- Message dialogs (EzDialog; replaces every stock MessageBox) ----

    /// <summary>
    /// Themed message dialog with 1-3 custom buttons. Returns the index of the clicked button; Esc / closing returns the cancel index
    /// (default: the last button when there are several, else 0). Technical text goes in <paramref name="details"/> (collapsed expander).
    /// Dialogs never show an icon or glyph (the severity is the frame colour); <c>plain</c> is kept for call-site compatibility and changes nothing.
    /// </summary>
    public static int Show(string title, string message, EzDialogSeverity severity = EzDialogSeverity.Info, string? details = null,
        IReadOnlyList<string>? buttons = null, int primary = 0, int cancel = -2, Window? owner = null, bool plain = false)
    {
        buttons ??= new[] { "OK" };
        if (cancel == -2) cancel = buttons.Count > 1 ? buttons.Count - 1 : 0;
        var app = Application.Current;
        if (app == null) return cancel;
        if (!app.Dispatcher.CheckAccess())
            return app.Dispatcher.Invoke(() => Show(title, message, severity, details, buttons, primary, cancel, owner, plain));
        owner ??= app.Windows.OfType<Window>().FirstOrDefault(w => w.IsActive) ?? app.MainWindow;
        if (owner != null && !owner.IsVisible) owner = null;
        var dlg = new EzDialog(title, message, severity, details, buttons, primary, cancel, plain);
        if (owner != null) dlg.Owner = owner; else dlg.WindowStartupLocation = WindowStartupLocation.CenterScreen;
        dlg.ShowDialog();
        return dlg.Result;
    }

    public static void Info(string title, string message, string? details = null, Window? owner = null) =>
        Show(title, message, EzDialogSeverity.Info, details, owner: owner);

    public static void Warn(string title, string message, string? details = null, Window? owner = null) =>
        Show(title, message, EzDialogSeverity.Warning, details, owner: owner);

    public static void Error(string title, string message, string? details = null, Window? owner = null) =>
        Show(title, message, EzDialogSeverity.Error, details, owner: owner);

    /// <summary>Two-button question: true when <paramref name="yes"/> was chosen. <paramref name="defaultNo"/> makes the safe button the primary one.</summary>
    public static bool Confirm(string title, string message, string yes = "OK", string no = "Cancel", EzDialogSeverity severity = EzDialogSeverity.Question,
        string? details = null, bool defaultNo = false, Window? owner = null, bool plain = false) =>
        Show(title, message, severity, details, new[] { yes, no }, defaultNo ? 1 : 0, 1, owner, plain) == 0;

    /// <summary>Asks for one line of text (a rationale, a reason). Null when cancelled; the text can be empty unless <paramref name="required"/>.</summary>
    public static string? Prompt(string title, string message, string? initial = null, string ok = "OK", bool required = true, Window? owner = null)
    {
        var app = Application.Current;
        if (app == null) return null;
        if (!app.Dispatcher.CheckAccess()) return app.Dispatcher.Invoke(() => Prompt(title, message, initial, ok, required, owner));
        owner ??= app.Windows.OfType<Window>().FirstOrDefault(w => w.IsActive) ?? app.MainWindow;
        if (owner != null && !owner.IsVisible) owner = null;
        var dlg = new EzDialog(title, message, EzDialogSeverity.Question, null, new[] { ok, "Cancel" }, 0, 1);
        dlg.EnableInput(initial);
        if (owner != null) dlg.Owner = owner; else dlg.WindowStartupLocation = WindowStartupLocation.CenterScreen;
        while (true)
        {
            dlg.ShowDialog();
            if (dlg.Result != 0) return null;
            var text = dlg.InputText.Trim();
            if (text.Length > 0 || !required) return text;
            // required and empty: ask again with a fresh dialog (a closed window cannot be shown twice)
            dlg = new EzDialog(title, message + "\n(Please give a short reason.)", EzDialogSeverity.Question, null, new[] { ok, "Cancel" }, 0, 1);
            dlg.EnableInput(initial);
            if (owner != null) dlg.Owner = owner; else dlg.WindowStartupLocation = WindowStartupLocation.CenterScreen;
        }
    }

    public static string? PickFolder(string title, string? initial = null)
    {
        var dlg = new OpenFolderDialog { Title = title, Multiselect = false };
        var start = ExistingDir(initial);
        if (start != null) dlg.InitialDirectory = start;
        return dlg.ShowDialog() == true ? dlg.FolderName : null;
    }

    /// <summary>File name the file-or-folder dialog starts with: clicking Open with it returns the folder being shown.</summary>
    public const string SelectFolderPlaceholder = "Select this folder";

    /// <summary>
    /// One dialog that picks a file or a folder (the Ingest source): pick a file as usual, or open a folder and click Open with the
    /// "Select this folder" name. Returns the file or folder path, or null when cancelled.
    /// </summary>
    public static string? PickFileOrFolder(string title, string? filter = null, string? initial = null)
    {
        var dlg = new OpenFileDialog
        {
            Title = title, Filter = string.IsNullOrWhiteSpace(filter) ? "All files|*.*" : filter,
            CheckFileExists = false, CheckPathExists = true, ValidateNames = false, FileName = SelectFolderPlaceholder,
        };
        var start = ExistingDir(initial);
        if (start != null) dlg.InitialDirectory = start;
        return dlg.ShowDialog() == true ? ResolveFileOrFolder(dlg.FileName) : null;
    }

    /// <summary>The dialog's result as a file or folder: an existing file or folder as is, the placeholder (or any name that does not exist) as its folder.</summary>
    internal static string? ResolveFileOrFolder(string? picked)
    {
        if (string.IsNullOrWhiteSpace(picked)) return null;
        if (File.Exists(picked) || Directory.Exists(picked)) return picked;
        var dir = Path.GetDirectoryName(picked);
        return dir != null && Directory.Exists(dir) ? dir : null;
    }

    public static string? PickFile(string title, string? filter = null, string? initial = null)
    {
        var dlg = new OpenFileDialog { Title = title, Filter = string.IsNullOrWhiteSpace(filter) ? "All files|*.*" : filter, CheckFileExists = true };
        var start = ExistingDir(initial);
        if (start != null) dlg.InitialDirectory = start;
        try { return dlg.ShowDialog() == true ? dlg.FileName : null; }
        catch (ArgumentException)
        {
            dlg.Filter = "All files|*.*"; // malformed filter in a catalog entry
            return dlg.ShowDialog() == true ? dlg.FileName : null;
        }
    }

    /// <summary>Several files (null when cancelled).</summary>
    public static IReadOnlyList<string>? PickFiles(string title, string? filter = null, string? initial = null)
    {
        var dlg = new OpenFileDialog { Title = title, Filter = string.IsNullOrWhiteSpace(filter) ? "All files|*.*" : filter, CheckFileExists = true, Multiselect = true };
        var start = ExistingDir(initial);
        if (start != null) dlg.InitialDirectory = start;
        return dlg.ShowDialog() == true ? dlg.FileNames : null;
    }

    public static string? SaveFile(string title, string filter, string fileName, string? initial = null)
    {
        var dlg = new SaveFileDialog { Title = title, Filter = filter, FileName = fileName, OverwritePrompt = true };
        var start = ExistingDir(initial);
        if (start != null) dlg.InitialDirectory = start;
        return dlg.ShowDialog() == true ? dlg.FileName : null;
    }

    private static string? ExistingDir(string? p)
    {
        if (string.IsNullOrWhiteSpace(p)) return null;
        try
        {
            if (Directory.Exists(p)) return p;
            var parent = Path.GetDirectoryName(p);
            return parent != null && Directory.Exists(parent) ? parent : null;
        }
        catch { return null; }
    }
}
