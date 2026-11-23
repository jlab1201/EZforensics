using System.Windows;
using System.Windows.Controls;
using System.Windows.Automation;
using System.Windows.Controls.Primitives;
using System.Windows.Data;
using System.Windows.Input;
using EzForensics.App.ViewModels;

namespace EzForensics.App.Views;

public partial class TriageView : UserControl
{
    private void SelectionMenu_Click(object sender, RoutedEventArgs e)
    {
        if (sender is not Button b || b.ContextMenu == null || DataContext is not ViewModels.TriageViewModel vm) return;
        vm.RefreshSelectionMenu();
        b.ContextMenu.PlacementTarget = b;
        b.ContextMenu.IsOpen = true;
    }

    public TriageView()
    {
        InitializeComponent();
        DataContextChanged += (_, e) =>
        {
            if (e.OldValue is ViewModels.TriageViewModel o) { o.AdminWarningRequested -= ShowRunHint; o.Columns.PropertyChanged -= OnColumnsChanged; o.RowScrollRequested -= ScrollToRow; }
            if (e.NewValue is ViewModels.TriageViewModel n)
            {
                n.AdminWarningRequested += ShowRunHint;
                n.Columns.PropertyChanged += OnColumnsChanged;
                n.RowScrollRequested += ScrollToRow;
                ApplyColumns();
            }
        };
        Loaded += (_, _) => ApplyColumns();
        // Ctrl+V of files copied in Explorer (a file list, not text) into the source box
        CommandManager.AddPreviewExecutedHandler(SourceBox, (_, e) =>
        {
            if (e.Command != ApplicationCommands.Paste || !Clipboard.ContainsFileDropList()) return;
            var files = Clipboard.GetFileDropList();
            if (files.Count > 0 && files[0] is { } path && DataContext is ViewModels.TriageViewModel vm) { vm.SetSourceFromShell(path); e.Handled = true; }
        });
    }

    private ViewModels.TriageViewModel? Vm => DataContext as ViewModels.TriageViewModel;

    /// <summary>Header checkbox of the planned jobs: ticks / clears every selectable row, then shows the real tri-state again.</summary>
    private void SelectAllHeader_Click(object sender, RoutedEventArgs e)
    {
        if (Vm is not { } vm || sender is not CheckBox cb) return;
        vm.ToggleSelectAll();
        cb.SetCurrentValue(System.Windows.Controls.Primitives.ToggleButton.IsCheckedProperty, vm.SelectAllState);
    }

    /// <summary>The run hint below the planned jobs carries the Administrator choice: scroll it into view when Run selected needs that choice.</summary>
    private void ShowRunHint() =>
        Dispatcher.BeginInvoke(System.Windows.Threading.DispatcherPriority.Loaded, new Action(() => RunHintBar.BringIntoView()));

    // ------------------------------------------------------------------ job lists: row menu, column picker

    /// <summary>Right-click a row: the row is selected first, so the menu acts on it.</summary>
    private void JobsGrid_RightDown(object sender, MouseButtonEventArgs e)
    {
        if (ItemsControl.ContainerFromElement(JobsGrid, (DependencyObject)e.OriginalSource) is DataGridRow row) JobsGrid.SelectedItem = row.Item;
    }

    private void JobsGrid_ContextMenuOpening(object sender, ContextMenuEventArgs e)
    {
        if (ItemsControl.ContainerFromElement(JobsGrid, (DependencyObject)e.OriginalSource) is not DataGridRow { Item: IJobListRow { HasJob: true } }) e.Handled = true;
    }

    /// <summary>A job the user picked (Status link, Retry, Spectre): selects its row and scrolls it into view; the grid never scrolls by itself.</summary>
    private void ScrollToRow(IJobListRow row) =>
        Dispatcher.BeginInvoke(new Action(() =>
        {
            JobsGrid.UpdateLayout();
            JobsGrid.ScrollIntoView(row);
        }), System.Windows.Threading.DispatcherPriority.Loaded);

    private void OnColumnsChanged(object? sender, System.ComponentModel.PropertyChangedEventArgs e) => ApplyColumns();

    private DataGridColumn? _fillColumn;
    private DataGridLength _fillColumnWidth;
    private double _fillColumnMin;

    /// <summary>
    /// Shows or hides the optional columns as the (persisted) settings say. The last visible column takes the leftover width (1*, never
    /// narrower than its own pixel width), so no blank filler column follows it; when the columns overflow the grid still scrolls.
    /// </summary>
    private void ApplyColumns()
    {
        if (Vm is not { } vm) return;
        foreach (var c in JobsGrid.Columns)
            if (c.Header is string h && Array.IndexOf(IngestColumnsViewModel.Optional, h) >= 0)
                c.Visibility = vm.Columns.IsVisible(h) ? Visibility.Visible : Visibility.Collapsed;
        if (_fillColumn != null) { _fillColumn.MinWidth = _fillColumnMin; _fillColumn.Width = _fillColumnWidth; _fillColumn = null; }
        var last = JobsGrid.Columns.LastOrDefault(c => c.Visibility == Visibility.Visible);
        if (last == null) return;
        _fillColumn = last;
        _fillColumnWidth = last.Width;
        _fillColumnMin = last.MinWidth;
        var px = last.Width.IsAbsolute ? last.Width.Value : last.ActualWidth;
        last.MinWidth = Math.Max(last.MinWidth, px);
        last.Width = new DataGridLength(1, DataGridLengthUnitType.Star);
    }

