using System.Collections.Concurrent;
using System.Collections.ObjectModel;
using System.Collections.Specialized;
using System.ComponentModel;
using System.Windows.Threading;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using EzForensics.App.Services;
using EzForensics.Core.Case;
using EzForensics.Core.Execution;
using EzForensics.Core.Results;
using EzForensics.Core.Settings;

namespace EzForensics.App.ViewModels;

public sealed partial class ResultsViewModel
{
    // ------------------------------------------------------------ tree

    [RelayCommand]
    private async Task RefreshTreeAsync()
    {
        var folder = CaseFolder;
        if (string.IsNullOrWhiteSpace(folder) || !Directory.Exists(folder))
        {
            CancelStats();
            Tree.Clear();
            return;
        }
        IsLoadingTree = true;
        try
        {
            // Keep the user's expansion state across refreshes (by path); a new case starts fully collapsed.
            var all = Tree.SelectMany(n => n.SelfAndDescendants()).ToList();
            var expanded = new HashSet<string>(all.Where(n => n.IsExpanded).Select(n => n.FullPath), StringComparer.OrdinalIgnoreCase);
            // Stats of unchanged files carry over, so a refresh recomputes only new or changed files.
            var oldStats = new Dictionary<string, FileStats>(StringComparer.OrdinalIgnoreCase);
            foreach (var n in all) if (n.IsFile && n.Stats != null) oldStats[n.FullPath] = n.Stats;
            // The case index (manifests + layout) is reloaded with every refresh: Tool column, host folders, old-layout note.
            var (root, caseIndex) = await Task.Run(() =>
            {
                CaseIndex? ci = null;
                try { ci = CaseIndex.Load(folder); }
                catch (Exception ex) { AppLog.Error($"Could not read the case index of {folder}", ex); }
                return (BuildTree(folder, ci, true), ci);
            });
            if (!string.Equals(folder, CaseFolder, StringComparison.OrdinalIgnoreCase)) return; // case changed meanwhile
            TreeCaseIndex = caseIndex;
            ShowLegacyLayoutNote = caseIndex is { LayoutVersion: CaseLayoutVersion.HostArtifact, HasLegacyOutputs: true };
            var top = root?.Children.ToList() ?? new List<FileNode>();
            FileNode.Link(top, null, 0);
            foreach (var t in top) t.AggregateTools();
            foreach (var n in top.SelectMany(t => t.SelfAndDescendants()))
            {
                if (!n.IsFile) { if (expanded.Contains(n.FullPath)) n.IsExpanded = true; continue; }
                if (oldStats.TryGetValue(n.FullPath, out var st) && st.Matches(n.Length, n.LastWriteUtc)) n.Stats = st;
                else n.IsPending = true;
            }
            bool sameCase = string.Equals(folder, _treeFolder, StringComparison.OrdinalIgnoreCase);
            _treeFolder = folder;
            CancelStats();
            Tree.Clear();
            foreach (var c in top) Tree.Add(c);
            if (Columns.SortColumn != null) ApplyTreeSort();
            StartStats();
            ResetMultiSelection();
            if (!string.IsNullOrWhiteSpace(TreeSearchText)) RestartTreeSearch(); // keep the search filter over the new tree
            if (Tree.Count == 0) StatusText = $"No CSV files under {folder} yet.";
            // Same case: keep what the user expanded/collapsed. New case: open only the path to the file being viewed.
            else if (CurrentFile != null) RevealFile(CurrentFile, expand: !sameCase);
        }
        catch (Exception ex) { ShowError("Could not list CSV files", ex); }
        finally { IsLoadingTree = false; }
    }

