using System.Collections.Specialized;
using System.Windows;
using CommunityToolkit.Mvvm.ComponentModel;
using EzForensics.App.Services;
using EzForensics.Core.Case;
using EzForensics.Core.Results;

namespace EzForensics.App.ViewModels;

/// <summary>
/// Case tree file operations (#14: open, rename, delete, cut/copy/paste, duplicate, new folder, show in Explorer, copy path) and
/// multi-file views (#13: combined view, merge into new file). The rules (case bounds, protected files, sidecars, no overwrite,
/// file_operations.log) live in <see cref="CaseFileOperations"/>.
/// </summary>
public sealed partial class ResultsViewModel
{
    /// <summary>Paths put on the tree's clipboard by Copy / Cut (pasted inside the case only).</summary>
    private List<string> _fileClipboard = new();
    private bool _fileClipboardCut;

    /// <summary>Combined view: the cached merged CSV that is open (<c>&lt;case&gt;\.ezcache\combined\&lt;key&gt;.csv</c>).</summary>
    private string? _combinedPath;
    /// <summary>Info line for an open combined view (null otherwise).</summary>
    [ObservableProperty] private string? _combinedViewInfo;
    [ObservableProperty] private bool _isCombining;
    [ObservableProperty] private double _combineProgress;
    [ObservableProperty] private string _combineStatus = "";

    public bool CanPaste => _fileClipboard.Count > 0 || SafeClipboardHasFiles();

    partial void OnCurrentFileChanged(string? value)
    {
        if (!string.Equals(value, _combinedPath, StringComparison.OrdinalIgnoreCase))
        {
            _combinedPath = null;
            CombinedViewInfo = null;
        }
        // a file opened from elsewhere (saved view, jobs, reveal) moves the tree selection to it, unless the user is multi-selecting
        if (value != null && MultiSelectedCount < 2 && !string.Equals(SelectedNode?.FullPath, value, StringComparison.OrdinalIgnoreCase))
            RevealFile(value);
        UpdateOpenHighlight();
    }

    private CaseFileOperations Ops()
    {
        var folder = CaseFolder;
        if (string.IsNullOrWhiteSpace(folder) || !Directory.Exists(folder)) throw new InvalidOperationException("Open a case folder first.");
        var owner = IntPtr.Zero;
        try { if (System.Windows.Application.Current?.MainWindow is { } w) owner = new System.Windows.Interop.WindowInteropHelper(w).Handle; }
        catch (Exception) { }
        return new CaseFileOperations(folder, RecycleBin.For(owner));
    }

    // ------------------------------------------------------------ open

    /// <summary>Enter / context menu Open: one file opens as usual; several files open as a combined view.</summary>
    public async Task OpenSelectionAsync()
    {
        var files = SelectedFiles;
        if (files.Count == 0) return;
        if (files.Count == 1)
        {
            var f = files[0];
            if (!string.Equals(f.FullPath, CurrentFile, StringComparison.OrdinalIgnoreCase)) await OpenCsvAsync(f.FullPath);
            return;
        }
        await OpenCombinedAsync(files.Select(f => f.FullPath).ToList());
    }

    /// <summary>
    /// Combined read-only view of several CSVs with the same columns: they are merged (CsvMerger, leading SourceFile and Host columns)
    /// into a cached copy under the hidden <c>&lt;case&gt;\.ezcache\combined</c> folder and that copy is opened, so filters, sort,
    /// export and copy work exactly as for one file. Flags set there stay with the cached copy (never the source files).
    /// </summary>
    public async Task OpenCombinedAsync(IReadOnlyList<string> paths)
    {
        if (paths.Count == 0) return;
        if (paths.Count == 1) { await OpenCsvAsync(paths[0]); return; }
        var folder = CaseFolder;
        if (folder == null) return;
        await GuardAsync(async () =>
        {
            if (!await Task.Run(() => CsvMerger.HaveSameSchema(paths)))
            {
                ShowMessage("Selected files have different columns — use Merge into new file.", InfoSeverity.Warning);
                return;
            }
            var index = TreeCaseIndex;
            var inputs = paths.Select(p => new CsvMergeInput(p, index?.HostOf(p))).ToList();
            var (dir, key) = await Task.Run(() => (CaseFileOperations.EnsureCombinedCacheDir(folder), CaseFileOperations.CombinedKey(paths)));
            var target = Path.Combine(dir, key + ".csv");
            if (!File.Exists(target))
            {
                IsCombining = true;
                CombineStatus = $"Combining {paths.Count} files…";
                var tmp = Path.Combine(dir, key + ".partial");
                try
                {
                    if (File.Exists(tmp)) File.Delete(tmp); // own cache file from an interrupted run
                    await CsvMerger.MergeAsync(inputs, tmp, new CsvMergeOptions(), new Progress<double>(p => CombineProgress = p));
                    File.Move(tmp, target);
                }
                finally
                {
                    IsCombining = false;
                    CombineProgress = 0;
                    CombineStatus = "";
                }
            }
            else File.SetLastAccessTimeUtc(target, DateTime.UtcNow);
            await Task.Run(() => CaseFileOperations.PruneCombinedCache(folder, 5, target));
            _combinedPath = target;
            var hosts = inputs.Select(i => i.Host).Where(h => !string.IsNullOrEmpty(h)).Distinct(StringComparer.OrdinalIgnoreCase).Count();
            // one line on the tree panel; the rest is its "?" tooltip (CaseTreePanel)
            CombinedViewInfo = $"Combined view of {paths.Count} files" + (hosts > 1 ? $" from {hosts} hosts" : "") + " (read-only)";
            await OpenCsvAsync(target);
            AppLog.Info($"Combined view of {paths.Count} files: {target}");
        }, "Could not open the combined view");
    }

