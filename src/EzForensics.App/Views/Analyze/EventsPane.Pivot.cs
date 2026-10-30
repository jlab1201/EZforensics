using System.Windows;
using System.Windows.Automation;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Input;
using EzForensics.App.Services;
using EzForensics.App.ViewModels;
using static EzForensics.App.Views.Analyze.ViewHelpers;

namespace EzForensics.App.Views.Analyze;

/// <summary>Pivot: group panel drag / drop (column headers and chips), group header lines (toggle, keys, context menu).</summary>
public partial class EventsPane
{
    private const string PivotColumnFormat = "EzForensics.PivotColumn";
    private const string PivotChipFormat = "EzForensics.PivotChip";
    private DataGridColumnHeader? _hdrDrag;
    private Point _hdrDragStart;
    private PivotGroupChip? _chipDrag;
    private Point _chipDragStart;
    private GroupRowItem? _menuGroup;

    /// <summary>Flag column = flag + 16 px per group level (data rows are indented inside it).</summary>
    private void UpdateFlagColumnWidth()
    {
        if (Grid.Columns.Count == 0 || _vm == null) return;
        Grid.Columns[0].Width = new DataGridLength(40 + 16 * _vm.GroupColumns.Count);
    }

    // ------------------------------------------------------------ drag a column header onto the group panel

    private void Grid_PreviewMouseLeftButtonDown(object sender, MouseButtonEventArgs e)
    {
        _hdrDrag = null;
        if (e.OriginalSource is not DependencyObject src || _vm == null) return;
        if (FindAncestor<DataGridColumnHeader>(src) is { } header)
        {
            // the filter row, funnel / operator buttons and the resize grip keep their own mouse handling
            // (the header itself is a ButtonBase, so only controls between the click target and the header count)
            if (HasInteractiveBelow(src, header)) return;
            if (DataIndex(header.Column) < 0) return;
            _hdrDrag = header;
            _hdrDragStart = e.GetPosition(header);
            return;
        }
        if (FindAncestor<DataGridRow>(src) is { Item: GroupRowItem } row && FindAncestor<ButtonBase>(src) == null)
        {
            row.Focus();
            e.Handled = true; // a header line has no cells to select; a double-click toggles it
            if (e.ClickCount == 2 && row.Item is GroupRowItem g) _ = ToggleAsync(g, null);
        }
    }

    /// <summary>True when a text box, button or thumb sits between <paramref name="src"/> and <paramref name="header"/> (the header excluded).</summary>
    private static bool HasInteractiveBelow(DependencyObject src, DataGridColumnHeader header)
    {
        for (DependencyObject? d = src; d != null && !ReferenceEquals(d, header);
             d = d is System.Windows.Media.Visual or System.Windows.Media.Media3D.Visual3D ? System.Windows.Media.VisualTreeHelper.GetParent(d) : LogicalTreeHelper.GetParent(d))
            if (d is TextBoxBase or ButtonBase or Thumb) return true;
        return false;
    }

    private void Grid_PreviewMouseMove(object sender, MouseEventArgs e)
    {
        var header = _hdrDrag;
        if (header == null || _vm == null) return;
        if (e.LeftButton != MouseButtonState.Pressed) { _hdrDrag = null; return; }
        var p = e.GetPosition(header);
        // a column reorder turns into a group drag when the pointer leaves the header row vertically, enters the group panel
        // (from any direction) or leaves the grid sideways; plain horizontal moves inside the header row stay a column reorder
        bool outsideV = p.Y < -4 || p.Y > header.ActualHeight + 4;
        var pp = e.GetPosition(PivotPanel);
        bool overPanel = PivotPanel.IsVisible && pp.X >= -4 && pp.Y >= -4 && pp.X <= PivotPanel.ActualWidth + 4 && pp.Y <= PivotPanel.ActualHeight + 4;
        var pg = e.GetPosition(Grid);
        bool outsideGrid = pg.X < -4 || pg.X > Grid.ActualWidth + 4;
        bool moved = Math.Abs(p.Y - _hdrDragStart.Y) >= SystemParameters.MinimumVerticalDragDistance
                     || Math.Abs(p.X - _hdrDragStart.X) >= SystemParameters.MinimumHorizontalDragDistance;
        if (!moved || !(outsideV || overPanel || outsideGrid)) return;
        _hdrDrag = null;
        int col = DataIndex(header.Column);
        if (col < 0) return;
        var name = HeaderName(col);
        header.ReleaseMouseCapture(); // cancels the DataGrid's own column drag
        Mouse.Capture(null);
        try { DragDrop.DoDragDrop(header, new DataObject(PivotColumnFormat, name), DragDropEffects.Copy | DragDropEffects.Move); }
        catch (Exception ex) { AppLog.Warn("Pivot: header drag failed: " + ex.Message); }
    }

