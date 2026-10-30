using System.Windows.Threading;
using CommunityToolkit.Mvvm.ComponentModel;
using EzForensics.App.Services;
using EzForensics.App.Views.Analyze;
using EzForensics.Core.Case;
using EzForensics.Core.Results;

namespace EzForensics.App.ViewModels;

/// <summary>Case tree: search box filter (#9), multi-selection (#13), Tool column / layout note state (#19).</summary>
public sealed partial class ResultsViewModel : IMultiSelectTree
{
    // ------------------------------------------------------------ case index (Tool column, host folders, layout note)

    /// <summary>Case index of the tree's case, reloaded with every tree refresh (null when it could not be read).</summary>
    public CaseIndex? TreeCaseIndex { get; private set; }

    /// <summary>Layout v2 case that also holds layout v1 outputs (info line above the tree).</summary>
    [ObservableProperty] private bool _showLegacyLayoutNote;

    // ------------------------------------------------------------ search box

    /// <summary>Search box above the tree: filters by file name at once, by content (CsvKeywordSearch) after a short pause.</summary>
    [ObservableProperty] private string _treeSearchText = "";
    [ObservableProperty] private bool _isTreeSearching;
    [ObservableProperty] private double _treeSearchProgress;
    /// <summary>"12 files match", "Searching 40 of 120 files…" ("" without a search).</summary>
    [ObservableProperty] private string _treeSearchStatus = "";

    private DispatcherTimer? _treeSearchTimer;
    private CancellationTokenSource? _treeSearchCts;
    /// <summary>Expanded folders before the search started (restored when it is cleared).</summary>
    private HashSet<string>? _preSearchExpanded;
    private readonly Dictionary<string, KeywordSearchHit> _treeHits = new(StringComparer.OrdinalIgnoreCase);
    /// <summary>Query whose content hits are in <see cref="_treeHits"/> ("" = name filter only).</summary>
    private string _treeHitsQuery = "";
    private DateTime _lastTreeFilterApply;
    /// <summary>Grid search text pre-filled from the tree search (cleared with it, unless the user edited it since).</summary>
    private string? _autoFilledGlobalText;

    public bool IsTreeFiltered => !string.IsNullOrWhiteSpace(TreeSearchText);

    private string TreeQuery => (TreeSearchText ?? "").Trim();

    partial void OnTreeSearchTextChanged(string value)
    {
        OnPropertyChanged(nameof(IsTreeFiltered));
        RestartTreeSearch();
    }

    /// <summary>Cancels a running content search, filters by file name at once and schedules the content search.</summary>
    private void RestartTreeSearch()
    {
        _treeSearchCts?.Cancel();
        _treeSearchCts = null;
        _treeSearchTimer ??= CreateTreeSearchTimer();
        _treeSearchTimer.Stop();
        IsTreeSearching = false;
        var q = TreeQuery;
        if (q.Length == 0)
        {
            ClearTreeFilter();
            return;
        }
        _preSearchExpanded ??= AllNodes().Where(n => !n.IsFile && n.IsExpanded).Select(n => n.FullPath).ToHashSet(StringComparer.OrdinalIgnoreCase);
        if (!string.Equals(q, _treeHitsQuery, StringComparison.OrdinalIgnoreCase)) _treeHits.Clear();
        _treeHitsQuery = "";
        ApplyTreeFilter();
        if (q.Length >= CaseTreeFilter.MinContentQueryLength) _treeSearchTimer.Start();
    }