    // ------------------------------------------------------------ merge into new file

    /// <summary>
    /// Context menu "Merge into new file…": asks for the output (default <c>&lt;case&gt;\&lt;host or Merged&gt;\Merged\Merged_&lt;n&gt;.csv</c>),
    /// merges with progress, records the derived output (logs\derived_outputs.json + file_operations.log), refreshes and reveals it.
    /// </summary>
    public async Task MergeSelectionAsync()
    {
        var files = SelectedFiles.Select(f => f.FullPath).ToList();
        if (files.Count < 2) { ShowMessage("Select at least two CSV files to merge.", InfoSeverity.Warning); return; }
        var folder = CaseFolder;
        if (folder == null) return;
        var index = TreeCaseIndex;
        var hosts = files.Select(f => index?.HostOf(f)).Where(h => !string.IsNullOrEmpty(h)).Distinct(StringComparer.OrdinalIgnoreCase).ToList();
        var def = CaseFileOperations.DefaultMergePath(folder, hosts.Count == 1 && files.All(f => !string.IsNullOrEmpty(index?.HostOf(f))) ? hosts[0] : null);
        var picked = Dialogs.SaveFile("Merge into new file", "CSV files|*.csv", Path.GetFileName(def), Path.GetDirectoryName(def));
        if (picked == null) return;
        await GuardAsync(async () =>
        {
            var ops = Ops();
            if (!ops.IsInsideCase(picked)) throw new CaseFileOperationException($"The merged file must be saved inside the case folder {folder}.");
            var inputs = files.Select(p => new CsvMergeInput(p, index?.HostOf(p))).ToList();
            IsCombining = true;
            CombineStatus = $"Merging {files.Count} files…";
            (string Path, CsvMergeResult Result) r;
            try { r = await ops.MergeAsync(inputs, picked, new Progress<double>(p => CombineProgress = p)); }
            finally
            {
                IsCombining = false;
                CombineProgress = 0;
                CombineStatus = "";
            }
            await RefreshTreeAsync();
            RevealFile(r.Path);
            var renamed = !string.Equals(Path.GetFileName(r.Path), Path.GetFileName(picked), StringComparison.OrdinalIgnoreCase)
                ? $" (saved as {Path.GetFileName(r.Path)})" : "";
            ShowMessage($"Merged {files.Count} files ({r.Result.RowsWritten:N0} rows) into {ops.Relative(r.Path)}{renamed}. Recorded in logs\\{CaseFileOperations.DerivedOutputsFileName}.",
                InfoSeverity.Success);
        }, "Merge failed");
    }

    // ------------------------------------------------------------ rename / delete / new folder / duplicate

    public Task RenameAsync(FileNode node, string newName) =>
        RunFileOpAsync(new[] { node.FullPath }, ops => new[] { ops.Rename(node.FullPath, newName) }, "Rename", moves: true);

    /// <summary>Del: confirms (listing the items), then sends them and their sidecars to the Recycle Bin.</summary>
    public async Task DeleteSelectionAsync()
    {
        var items = TreeSelection.ToList();
        if (items.Count == 0) return;
        // Windows has no Recycle Bin on network shares / removable drives: it would delete permanently. Offer nothing destructive.
        if (CaseFolder != null && CaseFileOperations.RecycleRefusal(CaseFolder) is { } why)
        {
            ShowMessage("Delete is not available here: " + why, InfoSeverity.Warning);
            return;
        }
        var shown = items.Take(15).Select(n => (n.IsFile ? "  " : "  [folder] ") + RelativeToCase(n.FullPath)).ToList();
        if (items.Count > 15) shown.Add($"  … and {items.Count - 15} more");
        if (!EzForensics.App.Services.Dialogs.Confirm("Move to the Recycle Bin?", items.Count == 1 ? "1 item (with its flags and sidecar files)." : $"{items.Count} items (with their flags and sidecar files).",
                "Delete", "Cancel", EzForensics.App.Views.EzDialogSeverity.Warning, string.Join("\n", shown), defaultNo: true)) return;
        var paths = items.Select(n => n.FullPath).ToList();
        await RunFileOpAsync(paths, ops => { ops.Delete(paths); return Array.Empty<string>(); }, "Delete", moves: false);
    }

