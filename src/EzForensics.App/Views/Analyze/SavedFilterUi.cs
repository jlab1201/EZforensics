using System.Windows;
using System.Windows.Automation;
using System.Windows.Controls;
using System.Windows.Media;
using EzForensics.App.Services;
using EzForensics.App.ViewModels;
using EzForensics.Core.Results;

namespace EzForensics.App.Views.Analyze;

/// <summary>Prompts and menu pieces for saved filters, shared by the filter panel and the file tree.</summary>
public static class SavedFilterUi
{
    /// <summary>"Save filter as…": asks for a name (default: next free "Filter N"), confirms before overwriting and saves in the open file's category.</summary>
    public static void SaveAs(ResultsViewModel vm, DependencyObject owner)
    {
        if (!vm.CanSaveFilter) return;
        var name = PromptSaveName(owner, vm.CurrentSignature, vm.CurrentCategoryName, SavedFilters.Instance.NextNumberedName(vm.CurrentSignature));
        if (name != null) vm.SaveFilterAs(name);
    }

    /// <summary>
    /// Name prompt of "Save as": when the name already exists in the category it asks to replace; Cancel there goes back to the name prompt
    /// (pre-filled with the typed name). Returns the chosen name, or null only when the name prompt itself is cancelled.
    /// </summary>
    public static string? PromptSaveName(DependencyObject owner, string sig, string categoryName, string initial)
    {
        var cat = SavedFilters.Instance.CategoryName(sig) ?? categoryName;
        var text = initial;
        while (true)
        {
            var name = TextPromptWindow.Ask(owner, "Save filter as", $"Filter name (category: {cat})", text,
                t => string.IsNullOrWhiteSpace(t) ? "Enter a name." : null);
            if (name == null) return null;
            name = name.Trim();
            if (SavedFilters.Instance.Find(sig, name) == null ||
                Dialogs.Confirm("Replace saved filter", $"A filter named '{name}' already exists in {cat}. Replace it?", yes: "Replace", no: "Cancel", defaultNo: true,
                    owner: Window.GetWindow(owner)))
                return name;
            text = name;
        }
    }

    /// <summary>Tooltip for "Save filter": the name it writes.</summary>
    public static string SaveTooltip(ResultsViewModel vm) =>
        vm.SaveTargetExists ? $"Updates '{vm.SaveTargetName}'" : $"Saves as '{vm.SaveTargetName}' in {vm.CurrentCategoryName}";

    /// <summary>Adds one menu item per saved filter (checked = <paramref name="active"/>) or a disabled "No saved filters" line.</summary>
    public static void AddFilterItems(ItemsControl parent, IReadOnlyList<SavedFilter> filters, string categoryName, string? active, string idPrefix,
        Action<SavedFilter> click, string? noneId = null)
    {
        if (filters.Count == 0)
        {
            var none = new MenuItem { Header = $"No saved filters for {categoryName}", IsEnabled = false };
            AutomationProperties.SetAutomationId(none, (noneId ?? idPrefix) + ".None");
            parent.Items.Add(none);
            return;
        }
        int n = 0;
        foreach (var f in filters)
        {
            var captured = f;
            var mi = new MenuItem
            {
                Header = new TextBlock { Text = f.Name },
                IsCheckable = true,
                IsChecked = string.Equals(f.Name, active, StringComparison.OrdinalIgnoreCase),
                StaysOpenOnClick = false,
                ToolTip = Preview(f),
            };
            AutomationProperties.SetAutomationId(mi, idPrefix + "." + (++n));
            AutomationProperties.SetName(mi, f.Name);
            mi.Click += (_, _) => click(captured);
            parent.Items.Add(mi);
        }
    }

    /// <summary>Readable one-line form of a saved filter (expression plus search text).</summary>
    public static string Preview(SavedFilter f)
    {
        try
        {
            var spec = new FilterSpec { GlobalText = string.IsNullOrWhiteSpace(f.GlobalText) ? null : f.GlobalText, Expression = f.Expression?.ToNode() };
            var t = spec.ToDisplayString();
            return string.IsNullOrWhiteSpace(t) ? "(empty filter)" : t;
        }
        catch (Exception) { return "(unreadable filter)"; }
    }

    // ---- export / import (*.ezfilters.json)

    private const string FileFilter = "Saved filters (*.ezfilters.json)|*.ezfilters.json|All files (*.*)|*.*";

    /// <summary>Picker, save dialog, write. Returns the notice to show ("Exported N filters"), or null when cancelled / nothing to export.</summary>
    public static string? Export(Window owner)
    {
        var entries = SavedFilters.Instance.Snapshot()
            .SelectMany(x => x.Filters.Select(f => new PickerEntry(x.Category.Name, f.Name, Preview(f), f))).ToList();
        if (entries.Count == 0) { Dialogs.Info("Export saved filters", "There are no saved filters to export.", owner: owner); return null; }
        var pick = SavedItemPickerWindow.Pick(owner, "Export saved filters", "Choose the saved filters to export.", entries, askConflict: false, okText: "Export");
        if (pick is not { } p) return null;
        var path = Dialogs.SaveFile("Export saved filters", FileFilter, "saved-filters.ezfilters.json");
        if (path == null) return null;
        try
        {
            var chosen = p.Selected.Select(e => (SavedFilter)e.Tag).ToList();
            SavedFilters.Instance.Read(l => { SavedFilterStore.Export(l, chosen, path); return 0; });
            return $"Exported {chosen.Count} {(chosen.Count == 1 ? "filter" : "filters")}";
        }
        catch (Exception ex)
        {
            AppLog.Error("Could not export saved filters", ex);
            Dialogs.Error("Export saved filters", "The file could not be written.", ex.Message, owner);
            return null;
        }
    }

    /// <summary>Open dialog, read, picker (with the name-conflict choice), merge. Returns "Imported N, replaced R, skipped S", or null when cancelled / failed.</summary>
    public static string? Import(Window owner)
    {
        var path = Dialogs.PickFile("Import saved filters", FileFilter);
        if (path == null) return null;
        if (!SavedFilterStore.TryImport(path, out var lib, out var error))
        {
            Dialogs.Error("Import saved filters", "This file cannot be imported.", error, owner);
            return null;
        }
        var entries = lib.Filters.Select(f => new PickerEntry(lib.Category(f.CategorySignature)?.Name is { Length: > 0 } n ? n : "Imported layout", f.Name, Preview(f), f)).ToList();
        if (entries.Count == 0) { Dialogs.Info("Import saved filters", "The file contains no saved filters.", owner: owner); return null; }
        var pick = SavedItemPickerWindow.Pick(owner, "Import saved filters", "Choose the saved filters to import.", entries, askConflict: true, okText: "Import");
        if (pick is not { } p) return null;
        var chosen = p.Selected.Select(e => (SavedFilter)e.Tag).ToList();
        var r = SavedFilters.Instance.Mutate(l => l.Merge(lib, chosen, p.Conflict));
        return $"Imported {r.Added}, replaced {r.Replaced}, skipped {r.Skipped}";
    }
}
