using System.Text;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using EzForensics.App.Services;
using EzForensics.App.Views;
using EzForensics.Core.Explore;

namespace EzForensics.App.ViewModels.Explore;

// Right-click actions on files. Nothing here executes or shell-opens evidence: copying and exporting only read the file,
// "Show in Explorer" only reveals it (ShellOps.OpenFolder selects the file; it never launches it).
public sealed partial class ExploreViewModel
{
    [ObservableProperty] private bool _isExporting;
    [ObservableProperty] private double _exportProgress;

    private IReadOnlyList<FileRow> Targets(FileRow? row)
    {
        if (row != null && SelectedRows.Contains(row)) return SelectedRows;
        if (row != null) return new[] { row };
        return SelectedRows.Count > 0 ? SelectedRows : (SelectedRow != null ? new[] { SelectedRow } : Array.Empty<FileRow>());
    }

    [RelayCommand]
    private void CopyPath(FileRow? row)
    {
        var t = Targets(row);
        if (t.Count > 0) SetClipboard(string.Join(Environment.NewLine, t.Select(r => r.FullPath)));
    }

    [RelayCommand]
    private async Task CopySha256Async(FileRow? row)
    {
        var r = Targets(row).FirstOrDefault(x => !x.IsDirectory);
        if (r == null) return;
        await GuardAsync(async () =>
        {
            var hash = r.Sha256;
            if (string.IsNullOrEmpty(hash))
            {
                var facts = await GetFactsAsync(r.Entry, CancellationToken.None, null, background: false);
                r.Apply(facts);
                hash = facts.Hashes.Sha256;
            }
            SetClipboard(hash);
        }, "Could not hash the file");
    }

    [RelayCommand]
    private void ShowInExplorer(FileRow? row)
    {
        var r = Targets(row).FirstOrDefault();
        if (r == null) return;
        // Reveal only (explorer.exe /select,...). The evidence file itself is never started.
        Guard(() => ShellOps.OpenFolder(r.FullPath), "Could not show the file in Explorer");
    }

    [RelayCommand]
    private async Task SendFileToCyberChefAsync(FileRow? row)
    {
        var r = Targets(row).FirstOrDefault(x => !x.IsDirectory);
        if (r == null) return;
        await GuardAsync(async () =>
        {
            // One byte past the cap is enough: CyberChefHost trims to the cap and tells the user.
            var data = await Task.Run(() => ReadBytes(r.FullPath, 0, Views.Explore.CyberChefLogic.MaxInputBytes + 1));
            await SendToCyberChefAsync(data);
        }, "Could not send the file to CyberChef");
    }

    [RelayCommand(CanExecute = nameof(CanSendString))]
    private Task SendStringToCyberChefAsync(ExtractedString? s) =>
        s == null ? Task.CompletedTask : SendToCyberChefAsync(Encoding.UTF8.GetBytes(s.Text));

    private static bool CanSendString(ExtractedString? s) => s != null;

    [RelayCommand]
    private void CopyText(string? text) { if (!string.IsNullOrEmpty(text)) SetClipboard(text); }

    /// <summary>Bytes <paramref name="offset"/>..+<paramref name="count"/> of an evidence file (read-only).</summary>
    public static byte[] ReadBytes(string path, long offset, long count)
    {
        using var fs = FileFactsReader.OpenEvidence(path);
        fs.Position = Math.Min(offset, fs.Length);
        var n = (int)Math.Min(count, fs.Length - fs.Position);
        var buf = new byte[n];
        int read = 0, r;
        while (read < n && (r = fs.Read(buf, read, n - read)) > 0) read += r;
        return read == n ? buf : buf[..read];
    }

    [RelayCommand]
    private async Task ExportAsync(FileRow? row)
    {
        if (_sources == null || IsExporting) return;
        var files = Targets(row).Where(r => !r.IsDirectory).Select(r => r.FullPath).ToList();
        if (files.Count == 0) { ShowMessage("Select one or more files to export.", InfoSeverity.Warning); return; }
        IsExporting = true;
        ExportProgress = 0;
        try
        {
            var progress = new Progress<double>(p => ExportProgress = p);
            var sources = _sources;
            var result = await Task.Run(() => ExploreExport.Export(sources, files, progress));
            string msg = $"{result.Copied} file{(result.Copied == 1 ? "" : "s")} exported with a manifest (hashes, times, source paths)."
                         + (result.Failed > 0 ? $"\n{result.Failed} failed; see manifest.json." : "");
            int pick = Dialogs.Show("Export finished", msg + "\n\n" + result.Folder,
                result.Failed > 0 ? EzDialogSeverity.Warning : EzDialogSeverity.Success, null, new[] { "Open folder", "Close" }, 0, 1);
            // The export folder is our own output (copies), so opening it in Explorer is fine.
            if (pick == 0) Guard(() => ShellOps.OpenFolder(result.Folder), "Could not open the export folder");
        }
        catch (Exception ex) { ShowError("Export failed", ex); }
        finally { IsExporting = false; }
    }
}