    // ------------------------------------------------------------ chips: click flips the order, drag reorders / ungroups

    private void PivotChip_PreviewMouseLeftButtonDown(object sender, MouseButtonEventArgs e)
    {
        _chipDrag = null;
        if (sender is not FrameworkElement { Tag: PivotGroupChip chip } || e.OriginalSource is not DependencyObject src) return;
        if (FindAncestor<ButtonBase>(src) != null) return;
        _chipDrag = chip;
        _chipDragStart = e.GetPosition(PivotPanel);
    }

    private void PivotChip_PreviewMouseMove(object sender, MouseEventArgs e)
    {
        var chip = _chipDrag;
        if (chip == null || sender is not DependencyObject d) return;
        if (e.LeftButton != MouseButtonState.Pressed) { _chipDrag = null; return; }
        var p = e.GetPosition(PivotPanel);
        if (Math.Abs(p.X - _chipDragStart.X) < SystemParameters.MinimumHorizontalDragDistance
            && Math.Abs(p.Y - _chipDragStart.Y) < SystemParameters.MinimumVerticalDragDistance) return;
        _chipDrag = null;
        try { DragDrop.DoDragDrop(d, new DataObject(PivotChipFormat, chip.Column), DragDropEffects.Move); }
        catch (Exception ex) { AppLog.Warn("Pivot: chip drag failed: " + ex.Message); }
    }

    /// <summary>A click that did not turn into a drag flips the chip's group order.</summary>
    private void PivotChip_PreviewMouseLeftButtonUp(object sender, MouseButtonEventArgs e)
    {
        var chip = _chipDrag;
        _chipDrag = null;
        if (chip != null && _vm != null) _vm.ToggleGroupSortCommand.Execute(chip);
    }

    private void PivotPanelExpandAll_Click(object sender, RoutedEventArgs e) => _vm?.ExpandAllGroupsCommand.Execute(null);
    private void PivotPanelCollapseAll_Click(object sender, RoutedEventArgs e) => _vm?.CollapseAllGroupsCommand.Execute(null);
    private void PivotPanelClear_Click(object sender, RoutedEventArgs e) => _vm?.ClearGroupingCommand.Execute(null);

    private static PivotGroupChip? ChipOf(object sender) => (sender as FrameworkElement)?.DataContext as PivotGroupChip;

    private void PivotChipSortValue_Click(object sender, RoutedEventArgs e) => _vm?.SetGroupSortKey(ChipOf(sender), false);
    private void PivotChipSortCount_Click(object sender, RoutedEventArgs e) => _vm?.SetGroupSortKey(ChipOf(sender), true);
    private void PivotChipUngroup_Click(object sender, RoutedEventArgs e) => _vm?.RemoveGroupColumnCommand.Execute(ChipOf(sender));

    // ------------------------------------------------------------ grid as drop target: a chip dropped here is ungrouped

    private void Grid_ChipDragOver(object sender, DragEventArgs e)
    {
        bool ok = e.Data.GetDataPresent(PivotChipFormat);
        e.Effects = ok ? DragDropEffects.Move : DragDropEffects.None;
        e.Handled = ok;
    }

    private void Grid_ChipDrop(object sender, DragEventArgs e)
    {
        if (_vm == null || e.Data.GetData(PivotChipFormat) is not string column) return;
        var chip = _vm.GroupColumns.FirstOrDefault(c => c.Column == column);
        if (chip != null) _vm.RemoveGroupColumnCommand.Execute(chip);
        e.Handled = true;
    }

    // ------------------------------------------------------------ group panel drop target

