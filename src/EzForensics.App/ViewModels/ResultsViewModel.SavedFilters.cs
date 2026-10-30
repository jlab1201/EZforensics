using CommunityToolkit.Mvvm.ComponentModel;
using EzForensics.App.Services;
using EzForensics.Core.Results;
using EzForensics.Core.Settings;

namespace EzForensics.App.ViewModels;

public sealed partial class ResultsViewModel
{
    // ------------------------------------------------------------ saved filters

    /// <summary>Name of the saved filter last applied / saved for this file (null when none).</summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(SaveTargetName))]
    private string? _activeSavedFilter;

    private volatile Tuple<string, string?>? _fileArtifactId;

    /// <summary>Called from the advisory pass: the provenance artifact id of the CSV (used to name the category).</summary>
    private void NoteArtifactId(string csvPath, string? artifactId) => _fileArtifactId = Tuple.Create(csvPath, artifactId);

    /// <summary>Category key of the open file's column layout (empty when no file is open).</summary>
    public string CurrentSignature => Headers.Count == 0 ? "" : ColumnLayoutStore.Signature(Headers);

    /// <summary>Library name of the file's category, else a suggested name.</summary>
    public string CurrentCategoryName
    {
        get
        {
            if (Headers.Count == 0) return "";
            var existing = SavedFilters.Instance.CategoryName(CurrentSignature);
            if (!string.IsNullOrWhiteSpace(existing)) return existing;
            var art = _fileArtifactId is { } t && string.Equals(t.Item1, CurrentFile, StringComparison.OrdinalIgnoreCase) ? t.Item2 : null;
            try { return ArtifactCategoryNamer.Name(Headers, CurrentFile, art); }
            catch (Exception ex) { AppLog.Warn("Category name: " + ex.Message); return "Custom layout"; }
        }
    }

    /// <summary>Saved filters of the open file's category.</summary>
    public IReadOnlyList<SavedFilter> SavedFiltersForCurrent =>
        Headers.Count == 0 ? Array.Empty<SavedFilter>() : SavedFilters.Instance.ForSignature(CurrentSignature);

    public bool CanSaveFilter => HasFile && !IsFilterEmpty() && !string.IsNullOrWhiteSpace(CurrentSignature);

    /// <summary>Name "Save filter" would write: the active saved filter when it still exists in this category, else the next "Filter N".</summary>
    public string SaveTargetName =>
        ActiveSavedFilter != null && Headers.Count > 0 && SavedFilters.Instance.Find(CurrentSignature, ActiveSavedFilter) != null
            ? ActiveSavedFilter
            : (Headers.Count > 0 ? SavedFilters.Instance.NextNumberedName(CurrentSignature) : "Filter 1");

    public bool SaveTargetExists => ActiveSavedFilter != null && Headers.Count > 0 && SavedFilters.Instance.Find(CurrentSignature, ActiveSavedFilter) != null;

    /// <summary>Saves the current filters under <see cref="SaveTargetName"/> without asking.</summary>
    public void SaveFilter()
    {
        if (!CanSaveFilter) return;
        WriteFilter(SaveTargetName, CurrentCategoryName);
    }

    /// <summary>Saves under <paramref name="name"/> (overwriting) in the current category, renaming the category when <paramref name="categoryName"/> differs.</summary>
    public void SaveFilterAs(string name, string? categoryName = null)
    {
        if (!CanSaveFilter || string.IsNullOrWhiteSpace(name)) return;
        WriteFilter(name.Trim(), string.IsNullOrWhiteSpace(categoryName) ? CurrentCategoryName : categoryName.Trim());
    }

    private void WriteFilter(string name, string categoryName)
    {
        var sig = CurrentSignature;
        var headers = Headers.ToList();
        var node = AnalyzeFilterNode.From(BuildExpression());
        var text = string.IsNullOrWhiteSpace(GlobalText) ? "" : GlobalText.Trim();
        SavedFilters.Instance.Mutate(l =>
        {
            var cat = l.EnsureCategory(sig, categoryName, headers);
            if (!string.Equals(cat.Name, categoryName, StringComparison.Ordinal)) l.RenameCategory(sig, categoryName);
            l.Put(new SavedFilter { Name = name, CategorySignature = sig, Expression = node, GlobalText = text, SavedUtc = DateTime.UtcNow });
        });
        ActiveSavedFilter = name;
        MarkSessionDirty();
        ShowMessage($"Saved '{name}' to {categoryName}", InfoSeverity.Success);
    }

    /// <summary>Loads a saved filter in one go (filter row, funnels, extra conditions and search text).</summary>
    public void ApplySavedFilter(SavedFilter f)
    {
        var node = f.Expression?.ToNode();
        var root = node as FilterGroup ?? new FilterGroup(FilterGroupKind.And, node != null ? new[] { node } : Array.Empty<FilterNode>());
        EditFilters(() =>
        {
            LoadExpression(root);
            GlobalText = f.GlobalText ?? "";
            ActiveSavedFilter = f.Name;
        });
        WarnMissingColumns(f.Name, node);
    }

    /// <summary>Filter builder OK: expression, search text and saved-filter name in one go.</summary>
    public void ApplyBuilderResult(FilterGroup root, string globalText, string? savedName)
    {
        EditFilters(() =>
        {
            LoadExpression(root);
            GlobalText = globalText ?? "";
            ActiveSavedFilter = savedName;
        });
        if (savedName != null) WarnMissingColumns(savedName, root);
    }

    /// <summary>Warning toast for columns a filter refers to that this CSV does not have.</summary>
    public void WarnMissingColumns(string filterName, FilterNode? node)
    {
        if (node == null) return;
        var missing = SavedFilterStore.Columns(node).Where(c => !Headers.Contains(c, StringComparer.OrdinalIgnoreCase)).ToList();
        if (missing.Count > 0)
            ShowMessage($"Filter '{filterName}': columns not in this CSV were ignored: {string.Join(", ", missing)}", InfoSeverity.Warning);
    }

    /// <summary>Opens the file (when not already current) and applies the saved filter.</summary>
    public async Task OpenAndApplySavedFilterAsync(string path, SavedFilter f)
    {
        if (!string.Equals(path, CurrentFile, StringComparison.OrdinalIgnoreCase)) await OpenCsvAsync(path);
        if (string.Equals(path, CurrentFile, StringComparison.OrdinalIgnoreCase) && Rows != null) ApplySavedFilter(f);
    }

    /// <summary>Re-raises the saved-filter dependent properties (file opened / closed, library changed).</summary>
    public void RefreshSavedFilterState()
    {
        OnPropertyChanged(nameof(CurrentSignature));
        OnPropertyChanged(nameof(CurrentCategoryName));
        OnPropertyChanged(nameof(SavedFiltersForCurrent));
        OnPropertyChanged(nameof(CanSaveFilter));
        OnPropertyChanged(nameof(SaveTargetName));
        OnPropertyChanged(nameof(SaveTargetExists));
    }
}
