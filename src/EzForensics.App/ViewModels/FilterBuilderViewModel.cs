using System.Collections.ObjectModel;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using EzForensics.App.Services;
using EzForensics.Core.Results;

namespace EzForensics.App.ViewModels;

public abstract partial class BuilderNodeViewModel : ObservableObject
{
    protected BuilderNodeViewModel(FilterBuilderViewModel root, BuilderGroupViewModel? parent)
    {
        Root = root;
        Parent = parent;
    }

    public FilterBuilderViewModel Root { get; }
    public BuilderGroupViewModel? Parent { get; set; }
    public bool CanRemove => Parent != null;
    /// <summary>Nesting depth (root group = 0); drives the group border tint.</summary>
    public int Depth => Parent == null ? 0 : Parent.Depth + 1;

    [RelayCommand]
    private void Remove()
    {
        Parent?.Children.Remove(this);
        Root.Changed();
    }

    public abstract FilterNode? Build();
}

public sealed partial class BuilderGroupViewModel : BuilderNodeViewModel
{
    [ObservableProperty] private bool _isOr;
    [ObservableProperty] private bool _negate;

    public BuilderGroupViewModel(FilterBuilderViewModel root, BuilderGroupViewModel? parent) : base(root, parent) { }

    public ObservableCollection<BuilderNodeViewModel> Children { get; } = new();

    public int JoinIndex
    {
        get => IsOr ? 1 : 0;
        set => IsOr = value == 1;
    }

    partial void OnIsOrChanged(bool value)
    {
        OnPropertyChanged(nameof(JoinIndex));
        Root.Changed();
    }
    partial void OnNegateChanged(bool value) => Root.Changed();

    [RelayCommand]
    private void AddCondition()
    {
        Children.Add(new BuilderConditionViewModel(Root, this) { Column = Root.Columns.FirstOrDefault() ?? "" });
        Root.Changed();
    }

    [RelayCommand]
    private void AddGroup()
    {
        var g = new BuilderGroupViewModel(Root, this) { IsOr = !IsOr };
        g.Children.Add(new BuilderConditionViewModel(Root, g) { Column = Root.Columns.FirstOrDefault() ?? "" });
        Children.Add(g);
        Root.Changed();
    }

    public override FilterNode? Build()
    {
        var g = new FilterGroup { Kind = IsOr ? FilterGroupKind.Or : FilterGroupKind.And, Negate = Negate };
        foreach (var c in Children) if (c.Build() is { } n) g.Children.Add(n);
        return g.Children.Count == 0 ? null : g;
    }
}

public sealed partial class BuilderConditionViewModel : BuilderNodeViewModel
{
    [ObservableProperty] private string _column = "";
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(NeedsValue), nameof(IsBetween))]
    private FilterOpInfo _op = FilterOps.All[0];
    [ObservableProperty] private string _value = "";
    [ObservableProperty] private string _value2 = "";

    public BuilderConditionViewModel(FilterBuilderViewModel root, BuilderGroupViewModel? parent) : base(root, parent) { }

    public bool NeedsValue => Op.NeedsValue;
    public bool IsBetween => Op.Op == ColumnFilterMode.Between;

    partial void OnColumnChanged(string value) => Root.Changed();
    partial void OnOpChanged(FilterOpInfo value) => Root.Changed();
    partial void OnValueChanged(string value) => Root.Changed();
    partial void OnValue2Changed(string value) => Root.Changed();

    public override FilterNode? Build()
    {
        var c = new FilterCondition(Column, Op.Op, Value, IsBetween ? Value2 : null);
        return c.IsEmpty ? null : c;
    }
}

/// <summary>A value set / date set from a header funnel: shown as text, can be kept or removed.</summary>
public sealed class BuilderFixedViewModel : BuilderNodeViewModel
{
    private readonly FilterNode _node;

    public BuilderFixedViewModel(FilterBuilderViewModel root, BuilderGroupViewModel? parent, FilterNode node) : base(root, parent) => _node = node;

    public string Text => _node.ToDisplayString();
    public override FilterNode? Build() => _node.Clone();
}

/// <summary>Filter builder dialog: a tree of And/Or (optionally Not) groups over any column and operator, plus the saved filters of every category.</summary>
public sealed partial class FilterBuilderViewModel : ObservableObject, IDisposable
{
    [ObservableProperty] private string _preview = "";
    [ObservableProperty] private BuilderGroupViewModel _rootGroup = null!;
    /// <summary>Search-box text stored with saved filters: starts as the top-bar text, replaced when a saved filter is loaded; OK applies it to the search box.</summary>
    [ObservableProperty] private string _globalText = "";
    /// <summary>Saved filter the builder content came from / was last saved as (null = none).</summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(HasSavedName))]
    private string? _savedName;

