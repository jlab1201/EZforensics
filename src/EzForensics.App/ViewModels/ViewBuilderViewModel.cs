using System.Collections.ObjectModel;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using EzForensics.App.Services;
using EzForensics.Core.Results;

namespace EzForensics.App.ViewModels;

/// <summary>One saved view row on the builder's "Saved views" tab.</summary>
public sealed class SavedViewItemViewModel
{
    public SavedViewItemViewModel(SavedView view, bool canLoad)
    {
        View = view;
        CanLoad = canLoad;
        Summary = view.Summary();
    }

    public SavedView View { get; }
    public string Name => View.Name;
    public string CategorySignature => View.CategorySignature;
    public string Summary { get; }
    public bool CanLoad { get; }
    public string EditTooltip => CanLoad ? "Edit in builder" : "Open a CSV with this column layout to load it";
    public string RowTooltip => CanLoad ? "Double-click to load into the builder" : "Open a CSV with this column layout to load it";
}

/// <summary>One category (column layout) with its saved views.</summary>
public sealed partial class SavedViewCategoryViewModel : ObservableObject
{
    public SavedViewCategoryViewModel(SavedFilterCategory category, IEnumerable<SavedViewItemViewModel> items, bool isCurrent)
    {
        Signature = category.Signature;
        Name = category.Name;
        IsCurrent = isCurrent;
        IsExpanded = isCurrent;
        foreach (var i in items) Items.Add(i);
    }

    public string Signature { get; }
    public string Name { get; }
    public bool IsCurrent { get; }
    public ObservableCollection<SavedViewItemViewModel> Items { get; } = new();
    public int Count => Items.Count;
    public string Header => $"{Name} ({Count})";
    [ObservableProperty] private bool _isExpanded;
}

/// <summary>A column row of the view builder (checked = visible).</summary>
public sealed partial class ViewColumnItem : ObservableObject
{
    public ViewColumnItem(string name, bool visible, double? width)
    {
        Name = name;
        _isVisible = visible;
        Width = width;
    }

    public string Name { get; }
    public double? Width { get; }
    [ObservableProperty] private bool _isVisible;
}

/// <summary>A group column row of the view builder.</summary>
public sealed partial class ViewGroupItem : ObservableObject
{
    public ViewGroupItem(string column, PivotSort sort)
    {
        Column = column;
        _sort = sort;
    }

    public string Column { get; }

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(SortGlyph))]
    private PivotSort _sort;

    public string SortGlyph => Sort switch
    {
        PivotSort.ValueDesc => "Z→A",
        PivotSort.CountDesc => "#↓",
        PivotSort.CountAsc => "#↑",
        _ => "A→Z",
    };
}

/// <summary>
/// View builder: edits a draft of the open file's column layout (visible columns, order, optionally Pivot grouping) and manages saved views.
/// OK applies <see cref="Build"/> to the grid; Save / Save as write the draft to the library.
/// </summary>
public sealed partial class ViewBuilderViewModel : ObservableObject, IDisposable
{
    private IReadOnlyList<string> _headers;
    private readonly List<string> _collapsed;
    private bool _groupsTouched;
    private readonly System.Windows.Threading.DispatcherTimer _retargetTimer;

    public ViewBuilderViewModel(ResultsViewModel vm)
    {
        Results = vm;
        _headers = vm.Headers.ToList();
        _categorySignature = vm.CurrentSignature;
        _categoryName = vm.CurrentCategoryName;
        _savedName = vm.ActiveSavedView;
        var cur = vm.CaptureView(includeGrouping: true);
        _collapsed = cur.CollapsedGroups?.ToList() ?? new();
        LoadDraft(cur, replaceGroups: true);
        RefreshSaved();
        SavedViews.Instance.Changed += RefreshSaved;
        _hasCsv = IsCsvReady;
        vm.PropertyChanged += OnResultsChanged;
        _retargetTimer = new System.Windows.Threading.DispatcherTimer { Interval = TimeSpan.FromMilliseconds(200) };
        _retargetTimer.Tick += (_, _) => { _retargetTimer.Stop(); Retarget(); };
    }

    public void Dispose()
    {
        SavedViews.Instance.Changed -= RefreshSaved;
        _retargetTimer.Stop();
        Results.PropertyChanged -= OnResultsChanged;
    }