    private void PivotPanel_DragOver(object sender, DragEventArgs e)
    {
        bool ok = e.Data.GetDataPresent(PivotColumnFormat) || e.Data.GetDataPresent(PivotChipFormat);
        e.Effects = ok ? DragDropEffects.Move : DragDropEffects.None;
        if (ok) PivotPanel.SetResourceReference(Border.BorderBrushProperty, "Brush.Accent");
        e.Handled = true;
    }

    private void PivotPanel_DragLeave(object sender, DragEventArgs e) =>
        PivotPanel.SetResourceReference(Border.BorderBrushProperty, "Brush.Border");

    private void PivotPanel_Drop(object sender, DragEventArgs e)
    {
        PivotPanel.SetResourceReference(Border.BorderBrushProperty, "Brush.Border");
        if (_vm == null) return;
        int at = DropIndex(e.GetPosition(PivotChips));
        if (e.Data.GetData(PivotColumnFormat) is string column)
        {
            _vm.AddGroupColumn(column, at);
            e.Handled = true;
        }
        else if (e.Data.GetData(PivotChipFormat) is string chipColumn)
        {
            int from = _vm.GroupColumns.ToList().FindIndex(c => c.Column == chipColumn);
            if (from < 0) return;
            int to = at < 0 ? _vm.GroupColumns.Count - 1 : (at > from ? at - 1 : at);
            _vm.MoveGroupColumn(from, to);
            e.Handled = true;
        }
    }

    /// <summary>Chip index the drop lands before (-1 = after the last chip).</summary>
    private int DropIndex(Point p)
    {
        if (_vm == null) return -1;
        for (int i = 0; i < _vm.GroupColumns.Count; i++)
        {
            if (PivotChips.ItemContainerGenerator.ContainerFromIndex(i) is not FrameworkElement c || !c.IsVisible) continue;
            var tl = c.TranslatePoint(new Point(0, 0), PivotChips);
            bool sameLine = p.Y >= tl.Y && p.Y <= tl.Y + c.ActualHeight;
            if ((sameLine && p.X < tl.X + c.ActualWidth / 2) || p.Y < tl.Y) return i;
        }
        return -1;
    }

    // ------------------------------------------------------------ group header lines

    private void GroupChevron_Click(object sender, RoutedEventArgs e)
    {
        if (e.OriginalSource is not Button { Tag: "PivotToggle", DataContext: GroupRowItem g }) return;
        _ = ToggleAsync(g, null);
        e.Handled = true;
    }

    private async Task ToggleAsync(GroupRowItem g, bool? collapse)
    {
        if (_vm == null) return;
        try { await _vm.ToggleGroupAsync(g, collapse); }
        catch (Exception ex) { _vm.ShowError("Could not expand or collapse the group", ex); }
    }

    /// <summary>After a collapse / expand: brings the toggled header back into view and focuses it.</summary>
    private void OnPivotReflattened(string? key)
    {
        if (key == null || _vm?.Rows is not { } rows) return;
        int pos = rows.PositionOfGroup(key);
        if (pos < 0) return;
        Dispatcher.BeginInvoke(new Action(() => FocusLine(pos, null)), System.Windows.Threading.DispatcherPriority.Loaded);
    }

    /// <summary>Focuses line <paramref name="pos"/>: a header row gets keyboard focus, a data row becomes the current cell.</summary>
    private void FocusLine(int pos, DataGridColumn? column)
    {
        if (_vm?.Rows is not { } rows || (uint)pos >= (uint)rows.Count) return;
        var item = rows[pos];
        Grid.ScrollIntoView(item);
        if (item is RowItem)
        {
            var vis = VisibleDataColumns();
            var col = column ?? (vis.Length > 0 ? DataCol(vis[0]) : null);
            if (col == null) return;
            var info = new DataGridCellInfo(item, col);
            Grid.SelectedCells.Clear();
            Grid.CurrentCell = info;
            Grid.SelectedCells.Add(info);
            Grid.UpdateLayout();
            if (Grid.ItemContainerGenerator.ContainerFromIndex(pos) is DataGridRow r)
                FindVisualChildren<DataGridCell>(r).FirstOrDefault(c => c.Column == col)?.Focus();
        }
        else
        {
            Grid.UpdateLayout();
            if (Grid.ItemContainerGenerator.ContainerFromIndex(pos) is DataGridRow r) r.Focus();
        }
    }