    /// <summary>
    /// Mirrors the case folder: folders holding CSVs (and empty folders, e.g. a new one), CSV files. <c>logs</c> and dot-folders
    /// (<c>.ezstaging</c>, <c>.ezcache</c>) are never listed. Files get their Tool from <paramref name="index"/>; top-level host folders of a
    /// layout v2 case are marked <see cref="FileNode.IsHost"/>.
    /// </summary>
    private static FileNode? BuildTree(string dir, CaseIndex? index, bool isCaseRoot)
    {
        var node = new FileNode(Path.GetFileName(dir.TrimEnd('\\')), dir, false);
        var opts = new EnumerationOptions { IgnoreInaccessible = true, RecurseSubdirectories = false };
        var entries = new List<FileNode>();
        bool empty = true;
        foreach (var sub in Directory.EnumerateDirectories(dir, "*", opts))
        {
            empty = false;
            if (CaseTreeOrder.IsHiddenFolder(Path.GetFileName(sub))) continue;
            var child = BuildTree(sub, index, false);
            if (child == null) continue;
            if (isCaseRoot && CaseTreeOrder.IsCaseWide(child.Name)) child.IsCaseWide = true;
            else if (isCaseRoot && index != null && index.Hosts.Contains(child.Name, StringComparer.OrdinalIgnoreCase)) child.IsHost = true;
            else child.Kind = EvidenceKinds.ForFolder(child.Name);
            entries.Add(child);
        }
        // CSVs only: EZ Forensics' sidecars (.ezflags.json, .ezstats.json), manifests and logs are not listed.
        foreach (var f in Directory.EnumerateFiles(dir, "*", opts))
        {
            empty = false;
            if (!CaseTreeOrder.IsListedFile(Path.GetFileName(f))) continue;
            var n = new FileNode(Path.GetFileName(f), f, true);
            try { n.Tool = index?.Describe(f).ToolId; }
            catch (Exception ex) { AppLog.Error($"Could not describe {f}", ex); }
            try
            {
                var fi = new FileInfo(f);
                n.Length = fi.Length;
                n.LastWriteUtc = fi.LastWriteTimeUtc;
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { }
            entries.Add(n);
        }
        if (isCaseRoot) RelocateTimelines(entries);
        // Super timeline output (Timeline folder, SuperTimeline_*.csv newest first) first, then folders, then files.
        var sorted = CaseTreeOrder.Sort(entries, n => n.Name, n => !n.IsFile, n => n.LastWriteUtc);
        for (int i = 0; i < sorted.Count; i++)
        {
            sorted[i].DefaultIndex = i;
            node.Children.Add(sorted[i]);
        }
        return node.Children.Count > 0 || (empty && !isCaseRoot) ? node : null;
    }

    /// <summary>
    /// Super timelines are written under their host (<c>&lt;case&gt;\&lt;Host&gt;\Timeline</c>, or <c>_Case\Timeline</c> for unattributed
    /// evidence) and stay where they are. Cases from earlier versions hold one case-level <c>&lt;case&gt;\Timeline</c> (and loose
    /// SuperTimeline_*.csv): with exactly one host folder it is shown under that host (<see cref="CaseTreeOrder.LegacyTimelineHost"/>),
    /// otherwise it stays at the top of the tree.
    /// </summary>
    internal static void RelocateTimelines(List<FileNode> rootEntries)
    {
        var moving = rootEntries.Where(n => !n.IsHost && !n.IsCaseWide && CaseTreeOrder.IsSuperTimeline(n.Name, !n.IsFile)).ToList();
        if (moving.Count == 0) return;
        var hosts = rootEntries.Where(n => n.IsHost).ToList();
        var host = CaseTreeOrder.LegacyTimelineHost(hosts.Select(h => h.Name).ToList());
        if (host == null) return;
        var target = hosts.First(h => h.Name == host);
        foreach (var m in moving)
        {
            rootEntries.Remove(m);
            target.Children.Add(m);
        }
        var resorted = CaseTreeOrder.Sort(target.Children.ToList(), n => n.Name, n => !n.IsFile, n => n.LastWriteUtc);
        target.Children.Clear();
        for (int i = 0; i < resorted.Count; i++)
        {
            resorted[i].DefaultIndex = i;
            target.Children.Add(resorted[i]);
        }
    }

    [RelayCommand]
    private void ExpandAll()
    {
        foreach (var n in Tree.SelectMany(t => t.SelfAndDescendants())) if (!n.IsFile) n.IsExpanded = true;
    }

    [RelayCommand]
    private void CollapseAll()
    {
        foreach (var n in Tree.SelectMany(t => t.SelfAndDescendants())) n.IsExpanded = false;
        UpdateSelectionInfo();
    }

    /// <summary>Selects the tree node of <paramref name="path"/> and (with <paramref name="expand"/>) expands only its ancestors.</summary>
    public bool RevealFile(string path, bool expand = true)
    {
        var node = Tree.SelectMany(t => t.SelfAndDescendants())
            .FirstOrDefault(n => n.IsFile && string.Equals(n.FullPath, path, StringComparison.OrdinalIgnoreCase));
        if (node == null) return false;
        if (expand)
            for (var p = node.Parent; p != null; p = p.Parent) p.IsExpanded = true;
        foreach (var n in Tree.SelectMany(t => t.SelfAndDescendants())) if (n != node && n.IsSelected) n.IsSelected = false;
        node.IsSelected = true;
        RevealRequested?.Invoke(node);
        return true;
    }

    /// <summary>Raised after <see cref="RevealFile"/> so the view scrolls the node into view (vertically only).</summary>
    public event Action<FileNode>? RevealRequested;

    // ------------------------------------------------------------ tree columns (sort, picker)

    /// <summary>Header click: sorts the siblings of every folder by <paramref name="column"/> (again = reverse). Timeline stays first.</summary>
    public void SortTreeBy(string column)
    {
        if (Columns.SortColumn == column) Columns.SortDescending = !Columns.SortDescending;
        else
        {
            Columns.SortColumn = column;
            Columns.SortDescending = false;
        }
        ApplyTreeSort();
    }

    private void ApplyTreeSort()
    {
        // Moving items regenerates their containers; the TreeView then selects another item. Keep the user's selection.
        var all = Tree.SelectMany(t => t.SelfAndDescendants()).ToList();
        var selected = all.FirstOrDefault(n => n.IsSelected && n.IsFile && string.Equals(n.FullPath, CurrentFile, StringComparison.OrdinalIgnoreCase))
                       ?? all.FirstOrDefault(n => n.IsSelected);
        Reorder(Tree, Columns.SortColumn, Columns.SortDescending, true);
        FileNode.Link(Tree, null, 0);
        if (IsTreeFiltered) FileNode.LinkVisible(Tree);
        foreach (var n in all) if (n != selected && n.IsSelected) n.IsSelected = false;
        if (selected != null) selected.IsSelected = true;
    }

    private static void Reorder(ObservableCollection<FileNode> items, string? column, bool descending, bool top)
    {
        var def = items.OrderBy(n => n.DefaultIndex).ToList();
        var order = column == null ? def : CaseTreeOrder.SortBy(def, n => n.Name, n => !n.IsFile,
            column == TreeColumnsViewModel.Name ? null : n => n.SortKey(column), descending, top);
        for (int i = 0; i < order.Count; i++)
        {
            int cur = items.IndexOf(order[i]);
            if (cur != i) items.Move(cur, i);
        }
        foreach (var n in items) if (!n.IsFile) Reorder(n.Children, column, descending, false);
    }

    /// <summary>Column picker: shows or hides an optional column and saves the choice.</summary>
    public void SetColumnVisible(string column, bool visible)
    {
        Columns.SetVisible(column, visible);
        SaveTreeColumns();
    }

    /// <summary>Column picker "Show all": every optional column visible (widths kept).</summary>
    public void ShowAllTreeColumns()
    {
        foreach (var c in TreeColumnsViewModel.Optional) Columns.SetVisible(c, true);
        SaveTreeColumns();
    }

    /// <summary>Column picker "Reset": all columns visible at their default widths.</summary>
    public void ResetTreeColumns()
    {
        Columns.Load(new ResultsTreeColumns());
        SaveTreeColumns();
    }

    /// <summary>Persists column visibility and widths (<see cref="AppSettings.ResultsTreeColumns"/>).</summary>
    public void SaveTreeColumns()
    {
        S.Settings.ResultsTreeColumns = Columns.ToSettings();
        try { S.SaveSettings(); } catch (Exception ex) { AppLog.Error("Could not save settings", ex); }
    }

    // ------------------------------------------------------------ tree stats (Earliest / Latest / Events)

    private void AggregateTree()
    {
        var fmt = S.Settings.DateFormat;
        foreach (var t in Tree) t.Aggregate(fmt);
    }

    private void CancelStats()
    {
        _statsCts?.Cancel();
        _statsCts = null;
        _statsGen++;
        _statsTimer.Stop();
        IsComputingStats = false;
    }

    /// <summary>
    /// Computes the stats of every pending file in the background: cached ones first (sidecars), then the rest,
    /// smallest first, with limited parallelism. Results reach the tree in batches (<see cref="DrainStats"/>).
    /// </summary>
    private void StartStats()
    {
        CancelStats();
        var files = Tree.SelectMany(t => t.SelfAndDescendants()).Where(n => n.IsFile && n.IsPending).ToList();
        AggregateTree();
        if (files.Count == 0) return;
        var cts = _statsCts = new CancellationTokenSource();
        int gen = _statsGen;
        IsComputingStats = true;
        _statsTimer.Start();
        _ = Task.Run(() =>
        {
            var po = new ParallelOptions { MaxDegreeOfParallelism = Math.Clamp(Environment.ProcessorCount / 2, 1, 4), CancellationToken = cts.Token };
            try
            {
                var misses = new ConcurrentBag<FileNode>();
                Parallel.ForEach(files, po, n =>
                {
                    var s = _statsCache.TryGet(n.FullPath);
                    if (s != null) _statsQueue.Enqueue((gen, n, s)); else misses.Add(n);
                });
                Parallel.ForEach(misses.OrderBy(n => n.Length).ToList(), po, n =>
                {
                    FileStats? s = null;
                    try { s = _statsCache.GetOrCompute(n.FullPath, cts.Token); }
                    catch (OperationCanceledException) { throw; }
                    catch (Exception ex) { AppLog.Error($"Could not compute stats of {n.FullPath}", ex); }
                    _statsQueue.Enqueue((gen, n, s));
                });
            }
            catch (OperationCanceledException) { }
            catch (Exception ex) { AppLog.Error("Tree stats failed", ex); }
            finally { Volatile.Write(ref _statsDoneGen, gen); }
        });
    }

    private void DrainStats()
    {
        bool any = false;
        while (_statsQueue.TryDequeue(out var r))
        {
            if (r.Gen != _statsGen) continue;
            var n = r.Node;
            n.Stats = r.Stats;
            n.IsPending = false;
            if (r.Stats != null)
            {
                n.Length = r.Stats.Size;
                n.LastWriteUtc = r.Stats.LastWriteUtc;
            }
            any = true;
        }
        if (any) AggregateTree();
        if (Volatile.Read(ref _statsDoneGen) == _statsGen && _statsQueue.IsEmpty)
        {
            _statsTimer.Stop();
            IsComputingStats = false;
            // a stats column sort is final only once every value is known
            if (Columns.SortColumn is { } c && c != TreeColumnsViewModel.Name) ApplyTreeSort();
        }
    }

    /// <summary>Re-reads size / last-write time of the listed files and recomputes the stats of those that changed.</summary>
    private async Task RevalidateStatsAsync()
    {
        var files = Tree.SelectMany(t => t.SelfAndDescendants()).Where(n => n.IsFile).ToList();
        if (files.Count == 0) return;
        List<(FileNode Node, long Length, DateTime Mtime)> fresh;
        try
        {
            fresh = await Task.Run(() => files.Select(n =>
            {
                try
                {
                    var fi = new FileInfo(n.FullPath);
                    return fi.Exists ? (n, fi.Length, fi.LastWriteTimeUtc) : (n, -1L, default(DateTime));
                }
                catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { return (n, -1L, default(DateTime)); }
            }).ToList());
        }
        catch (Exception ex) { AppLog.Error("Could not check tree files", ex); return; }
        bool changed = false;
        foreach (var (n, len, mtime) in fresh)
        {
            if (len < 0 || !Tree.Contains(Top(n))) continue; // gone, or the tree was rebuilt meanwhile
            if (n.Length == len && n.LastWriteUtc == mtime && (n.Stats == null || n.Stats.Matches(len, mtime))) continue;
            n.Length = len;
            n.LastWriteUtc = mtime;
            n.Stats = null;
            n.IsPending = true;
            changed = true;
        }
        if (changed) StartStats();
    }

    private static FileNode Top(FileNode n)
    {
        while (n.Parent != null) n = n.Parent;
        return n;
    }
}
