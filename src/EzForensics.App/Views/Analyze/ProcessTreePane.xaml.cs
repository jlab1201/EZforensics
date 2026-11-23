using System.ComponentModel;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Data;
using System.Windows.Input;
using System.Windows.Media.Animation;
using System.Windows.Threading;
using EzForensics.App.Services;
using EzForensics.App.ViewModels;

namespace EzForensics.App.Views.Analyze;

/// <summary>
/// Process tree pane (DataContext: <see cref="ProcessTreeViewModel"/>): floating controls (left rail with the View menu, status toast),
/// zoom buttons, detail card resize / collapse.
/// </summary>
public partial class ProcessTreePane : UserControl
{
    private double _expandedHeight = double.NaN;
    private readonly DispatcherTimer _toastTimer = new() { Interval = TimeSpan.FromSeconds(4) };
    private ProcessTreeViewModel? _vm;
    // the Navigator's size, kept for the app session
    private static double s_navWidth = 260, s_navHeight = double.NaN;

    public ProcessTreePane()
    {
        InitializeComponent();
        NavigatorPanel.Width = s_navWidth;
        NavigatorPanel.Height = s_navHeight;
        NavTree.AddHandler(MouseLeftButtonDownEvent, new MouseButtonEventHandler(NavTree_MouseLeftButtonDown), handledEventsToo: true);
        CanvasHost.SizeChanged += (_, _) => { ClampCard(); ClampNavigator(); PidChooser.MaxWidth = Math.Max(240, CanvasHost.ActualWidth * 0.6); };
        NavigatorPanel.SizeChanged += (_, _) => UpdateFitInsets();
        DetailCard.SizeChanged += (_, _) => UpdateFitInsets();
        _toastTimer.Tick += (_, _) => { _toastTimer.Stop(); if (_vm is not { IsLoading: true }) HideToast(); };
        DataContextChanged += (_, _) => Attach(DataContext as ProcessTreeViewModel);
        Unloaded += (_, _) => _toastTimer.Stop();    }

    /// <summary>Tells the canvas how much width the open Navigator / Details card cover, and refits when "Fit to screen" is on.</summary>
    private void UpdateFitInsets()
    {
        double left = NavigatorHost.Visibility == Visibility.Visible ? NavigatorPanel.ActualWidth + 12 : 0;
        double right = DetailHost.Visibility == Visibility.Visible ? DetailCard.ActualWidth + 20 : 0;
        if (Math.Abs(left - Canvas.FitLeftInset) < 0.5 && Math.Abs(right - Canvas.FitRightInset) < 0.5) return;
        Canvas.FitLeftInset = left;
        Canvas.FitRightInset = right;
        if (Canvas.FitToScreen) Canvas.Fit();
    }

    private ProcessTreeViewModel? Vm => DataContext as ProcessTreeViewModel;

    private void Attach(ProcessTreeViewModel? vm)
    {
        if (_vm != null) _vm.PropertyChanged -= Vm_PropertyChanged;
        _vm = vm;
        if (vm != null) vm.PropertyChanged += Vm_PropertyChanged;
        if (vm != null && (vm.IsLoading || !string.IsNullOrEmpty(vm.StatusText))) ShowToast();
        else HideToast();
    }

    private void Vm_PropertyChanged(object? sender, PropertyChangedEventArgs e)
    {
        switch (e.PropertyName)
        {
            case nameof(ProcessTreeViewModel.IsNavigatorOpen) or nameof(ProcessTreeViewModel.Detail):
                Dispatcher.BeginInvoke(DispatcherPriority.Loaded, new Action(UpdateFitInsets));
                break;
            case nameof(ProcessTreeViewModel.StatusText) or nameof(ProcessTreeViewModel.IsLoading):
                if (_vm is { } vm && (vm.IsLoading || !string.IsNullOrEmpty(vm.StatusText))) ShowToast();
                else HideToast();
                break;
        }
    }

    // ---- status toast: visible while loading, and for 4 s after the status text changes

