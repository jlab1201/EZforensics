using System.Windows;
using System.Windows.Automation;
using System.Windows.Controls;
using EzForensics.App.Services;
using EzForensics.App.ViewModels;
using EzForensics.Core.Results;

namespace EzForensics.App.Views.Analyze;

/// <summary>Prompts and menu pieces for saved views (column layouts), shared by the Actions menu, the file tree and the view builder.</summary>
public static class SavedViewUi
{
    /// <summary>"Save view as…": asks for a name (and, while grouped, whether to include the Pivot grouping), confirms before overwriting, saves.</summary>
    public static void SaveAs(ResultsViewModel vm, DependencyObject owner)
    {
        if (!vm.CanSaveView) return;
        var r = PromptSaveName(owner, vm.CurrentSignature, vm.CurrentCategoryName, SavedViews.Instance.NextNumberedName(vm.CurrentSignature), vm.IsGrouped);
        if (r is { } x) vm.SaveViewAs(x.Name, x.IncludeGrouping);
    }

    /// <summary>
    /// Name prompt; an existing name asks to replace (Cancel goes back to the prompt). <paramref name="offerGrouping"/> adds the
    /// "Include grouping (Pivot)" check box. Returns null only when the name prompt itself is cancelled.
    /// </summary>
    public static (string Name, bool IncludeGrouping)? PromptSaveName(DependencyObject owner, string sig, string categoryName, string initial, bool offerGrouping)
    {
        var cat = SavedViews.Instance.CategoryName(sig) ?? categoryName;
        var text = initial;
        while (true)
        {
            string? name; bool grouping = false;
            Func<string, string?> validate = t => string.IsNullOrWhiteSpace(t) ? "Enter a name." : null;
            if (offerGrouping)
            {
                var r = TextPromptWindow.AskWithCheck(owner, "Save view as", $"View name (category: {cat})", text, validate, "Include grouping (Pivot)");
                name = r?.Text; grouping = r?.Checked ?? false;
            }
            else name = TextPromptWindow.Ask(owner, "Save view as", $"View name (category: {cat})", text, validate);
            if (name == null) return null;
            name = name.Trim();
            if (SavedViews.Instance.Find(sig, name) == null ||
                Dialogs.Confirm("Replace saved view", $"A view named '{name}' already exists in {cat}. Replace it?", yes: "Replace", no: "Cancel", defaultNo: true,
                    owner: Window.GetWindow(owner)))
                return (name, grouping);
            text = name;
        }
    }

    /// <summary>Adds one menu item per saved view (checked = <paramref name="active"/>) or a disabled "No saved views" line.</summary>
    public static void AddViewItems(ItemsControl parent, IReadOnlyList<SavedView> views, string categoryName, string? active, string idPrefix,
        Action<SavedView> click, string? noneId = null)
    {
        if (views.Count == 0)
        {
            var none = new MenuItem { Header = $"No saved views for {categoryName}", IsEnabled = false };
            AutomationProperties.SetAutomationId(none, (noneId ?? idPrefix) + ".None");
            parent.Items.Add(none);
            return;
        }
        int n = 0;
        foreach (var v in views)
        {
            var captured = v;
            var mi = new MenuItem
            {
                Header = new TextBlock { Text = v.Name },
                IsCheckable = true,
                IsChecked = string.Equals(v.Name, active, StringComparison.OrdinalIgnoreCase),
                StaysOpenOnClick = false,
                ToolTip = v.Summary(),
            };
            AutomationProperties.SetAutomationId(mi, idPrefix + "." + (++n));
            AutomationProperties.SetName(mi, v.Name);
            mi.Click += (_, _) => click(captured);
            parent.Items.Add(mi);
        }
    }

    private static ViewBuilderWindow? _viewBuilder;