    /// <summary>The Analyze results the builder applies to.</summary>
    public ResultsViewModel Results { get; }

    [ObservableProperty][NotifyPropertyChangedFor(nameof(CanSave))] private string _categorySignature;
    [ObservableProperty] private string _categoryName;

    // ------------------------------------------------------------ apply / re-target (modeless window)

    private bool IsCsvReady => Results.Headers.Count > 0 && Results.Rows != null && Results.GridBridge != null;

    /// <summary>A CSV is open and its grid is ready (Apply / OK need it).</summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(CanApply), nameof(CanOk), nameof(ApplyTip), nameof(OkTip))]
    private bool _hasCsv;

    public bool CanApply => HasCsv && Columns.Any(c => c.IsVisible);
    /// <summary>OK / Close: Close (Saved tab) always works; OK needs a CSV and one visible column.</summary>
    public bool CanOk => CanApply || SelectedTabIndex == 1;
    public string ApplyTip => !HasCsv ? "Open a CSV first" : (CanApply ? "Applies the layout to the table (window stays open)" : "Show at least one column");
    public string OkTip => !HasCsv ? "Open a CSV first" : (CanApply ? "Applies the layout to the table and closes" : "Show at least one column");

    partial void OnSelectedTabIndexChanged(int value) => OnPropertyChanged(nameof(CanOk));

    /// <summary>Applies the draft to the open file's grid. False when nothing was applied.</summary>
    public bool Apply()
    {
        if (!CanApply) return false;
        try
        {
            var view = Build();
            // widths the user changed in the grid since the draft was taken win over the draft's
            var live = Results.GridBridge?.CaptureColumns().ToDictionary(c => c.Name, c => c.Width, StringComparer.OrdinalIgnoreCase);
            if (live != null) foreach (var c in view.Columns) if (live.TryGetValue(c.Name, out var w)) c.Width = w;
            Results.ApplyViewCore(view, SavedName);
            return true;
        }
        catch (Exception ex)
        {
            Results.ShowError("Could not apply the view", ex);
            return false;
        }
    }

    private void OnResultsChanged(object? sender, System.ComponentModel.PropertyChangedEventArgs e)
    {
        if (e.PropertyName is nameof(ResultsViewModel.Headers) or nameof(ResultsViewModel.Rows) or nameof(ResultsViewModel.CurrentFile))
        {
            HasCsv = IsCsvReady;
            _retargetTimer.Stop();
            _retargetTimer.Start();
        }
    }

    /// <summary>The open file changed: when its column layout differs, take its columns, grouping and category as the new draft.</summary>
    public void Retarget()
    {
        HasCsv = IsCsvReady;
        if (!HasCsv) return;
        var headers = Results.Headers.ToList();
        var sig = Results.CurrentSignature;
        var sameLayout = string.Equals(sig, CategorySignature, StringComparison.OrdinalIgnoreCase) && _headers.SequenceEqual(headers, StringComparer.Ordinal);
        _headers = headers;
        CategorySignature = sig;
        CategoryName = Results.CurrentCategoryName;
        if (!sameLayout)
        {
            SavedName = Results.ActiveSavedView;
            var cur = Results.CaptureView(includeGrouping: true);
            _collapsed.Clear();
            if (cur.CollapsedGroups != null) _collapsed.AddRange(cur.CollapsedGroups);
            _groupsTouched = false;
            Groups.Clear();
            LoadDraft(cur, replaceGroups: true);
            Notice = "";
        }
        RefreshSaved();
    }

    public ObservableCollection<ViewColumnItem> Columns { get; } = new();
    public ObservableCollection<ViewGroupItem> Groups { get; } = new();
    /// <summary>Columns not yet used as a group column (the "add group" combo).</summary>
    public ObservableCollection<string> GroupChoices { get; } = new();

    [ObservableProperty] private ViewColumnItem? _selectedColumn;
    [ObservableProperty] private string? _selectedGroupChoice;
    [ObservableProperty][NotifyPropertyChangedFor(nameof(HasSavedName))] private string? _savedName;
    [ObservableProperty] private string _notice = "";
    /// <summary>0 = Builder, 1 = Saved views.</summary>
    [ObservableProperty] private int _selectedTabIndex;
    [ObservableProperty] private string _summary = "";