    private void ShowToast()
    {
        Toast.BeginAnimation(OpacityProperty, null);
        Toast.Opacity = 1;
        _toastTimer.Stop();
        if (_vm is not { IsLoading: true }) _toastTimer.Start();
    }

    private void HideToast()
    {
        _toastTimer.Stop();
        if (Toast.Opacity <= 0) return;
        Toast.BeginAnimation(OpacityProperty, new DoubleAnimation(0, TimeSpan.FromMilliseconds(400)) { FillBehavior = FillBehavior.HoldEnd });
    }

    /// <summary>A View item was clicked: its command runs, then the popover closes.</summary>
    private void ViewItem_Click(object sender, RoutedEventArgs e) => ViewPopup.IsOpen = false;

    private void NavigatorClose_Click(object sender, RoutedEventArgs e)
    {
        if (Vm is { } vm) vm.IsNavigatorOpen = false;
    }

    /// <summary>
    /// A click on a Navigator row (only the innermost row under the pointer, never the chevron) runs the row's action once. Registered on the TreeView itself with
    /// handledEventsToo (constructor): with real mouse input the input system re-raises MouseLeftButtonDown on each element of the MouseDown route after the
    /// TreeViewItem class handler has already marked it handled, and a Style EventSetter (even HandledEventsToo) on the TreeViewItem was then never called, so rows
    /// did nothing. The TreeView sees the press exactly once; the row is the first TreeViewItem up from the OriginalSource. The second press of a double-click is
    /// ignored (a group would select and clear). ProcessTreePaneSmokeTests.Navigator_rows_respond_to_a_real_click_in_a_shown_window drives real raw mouse input.
    /// </summary>
    private void NavTree_MouseLeftButtonDown(object sender, MouseButtonEventArgs e)
    {
        if (e.ClickCount > 1 || e.OriginalSource is not DependencyObject src) return;
        for (DependencyObject? d = src; d != null && !ReferenceEquals(d, NavTree); d = d is System.Windows.Media.Visual ? (System.Windows.Media.VisualTreeHelper.GetParent(d) ?? LogicalTreeHelper.GetParent(d)) : LogicalTreeHelper.GetParent(d))
        {
            if (d is System.Windows.Controls.Primitives.ButtonBase) return;   // the chevron, "Flag all"
            if (d is TreeViewItem item)
            {
                if (!item.IsEnabled || item.DataContext is not NavigatorTreeItem row) return;
                string kind = row switch { NavigatorSectionViewModel => "section", NavigatorGroupViewModel => "group", NavigatorMemberViewModel => "member", _ => "row" };
                string extra = row is NavigatorMemberViewModel m ? $" (PID {m.Node.Pid})" : "";
                AppLog.Info($"Process tree: Navigator {kind} '{row.DisplayName}'{extra} clicked");
                row.Activate();
                return;
            }
        }
    }

    private void ZoomIn_Click(object sender, RoutedEventArgs e) => Canvas.ZoomBy(1.2);
    private void ZoomOut_Click(object sender, RoutedEventArgs e) => Canvas.ZoomBy(1 / 1.2);

    // ---- detail card: collapse to the title bar, resize from the left edge / bottom / bottom-left corner

    private bool Collapsed => CollapseToggle.IsChecked == true;

    private void CollapseToggle_Changed(object sender, RoutedEventArgs e)
    {
        if (Collapsed)
        {
            _expandedHeight = DetailCard.Height;
            DetailBody.Visibility = Visibility.Collapsed;
            DetailCard.Height = double.NaN;
            CollapseGlyph.Text = "";
        }
        else
        {
            DetailBody.Visibility = Visibility.Visible;
            DetailCard.Height = _expandedHeight; // NaN: sized to its content, up to MaxHeight
            CollapseGlyph.Text = "";
            ClampCard();
        }
    }

    private void ResizeLeft_DragDelta(object sender, DragDeltaEventArgs e) => ResizeBy(-e.HorizontalChange, 0);
    private void ResizeBottom_DragDelta(object sender, DragDeltaEventArgs e) => ResizeBy(0, e.VerticalChange);
    private void ResizeCorner_DragDelta(object sender, DragDeltaEventArgs e) => ResizeBy(-e.HorizontalChange, e.VerticalChange);