    public Task NewFolderAsync(FileNode? at, string name = "New folder")
    {
        var parent = at == null ? CaseFolder! : at.IsFile ? Path.GetDirectoryName(at.FullPath)! : at.FullPath;
        return RunFileOpAsync(Array.Empty<string>(), ops => new[] { ops.CreateFolder(parent, name) }, "New folder", moves: false);
    }

    public Task DuplicateSelectionAsync()
    {
        var paths = TreeSelection.Select(n => n.FullPath).ToList();
        if (paths.Count == 0) return Task.CompletedTask;
        return RunFileOpAsync(Array.Empty<string>(), ops => paths.Select(ops.Duplicate).ToList(), "Duplicate", moves: false);
    }

    // ------------------------------------------------------------ clipboard

    /// <summary>Ctrl+C: remembers the items for Paste and puts their paths on the Windows clipboard (file drop list).</summary>
    public void CopySelection() => PutOnClipboard(cut: false);

    /// <summary>Ctrl+X: Paste then moves the items (within the case).</summary>
    public void CutSelection() => PutOnClipboard(cut: true);

    private void PutOnClipboard(bool cut)
    {
        var paths = TreeSelection.Select(n => n.FullPath).ToList();
        if (paths.Count == 0) return;
        _fileClipboard = paths;
        _fileClipboardCut = cut;
        try
        {
            var sc = new StringCollection();
            sc.AddRange(paths.ToArray());
            var data = new DataObject();
            data.SetFileDropList(sc);
            // Explorer: 2 = move (cut), 5 = copy
            data.SetData("Preferred DropEffect", new MemoryStream(BitConverter.GetBytes(cut ? 2 : 5)));
            // another process (clipboard manager) may hold the clipboard for a moment: retry, then keep the data without flushing
            for (int attempt = 0; ; attempt++)
            {
                try { Clipboard.SetDataObject(data, true); break; }
                catch (System.Runtime.InteropServices.COMException) when (attempt < 4) { Thread.Sleep(60); }
                catch (System.Runtime.InteropServices.COMException) { Clipboard.SetDataObject(data, false); break; }
            }
        }
        catch (Exception ex) { AppLog.Error("Could not set the clipboard", ex); }
        ShowMessage($"{(cut ? "Cut" : "Copied")} {paths.Count} item{(paths.Count == 1 ? "" : "s")}. Select a folder and press Ctrl+V to paste.", InfoSeverity.Info);
        OnPropertyChanged(nameof(CanPaste));
    }

    /// <summary>Ctrl+V: pastes into the selected folder (or the selected file's folder, or the case folder). Never overwrites.</summary>
    public async Task PasteAsync(FileNode? at)
    {
        var folder = CaseFolder;
        if (folder == null) return;
        var dest = at == null ? folder : at.IsFile ? Path.GetDirectoryName(at.FullPath)! : at.FullPath;
        var paths = _fileClipboard.ToList();
        bool cut = _fileClipboardCut;
        if (paths.Count == 0)
        {
            try { if (Clipboard.ContainsFileDropList()) paths = Clipboard.GetFileDropList().Cast<string>().ToList(); }
            catch (Exception ex) { AppLog.Error("Could not read the clipboard", ex); }
            cut = false;
        }
        if (paths.Count == 0) return;
        await RunFileOpAsync(cut ? paths : Array.Empty<string>(),
            ops => paths.Select(p => cut ? ops.Move(p, dest) : ops.Copy(p, dest)).ToList(), cut ? "Move" : "Paste", moves: cut);
        if (cut)
        {
            _fileClipboard = new List<string>();
            OnPropertyChanged(nameof(CanPaste));
        }
    }

    private static bool SafeClipboardHasFiles()
    {
        try { return Clipboard.ContainsFileDropList(); }
        catch (Exception) { return false; }
    }

    // ------------------------------------------------------------ shell

    public void ShowSelectionInExplorer()
    {
        var n = TreeSelection.FirstOrDefault();
        Guard(() => ShellOps.OpenFolder(n?.FullPath ?? CaseFolder), "Could not open Explorer");
    }

    public void CopySelectionPaths()
    {
        var paths = TreeSelection.Select(n => n.FullPath).ToList();
        if (paths.Count == 0) return;
        Guard(() => Clipboard.SetText(string.Join(Environment.NewLine, paths)), "Could not copy");
    }