    public bool HasSavedName => SavedName != null;
    public bool CanSave => CategorySignature.Length > 0 && Columns.Any(c => c.IsVisible);

    public ObservableCollection<SavedViewCategoryViewModel> SavedGroups { get; } = new();
    public bool HasSavedGroups => SavedGroups.Count > 0;

    // ------------------------------------------------------------ draft

    private void LoadDraft(SavedView v, bool replaceGroups)
    {
        foreach (var c in Columns) c.PropertyChanged -= OnColumnChanged;
        Columns.Clear();
        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var c in v.Columns)
        {
            var h = _headers.FirstOrDefault(x => string.Equals(x, c.Name, StringComparison.OrdinalIgnoreCase));
            if (h != null && seen.Add(h)) Columns.Add(new ViewColumnItem(h, c.Visible, c.Width));
        }
        // columns the view does not mention keep their place at the end, visible
        foreach (var h in _headers) if (seen.Add(h)) Columns.Add(new ViewColumnItem(h, true, null));
        foreach (var c in Columns) c.PropertyChanged += OnColumnChanged;
        if (replaceGroups && v.Groups != null)
        {
            Groups.Clear();
            foreach (var g in v.Groups)
            {
                var h = _headers.FirstOrDefault(x => string.Equals(x, g.Column, StringComparison.OrdinalIgnoreCase));
                if (h != null && Groups.All(x => x.Column != h)) Groups.Add(new ViewGroupItem(h, g.Sort));
            }
            _collapsed.Clear();
            if (v.CollapsedGroups != null && Groups.Count > 0) _collapsed.AddRange(v.CollapsedGroups);
        }
        RefreshGroupChoices();
        UpdateSummary();
    }

    private void OnColumnChanged(object? s, System.ComponentModel.PropertyChangedEventArgs e) => UpdateSummary();

    private void UpdateSummary()
    {
        Summary = Build().Summary();
        OnPropertyChanged(nameof(CanSave));
        OnPropertyChanged(nameof(CanApply));
        OnPropertyChanged(nameof(CanOk));
        OnPropertyChanged(nameof(ApplyTip));
        OnPropertyChanged(nameof(OkTip));
    }

    private void RefreshGroupChoices()
    {
        GroupChoices.Clear();
        foreach (var h in _headers) if (Groups.All(g => g.Column != h)) GroupChoices.Add(h);
        SelectedGroupChoice = GroupChoices.FirstOrDefault();
    }

    /// <summary>The draft as an unnamed view (always carries the Pivot grouping the builder shows, possibly empty).</summary>
    public SavedView Build() => new()
    {
        CategorySignature = CategorySignature,
        Name = SavedName ?? "",
        SavedUtc = DateTime.UtcNow,
        Columns = Columns.Select(c => new SavedViewColumn { Name = c.Name, Visible = c.IsVisible, Width = c.Width }).ToList(),
        Groups = Groups.Select(g => new PivotGroupSpec { Column = g.Column, Sort = g.Sort }).ToList(),
        CollapsedGroups = _groupsTouched ? new List<string>() : _collapsed.ToList(),
    };

    [RelayCommand]
    private void MoveUp() => Move(-1);

    [RelayCommand]
    private void MoveDown() => Move(1);

    private void Move(int delta)
    {
        var sel = SelectedColumn;
        if (sel == null) return;
        int i = Columns.IndexOf(sel), j = i + delta;
        if (i < 0 || j < 0 || j >= Columns.Count) return;
        Columns.Move(i, j);
        UpdateSummary();
    }

    [RelayCommand]
    private void ShowAll()
    {
        foreach (var c in Columns) c.IsVisible = true;
    }

    /// <summary>Hides every column except the selected one (or the first): the grid keeps at least one.</summary>
    [RelayCommand]
    private void HideAll()
    {
        var keep = SelectedColumn ?? Columns.FirstOrDefault();
        foreach (var c in Columns) c.IsVisible = ReferenceEquals(c, keep);
    }

    [RelayCommand]
    private void AddGroup()
    {
        var col = SelectedGroupChoice;
        if (string.IsNullOrEmpty(col) || Groups.Any(g => g.Column == col)) return;
        Groups.Add(new ViewGroupItem(col, PivotSort.ValueAsc));
        _groupsTouched = true;
        RefreshGroupChoices();
        UpdateSummary();
    }

    [RelayCommand]
    private void RemoveGroup(ViewGroupItem? g)
    {
        if (g == null || !Groups.Remove(g)) return;
        _groupsTouched = true;
        RefreshGroupChoices();
        UpdateSummary();
    }

    [RelayCommand]
    private void CycleGroupSort(ViewGroupItem? g)
    {
        if (g == null) return;
        g.Sort = PivotGroupChip.Next(g.Sort);
        _groupsTouched = true;
        UpdateSummary();
    }

    // ------------------------------------------------------------ saved views

    private void RefreshSaved()
    {
        SavedGroups.Clear();
        var snap = SavedViews.Instance.Snapshot()
            .Where(x => x.Views.Count > 0 || string.Equals(x.Category.Signature, CategorySignature, StringComparison.OrdinalIgnoreCase))
            .OrderByDescending(x => string.Equals(x.Category.Signature, CategorySignature, StringComparison.OrdinalIgnoreCase))
            .ThenBy(x => x.Category.Name, StringComparer.OrdinalIgnoreCase);
        foreach (var (cat, views) in snap)
        {
            bool cur = string.Equals(cat.Signature, CategorySignature, StringComparison.OrdinalIgnoreCase);
            SavedGroups.Add(new SavedViewCategoryViewModel(cat, views.Select(v => new SavedViewItemViewModel(v, cur)), cur));
        }
        OnPropertyChanged(nameof(HasSavedGroups));
    }

    /// <summary>Puts a saved view into the builder and switches to the Builder tab (a view without grouping leaves the grouping as is).</summary>
    public void LoadSaved(SavedViewItemViewModel item)
    {
        if (!item.CanLoad) return;
        if (item.View.Groups != null) _groupsTouched = false;
        LoadDraft(item.View, replaceGroups: true);
        SavedName = item.Name;
        SelectedTabIndex = 0;
        Notice = "";
    }

    public bool SavedExists(string name) => CategorySignature.Length > 0 && SavedViews.Instance.Find(CategorySignature, name) != null;

    public string SaveTargetName => SavedName != null && SavedExists(SavedName) ? SavedName : SavedViews.Instance.NextNumberedName(CategorySignature);

    /// <summary>Saves the draft in the current category (grouping included when any group column is set); the dialog stays open.</summary>
    public void Save(string? name = null)
    {
        if (!CanSave) return;
        name = string.IsNullOrWhiteSpace(name) ? SaveTargetName : name.Trim();
        var view = Build();
        view.Name = name;
        if (view.Groups is { Count: 0 }) { view.Groups = null; view.CollapsedGroups = null; }
        var sig = CategorySignature;
        var catName = string.IsNullOrWhiteSpace(CategoryName) ? "Custom layout" : CategoryName;
        var cols = _headers.ToList();
        SavedViews.Instance.Mutate(l =>
        {
            l.EnsureCategory(sig, catName, cols);
            l.Put(view);
        });
        SavedName = name;
    }

    public bool Rename(SavedViewItemViewModel item, string newName)
    {
        var ok = SavedViews.Instance.Mutate(l => l.Rename(item.CategorySignature, item.Name, newName));
        if (ok && IsLoaded(item)) SavedName = newName.Trim();
        return ok;
    }

    private bool IsLoaded(SavedViewItemViewModel item) =>
        string.Equals(SavedName, item.Name, StringComparison.OrdinalIgnoreCase) &&
        string.Equals(item.CategorySignature, CategorySignature, StringComparison.OrdinalIgnoreCase);

    public void Delete(SavedViewItemViewModel item)
    {
        SavedViews.Instance.Mutate(l => l.Remove(item.CategorySignature, item.Name));
        if (IsLoaded(item)) SavedName = null;
    }

    public void RenameCategory(SavedViewCategoryViewModel cat, string newName) => SavedViews.Instance.Mutate(l => l.RenameCategory(cat.Signature, newName));

    public void DeleteCategory(SavedViewCategoryViewModel cat)
    {
        SavedViews.Instance.Mutate(l => l.RemoveCategory(cat.Signature));
        if (cat.IsCurrent) SavedName = null;
    }
}