    public bool HasSavedName => SavedName != null;
    [ObservableProperty] private string _notice = "";
    /// <summary>The builder tree as filter syntax (what the editable box under the builder shows; the search-box text is not part of it).</summary>
    [ObservableProperty] private string _syntaxText = "";
    /// <summary>Why the typed syntax could not be parsed (null when it is fine).</summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(HasSyntaxError), nameof(SyntaxErrorText))]
    private string? _syntaxError;
    public bool HasSyntaxError => SyntaxError != null;
    public string SyntaxErrorText => SyntaxError == null ? "" : "Invalid syntax: " + SyntaxError;
    /// <summary>True while the user is typing in the syntax box; the box is then not overwritten by builder changes.</summary>
    public bool SyntaxEditing { get; set; }
    /// <summary>0 = Builder, 1 = Saved filters.</summary>
    [ObservableProperty] private int _selectedTabIndex;

    private readonly System.Windows.Threading.DispatcherTimer? _retargetTimer;
    private bool _retargeting;
    /// <summary>True once a saved filter was loaded since the last apply: only then OK / Apply overwrite the search box with <see cref="GlobalText"/>.</summary>
    private bool _globalTouched;

    public FilterBuilderViewModel(IReadOnlyList<string> columns, FilterGroup current, string globalText = "", string? savedName = null,
        string categorySignature = "", string categoryName = "", ResultsViewModel? results = null)
    {
        _headers = columns;
        Columns = new ObservableCollection<string>(new[] { FilterNode.AnyColumn }.Concat(columns));
        _categorySignature = categorySignature;
        _categoryName = categoryName;
        _globalText = globalText ?? "";
        _savedName = savedName;
        _rootGroup = FromNode(current, null) as BuilderGroupViewModel ?? new BuilderGroupViewModel(this, null);
        Results = results;
        Changed();
        RefreshSaved();
        SavedFilters.Instance.Changed += RefreshSaved;
        if (results != null)
        {
            _hasCsv = results.Headers.Count > 0;
            results.PropertyChanged += OnResultsChanged;
            _retargetTimer = new System.Windows.Threading.DispatcherTimer { Interval = TimeSpan.FromMilliseconds(150) };
            _retargetTimer.Tick += (_, _) => { _retargetTimer.Stop(); Retarget(); };
        }
        else _hasCsv = true;
    }

    public void Dispose()
    {
        SavedFilters.Instance.Changed -= RefreshSaved;
        _retargetTimer?.Stop();
        if (Results != null) Results.PropertyChanged -= OnResultsChanged;
    }

    /// <summary>The Analyze results the builder applies to (null in tests).</summary>
    public ResultsViewModel? Results { get; }

    /// <summary>The CSV's real column names.</summary>
    [ObservableProperty] private IReadOnlyList<string> _headers;
    /// <summary>Choices of the column box: "*" (shown as "Any field") followed by the real columns.</summary>
    public ObservableCollection<string> Columns { get; }
    public IReadOnlyList<FilterOpInfo> Ops => FilterOps.All;
    [ObservableProperty][NotifyPropertyChangedFor(nameof(CanSave))] private string _categorySignature;
    [ObservableProperty] private string _categoryName;

    // ------------------------------------------------------------ apply / re-target (modeless window)

    /// <summary>A CSV is open (Apply / OK need one).</summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(CanApply), nameof(CanOk), nameof(ApplyTip), nameof(OkTip))]
    private bool _hasCsv = true;

    public bool CanApply => HasCsv;
    /// <summary>OK / Close: Close (Saved tab) always works; OK needs a CSV.</summary>
    public bool CanOk => HasCsv || SelectedTabIndex == 1;
    public string ApplyTip => HasCsv ? "Applies the filter to the open file (window stays open)" : "Open a CSV first";
    public string OkTip => HasCsv ? "Applies the filter and closes" : "Open a CSV first";

    partial void OnSelectedTabIndexChanged(int value) => OnPropertyChanged(nameof(CanOk));

    /// <summary>Applies the builder content (tree, search text, saved name) to the open file. False when nothing was applied.</summary>
    public bool Apply()
    {
        if (Results == null || !HasCsv) return false;
        try
        {
            var text = _globalTouched ? GlobalText : Results.GlobalText;
            Results.ApplyBuilderResult(Build(), text, SavedName);
            _globalTouched = false;
            if (!string.Equals(GlobalText, Results.GlobalText, StringComparison.Ordinal)) GlobalText = Results.GlobalText;
            return true;
        }
        catch (Exception ex)
        {
            Results.ShowError("Invalid filter", ex);
            return false;
        }
    }