    // ------------------------------------------------------------ plumbing

    private string RelativeToCase(string path)
    {
        var root = (CaseFolder ?? "").TrimEnd('\\') + "\\";
        return path.StartsWith(root, StringComparison.OrdinalIgnoreCase) ? path[root.Length..] : path;
    }

    /// <summary>
    /// Runs a file operation off the UI thread. When the open file is among <paramref name="affected"/> (or inside an affected folder) it is
    /// closed first (its flags are saved, so the flag sidecar moves with it) and, for moves / renames, re-opened at its new path.
    /// Then the tree is refreshed (expansion kept) and the first result revealed.
    /// </summary>
    private async Task RunFileOpAsync(IReadOnlyList<string> affected, Func<CaseFileOperations, IReadOnlyList<string>> op, string what, bool moves)
    {
        CaseFileOperations ops;
        try { ops = Ops(); }
        catch (Exception ex) { ShowError(what, ex); return; }
        var current = CurrentFile;
        int hit = current == null ? -1 : IndexOfAffected(affected, current);
        if (hit >= 0) CloseCurrentFile();
        CancelStats();
        IReadOnlyList<string> results = Array.Empty<string>();
        Exception? error = null;
        try { results = await Task.Run(() => op(ops)); }
        catch (Exception ex) { error = ex; }
        string? reopen = null;
        if (hit >= 0 && current != null)
        {
            if (error != null || (!moves && File.Exists(current))) reopen = current; // nothing happened to it
            else if (moves && hit < results.Count)
            {
                var from = Path.GetFullPath(affected[hit]).TrimEnd('\\');
                reopen = string.Equals(from, Path.GetFullPath(current), StringComparison.OrdinalIgnoreCase)
                    ? results[hit]
                    : results[hit] + current[from.Length..];
            }
        }
        await RefreshTreeAsync();
        if (reopen != null && File.Exists(reopen))
        {
            _ = OpenCsvAsync(reopen);
            RevealFile(reopen);
        }
        else if (hit >= 0 && error == null && !moves) StatusText = "The open file was deleted.";
        if (error != null)
        {
            if (error is CaseFileOperationException) ShowMessage(error.Message, InfoSeverity.Warning);
            else ShowError($"{what} failed", error);
            return;
        }
        if (results.Count > 0 && reopen == null) RevealPath(results[0]);
        ShowMessage(what switch
        {
            "Delete" => $"Sent {affected.Count} item{(affected.Count == 1 ? "" : "s")} to the Recycle Bin (unless you confirmed a Windows \"delete permanently\" prompt).",
            "Rename" => $"Renamed to {Path.GetFileName(results.FirstOrDefault() ?? "")}.",
            "New folder" => $"Created {RelativeToCase(results.FirstOrDefault() ?? "")}.",
            _ => $"{what}: {results.Count} item{(results.Count == 1 ? "" : "s")} → {string.Join(", ", results.Take(3).Select(Path.GetFileName))}{(results.Count > 3 ? ", …" : "")}",
        }, InfoSeverity.Success);
    }

    private static int IndexOfAffected(IReadOnlyList<string> affected, string current)
    {
        string cur;
        try { cur = Path.GetFullPath(current); } catch (Exception) { return -1; }
        for (int i = 0; i < affected.Count; i++)
        {
            string a;
            try { a = Path.GetFullPath(affected[i]).TrimEnd('\\'); } catch (Exception) { continue; }
            if (string.Equals(a, cur, StringComparison.OrdinalIgnoreCase) || cur.StartsWith(a + "\\", StringComparison.OrdinalIgnoreCase)) return i;
        }
        return -1;
    }

    /// <summary>Closes the open file (grid cleared, flags saved and released).</summary>
    private void CloseCurrentFile()
    {
        _openCts?.Cancel();
        _filterCts?.Cancel();
        _indexTimer.Stop();
        var old = _index;
        _index = null;
        Rows = null;
        Headers = Array.Empty<string>();
        Cells.Clear();
        ColumnsChanged?.Invoke();
        old?.Dispose();
        CloseFlags();
        CurrentFile = null;
        IsIndexing = false;
        StatusText = "Open a CSV file to view it.";
    }

    /// <summary>Selects and reveals a file or folder node by path (ancestors expanded).</summary>
    public bool RevealPath(string path)
    {
        var node = AllNodes().FirstOrDefault(n => string.Equals(n.FullPath, path, StringComparison.OrdinalIgnoreCase));
        if (node == null) return false;
        if (node.IsFile) return RevealFile(path);
        for (var p = node.Parent; p != null; p = p.Parent) p.IsExpanded = true;
        foreach (var n in AllNodes()) if (n != node && n.IsSelected) n.IsSelected = false;
        node.IsSelected = true;
        RevealRequested?.Invoke(node);
        return true;
    }
}