    /// <summary>Keys on pivot lines: Left / Right collapse / expand a header; Up / Down step onto and off header lines.</summary>
    private bool HandlePivotKey(KeyEventArgs e)
    {
        if (_vm?.Rows is not { IsGrouped: true } rows || Keyboard.Modifiers != ModifierKeys.None) return false;
        var focusedRow = Keyboard.FocusedElement as DataGridRow;
        if (focusedRow?.Item is GroupRowItem g)
        {
            switch (e.Key)
            {
                case Key.Left: _ = ToggleAsync(g, true); return true;
                case Key.Right: _ = ToggleAsync(g, false); return true;
                case Key.Enter: _ = ToggleAsync(g, null); return true;
                case Key.Down: FocusLine(g.Position + 1, Grid.CurrentCell.Column); return true;
                case Key.Up: FocusLine(g.Position - 1, Grid.CurrentCell.Column); return true;
                case Key.Apps: return false;
            }
            return false;
        }
        if (Grid.CurrentCell.Item is RowItem cur && e.Key is Key.Down or Key.Up)
        {
            int next = cur.Position + (e.Key == Key.Down ? 1 : -1);
            if ((uint)next < (uint)rows.Count && rows[next] is GroupRowItem)
            {
                FocusLine(next, Grid.CurrentCell.Column);
                return true;
            }
        }
        return false;
    }

    // ------------------------------------------------------------ menus

    private void BuildGroupMenu(ContextMenu menu, GroupRowItem g)
    {
        var vm = _vm!;
        var value = g.Node.Value;
        MenuUi.Group(menu.Items, "Group");
        menu.Items.Add(PivotItem("ExpandAll", "Full Expand", () => vm.ExpandAllGroupsCommand.Execute(null), MenuUi.Expand, "Accent"));
        menu.Items.Add(PivotItem("CollapseAll", "Full Collapse", () => vm.CollapseAllGroupsCommand.Execute(null), MenuUi.Collapse, "Accent"));
        MenuUi.Group(menu.Items, "Filter");
        menu.Items.Add(PivotItem("FilterToGroup", $"Filter to this group  [{Shorten(g.ValueText)}]", () => vm.FilterToGroup(g.Node), MenuUi.Filter, "Info"));
        MenuUi.Group(menu.Items, "Copy");
        menu.Items.Add(PivotItem("CopyValue", "Copy group value", () => SetClipboard(value), MenuUi.Copy, "Accent"));
        MenuUi.Group(menu.Items, "Grouping");
        menu.Items.Add(PivotItem("Remove", "Remove grouping", () => vm.ClearGroupingCommand.Execute(null), MenuUi.Clear, "Error"));
    }

    private static MenuItem PivotItem(string id, string header, Action action, string glyph, string kind)
    {
        var mi = Item("Pivot." + id, header, action, true, glyph, kind);
        AutomationProperties.SetAutomationId(mi, "Results.Pivot.Menu." + id);
        return mi;
    }

    private MenuItem GroupByItem(int col)
    {
        var name = HeaderName(col);
        bool already = col >= 0 && _vm!.IsGroupColumn(name);
        var text = col < 0 ? "Group by this column (not the flag column)" : $"Group by this column  [{Shorten(name)}]";
        var mi = Item("Header.GroupBy", text, () => _vm?.AddGroupColumn(name), col >= 0 && !already, MenuUi.GroupBy, "Accent");
        AutomationProperties.SetAutomationId(mi, "Results.Header.Menu.GroupBy");
        return mi;
    }

    /// <summary>"Ungroup" for a grouped column (null for any other column).</summary>
    private MenuItem? UngroupItem(int col)
    {
        if (col < 0) return null;
        var name = HeaderName(col);
        var chip = _vm!.GroupColumns.FirstOrDefault(c => string.Equals(c.Column, name, StringComparison.OrdinalIgnoreCase));
        if (chip == null) return null;
        var mi = Item("Header.Ungroup", $"Ungroup  [{Shorten(name)}]", () => _vm?.RemoveGroupColumnCommand.Execute(chip), true, MenuUi.Clear, "Error");
        AutomationProperties.SetAutomationId(mi, "Results.Header.Menu.Ungroup");
        return mi;
    }
}