    /// <summary>
    /// Opens the (modeless) view builder over the open file's current layout, or activates the open one (one instance); savedTab selects the
    /// Saved views tab, otherwise the Builder tab. The window applies through its Apply / OK buttons.
    /// </summary>
    public static void Edit(ResultsViewModel? vm, DependencyObject owner, bool savedTab = false)
    {
        if (vm == null) return;
        if (_viewBuilder is { } open)
        {
            if (open.DataContext is ViewBuilderViewModel b && ReferenceEquals(b.Results, vm))
            {
                if (open.WindowState == WindowState.Minimized) open.WindowState = WindowState.Normal;
                b.SelectedTabIndex = savedTab ? 1 : 0;
                open.Activate();
                return;
            }
            open.Close();
        }
        if (vm.Headers.Count == 0 || vm.GridBridge == null)
        {
            vm.ShowMessage("Open a CSV first.", InfoSeverity.Warning);
            return;
        }
        var bvm = new ViewBuilderViewModel(vm);
        if (savedTab) bvm.SelectedTabIndex = 1;
        var win = new ViewBuilderWindow { DataContext = bvm, Owner = Window.GetWindow(owner) };
        win.Closed += (_, _) => { if (ReferenceEquals(_viewBuilder, win)) _viewBuilder = null; };
        _viewBuilder = win;
        win.Show();
    }

    // ---- export / import (*.ezviews.json)

    private const string FileFilter = "Saved views (*.ezviews.json)|*.ezviews.json|All files (*.*)|*.*";

    /// <summary>Picker, save dialog, write. Returns the notice to show ("Exported N views"), or null when cancelled / nothing to export.</summary>
    public static string? Export(Window owner)
    {
        var entries = SavedViews.Instance.Snapshot()
            .SelectMany(x => x.Views.Select(v => new PickerEntry(x.Category.Name, v.Name, v.Summary(), v))).ToList();
        if (entries.Count == 0) { Dialogs.Info("Export saved views", "There are no saved views to export.", owner: owner); return null; }
        var pick = SavedItemPickerWindow.Pick(owner, "Export saved views", "Choose the saved views to export.", entries, askConflict: false, okText: "Export");
        if (pick is not { } p) return null;
        var path = Dialogs.SaveFile("Export saved views", FileFilter, "saved-views.ezviews.json");
        if (path == null) return null;
        try
        {
            var chosen = p.Selected.Select(e => (SavedView)e.Tag).ToList();
            SavedViews.Instance.Read(l => { SavedViewStore.Export(l, chosen, path); return 0; });
            return $"Exported {chosen.Count} {(chosen.Count == 1 ? "view" : "views")}";
        }
        catch (Exception ex)
        {
            AppLog.Error("Could not export saved views", ex);
            Dialogs.Error("Export saved views", "The file could not be written.", ex.Message, owner);
            return null;
        }
    }

    /// <summary>Open dialog, read, picker (with the name-conflict choice), merge. Returns "Imported N, replaced R, skipped S", or null when cancelled / failed.</summary>
    public static string? Import(Window owner)
    {
        var path = Dialogs.PickFile("Import saved views", FileFilter);
        if (path == null) return null;
        if (!SavedViewStore.TryImport(path, out var lib, out var error))
        {
            Dialogs.Error("Import saved views", "This file cannot be imported.", error, owner);
            return null;
        }
        var entries = lib.Views.Select(v => new PickerEntry(lib.Category(v.CategorySignature)?.Name is { Length: > 0 } n ? n : "Imported layout", v.Name, v.Summary(), v)).ToList();
        if (entries.Count == 0) { Dialogs.Info("Import saved views", "The file contains no saved views.", owner: owner); return null; }
        var pick = SavedItemPickerWindow.Pick(owner, "Import saved views", "Choose the saved views to import.", entries, askConflict: true, okText: "Import");
        if (pick is not { } p) return null;
        var chosen = p.Selected.Select(e => (SavedView)e.Tag).ToList();
        var r = SavedViews.Instance.Mutate(l => l.Merge(lib, chosen, p.Conflict));
        return $"Imported {r.Added}, replaced {r.Replaced}, skipped {r.Skipped}";
    }
}