    private void OnResultsChanged(object? sender, System.ComponentModel.PropertyChangedEventArgs e)
    {
        if (e.PropertyName is nameof(ResultsViewModel.Headers) or nameof(ResultsViewModel.Rows) or nameof(ResultsViewModel.CurrentFile))
        {
            if (Results != null) HasCsv = Results.Headers.Count > 0;
            _retargetTimer?.Stop();
            _retargetTimer?.Start();
        }
    }

    /// <summary>The open file changed: refresh the columns and category; the tree stays (columns the new file lacks are reported in the notice).</summary>
    public void Retarget()
    {
        if (Results == null) return;
        HasCsv = Results.Headers.Count > 0;
        if (!HasCsv) return;
        var headers = Results.Headers.ToList();
        var sig = Results.CurrentSignature;
        var sameColumns = Headers.SequenceEqual(headers, StringComparer.Ordinal);
        if (!sameColumns)
        {
            _retargeting = true;
            try
            {
                // clearing the list makes each column box push null into its condition: remember and restore the choices
                var saved = AllConditions(RootGroup).Select(c => (c, c.Column)).ToList();
                Headers = headers;
                Columns.Clear();
                Columns.Add(FilterNode.AnyColumn);
                foreach (var h in headers) Columns.Add(h);
                foreach (var (c, col) in saved) c.Column = col;
            }
            finally { _retargeting = false; }
        }
        if (!string.Equals(sig, CategorySignature, StringComparison.OrdinalIgnoreCase))
        {
            CategorySignature = sig;
            CategoryName = Results.CurrentCategoryName;
            SavedName = null;
            RefreshSaved();
        }
        Changed();
        var missing = SavedFilterStore.Columns(Build()).Where(c => !headers.Contains(c, StringComparer.OrdinalIgnoreCase)).ToList();
        if (missing.Count > 0) Notice = $"Columns not in this CSV (ignored when applied): {string.Join(", ", missing)}";
        else if (Notice.StartsWith("Columns not in this CSV", StringComparison.Ordinal)) Notice = "";
    }

    private static IEnumerable<BuilderConditionViewModel> AllConditions(BuilderGroupViewModel g)
    {
        foreach (var n in g.Children)
        {
            if (n is BuilderConditionViewModel c) yield return c;
            else if (n is BuilderGroupViewModel sub) foreach (var x in AllConditions(sub)) yield return x;
        }
    }
    public ObservableCollection<SavedCategoryViewModel> SavedGroups { get; } = new();
    public bool HasSavedGroups => SavedGroups.Count > 0;
    public bool CanSave => CategorySignature.Length > 0 && !IsContentEmpty;
    private bool IsContentEmpty => Build().IsEmpty && string.IsNullOrWhiteSpace(GlobalText);

    partial void OnGlobalTextChanged(string value) => Changed();
    partial void OnRootGroupChanged(BuilderGroupViewModel value) => Changed();

    /// <summary>Replaces the tree with <paramref name="node"/> (a group; other nodes get wrapped in an And group).</summary>
    public void LoadNode(FilterNode? node)
    {
        var g = node as FilterGroup ?? new FilterGroup(FilterGroupKind.And, node != null ? new[] { node } : Array.Empty<FilterNode>());
        RootGroup = FromNode(g, null) as BuilderGroupViewModel ?? new BuilderGroupViewModel(this, null);
    }

    private BuilderNodeViewModel FromNode(FilterNode n, BuilderGroupViewModel? parent)
    {
        switch (n)
        {
            case FilterGroup g:
            {
                var vm = new BuilderGroupViewModel(this, parent) { IsOr = g.Kind == FilterGroupKind.Or, Negate = g.Negate };
                foreach (var c in g.Children) if (!c.IsEmpty) vm.Children.Add(FromNode(c, vm));
                return vm;
            }
            case FilterCondition c:
                return new BuilderConditionViewModel(this, parent)
                {
                    Column = c.Column, Op = FilterOps.Get(c.Op), Value = c.Value, Value2 = c.Value2 ?? "",
                };
            default:
                return new BuilderFixedViewModel(this, parent, n);
        }
    }

    internal void Changed()
    {
        if (RootGroup == null || _retargeting) return; // still constructing / swapping the column list
        try
        {
            var spec = new FilterSpec
            {
                Expression = Build(),
                GlobalText = !string.IsNullOrWhiteSpace(GlobalText) ? GlobalText.Trim() : null,
            };
            Preview = spec.ToDisplayString();
            if (!SyntaxEditing)
            {
                SyntaxText = Build().ToDisplayString();
                SyntaxError = null;
            }
        }
        catch (Exception ex) { Preview = ex.Message; }
        OnPropertyChanged(nameof(CanSave));
    }

