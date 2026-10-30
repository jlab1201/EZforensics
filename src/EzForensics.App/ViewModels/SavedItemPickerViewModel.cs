using System.Collections.ObjectModel;
using CommunityToolkit.Mvvm.ComponentModel;
using EzForensics.Core.Results;

namespace EzForensics.App.ViewModels;

/// <summary>One thing the picker lists (a saved filter or view): its category, name, a detail line and the original object.</summary>
public sealed record PickerEntry(string Category, string Name, string Detail, object Tag);

/// <summary>A checkable row of the saved-item picker.</summary>
public sealed partial class PickerItemViewModel : ObservableObject
{
    private readonly SavedItemPickerViewModel _root;
    public PickerItemViewModel(SavedItemPickerViewModel root, PickerEntry entry, bool isChecked)
    {
        _root = root;
        Entry = entry;
        _isChecked = isChecked;
    }

    public PickerEntry Entry { get; }
    public string Name => Entry.Name;
    public string Detail => Entry.Detail;

    [ObservableProperty] private bool _isChecked;

    partial void OnIsCheckedChanged(bool value) => _root.Refresh();
}

/// <summary>A category header of the picker: tri-state over its items.</summary>
public sealed class PickerGroupViewModel : ObservableObject
{
    private readonly SavedItemPickerViewModel _root;
    public PickerGroupViewModel(SavedItemPickerViewModel root, string name)
    {
        _root = root;
        Name = name;
    }

    public string Name { get; }
    public ObservableCollection<PickerItemViewModel> Items { get; } = new();
    public string Header => $"{Name} ({Items.Count})";

    /// <summary>True = all, false = none, null = some. A click sets every item (checked only for true).</summary>
    public bool? IsChecked
    {
        get => Items.All(i => i.IsChecked) ? true : Items.Any(i => i.IsChecked) ? null : false;
        set
        {
            var on = value == true;
            _root.Bulk(() => { foreach (var i in Items) i.IsChecked = on; });
        }
    }

    public void Raise() => OnPropertyChanged(nameof(IsChecked));
}

/// <summary>State of <c>SavedItemPickerWindow</c>: grouped checkable items, tri-state Select all, count, and (import) the name-conflict choice.</summary>
public sealed partial class SavedItemPickerViewModel : ObservableObject
{
    private bool _bulk;

    public SavedItemPickerViewModel(string title, string caption, IEnumerable<PickerEntry> entries, bool askConflict, string okText)
    {
        Title = title;
        Caption = caption;
        AskConflict = askConflict;
        OkText = okText;
        foreach (var g in entries.GroupBy(e => e.Category).OrderBy(g => g.Key, StringComparer.OrdinalIgnoreCase))
        {
            var grp = new PickerGroupViewModel(this, g.Key);
            foreach (var e in g) grp.Items.Add(new PickerItemViewModel(this, e, true));
            Groups.Add(grp);
        }
        Refresh();
    }

    public string Title { get; }
    public string Caption { get; }
    public string OkText { get; }
    public bool AskConflict { get; }
    public ObservableCollection<PickerGroupViewModel> Groups { get; } = new();

    [ObservableProperty] private int _selectedCount;
    [ObservableProperty] private bool _canOk;
    [ObservableProperty] private string _countText = "";

    /// <summary>0 = Replace, 1 = Keep both (default), 2 = Skip.</summary>
    public int ConflictIndex { get; set; } = 1;
    public bool ConflictReplace { get => ConflictIndex == 0; set { if (value) ConflictIndex = 0; } }
    public bool ConflictKeepBoth { get => ConflictIndex == 1; set { if (value) ConflictIndex = 1; } }
    public bool ConflictSkip { get => ConflictIndex == 2; set { if (value) ConflictIndex = 2; } }

    public ConflictMode Conflict => ConflictIndex switch { 0 => ConflictMode.Replace, 2 => ConflictMode.Skip, _ => ConflictMode.KeepBoth };

    public IEnumerable<PickerEntry> Selected => Groups.SelectMany(g => g.Items).Where(i => i.IsChecked).Select(i => i.Entry);

    public int TotalCount => Groups.Sum(g => g.Items.Count);

    /// <summary>True = all, false = none, null = some.</summary>
    public bool? SelectAll
    {
        get => SelectedCount == 0 ? false : SelectedCount == TotalCount ? true : null;
        set
        {
            var on = value == true;
            Bulk(() => { foreach (var g in Groups) foreach (var i in g.Items) i.IsChecked = on; });
        }
    }

    internal void Bulk(Action change)
    {
        _bulk = true;
        try { change(); }
        finally { _bulk = false; }
        Refresh();
    }

    internal void Refresh()
    {
        if (_bulk) return;
        SelectedCount = Groups.Sum(g => g.Items.Count(i => i.IsChecked));
        CanOk = SelectedCount > 0;
        CountText = $"{SelectedCount} selected";
        foreach (var g in Groups) g.Raise();
        OnPropertyChanged(nameof(SelectAll));
    }
}
