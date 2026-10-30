using CommunityToolkit.Mvvm.ComponentModel;
using EzForensics.App.Services;
using EzForensics.Core.Results;
using EzForensics.Core.Settings;

namespace EzForensics.App.ViewModels;

/// <summary>The grid's column state (lives in the Analyze events pane); the view model reads and sets it through this.</summary>
public interface IGridColumnBridge
{
    /// <summary>All data columns in display order (name, visible, width when not the default).</summary>
    List<SavedViewColumn> CaptureColumns();

    /// <summary>
    /// Applies the columns (names are exact header names): the listed ones are placed first in the given order with the given visibility and
    /// width (null = default); columns not listed keep their state and follow in their current order.
    /// </summary>
    void ApplyColumns(IReadOnlyList<SavedViewColumn> columns);
}

public sealed partial class ResultsViewModel
{
    // ------------------------------------------------------------ saved views (column layouts)

    /// <summary>Set by the events pane while it is attached.</summary>
    public IGridColumnBridge? GridBridge { get; set; }

    /// <summary>Name of the saved view last applied / saved for this file (null when none).</summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(SaveViewTargetName), nameof(SaveViewTargetExists))]
    private string? _activeSavedView;

    public IReadOnlyList<SavedView> SavedViewsForCurrent =>
        Headers.Count == 0 ? Array.Empty<SavedView>() : SavedViews.Instance.ForSignature(CurrentSignature);

    public bool CanSaveView => HasFile && Rows != null && GridBridge != null && !string.IsNullOrWhiteSpace(CurrentSignature);

    public bool SaveViewTargetExists => ActiveSavedView != null && Headers.Count > 0 && SavedViews.Instance.Find(CurrentSignature, ActiveSavedView) != null;

    /// <summary>Name "Save view" would write: the active view when it still exists, else the next "View N".</summary>
    public string SaveViewTargetName =>
        SaveViewTargetExists ? ActiveSavedView! : (Headers.Count > 0 ? SavedViews.Instance.NextNumberedName(CurrentSignature) : "View 1");

    /// <summary>The current column layout (and, with <paramref name="includeGrouping"/>, the Pivot grouping) as an unnamed view.</summary>
    public SavedView CaptureView(bool includeGrouping)
    {
        var v = new SavedView { CategorySignature = CurrentSignature, SavedUtc = DateTime.UtcNow };
        if (GridBridge != null) v.Columns.AddRange(GridBridge.CaptureColumns());
        else v.Columns.AddRange(Headers.Select(h => new SavedViewColumn { Name = h }));
        if (includeGrouping)
        {
            v.Groups = GroupColumns.Select(c => new PivotGroupSpec { Column = c.Column, Sort = c.Sort }).ToList();
        }
        return v;
    }

    /// <summary>Saves under <see cref="SaveViewTargetName"/> without asking; grouping is included when it is active.</summary>
    public void SaveView()
    {
        if (!CanSaveView) return;
        WriteView(SaveViewTargetName, CurrentCategoryName, CaptureView(IsGrouped));
    }

    public void SaveViewAs(string name, bool includeGrouping)
    {
        if (!CanSaveView || string.IsNullOrWhiteSpace(name)) return;
        WriteView(name.Trim(), CurrentCategoryName, CaptureView(includeGrouping && IsGrouped));
    }

    private void WriteView(string name, string categoryName, SavedView view)
    {
        var sig = CurrentSignature;
        var headers = Headers.ToList();
        view.Name = name;
        view.CategorySignature = sig;
        SavedViews.Instance.Mutate(l =>
        {
            var cat = l.EnsureCategory(sig, categoryName, headers);
            if (!string.Equals(cat.Name, categoryName, StringComparison.Ordinal)) l.RenameCategory(sig, categoryName);
            l.Put(view);
        });
        ActiveSavedView = name;
        MarkSessionDirty();
        ShowMessage($"Saved view '{name}' to {categoryName}", InfoSeverity.Success);
    }

    /// <summary>Applies a saved view to the grid: order, visibility and widths, then the grouping when the view has one.</summary>
    public void ApplySavedView(SavedView v) => ApplyViewCore(v, v.Name);