    private void ResizeBy(double dw, double dh)
    {
        DetailCard.Width = Math.Max(DetailCard.MinWidth, DetailCard.Width + dw);
        if (!Collapsed && dh != 0)
        {
            double h = double.IsNaN(DetailCard.Height) ? DetailCard.ActualHeight : DetailCard.Height;
            DetailCard.Height = Math.Max(160, h + dh);
        }
        ClampCard();
    }

    private void NavResizeRight_DragDelta(object sender, DragDeltaEventArgs e) => NavigatorResizeBy(e.HorizontalChange, 0);
    private void NavResizeBottom_DragDelta(object sender, DragDeltaEventArgs e) => NavigatorResizeBy(0, e.VerticalChange);
    private void NavResizeCorner_DragDelta(object sender, DragDeltaEventArgs e) => NavigatorResizeBy(e.HorizontalChange, e.VerticalChange);

    private void NavigatorResizeBy(double dw, double dh)
    {
        NavigatorPanel.Width = Math.Max(NavigatorPanel.MinWidth, NavigatorPanel.Width + dw);
        if (dh != 0)
        {
            double h = double.IsNaN(NavigatorPanel.Height) ? NavigatorPanel.ActualHeight : NavigatorPanel.Height;
            NavigatorPanel.Height = Math.Max(160, h + dh);
        }
        ClampNavigator();
    }

    /// <summary>Keeps the Navigator inside the canvas (width leaves room on the right, height fits the row under the chips) and remembers its size.</summary>
    private void ClampNavigator()
    {
        double maxW = Math.Max(NavigatorPanel.MinWidth, CanvasHost.ActualWidth - 140);
        if (NavigatorPanel.Width > maxW) NavigatorPanel.Width = maxW;
        if (NavigatorHost.Parent is Grid g && g.RowDefinitions.Count > 2 && g.RowDefinitions[2].ActualHeight > 0)
        {
            double maxH = Math.Max(160, g.RowDefinitions[2].ActualHeight - NavigatorHost.Margin.Top);
            NavigatorPanel.MaxHeight = maxH;
            if (!double.IsNaN(NavigatorPanel.Height) && NavigatorPanel.Height > maxH) NavigatorPanel.Height = maxH;
        }
        s_navWidth = NavigatorPanel.Width;
        s_navHeight = NavigatorPanel.Height;
    }

    /// <summary>Keeps the card inside the canvas: MaxHeight follows the pane (height minus 32), the width leaves room on the left (host chip / Navigator).</summary>
    private void ClampCard()
    {
        double maxW = Math.Max(DetailCard.MinWidth, CanvasHost.ActualWidth - 120);
        double maxH = Math.Max(DetailCard.MinHeight, CanvasHost.ActualHeight - 32);
        DetailCard.MaxHeight = maxH;
        if (DetailCard.Width > maxW) DetailCard.Width = maxW;
        if (!Collapsed && !double.IsNaN(DetailCard.Height) && DetailCard.Height > maxH) DetailCard.Height = maxH;
    }
}

/// <summary>
/// Attached <c>Spec</c> (<see cref="ProcessGridSpec"/>): builds the Tier chip column (when some row is a link), the Source chip column, then one text column per header
/// bound to <see cref="EzForensics.Core.Processes.ProcessActivityRow.Cells"/> by index (headers may contain any character). Chip templates: <c>TierChipTemplate</c>,
/// <c>SourceChipTemplate</c> from the pane's resources.
/// </summary>
public static class FacetGridColumns
{
    /// <summary>Fills the row gutter with the 1-based position of each row (the detail rows carry no CSV row index).</summary>
    public static readonly DependencyProperty RowNumbersProperty = DependencyProperty.RegisterAttached("RowNumbers", typeof(bool), typeof(FacetGridColumns),
        new PropertyMetadata(false, (d, e) =>
        {
            if (d is not DataGrid g) return;
            g.LoadingRow -= NumberRow;
            if (e.NewValue is true) g.LoadingRow += NumberRow;
        }));
    public static bool GetRowNumbers(DependencyObject d) => (bool)d.GetValue(RowNumbersProperty);
    public static void SetRowNumbers(DependencyObject d, bool value) => d.SetValue(RowNumbersProperty, value);
    private static void NumberRow(object? sender, DataGridRowEventArgs e) => e.Row.Header = (e.Row.GetIndex() + 1).ToString();

