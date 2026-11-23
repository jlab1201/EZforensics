using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using EzForensics.App.ViewModels.Learn;

namespace EzForensics.App.Views.Learn;

/// <summary>
/// The Inbox tab: a review list. The list's selection feeds the action bar (and the view model can clear it: a filter change, Esc);
/// the "+ Filter" button and the category and type chips open menus built when they are clicked.
/// </summary>
public partial class InboxView : UserControl
{
    private InboxViewModel? _vm;
    private bool _pushing;

    public InboxView()
    {
        InitializeComponent();
        DataContextChanged += (_, _) =>
        {
            if (_vm != null) _vm.SelectionPushed -= OnSelectionPushed;
            _vm = DataContext as InboxViewModel;
            if (_vm != null) _vm.SelectionPushed += OnSelectionPushed;
        };
    }

    /// <summary>The view model changed the selection itself (cleared it, or put failed rows back): mirror it in the list.</summary>
    private void OnSelectionPushed(IReadOnlyList<InboxRow> rows)
    {
        _pushing = true;
        try
        {
            RowList.SelectedItems.Clear();
            foreach (var r in rows) RowList.SelectedItems.Add(r);
        }
        finally { _pushing = false; }
    }

    private void RowList_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (!_pushing && _vm != null) _vm.SetSelected(RowList.SelectedItems.OfType<InboxRow>());
    }

    private void UserControl_PreviewKeyDown(object sender, KeyEventArgs e)
    {
        if (e.Key != Key.Escape || _vm is not { HasSelection: true }) return;
        _vm.ClearSelectionCommand.Execute(null);
        e.Handled = true;
    }

    // ---- menus

    private static void Open(Button anchor, ContextMenu menu)
    {
        menu.PlacementTarget = anchor;
        menu.Placement = System.Windows.Controls.Primitives.PlacementMode.Bottom;
        menu.IsOpen = true;
    }

    private static ContextMenu ValueMenu(IEnumerable<string> values, string? current, Action<string> pick)
    {
        var menu = new ContextMenu();
        foreach (var v in values)
        {
            var item = new MenuItem { Header = v, IsChecked = string.Equals(v, current, StringComparison.OrdinalIgnoreCase) };
            var value = v;
            item.Click += (_, _) => pick(value);
            menu.Items.Add(item);
        }
        return menu;
    }

    /// <summary>"+ Filter": first the facet, then a value of it (only values the waiting documents have).</summary>
    private void AddFilter_Click(object sender, RoutedEventArgs e)
    {
        if (_vm == null) return;
        var menu = new ContextMenu();
        foreach (var facet in InboxViewModel.Facets)
        {
            var values = _vm.FacetValues(facet);
            var f = facet;
            var parent = new MenuItem { Header = facet, IsEnabled = values.Count > 0 };
            foreach (var v in values)
            {
                var value = v;
                var item = new MenuItem { Header = v };
                item.Click += (_, _) => _vm.AddFilter(f, value);
                parent.Items.Add(item);
            }
            menu.Items.Add(parent);
        }
        Open(AddFilterButton, menu);
    }

    private void SetCategory_Click(object sender, RoutedEventArgs e)
    {
        if (_vm != null) Open((Button)sender, ValueMenu(_vm.Categories, null, v => _vm.SetCategoryCommand.Execute(v)));
    }

    private void SetType_Click(object sender, RoutedEventArgs e)
    {
        if (_vm != null) Open((Button)sender, ValueMenu(_vm.Types, null, v => _vm.SetTypeCommand.Execute(v)));
    }

    private void CategoryChip_Click(object sender, RoutedEventArgs e)
    {
        if (sender is Button { DataContext: InboxRow row } b && _vm != null)
            Open(b, ValueMenu(_vm.Categories, row.Category, v => row.SetCategoryCommand.Execute(v)));
    }

    private void TypeChip_Click(object sender, RoutedEventArgs e)
    {
        if (sender is Button { DataContext: InboxRow row } b && _vm != null)
            Open(b, ValueMenu(_vm.Types, row.NoteType, v => row.SetTypeCommand.Execute(v)));
    }

    // ---- tag editor (an editable box with the known tags as suggestions)

    private void TagBox_KeyDown(object sender, KeyEventArgs e)
    {
        if (e.Key != Key.Enter || sender is not ComboBox { DataContext: InboxRow row } box) return;
        var text = box.Text.Trim();
        if (text.Length > 0) row.AddTag(text);
        box.Text = "";
        e.Handled = true;
    }

    private void TagBox_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (sender is not ComboBox { DataContext: InboxRow row } box || box.SelectedItem is not string tag) return;
        row.AddTag(tag);
        box.SelectedItem = null;
        box.Text = "";
    }
}