    private DispatcherTimer CreateTreeSearchTimer()
    {
        var t = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(300) };
        t.Tick += (_, _) =>
        {
            t.Stop();
            _ = RunTreeContentSearchAsync();
        };
        return t;
    }

    private IEnumerable<FileNode> AllNodes() => Tree.SelectMany(t => t.SelfAndDescendants());

    /// <summary>Shows only files whose name contains the query or that have content hits, plus their folders (expanded).</summary>
    private void ApplyTreeFilter()
    {
        _lastTreeFilterApply = DateTime.UtcNow;
        var q = TreeQuery;
        if (q.Length == 0) return;
        var visible = CaseTreeFilter.Visible(Tree, n => n.Children,
            n => n.IsFile && (CaseTreeFilter.NameMatches(n.Name, q) || _treeHits.ContainsKey(n.FullPath)));
        int files = 0;
        foreach (var n in AllNodes())
        {
            bool vis = visible.Contains(n);
            n.IsFilteredOut = !vis;
            if (n.IsFile)
            {
                if (vis) files++;
                if (_treeHits.TryGetValue(n.FullPath, out var h))
                {
                    n.HitCount = h.HitCount;
                    n.HitText = CaseTreeFilter.FormatHitsBadge(h.HitCount, h.CountCapped, System.Globalization.CultureInfo.CurrentCulture);
                    n.HitTip = CaseTreeFilter.FormatHits(h.HitCount, h.CountCapped, System.Globalization.CultureInfo.CurrentCulture) + " (lines containing the search text)";
                }
                else
                {
                    n.HitCount = 0;
                    n.HitText = "";
                }
            }
            else if (vis && !n.IsExpanded) n.IsExpanded = true;
        }
        FileNode.LinkVisible(Tree);
        if (!IsTreeSearching)
            TreeSearchStatus = files == 0 ? "No files match." : files == 1 ? "1 file matches." : $"{files:N0} files match.";
    }

    private async Task RunTreeContentSearchAsync()
    {
        var q = TreeQuery;
        if (q.Length < CaseTreeFilter.MinContentQueryLength) return;
        _treeSearchCts?.Cancel();
        var cts = _treeSearchCts = new CancellationTokenSource();
        var files = AllNodes().Where(n => n.IsFile).Select(n => n.FullPath).ToList();
        if (files.Count == 0) return;
        IsTreeSearching = true;
        TreeSearchProgress = 0;
        TreeSearchStatus = $"Searching {files.Count:N0} files…";
        _treeHits.Clear();
        var progress = new Progress<KeywordSearchProgress>(p =>
        {
            if (cts.IsCancellationRequested || _treeSearchCts != cts) return; // late report after completion/cancel
            TreeSearchProgress = p.FilesTotal == 0 ? 1 : (double)p.FilesDone / p.FilesTotal;
            TreeSearchStatus = $"Searching… {p.FilesDone:N0} of {p.FilesTotal:N0} files";
        });
        try
        {
            await foreach (var hit in CsvKeywordSearch.SearchAsync(files, q, progress, cts.Token))
            {
                if (cts.IsCancellationRequested) break;
                _treeHits[hit.Path] = hit;
                if (DateTime.UtcNow - _lastTreeFilterApply > TimeSpan.FromMilliseconds(250)) ApplyTreeFilter();
            }
        }
        catch (OperationCanceledException) { }
        catch (Exception ex) { AppLog.Error("Tree content search failed", ex); }
        finally
        {
            if (_treeSearchCts == cts)
            {
                _treeSearchCts = null;
                IsTreeSearching = false;
                if (!cts.IsCancellationRequested)
                {
                    _treeHitsQuery = q;
                    ApplyTreeFilter();
                }
            }
        }
    }

    /// <summary>Esc / empty box: full tree, the expansion from before the search, the open file selected.</summary>
    private void ClearTreeFilter()
    {
        _treeHits.Clear();
        _treeHitsQuery = "";
        TreeSearchStatus = "";
        TreeSearchProgress = 0;
        if (_autoFilledGlobalText is { } auto)
        {
            _autoFilledGlobalText = null;
            if (string.Equals(GlobalText, auto, StringComparison.Ordinal)) GlobalText = "";
        }
        var expanded = _preSearchExpanded;
        _preSearchExpanded = null;
        foreach (var n in AllNodes())
        {
            n.IsFilteredOut = false;
            n.HitCount = 0;
            n.HitText = "";
            if (expanded != null && !n.IsFile) n.IsExpanded = expanded.Contains(n.FullPath);
        }
        FileNode.Link(Tree, null, 0);
        if (CurrentFile != null) RevealFile(CurrentFile, expand: true);
    }

    /// <summary>Content-search keyword to pre-fill the grid's search when <paramref name="node"/> is opened from a filtered tree (else null).</summary>
    private string? TreeSearchKeywordFor(FileNode node)
    {
        var q = TreeQuery;
        if (q.Length < CaseTreeFilter.MinContentQueryLength || !node.IsFile || node.HitCount <= 0) return null;
        // the grid search has a syntax (AND/OR/NOT, phrases): keep the tree query a literal phrase
        return q.IndexOfAny(new[] { ' ', '"', '(', ')' }) >= 0 || q is "OR" or "AND" or "NOT" ? "\"" + q.Replace("\"", "") + "\"" : q;
    }

    // ------------------------------------------------------------ multi-selection

    private FileNode? _selectionAnchor;
    /// <summary>Number of tree items in the multi-selection (the selection bar shows from 2).</summary>
    [ObservableProperty] private int _multiSelectedCount;
    [ObservableProperty] private string _selectionText = "";

    /// <summary>Selected tree items in visible (tree) order; the TreeView's selected item when nothing is multi-selected.</summary>
    public IReadOnlyList<FileNode> TreeSelection
    {
        get
        {
            var list = AllNodes().Where(n => n.IsMultiSelected && !n.IsFilteredOut && !n.IsHiddenByCollapse).ToList();
            if (list.Count == 0 && SelectedNode != null) list.Add(SelectedNode);
            return list;
        }
    }

    /// <summary>Selected files only (folders in the selection are ignored).</summary>
    public IReadOnlyList<FileNode> SelectedFiles => TreeSelection.Where(n => n.IsFile).ToList();

    /// <summary>Visible rows of the tree in display order (expanded folders, not filtered out).</summary>
    public List<FileNode> VisibleTreeOrder()
    {
        var list = new List<FileNode>();
        void Walk(IEnumerable<FileNode> nodes)
        {
            foreach (var n in nodes)
            {
                if (n.IsFilteredOut) continue;
                list.Add(n);
                if (!n.IsFile && n.IsExpanded) Walk(n.Children);
            }
        }
        Walk(Tree);
        return list;
    }

    private void ResetMultiSelection()
    {
        foreach (var n in AllNodes()) if (n.IsMultiSelected && !n.IsSelected) n.IsMultiSelected = false;
        var sel = AllNodes().FirstOrDefault(n => n.IsSelected);
        if (sel != null) sel.IsMultiSelected = true;
        _selectionAnchor = sel;
        UpdateSelectionInfo();
    }

    public void SelectSingle(object? item)
    {
        var node = item as FileNode;
        foreach (var n in AllNodes()) if (n != node && n.IsMultiSelected) n.IsMultiSelected = false;
        if (node != null) node.IsMultiSelected = true;
        _selectionAnchor = node;
        UpdateSelectionInfo();
    }

    public void ToggleSelection(object item)
    {
        if (item is not FileNode node) return;
        if (!AllNodes().Any(n => n.IsMultiSelected) && SelectedNode != null) SelectedNode.IsMultiSelected = true;
        node.IsMultiSelected = !node.IsMultiSelected;
        _selectionAnchor = node;
        UpdateSelectionInfo();
    }

    public void SelectRange(object item, bool additive)
    {
        if (item is not FileNode node) return;
        var order = VisibleTreeOrder();
        var anchor = _selectionAnchor != null && order.Contains(_selectionAnchor) ? _selectionAnchor : SelectedNode;
        var range = CaseTreeFilter.Range(order, anchor, node).ToHashSet();
        foreach (var n in AllNodes())
        {
            if (range.Contains(n)) n.IsMultiSelected = true;
            else if (!additive && n.IsMultiSelected) n.IsMultiSelected = false;
        }
        _selectionAnchor ??= node;
        UpdateSelectionInfo();
    }

    public void SelectAllVisible()
    {
        var order = VisibleTreeOrder().ToHashSet();
        foreach (var n in AllNodes()) n.IsMultiSelected = order.Contains(n);
        UpdateSelectionInfo();
    }

    public bool IsInSelection(object item) => item is FileNode { IsMultiSelected: true } && MultiSelectedCount > 1;

    /// <summary>A tree folder was collapsed (its descendants left the multi-selection): refresh the selection bar.</summary>
    public void OnTreeFolderCollapsed() => UpdateSelectionInfo();

    private void UpdateSelectionInfo()
    {
        var sel = AllNodes().Where(n => n.IsMultiSelected).ToList();
        MultiSelectedCount = sel.Count;
        int files = sel.Count(n => n.IsFile), folders = sel.Count - files;
        SelectionText = sel.Count < 2 ? "" :
            folders == 0 ? $"{files:N0} files selected" :
            files == 0 ? $"{folders:N0} folders selected" : $"{files:N0} files, {folders:N0} folders selected";
        UpdateOpenHighlight();
    }

    /// <summary>Marks the tree node of the file in the events pane, so the open file always shows selected (except during a multi-selection).</summary>
    private void UpdateOpenHighlight()
    {
        bool show = MultiSelectedCount < 2;
        foreach (var n in AllNodes())
        {
            bool on = show && n.IsFile && string.Equals(n.FullPath, CurrentFile, StringComparison.OrdinalIgnoreCase);
            if (n.IsOpenHighlighted != on) n.IsOpenHighlighted = on;
        }
    }

    /// <summary>A plain click on a folder only expands/collapses it; the selection stays on the open file.</summary>
    public bool SelectsOnClick(object item) => item is FileNode { IsFile: true };
}
