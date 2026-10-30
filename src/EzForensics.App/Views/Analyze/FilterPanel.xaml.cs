using System.ComponentModel;
using System.Text;
using System.Windows;
using System.Windows.Automation;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Data;
using System.Windows.Input;
using System.Windows.Media;
using EzForensics.App.ViewModels;
using EzForensics.Core.Results;
using static EzForensics.App.Views.Analyze.ViewHelpers;

namespace EzForensics.App.Views.Analyze;

/// <summary>Bottom filter panel: the whole active filter as text (DataContext: ResultsViewModel).</summary>
public partial class FilterPanel : UserControl
{
    private static string SaveTooltip(ResultsViewModel vm) => SavedFilterUi.SaveTooltip(vm);

    public FilterPanel()
    {
        InitializeComponent();
        FilterText.MouseLeftButtonDown += (_, _) => FilterText.Focus();
    }

    private void CopyFilter_Click(object sender, RoutedEventArgs e) => CopyText();

    private void FilterText_KeyDown(object sender, KeyEventArgs e)
    {
        if (e.Key == Key.C && Keyboard.Modifiers == ModifierKeys.Control) { CopyText(); e.Handled = true; }
    }

    private void CopyText()
    {
        var t = (DataContext as ResultsViewModel)?.FilterDisplayText;
        if (string.IsNullOrEmpty(t)) return;
        try { Clipboard.SetText(t); } catch (System.Runtime.InteropServices.COMException) { }
    }

    private void ActionsButton_Click(object sender, RoutedEventArgs e)
    {
        if (DataContext is not ResultsViewModel vm) return;
        BuildMenu(vm);
        ActionsMenu.PlacementTarget = ActionsButton;
        ActionsMenu.IsOpen = true;
    }

    private void BuildMenu(ResultsViewModel vm)
    {
        var menu = ActionsMenu;
        menu.Items.Clear();
        MenuItem Add(string id, string header, string? glyph, bool enabled, Action click, string? tip = null)
        {
            var mi = new MenuItem { Header = header, IsEnabled = enabled, ToolTip = tip };
            if (glyph != null) mi.Icon = MenuUi.Icon(glyph);
            AutomationProperties.SetAutomationId(mi, "Results.FilterPanel." + id);
            AutomationProperties.SetName(mi, header.TrimEnd('…'));
            mi.Click += (_, _) => click();
            menu.Items.Add(mi);
            return mi;
        }

        Add("Edit", "Edit filter…", "", vm.Headers.Count > 0, () => EditFilter(vm, this), "Filter builder: And / Or / Not groups over any column");
        Add("Save", "Save filter", "", vm.CanSaveFilter, vm.SaveFilter, SaveTooltip(vm));
        Add("SaveAs", "Save filter as…", "", vm.CanSaveFilter, () => SavedFilterUi.SaveAs(vm, this), "Saves under a name you choose (category: " + vm.CurrentCategoryName + ")");
        menu.Items.Add(new Separator());
        Add("Manage", "Manage saved filters…", "", vm.Headers.Count > 0, () => EditFilter(vm, this, savedTab: true), "Rename, delete and load saved filters of every category");
        menu.Items.Add(new Separator());
        Add("ClearAll", "Clear filters", "", vm.HasAnyFilter, () => vm.ClearFiltersCommand.Execute(null), "Removes the search text, the filter and the flag filter");
    }
}