    public static readonly DependencyProperty SpecProperty = DependencyProperty.RegisterAttached("Spec", typeof(ProcessGridSpec), typeof(FacetGridColumns),
        new PropertyMetadata(null, OnSpecChanged));

    public static ProcessGridSpec? GetSpec(DependencyObject d) => (ProcessGridSpec?)d.GetValue(SpecProperty);
    public static void SetSpec(DependencyObject d, ProcessGridSpec? value) => d.SetValue(SpecProperty, value);

    private static void OnSpecChanged(DependencyObject d, DependencyPropertyChangedEventArgs e)
    {
        if (d is not DataGrid grid) return;
        grid.Columns.Clear();
        if (e.NewValue is not ProcessGridSpec spec) return;
        // the chip templates are pane resources: a grid still being built from its DataTemplate cannot reach them yet
        if (!grid.IsLoaded)
        {
            void OnLoaded(object? sender, RoutedEventArgs args)
            {
                grid.Loaded -= OnLoaded;
                if (ReferenceEquals(GetSpec(grid), spec)) Build(grid, spec);
            }
            grid.Loaded += OnLoaded;
            return;
        }
        Build(grid, spec);
    }

    private static void Build(DataGrid grid, ProcessGridSpec spec)
    {
        grid.Columns.Clear();
        if (spec.ShowTier && grid.TryFindResource("TierChipTemplate") is DataTemplate tier)
            grid.Columns.Add(new DataGridTemplateColumn { Header = "Tier", CellTemplate = tier, Width = new DataGridLength(1, DataGridLengthUnitType.SizeToCells), MinWidth = 64, SortMemberPath = "TierText", CanUserSort = true });
        if (grid.TryFindResource("SourceChipTemplate") is DataTemplate source)
            grid.Columns.Add(new DataGridTemplateColumn { Header = "Source", CellTemplate = source, Width = new DataGridLength(1, DataGridLengthUnitType.SizeToCells), MinWidth = 72, SortMemberPath = "Source", CanUserSort = true });
        for (int i = 0; i < spec.Headers.Count; i++)
        {
            var binding = new Binding($"Cells[{i}]") { Mode = BindingMode.OneWay };
            var col = new DataGridTextColumn { Header = spec.Headers[i], Binding = binding, MaxWidth = 520 };
            if (EzForensics.Core.Processes.PidLinks.IsPidColumn(spec.Headers[i]))
            {
                // PID cells select that process in the same tree (PidLinkCells; the click is tracked once per grid)
                col.ClipboardContentBinding = binding;
                col.Binding = null;
                col.ElementStyle = PidLinkCells.ColumnStyle(binding, null);
            }
            grid.Columns.Add(col);
        }
        if (GetPidTracked(grid)) return;
        SetPidTracked(grid, true);
        PidLinkCells.Track(grid, (row, pid) =>
        {
            if (FindTree(grid) is not { } tree) return;
            tree.SelectPidFromCard(pid, row as EzForensics.Core.Processes.ProcessActivityRow, GetSpec(grid)?.Headers);
        });
    }

    private static readonly DependencyProperty PidTrackedProperty = DependencyProperty.RegisterAttached("PidTracked", typeof(bool), typeof(FacetGridColumns), new PropertyMetadata(false));
    private static bool GetPidTracked(DependencyObject d) => (bool)d.GetValue(PidTrackedProperty);
    private static void SetPidTracked(DependencyObject d, bool value) => d.SetValue(PidTrackedProperty, value);

    /// <summary>The pane's tree view model above a card grid.</summary>
    private static ProcessTreeViewModel? FindTree(DependencyObject d) => ViewHelpers.FindAncestor<ProcessTreePane>(d)?.DataContext as ProcessTreeViewModel;
}