    /// <summary>Right-click on a column header: the column picker.</summary>
    private void Grid_HeaderRightUp(object sender, MouseButtonEventArgs e)
    {
        if (Vm == null || e.OriginalSource is not DependencyObject src) return;
        for (var o = src; o != null; o = o is System.Windows.Media.Visual ? System.Windows.Media.VisualTreeHelper.GetParent(o) : LogicalTreeHelper.GetParent(o))
        {
            if (o is DataGridColumnHeader h)
            {
                ShowColumnsMenu(h);
                e.Handled = true;
                return;
            }
            if (o is DataGrid) return;
        }
    }

    private void Columns_Click(object sender, RoutedEventArgs e)
    {
        if (sender is Button b) ShowColumnsMenu(b);
    }

    private void ShowColumnsMenu(FrameworkElement target)
    {
        var menu = new ContextMenu { PlacementTarget = target, Placement = PlacementMode.Bottom };
        AutomationProperties.SetAutomationId(menu, "Triage.ColumnsMenu");
        FillColumnsMenu(menu);
        menu.IsOpen = true;
    }

    /// <summary>Column picker: a check box per optional column (Tool, Artifact, Category, Progress and Status are always shown), kept open on click, saved at once; Show all / Reset below.</summary>
    private void FillColumnsMenu(ContextMenu menu)
    {
        if (Vm is not { } vm) return;
        var cols = vm.Columns;
        menu.Items.Clear();
        foreach (var col in IngestColumnsViewModel.Optional)
        {
            var c = col;
            var mi = new MenuItem { Header = new TextBlock { Text = c }, IsCheckable = true, StaysOpenOnClick = true };
            mi.SetBinding(MenuItem.IsCheckedProperty, new Binding(IngestColumnsViewModel.PropertyOf(c)) { Source = cols, Mode = BindingMode.TwoWay });
            AutomationProperties.SetAutomationId(mi, "Triage.Columns." + c.Replace(" ", ""));
            AutomationProperties.SetName(mi, c);
            mi.Checked += (_, _) => vm.SaveIngestColumns();
            mi.Unchecked += (_, _) => vm.SaveIngestColumns();
            menu.Items.Add(mi);
        }
        menu.Items.Add(new Separator());
        var all = new MenuItem { Header = new TextBlock { Text = "Show all" }, StaysOpenOnClick = true };
        AutomationProperties.SetAutomationId(all, "Triage.Columns.ShowAll");
        AutomationProperties.SetName(all, "Show all");
        all.Click += (_, _) => vm.ShowAllIngestColumns();
        menu.Items.Add(all);
        var reset = new MenuItem { Header = new TextBlock { Text = "Reset (default columns)" }, StaysOpenOnClick = true };
        AutomationProperties.SetAutomationId(reset, "Triage.Columns.Reset");
        AutomationProperties.SetName(reset, "Reset");
        reset.Click += (_, _) => vm.ResetIngestColumns();
        menu.Items.Add(reset);
    }

    // ------------------------------------------------------------------ Detected artifacts cards: click opens the detail window

    /// <summary>
    /// A click on a link, a scroll bar or a checkbox only does its own thing; anything else on the card opens the details. The checkboxes
    /// have no content (their labels are separate TextBlocks), so only the box itself toggles.
    /// </summary>
    private static bool IsOwnControl(object? source)
    {
        for (var o = source as DependencyObject; o != null;
             o = o is System.Windows.Media.Visual or System.Windows.Media.Media3D.Visual3D ? System.Windows.Media.VisualTreeHelper.GetParent(o) : LogicalTreeHelper.GetParent(o))
        {
            if (o is System.Windows.Controls.Primitives.ButtonBase or System.Windows.Controls.Primitives.ScrollBar or System.Windows.Documents.Hyperlink) return true;
        }
        return false;
    }

    private void ShowDetails(MouseButtonEventArgs e, string heading, string glyph, Func<ViewModels.TriageViewModel, IReadOnlyList<EzForensics.Core.Triage.DetailBox>> boxes)
    {
        if (e.Handled || Vm is not { } vm || IsOwnControl(e.OriginalSource)) return;
        e.Handled = true;
        IngestDetailWindow.Show(Window.GetWindow(this), heading, glyph, boxes(vm));
    }

    private void DiskCard_Click(object sender, MouseButtonEventArgs e) => ShowDetails(e, "Disk", "", vm => vm.DiskDetails());
    private void MemoryCard_Click(object sender, MouseButtonEventArgs e) => ShowDetails(e, "Memory", "", vm => vm.MemoryDetails());
    private void NetworkCard_Click(object sender, MouseButtonEventArgs e) => ShowDetails(e, "Network", "", vm => vm.NetworkDetails());

    // ------------------------------------------------------------------ drop a file or folder anywhere on the page

    private static string? DroppedPath(DragEventArgs e) =>
        e.Data.GetDataPresent(DataFormats.FileDrop) && e.Data.GetData(DataFormats.FileDrop) is string[] { Length: > 0 } paths ? paths[0] : null;

    private void Page_PreviewDragOver(object sender, DragEventArgs e)
    {
        if (DroppedPath(e) == null) return;   // text drags keep the text boxes' own behaviour
        e.Effects = Vm?.IsScanning == true ? DragDropEffects.None : DragDropEffects.Copy;
        e.Handled = true;
    }

    private void Page_PreviewDrop(object sender, DragEventArgs e)
    {
        if (DroppedPath(e) is not { } path || Vm is not { } vm || vm.IsScanning) return;
        vm.SetSourceFromShell(path);
        SourceBox.Focus();
        SourceBox.CaretIndex = SourceBox.Text.Length;
        e.Handled = true;
    }
}