    /// <summary>Parses text typed in the syntax box; a valid expression replaces the builder tree, an invalid one only sets <see cref="SyntaxError"/>.</summary>
    public void ApplySyntax(string? text)
    {
        if (!FilterSyntax.TryParse(text, out var node, out var error))
        {
            SyntaxError = error ?? "could not parse";
            return;
        }
        SyntaxError = null;
        if (string.Equals(text?.Trim() ?? "", Build().ToDisplayString(), StringComparison.Ordinal)) return; // nothing new
        LoadNode(node);
    }

    /// <summary>Ends syntax editing and shows the builder's own text again (when the typed text parsed).</summary>
    public void EndSyntaxEditing()
    {
        SyntaxEditing = false;
        if (SyntaxError == null) SyntaxText = Build().ToDisplayString();
    }

    public FilterGroup Build() => RootGroup.Build() as FilterGroup ?? new FilterGroup();

    // ------------------------------------------------------------ saved filters

    private void RefreshSaved()
    {
        SavedGroups.Clear();
        var snap = SavedFilters.Instance.Snapshot()
            .Where(x => x.Filters.Count > 0 || string.Equals(x.Category.Signature, CategorySignature, StringComparison.OrdinalIgnoreCase))
            .OrderByDescending(x => string.Equals(x.Category.Signature, CategorySignature, StringComparison.OrdinalIgnoreCase))
            .ThenBy(x => x.Category.Name, StringComparer.OrdinalIgnoreCase);
        foreach (var (cat, filters) in snap)
        {
            bool cur = string.Equals(cat.Signature, CategorySignature, StringComparison.OrdinalIgnoreCase);
            SavedGroups.Add(new SavedCategoryViewModel(cat, filters.Select(f => new SavedFilterItemViewModel(f, cur)), cur));
        }
        OnPropertyChanged(nameof(HasSavedGroups));
    }

    /// <summary>Puts a saved filter into the builder (tree, search text, name) and switches to the Builder tab.</summary>
    public void LoadSaved(SavedFilterItemViewModel item)
    {
        if (!item.CanLoad) return;
        LoadNode(item.Filter.Expression?.ToNode());
        GlobalText = item.Filter.GlobalText ?? "";
        _globalTouched = true;
        SavedName = item.Name;
        SelectedTabIndex = 0;
        Notice = "";
    }

    public bool SavedExists(string name) => CategorySignature.Length > 0 && SavedFilters.Instance.Find(CategorySignature, name) != null;

    /// <summary>Name "Save" writes: the loaded/saved name while it still exists, else the next "Filter N".</summary>
    public string SaveTargetName => SavedName != null && SavedExists(SavedName) ? SavedName : SavedFilters.Instance.NextNumberedName(CategorySignature);

    /// <summary>Saves the builder content (tree and search text) in the current category; the dialog stays open.</summary>
    public void Save(string? name = null)
    {
        if (!CanSave) return;
        name = string.IsNullOrWhiteSpace(name) ? SaveTargetName : name.Trim();
        var node = AnalyzeFilterNode.From(Build());
        var text = string.IsNullOrWhiteSpace(GlobalText) ? "" : GlobalText.Trim();
        var sig = CategorySignature;
        var catName = string.IsNullOrWhiteSpace(CategoryName) ? "Custom layout" : CategoryName;
        var cols = Headers.ToList();
        SavedFilters.Instance.Mutate(l =>
        {
            l.EnsureCategory(sig, catName, cols);
            l.Put(new SavedFilter { Name = name, CategorySignature = sig, Expression = node, GlobalText = text, SavedUtc = DateTime.UtcNow });
        });
        SavedName = name;
        Notice = $"Saved '{name}' to {SavedFilters.Instance.CategoryName(sig) ?? catName}";
    }

    public bool Rename(SavedFilterItemViewModel item, string newName)
    {
        var ok = SavedFilters.Instance.Mutate(l => l.Rename(item.CategorySignature, item.Name, newName));
        if (ok && IsLoaded(item)) SavedName = newName.Trim();
        return ok;
    }

    private bool IsLoaded(SavedFilterItemViewModel item) =>
        string.Equals(SavedName, item.Name, StringComparison.OrdinalIgnoreCase) &&
        string.Equals(item.CategorySignature, CategorySignature, StringComparison.OrdinalIgnoreCase);

    public void Delete(SavedFilterItemViewModel item)
    {
        SavedFilters.Instance.Mutate(l => l.Remove(item.CategorySignature, item.Name));
        if (IsLoaded(item)) SavedName = null;
    }

    public void RenameCategory(SavedCategoryViewModel cat, string newName) => SavedFilters.Instance.Mutate(l => l.RenameCategory(cat.Signature, newName));

    public void DeleteCategory(SavedCategoryViewModel cat)
    {
        SavedFilters.Instance.Mutate(l => l.RemoveCategory(cat.Signature));
        if (cat.IsCurrent) SavedName = null;
    }
}
