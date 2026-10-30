using System.Collections.ObjectModel;
using CommunityToolkit.Mvvm.ComponentModel;
using EzForensics.Core.Results;

namespace EzForensics.App.ViewModels;

/// <summary>Readable text of a saved filter.</summary>
public static class SavedFilterPreview
{
    public static string Text(SavedFilter f)
    {
        try
        {
            var spec = new FilterSpec { GlobalText = string.IsNullOrWhiteSpace(f.GlobalText) ? null : f.GlobalText, Expression = f.Expression?.ToNode() };
            var t = spec.ToDisplayString();
            return string.IsNullOrWhiteSpace(t) ? "(empty filter)" : t;
        }
        catch (Exception) { return "(unreadable filter)"; }
    }
}

/// <summary>One saved filter row on the builder's "Saved filters" tab.</summary>
public sealed class SavedFilterItemViewModel
{
    public SavedFilterItemViewModel(SavedFilter filter, bool canLoad)
    {
        Filter = filter;
        CanLoad = canLoad;
        Preview = SavedFilterPreview.Text(filter);
    }

    public SavedFilter Filter { get; }
    public string Name => Filter.Name;
    public string CategorySignature => Filter.CategorySignature;
    public string Preview { get; }
    public bool CanLoad { get; }
    public string EditTooltip => CanLoad ? "Edit in builder" : "Open a CSV with this column layout to load it";
    public string RowTooltip => CanLoad ? "Double-click to load into the builder" : "Open a CSV with this column layout to load it";
}

/// <summary>One category (column layout) with its saved filters.</summary>
public sealed partial class SavedCategoryViewModel : ObservableObject
{
    public SavedCategoryViewModel(SavedFilterCategory category, IEnumerable<SavedFilterItemViewModel> items, bool isCurrent)
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
    public ObservableCollection<SavedFilterItemViewModel> Items { get; } = new();
    public int Count => Items.Count;
    public string Header => $"{Name} ({Count})";
    [ObservableProperty] private bool _isExpanded;
}