    /// <summary>Applies an unsaved layout (view builder OK); <paramref name="activeName"/> is the saved view it came from, if any.</summary>
    public void ApplyViewCore(SavedView v, string? activeName)
    {
        if (GridBridge == null || Headers.Count == 0) return;
        var cols = new List<SavedViewColumn>();
        var missing = new List<string>();
        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var c in v.Columns)
        {
            var h = Headers.FirstOrDefault(x => string.Equals(x, c.Name, StringComparison.OrdinalIgnoreCase));
            if (h == null) { missing.Add(c.Name); continue; }
            if (!seen.Add(h)) continue;
            cols.Add(new SavedViewColumn { Name = h, Visible = c.Visible, Width = c.Width });
        }
        RememberViewCollapse();
        // a view without a pivot defines "no grouping": ungroup (columns return to the grid) before the view's own visibility is applied
        if (v.Groups == null || v.Groups.Count == 0) ClearGroupingCommand.Execute(null);
        GridBridge.ApplyColumns(cols);
        _activeViewAppliedGrouping = v.Groups != null;
        if (v.Groups != null)
        {
            GroupColumns.Clear();
            _collapsedGroups.Clear();
            foreach (var g in v.Groups)
            {
                var h = Headers.FirstOrDefault(x => string.Equals(x, g.Column, StringComparison.OrdinalIgnoreCase));
                if (h == null) { if (!missing.Contains(g.Column, StringComparer.OrdinalIgnoreCase)) missing.Add(g.Column); continue; }
                if (!IsGroupColumn(h)) GroupColumns.Add(new PivotGroupChip(h, g.Sort));
            }
            // fully collapsed by default; returning to the view in this session restores the groups the analyst opened
            if (GroupColumns.Count > 0 && ViewCollapseKey(activeName) is { } key && s_viewCollapse.TryGetValue(key, out var remembered))
            {
                _collapsedGroups.Clear();
                _collapsedGroups.UnionWith(remembered);
                _collapseAllPending = false;
            }
            else SeedCollapsed(null);
            OnGroupingChanged(structural: false);
        }
        ActiveSavedView = activeName;
        MarkSessionDirty();
        if (missing.Count > 0)
            ShowMessage($"View '{(string.IsNullOrEmpty(v.Name) ? activeName ?? "layout" : v.Name)}': columns not in this CSV were skipped: {string.Join(", ", missing)}", InfoSeverity.Warning);
    }

    private bool _activeViewAppliedGrouping;

    /// <summary>
    /// Collapse state per file + view + group columns, for this app session only (never saved to the view or exported), so going back to a
    /// view shows the groups as the analyst left them.
    /// </summary>
    private static readonly Dictionary<string, HashSet<string>> s_viewCollapse = new(StringComparer.OrdinalIgnoreCase);

    private string? ViewCollapseKey(string? viewName) =>
        string.IsNullOrEmpty(viewName) || string.IsNullOrEmpty(CurrentFile) || GroupColumns.Count == 0
            ? null
            : CurrentFile + "|" + viewName + "|" + string.Join("|", GroupColumns.Select(c => c.Column));

    /// <summary>Records the current collapse state for the active view (no-op when no view is active or nothing is grouped yet).</summary>
    private void RememberViewCollapse()
    {
        if (_collapseAllPending || _pivotRoots == null || ViewCollapseKey(ActiveSavedView) is not { } key) return;
        s_viewCollapse[key] = new HashSet<string>(_collapsedGroups, StringComparer.Ordinal);
    }

    /// <summary>True when a saved view is active or the columns differ from the default layout (hidden or reordered).</summary>
    public bool CanClearView
    {
        get
        {
            if (!CanSaveView) return false;
            if (ActiveSavedView != null) return true;
            var cols = GridBridge!.CaptureColumns();
            if (cols.Any(c => !c.Visible)) return true;
            return cols.Count == Headers.Count && !cols.Select(c => c.Name).SequenceEqual(Headers, StringComparer.Ordinal);
        }
    }

    /// <summary>Forgets the active view and restores the default layout (all columns, file order, default widths). Filters stay; grouping is removed only if the view applied it.</summary>
    public void ClearView()
    {
        if (GridBridge == null || Headers.Count == 0) return;
        GridBridge.ApplyColumns(Headers.Select(h => new SavedViewColumn { Name = h, Visible = true }).ToList());
        if (_activeViewAppliedGrouping && IsGrouped) ClearGroupingCommand.Execute(null);
        _activeViewAppliedGrouping = false;
        ActiveSavedView = null;
        MarkSessionDirty();
        ShowMessage("View cleared", InfoSeverity.Info);
    }

    /// <summary>Opens the file (when not already current) and applies the saved view.</summary>
    public async Task OpenAndApplySavedViewAsync(string path, SavedView v)
    {
        if (!string.Equals(path, CurrentFile, StringComparison.OrdinalIgnoreCase)) await OpenCsvAsync(path);
        if (!string.Equals(path, CurrentFile, StringComparison.OrdinalIgnoreCase) || Rows == null) return;
        // the pane builds its columns when the file opens; give it a chance to attach before applying
        await System.Windows.Threading.Dispatcher.Yield(System.Windows.Threading.DispatcherPriority.Loaded);
        ApplySavedView(v);
    }

    public void RefreshSavedViewState()
    {
        OnPropertyChanged(nameof(SavedViewsForCurrent));
        OnPropertyChanged(nameof(CanSaveView));
        OnPropertyChanged(nameof(SaveViewTargetName));
        OnPropertyChanged(nameof(SaveViewTargetExists));
    }
}
