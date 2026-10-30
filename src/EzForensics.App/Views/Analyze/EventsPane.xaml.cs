using System.ComponentModel;
using System.Globalization;
using System.Text;
using System.Windows;
using System.Windows.Automation;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Data;
using System.Windows.Input;
using System.Windows.Media;
using EzForensics.App.Services;
using EzForensics.App.ViewModels;
using EzForensics.Core.Results;
using EzForensics.Core.Settings;
using static EzForensics.App.Views.Analyze.ViewHelpers;

namespace EzForensics.App.Views.Analyze;

/// <summary>Right-hand pane: file name + actions, filter bar, status bar, filter panel, advisory slot and the events grid (DataContext: ResultsViewModel).</summary>
public partial class EventsPane : UserControl, IGridColumnBridge
{
    private const int MenuValueMax = 40;
    private ResultsViewModel? _vm;
    private Popup? _funnelPopup;
    private CellViewerWindow? _viewer;
    // cell the context menu was opened on
    private RowItem? _menuRow;
    private int _menuCol = -1;

    public EventsPane()
    {
        InitializeComponent();
        PreviewKeyDown += Pane_PreviewKeyDown;
        DataContextChanged += (_, e) =>
        {
            Detach();
            _vm = e.NewValue as ResultsViewModel;
            Attach();
            BuildColumns();
        };
        Unloaded += (_, _) => { Detach(); AppServices.Current.FocusModeChanged -= OnFocusMode; };
        Loaded += (_, _) =>
        {
            Detach();
            Attach();
            BuildColumns();
            AppServices.Current.FocusModeChanged -= OnFocusMode;
            AppServices.Current.FocusModeChanged += OnFocusMode;
            OnFocusMode(AppServices.Current.IsFocusMode);
        };
        // a column resize ends with a mouse-up somewhere over the grid (the header thumb captures the mouse)
        Grid.AddHandler(ScrollViewer.ScrollChangedEvent, new ScrollChangedEventHandler((_, e) =>
        {
            if (e.OriginalSource is ScrollViewer sv && ReferenceEquals(sv.TemplatedParent, Grid) && e.VerticalChange != 0) ReportView();
        }));
        Grid.SelectedCellsChanged += (_, _) => ReportView();
        Grid.ColumnReordered += (_, _) => ReportView();
        Grid.AddHandler(PreviewMouseLeftButtonUpEvent, new MouseButtonEventHandler((_, _) =>
            Dispatcher.BeginInvoke(new Action(SaveLayoutIfChanged), System.Windows.Threading.DispatcherPriority.Background)), true);
        // a plain click on a PID link (PID column cell or "NewProcessId: 0x…" token) selects that process in the host's process tree
        PidLinkCells.Track(Grid, (row, pid) =>
        {
            if (_vm is { CurrentFile: { } csv } vm && row is RowItem r) _ = vm.OpenProcessAtAsync(csv, r.Source, pid);
        });
    }

    // ------------------------------------------------------------ focus mode

    private void OnFocusMode(bool on)
    {
        FocusButton.IsChecked = on;
        FocusGlyph.Text = on ? "" : ""; // BackToWindow / FullScreen
        FocusButton.ToolTip = on ? "Leave focus mode (F11 or Esc)" : "Focus mode (F11)";
    }

    private void Search_PreviewKeyDown(object sender, KeyEventArgs e)
    {
        if (e.Key != Key.Escape || _vm == null || !_vm.HasAnyFilter) return;
        _vm.ClearFiltersCommand.Execute(null);
        e.Handled = true;
    }

    private void FocusButton_Click(object sender, RoutedEventArgs e) =>
        AppServices.Current.SetFocusMode(FocusButton.IsChecked == true);

    private void Attach()
    {
        if (_vm == null) return;
        _vm.ColumnsChanged += BuildColumns;
        _vm.FlagsChanged += RefreshRealisedFlags;
        _vm.PropertyChanged += OnVmPropertyChanged;
        _vm.SessionViewRestoreRequested += OnSessionViewRestore;
        _vm.PivotReflattened += OnPivotReflattened;
        _vm.GroupColumns.CollectionChanged += OnGroupColumnsChanged;
        _vm.GridBridge = this;
        SyncFlagModeChecks();
    }

    private void Detach()
    {
        if (_vm == null) return;
        _vm.ColumnsChanged -= BuildColumns;
        _vm.FlagsChanged -= RefreshRealisedFlags;
        _vm.PropertyChanged -= OnVmPropertyChanged;
        _vm.SessionViewRestoreRequested -= OnSessionViewRestore;
        _vm.PivotReflattened -= OnPivotReflattened;
        _vm.GroupColumns.CollectionChanged -= OnGroupColumnsChanged;
        if (ReferenceEquals(_vm.GridBridge, this)) _vm.GridBridge = null;
    }

    // ------------------------------------------------------------ session (column order, selected row, scroll position)

    private bool _restoringView;

    /// <summary>Tells the view model where the grid is (it saves it with the file's other view state).</summary>
    private void ReportView()
    {
        if (_vm == null || _restoringView || DataColumnCount == 0 || _vm.Headers.Count != DataColumnCount) return;
        int n = DataColumnCount;
        var order = Enumerable.Range(0, n).OrderBy(i => DataCol(i).DisplayIndex).ToArray();
        if (order.SequenceEqual(Enumerable.Range(0, n))) order = Array.Empty<int>();
        var row = Grid.CurrentCell.Item as RowItem;
        var sv = FindVisualChildren<ScrollViewer>(Grid).FirstOrDefault(x => ReferenceEquals(x.TemplatedParent, Grid));
        _vm.NoteViewState(order, row?.Position ?? -1, row?.Source ?? -1, sv == null ? 0 : (int)sv.VerticalOffset);
    }

    private void OnSessionViewRestore(AnalyzeFileState st) =>
        Dispatcher.BeginInvoke(new Action(() => ApplySessionView(st)), System.Windows.Threading.DispatcherPriority.Loaded);

    private void ApplySessionView(AnalyzeFileState st)
    {
        var vm = _vm;
        if (vm == null) return;
        _restoringView = true;
        try
        {
            int n = DataColumnCount;
            if (vm.Headers.Count != n || n == 0) return;
            var order = st.ColumnOrder;
            if (order.Count == n && order.OrderBy(x => x).SequenceEqual(Enumerable.Range(0, n)))
                for (int k = 0; k < n; k++) DataCol(order[k]).DisplayIndex = k + 1; // column 0 is the frozen flag column
            var rows = vm.Rows;
            RowItem? item = null;
            if (rows != null && st.SelectedPosition >= 0 && st.SelectedPosition < rows.Count
                && rows[st.SelectedPosition] is RowItem it && it.Source == st.SelectedSource) item = it;
            var vis = VisibleDataColumns();
            if (item != null && vis.Length > 0)
            {
                var info = new DataGridCellInfo(item, DataCol(vis[0]));
                Grid.SelectedCells.Clear();
                Grid.CurrentCell = info;
                Grid.SelectedCells.Add(info);
            }
            Grid.UpdateLayout();
            var sv = FindVisualChildren<ScrollViewer>(Grid).FirstOrDefault(x => ReferenceEquals(x.TemplatedParent, Grid));
            if (sv != null && st.ScrollOffset > 0) { sv.UpdateLayout(); sv.ScrollToVerticalOffset(Math.Min(st.ScrollOffset, sv.ScrollableHeight)); }
        }
        catch (Exception ex) { AppLog.Warn("Analyze session: could not restore the grid position: " + ex.Message); }
        finally
        {
            _restoringView = false;
            vm.ViewRestoreDone();
            ReportView();
        }
    }

    private void OnVmPropertyChanged(object? sender, PropertyChangedEventArgs e)
    {
        if (e.PropertyName is nameof(ResultsViewModel.FlagFilterText) or nameof(ResultsViewModel.FlagFilter)) SyncFlagModeChecks();
        else if (e.PropertyName == nameof(ResultsViewModel.HasSearchText) && _vm?.HasSearchText == true) SearchHelpPopup.IsOpen = false;
        else if (e.PropertyName == nameof(ResultsViewModel.PivotRowIndent)) UpdateFlagColumnWidth();
    }

    /// <summary>Frozen flag column, then one text column per CSV header (bound to the row indexer [0], [1], …) with a filter-row header.</summary>
    private void BuildColumns()
    {
        Grid.Columns.Clear();
        _pivotHidden.Clear();
        if (_vm == null) return;
        var headers = _vm.Headers;
        if (headers.Count == 0) return;

        Grid.Columns.Add(new DataGridTemplateColumn
        {
            Header = _vm,
            HeaderTemplate = (DataTemplate)FindResource("FlagHeaderTemplate"),
            HeaderStyle = (Style)FindResource("StretchHeader"),
            CellTemplate = (DataTemplate)FindResource("FlagCellTemplate"),
            Width = new DataGridLength(40),
            CanUserSort = false,
            CanUserResize = false,
            CanUserReorder = false,
        });

        var cellStyle = new Style(typeof(TextBlock));
        cellStyle.Setters.Add(new Setter(TextBlock.TextTrimmingProperty, TextTrimming.CharacterEllipsis));
        var headerTemplate = (DataTemplate)FindResource("ColumnHeaderTemplate");
        var headerStyle = (Style)FindResource("StretchHeader");
        // PID links only when the file's PIDs open a host's process tree (not the super timeline or _Case); other files keep the plain Text binding
        bool links = _vm.PidLinkHost != null;
        for (int i = 0; i < headers.Count; i++)
        {
            var binding = new Binding($"[{i}]") { Mode = BindingMode.OneTime };
            var col = new DataGridTextColumn
            {
                Header = i < _vm.Cells.Count ? _vm.Cells[i] : headers[i],
                HeaderTemplate = headerTemplate,
                HeaderStyle = headerStyle,
                Binding = binding,
                SortMemberPath = headers[i],
                Width = new DataGridLength(DefaultWidth(headers[i])),
                MinWidth = ColumnLayoutStore.MinWidth,
                ElementStyle = cellStyle,
            };
            if (links)
            {
                // the text comes through the attached property (link look / token inlines); Ctrl+C still copies the cell value
                col.ClipboardContentBinding = binding;
                col.Binding = null;
                if (Core.Processes.PidLinks.IsPidColumn(headers[i])) col.ElementStyle = PidLinkCells.ColumnStyle(binding, cellStyle);
                else if (Core.Processes.PidLinks.IsPidTokenColumn(headers[i])) col.ElementStyle = PidLinkCells.TokenStyle(binding, cellStyle);
                else col.Binding = binding;   // other text columns stay plain (no token scan)
            }
            if (_vm.SortColumn == headers[i])
                col.SortDirection = _vm.SortDescending ? ListSortDirection.Descending : ListSortDirection.Ascending;
            Grid.Columns.Add(col);
        }
        UpdateFlagColumnWidth();
        LoadLayout(headers);
        SyncPivotColumns();
    }

    private static double DefaultWidth(string header) => Math.Clamp(header.Length * 9 + 60, 120, 280);

    // ------------------------------------------------------------ saved views bridge

    public List<SavedViewColumn> CaptureColumns()
    {
        var list = new List<SavedViewColumn>();
        if (_vm == null || DataColumnCount == 0 || _vm.Headers.Count != DataColumnCount) return list;
        foreach (var i in Enumerable.Range(0, DataColumnCount).OrderBy(i => DataCol(i).DisplayIndex))
        {
            var c = DataCol(i);
            double w = c.Width.IsAbsolute ? c.Width.Value : c.ActualWidth;
            list.Add(new SavedViewColumn
            {
                Name = _vm.Headers[i],
                Visible = !IsHidden(i),
                Width = Math.Abs(w - DefaultWidth(_vm.Headers[i])) > 0.5 ? Math.Round(ColumnLayoutStore.ClampWidth(w), 1) : null,
            });
        }
        return list;
    }

    public void ApplyColumns(IReadOnlyList<SavedViewColumn> columns)
    {
        if (_vm == null || DataColumnCount == 0 || _vm.Headers.Count != DataColumnCount) return;
        int n = DataColumnCount;
        var placed = new List<int>();
        foreach (var sc in columns)
        {
            int i = _vm.Headers.ToList().FindIndex(h => string.Equals(h, sc.Name, StringComparison.OrdinalIgnoreCase));
            if (i < 0 || placed.Contains(i)) continue;
            placed.Add(i);
            SetUserHidden(i, !sc.Visible);
            DataCol(i).Width = new DataGridLength(sc.Width is { } w ? ColumnLayoutStore.ClampWidth(w) : DefaultWidth(_vm.Headers[i]));
        }
        if (n > 0 && Enumerable.Range(0, n).All(IsHidden)) SetUserHidden(placed.Count > 0 ? placed[0] : 0, false); // keep one column
        var rest = Enumerable.Range(0, n).Where(i => !placed.Contains(i)).OrderBy(i => DataCol(i).DisplayIndex);
        int k = 1; // column 0 is the frozen flag column
        _restoringView = true;
        try { foreach (var i in placed.Concat(rest)) DataCol(i).DisplayIndex = k++; }
        finally { _restoringView = false; }
        Grid.UpdateLayout();
        SaveLayoutIfChanged();
        ReportView();
    }

    // ------------------------------------------------------------ column layout (hidden columns, widths)

    private string _sig = "";
    private ColumnLayout _layout = new();

    private int DataColumnCount => Math.Max(0, Grid.Columns.Count - 1);
    private DataGridColumn DataCol(int i) => Grid.Columns[i + 1];
    private readonly Dictionary<int, bool> _pivotHidden = new(); // grouped data column -> was it hidden by the user before grouping

    /// <summary>Hidden by the user (a grouped column counts as shown unless it was hidden before it was grouped).</summary>
    private bool IsHidden(int i) => _pivotHidden.TryGetValue(i, out var userHidden) ? userHidden : DataCol(i).Visibility != Visibility.Visible;

    /// <summary>Sets the user's visibility; a grouped column stays collapsed and only remembers the choice.</summary>
    private void SetUserHidden(int i, bool hidden)
    {
        if (_pivotHidden.ContainsKey(i)) _pivotHidden[i] = hidden;
        else DataCol(i).Visibility = hidden ? Visibility.Collapsed : Visibility.Visible;
    }

    private void OnGroupColumnsChanged(object? sender, System.Collections.Specialized.NotifyCollectionChangedEventArgs e) => SyncPivotColumns();

    /// <summary>Grouped columns leave the grid; ungrouping restores exactly the visibility they had (position and width are untouched).</summary>
    private void SyncPivotColumns()
    {
        if (_vm == null || DataColumnCount == 0 || _vm.Headers.Count != DataColumnCount) return;
        var headers = _vm.Headers.ToList();
        var want = new HashSet<int>();
        foreach (var g in _vm.GroupColumns)
        {
            int i = headers.FindIndex(h => string.Equals(h, g.Column, StringComparison.OrdinalIgnoreCase));
            if (i >= 0) want.Add(i);
        }
        foreach (var (i, userHidden) in _pivotHidden.ToList())
        {
            if (want.Contains(i)) continue;
            DataCol(i).Visibility = userHidden ? Visibility.Collapsed : Visibility.Visible;
            _pivotHidden.Remove(i);
        }
        foreach (var i in want)
        {
            if (_pivotHidden.ContainsKey(i)) continue;
            _pivotHidden[i] = DataCol(i).Visibility != Visibility.Visible;
            DataCol(i).Visibility = Visibility.Collapsed;
        }
    }

    /// <summary>CSV column indices of the visible data columns, in display order.</summary>
    private int[] VisibleDataColumns() =>
        Enumerable.Range(0, DataColumnCount).Where(i => DataCol(i).Visibility == Visibility.Visible).OrderBy(i => DataCol(i).DisplayIndex).ToArray();

    private void LoadLayout(IReadOnlyList<string> headers)
    {
        _sig = ColumnLayoutStore.Signature(headers);
        _layout = AppServices.Current.Settings.ColumnLayouts.TryGetValue(_sig, out var l) ? l : new ColumnLayout();
        int n = DataColumnCount;
        foreach (var i in _layout.Hidden) if ((uint)i < (uint)n) DataCol(i).Visibility = Visibility.Collapsed;
        if (n > 0 && VisibleDataColumns().Length == 0) foreach (var i in Enumerable.Range(0, n)) DataCol(i).Visibility = Visibility.Visible;
        foreach (var (i, w) in _layout.Widths) if ((uint)i < (uint)n) DataCol(i).Width = new DataGridLength(ColumnLayoutStore.ClampWidth(w));
    }

    private ColumnLayout CurrentLayout()
    {
        var l = new ColumnLayout();
        for (int i = 0; i < DataColumnCount; i++)
        {
            var c = DataCol(i);
            if (IsHidden(i)) l.Hidden.Add(i);
            double w = c.Width.IsAbsolute ? c.Width.Value : c.ActualWidth;
            if (Math.Abs(w - DefaultWidth(_vm!.Headers[i])) > 0.5) l.Widths[i] = Math.Round(ColumnLayoutStore.ClampWidth(w), 1);
        }
        return l;
    }

    private static bool Same(ColumnLayout a, ColumnLayout b) =>
        a.Hidden.SequenceEqual(b.Hidden) && a.Widths.Count == b.Widths.Count &&
        a.Widths.All(kv => b.Widths.TryGetValue(kv.Key, out var w) && Math.Abs(w - kv.Value) < 0.5);

    private void SaveLayoutIfChanged()
    {
        if (_vm == null || DataColumnCount == 0 || _vm.Headers.Count != DataColumnCount) return;
        var cur = CurrentLayout();
        if (Same(cur, _layout)) return;
        _layout = cur;
        var s = AppServices.Current;
        ColumnLayoutStore.Put(s.Settings.ColumnLayouts, _sig, cur);
        try { s.SaveSettings(); } catch (Exception ex) { AppLog.Error("Could not save column layout", ex); }
    }

    private void SetColumnVisible(int i, bool visible)
    {
        if ((uint)i >= (uint)DataColumnCount) return;
        if (!visible && VisibleDataColumns().Length <= 1 && !IsHidden(i) && !_pivotHidden.ContainsKey(i)) return; // keep at least one data column
        SetUserHidden(i, !visible);
    }

    private void ShowAllColumns()
    {
        for (int i = 0; i < DataColumnCount; i++) SetUserHidden(i, false);
        SaveLayoutIfChanged();
    }

    private void ResetColumnWidths()
    {
        if (_vm == null) return;
        for (int i = 0; i < DataColumnCount; i++) DataCol(i).Width = new DataGridLength(DefaultWidth(_vm.Headers[i]));
        SaveLayoutIfChanged();
    }

    /// <summary>Width that fits the header and the first ~200 rows (FormattedText with the grid font), clamped to 40..800.</summary>
    private double MeasureBestFit(int i)
    {
        if (_vm == null) return 120;
        var face = new Typeface(Grid.FontFamily, Grid.FontStyle, Grid.FontWeight, Grid.FontStretch);
        double dpi = VisualTreeHelper.GetDpi(Grid).PixelsPerDip;
        double Measure(string t) => new FormattedText(t, CultureInfo.CurrentCulture, FlowDirection.LeftToRight, face, Grid.FontSize, Brushes.Black, dpi).WidthIncludingTrailingWhitespace;
        double cells = 0;
        if (_vm.Rows is { } rows)
        {
            int n = Math.Min(rows.Count, 200);
            for (int r = 0; r < n; r++)
            {
                if (rows[r] is not RowItem row) continue;
                var v = row[i];
                if (v.Length == 0) continue;
                if (v.Length > 400) v = v[..400];
                v = v.Replace('\r', ' ').Replace('\n', ' ').Replace('\t', ' ');
                cells = Math.Max(cells, Measure(v));
            }
        }
        double header = Math.Max(Measure(_vm.Headers[i]) + 28, 60);
        return Math.Round(ColumnLayoutStore.ClampWidth(Math.Max(cells + 18, header)));
    }

    private void BestFit(int i) { if ((uint)i < (uint)DataColumnCount) DataCol(i).Width = new DataGridLength(MeasureBestFit(i)); SaveLayoutIfChanged(); }

    private void BestFitAll()
    {
        for (int i = 0; i < DataColumnCount; i++) if (!IsHidden(i)) DataCol(i).Width = new DataGridLength(MeasureBestFit(i));
        SaveLayoutIfChanged();
    }

    /// <summary>Header right-click menu.</summary>
    private void BuildHeaderMenu(ContextMenu menu, DataGridColumnHeader header)
    {
        int col = DataIndex(header.Column);
        string name = HeaderName(col);
        bool anyHidden = Enumerable.Range(0, DataColumnCount).Any(IsHidden);
        bool canHide = col >= 0 && !IsHidden(col) && VisibleDataColumns().Length > 1;
        MenuUi.Group(menu.Items, "Columns");
        menu.Items.Add(Item("Header.Hide", col >= 0 ? $"Hide this column  [{Shorten(name)}]" : "Hide this column (the flag column cannot be hidden)", () => { SetColumnVisible(col, false); SaveLayoutIfChanged(); }, canHide, MenuUi.Hide, "Accent"));
        menu.Items.Add(Item("Header.ShowAll", "Show all columns", ShowAllColumns, anyHidden, MenuUi.View, "Accent"));
        menu.Items.Add(Item("Header.Columns", "Columns…", () => Dispatcher.BeginInvoke(new Action(() => OpenColumnsPopup(header)), System.Windows.Threading.DispatcherPriority.Input), true, MenuUi.Columns, "Accent"));
        MenuUi.Group(menu.Items, "Width");
        menu.Items.Add(Item("Header.BestFit", "Best fit this column", () => BestFit(col), col >= 0, MenuUi.Fit, "Info"));
        menu.Items.Add(Item("Header.BestFitAll", "Best fit all columns", BestFitAll, true, MenuUi.Fit, "Info"));
        menu.Items.Add(Item("Header.ResetWidths", "Reset column widths", ResetColumnWidths, true, MenuUi.Reset, "Info"));
        MenuUi.Group(menu.Items, "Group");
        menu.Items.Add(GroupByItem(col));
        if (UngroupItem(col) is { } ungroup) menu.Items.Add(ungroup);
        var vm = _vm!;
        MenuUi.Group(menu.Items, "View");
        menu.Items.Add(HeaderItem("SaveView", "Save view", () => vm.SaveView(), vm.CanSaveView, "", "Success"));
        menu.Items.Add(HeaderItem("SaveViewAs", "Save view as…", () => SavedViewUi.SaveAs(vm, this), vm.CanSaveView, "", "Success"));
        menu.Items.Add(HeaderItem("ManageViews", "Manage views…", () => SavedViewUi.Edit(vm, this, savedTab: true), vm.CanSaveView, "", ""));
        MenuUi.Group(menu.Items, "Clear");
        menu.Items.Add(HeaderItem("ClearView", "Clear view", vm.ClearView, vm.CanClearView, MenuUi.Clear, "Error"));
    }

    private static MenuItem HeaderItem(string id, string header, Action action, bool enabled, string glyph, string kind)
    {
        var mi = Item("Header." + id, header, action, enabled, glyph, kind);
        AutomationProperties.SetAutomationId(mi, "Results.Header.Menu." + id);
        return mi;
    }

    private Popup? _columnsPopup;

    /// <summary>Popup with a check box per CSV column plus All / None.</summary>
    private void OpenColumnsPopup(FrameworkElement target)
    {
        if (_vm == null || DataColumnCount == 0) return;
        if (_columnsPopup != null) _columnsPopup.IsOpen = false;
        var list = new StackPanel();
        var boxes = new List<CheckBox>();
        bool syncing = false;
        for (int k = 0; k < DataColumnCount; k++)
        {
            int idx = k;
            var cb = new CheckBox { Content = _vm.Headers[k], IsChecked = !IsHidden(k), Margin = new Thickness(0, 2, 0, 2), ToolTip = _vm.Headers[k] };
            AutomationProperties.SetAutomationId(cb, "Results.Columns.Item." + k);
            AutomationProperties.SetName(cb, _vm.Headers[k]);
            RoutedEventHandler changed = (_, _) =>
            {
                if (syncing) return;
                SetColumnVisible(idx, cb.IsChecked == true);
                syncing = true;
                try { cb.IsChecked = !IsHidden(idx); } finally { syncing = false; } // the last visible column stays
                SaveLayoutIfChanged();
            };
            cb.Checked += changed;
            cb.Unchecked += changed;
            boxes.Add(cb);
            list.Children.Add(cb);
        }
        void SetAll(bool on)
        {
            syncing = true;
            try
            {
                for (int k = 0; k < boxes.Count; k++)
                {
                    // None keeps the first column so the grid is never empty
                    bool v = on || k == 0;
                    boxes[k].IsChecked = v;
                    SetUserHidden(k, !v);
                }
            }
            finally { syncing = false; }
            SaveLayoutIfChanged();
        }
        Button Btn(string text, string id, Action a)
        {
            var b = new Button { Content = text, Margin = new Thickness(0, 0, 6, 0), MinHeight = 26, Padding = new Thickness(10, 2, 10, 2) };
            AutomationProperties.SetAutomationId(b, id);
            AutomationProperties.SetName(b, text);
            b.Click += (_, _) => a();
            return b;
        }
        var bar = new StackPanel { Orientation = Orientation.Horizontal, Margin = new Thickness(0, 0, 0, 6) };
        bar.Children.Add(Btn("All", "Results.Columns.All", () => SetAll(true)));
        bar.Children.Add(Btn("None", "Results.Columns.None", () => SetAll(false)));
        var body = new StackPanel();
        body.Children.Add(new TextBlock { Text = "Columns", Style = (Style)FindResource("Caption"), Margin = new Thickness(0, 0, 0, 4) });
        body.Children.Add(bar);
        body.Children.Add(new ScrollViewer { MaxHeight = 340, VerticalScrollBarVisibility = ScrollBarVisibility.Auto, Content = list });
        var root = new Border
        {
            BorderThickness = new Thickness(1),
            CornerRadius = new CornerRadius(4),
            Padding = new Thickness(10, 8, 10, 8),
            MinWidth = 240,
            Child = body,
        };
        root.SetResourceReference(Border.BackgroundProperty, "Brush.Surface");
        root.SetResourceReference(Border.BorderBrushProperty, "Brush.BorderStrong");
        root.SetResourceReference(System.Windows.Documents.TextElement.ForegroundProperty, "Brush.Text");
        AutomationProperties.SetAutomationId(root, "Results.Columns.Popup");
        var popup = new Popup { Child = Scaled(root), PlacementTarget = target, Placement = PlacementMode.Bottom, StaysOpen = false, AllowsTransparency = true };
        popup.Closed += (_, _) => { if (_columnsPopup == popup) _columnsPopup = null; };
        _columnsPopup = popup;
        popup.IsOpen = true;
    }

    // ------------------------------------------------------------ Actions menu

    private void ActionsButton_Click(object sender, RoutedEventArgs e)
    {
        ActionsMenu.PlacementTarget = ActionsButton;
        ActionsMenu.IsOpen = true;
    }

    private void ActionsMenu_Opened(object sender, RoutedEventArgs e)
    {
        bool has = _vm?.Rows != null;
        bool flagged = has && _vm!.FlaggedCount > 0;
        ActExportFiltered.IsEnabled = has;
        ActExportFlagged.IsEnabled = flagged;
        ActCopySelected.IsEnabled = has && Grid.SelectedCells.Count > 0;
        ActCopyFlagged.IsEnabled = flagged;
        ActCopyFiltered.IsEnabled = has;
        ActTimeline.IsEnabled = has;
        ActShowFile.IsEnabled = _vm?.CurrentFile != null;
        ActResetWidths.IsEnabled = has;
        ActViewSave.IsEnabled = ActViewSaveAs.IsEnabled = ActViewManage.IsEnabled = _vm?.CanSaveView == true;
        ActViewList.IsEnabled = _vm?.CanSaveView == true;
        ActViewClear.IsEnabled = _vm?.CanClearView == true;
        if (_vm is { CanSaveView: true })
            ActViewSave.ToolTip = _vm.SaveViewTargetExists ? $"Updates '{_vm.SaveViewTargetName}'" : $"Saves as '{_vm.SaveViewTargetName}' in {_vm.CurrentCategoryName}";
        ActExportFiltered.InputGestureText = HotKeyText(HotKeys.ExportFiltered);
        ActShowFile.InputGestureText = HotKeyText(HotKeys.ShowInExplorer);
        if (_vm != null)
        {
            ActHideHints.IsChecked = _vm.HideHintsChecked;
            ActHideHints.IsEnabled = _vm.HideHintsEnabled;
            ActHideHints.ToolTip = _vm.HideHintsEnabled
                ? "Hides reliability banners and column markers on Analyze until you close the app"
                : "Hidden in Settings > Options > Display";
        }
    }

    private void ActViewSave_Click(object sender, RoutedEventArgs e) => _vm?.SaveView();
    private void ActViewSaveAs_Click(object sender, RoutedEventArgs e) { if (_vm != null) SavedViewUi.SaveAs(_vm, this); }
    private void ActViewClear_Click(object sender, RoutedEventArgs e) => _vm?.ClearView();
    private void ActViewManage_Click(object sender, RoutedEventArgs e) => SavedViewUi.Edit(_vm, this, savedTab: true);

    private void ActViewList_SubmenuOpened(object sender, RoutedEventArgs e)
    {
        if (e.OriginalSource != ActViewList) return;
        ActViewList.Items.Clear();
        var vm = _vm;
        if (vm == null || vm.Headers.Count == 0) return;
        SavedViewUi.AddViewItems(ActViewList, vm.SavedViewsForCurrent, vm.CurrentCategoryName, vm.ActiveSavedView, "Results.Actions.View.Item",
            v => vm.ApplySavedView(v), "Results.Actions.View.List");
    }

    private void ActHideHints_Click(object sender, RoutedEventArgs e) => _vm?.SetHideHintsThisSession(ActHideHints.IsChecked);

    private void ActExportFiltered_Click(object sender, RoutedEventArgs e) => _vm?.ExportCommand.Execute(null);
    private void ActExportFlagged_Click(object sender, RoutedEventArgs e) => _vm?.ExportFlaggedCommand.Execute(null);
    private void ActTimeline_Click(object sender, RoutedEventArgs e) => _vm?.OpenInDefaultAppCommand.Execute(null);
    private void ActShowFile_Click(object sender, RoutedEventArgs e) => _vm?.OpenFolderCommand.Execute(null);
    private void ActShortcuts_Click(object sender, RoutedEventArgs e) => ShowShortcutsDialog();

    /// <summary>Lists every Analyze shortcut (effective gestures for the editable ones).</summary>
    private void ShowShortcutsDialog() =>
        ShortcutsWindow.ShowFor(this, _vm?.HotKeyMap ?? HotKeys.Resolve(AppServices.Current.Settings.HotKeys));

    private void ActResetWidths_Click(object sender, RoutedEventArgs e) => ResetColumnWidths();
    private async void ActCopySelected_Click(object sender, RoutedEventArgs e) { if (_vm != null) await _vm.CopySelectedRowsAsync(SelectedRows(), VisibleDataColumns()); }
    private async void ActCopyFlagged_Click(object sender, RoutedEventArgs e) { if (_vm != null) await _vm.CopyFlaggedRowsAsync(VisibleDataColumns()); }
    private async void ActCopyFiltered_Click(object sender, RoutedEventArgs e) { if (_vm != null) await _vm.CopyAllFilteredRowsAsync(VisibleDataColumns()); }

    private void FlagCell_Click(object sender, RoutedEventArgs e)
    {
        if (_vm == null || sender is not FrameworkElement { DataContext: RowItem row }) return;
        _vm.CycleFlag(row, SelectedRows());
    }

    /// <summary>CSV column index of a grid column (-1 for the flag column).</summary>
    private int DataIndex(DataGridColumn? c) => c == null ? -1 : Grid.Columns.IndexOf(c) - 1;

    private string HeaderName(int dataIndex) =>
        _vm != null && (uint)dataIndex < (uint)_vm.Headers.Count ? _vm.Headers[dataIndex] : "";

    private async void Grid_Sorting(object sender, DataGridSortingEventArgs e)
    {
        e.Handled = true; // we sort through FilterEngine.SortAsync, not the CollectionView
        if (_vm == null || DataIndex(e.Column) < 0) return;
        var col = e.Column;
        var dir = col.SortDirection == ListSortDirection.Ascending ? ListSortDirection.Descending : ListSortDirection.Ascending;
        foreach (var c in Grid.Columns) if (c != col) c.SortDirection = null;
        col.SortDirection = dir;
        try { await _vm.SortAsync(col.SortMemberPath, dir == ListSortDirection.Descending); }
        catch (Exception ex) { _vm.ShowError("Sort failed", ex); }
    }

    // ------------------------------------------------------------ flags

    private void RefreshRealisedFlags()
    {
        foreach (var row in FindVisualChildren<DataGridRow>(Grid))
            (row.Item as RowItem)?.RaiseFlagChanged();
    }

    private List<RowItem> SelectedRows()
    {
        var rows = Grid.SelectedCells.Select(c => c.Item).OfType<RowItem>().Distinct().ToList();
        if (rows.Count == 0 && Grid.CurrentCell.Item is RowItem cur) rows.Add(cur);
        return rows;
    }

    private void Grid_PreviewKeyDown(object sender, KeyEventArgs e)
    {
        if (_vm == null || e.OriginalSource is not DependencyObject src) return;
        // keys typed in the filter row (inside the column headers) are not grid commands
        if (FindAncestor<DataGridColumnHeader>(src) != null || src is TextBox) return;
        if (HandlePivotKey(e)) { e.Handled = true; return; }
        var mods = Keyboard.Modifiers;
        if ((e.Key == Key.Space && mods == ModifierKeys.None) || (e.Key == Key.F2 && mods == ModifierKeys.Control))
        {
            _vm.RotateFlags(SelectedRows(), Grid.CurrentCell.Item as RowItem);
            e.Handled = true;
        }
        else if (mods == ModifierKeys.Control && FlagKey(e.Key) is { } n)
        {
            var opts = _vm.FlagOptions;
            if (n != 0 && n > opts.Count) return;
            _vm.SetFlagId(SelectedRows(), n == 0 ? null : opts[n - 1].Id);
            e.Handled = true;
        }
    }

    // ------------------------------------------------------------ table-driven shortcuts (Options > Hot Keys)

    private string HotKeyText(string id) => _vm != null && _vm.HotKeyMap.TryGetValue(id, out var g) ? g : "";

    /// <summary>Runs the action bound to the pressed chord. Cell actions need the grid focused; typing in a text box is never intercepted.</summary>
    private void Pane_PreviewKeyDown(object sender, KeyEventArgs e)
    {
        if (_vm == null || e.Handled) return;
        if (Keyboard.FocusedElement is System.Windows.Controls.Primitives.TextBoxBase or PasswordBox or ComboBox { IsEditable: true }) return;
        if (HotKeyInput.FromEvent(e) is not { } g) return;
        if (HotKeys.ActionFor(_vm.HotKeyMap, g) is not { } id) return;
        bool inGrid = Grid.IsKeyboardFocusWithin && Keyboard.FocusedElement is DependencyObject f && FindAncestor<DataGridColumnHeader>(f) == null;
        if (RunHotKey(id, inGrid)) e.Handled = true;
    }

    private bool RunHotKey(string id, bool inGrid)
    {
        if (_vm == null) return false;
        switch (id)
        {
            case HotKeys.ClearFilters:
                if (!_vm.HasAnyFilter) return false;
                _vm.ClearFiltersCommand.Execute(null);
                return true;
            case HotKeys.ExportFiltered:
                if (_vm.Rows == null) return false;
                _vm.ExportCommand.Execute(null);
                return true;
            case HotKeys.ShowInExplorer:
                if (_vm.CurrentFile == null) return false;
                _vm.OpenFolderCommand.Execute(null);
                return true;
        }
        if (!inGrid || Grid.CurrentCell.Item is not RowItem row) return false;
        int col = DataIndex(Grid.CurrentCell.Column);
        switch (id)
        {
            case HotKeys.CopyRow:
                SetClipboard(string.Join("\t", row.Cells));
                return true;
            case HotKeys.ViewValue:
                if (col < 0) return false;
                OpenViewer(row, col);
                return true;
            case HotKeys.FilterContains or HotKeys.FilterNotContains or HotKeys.FilterEquals:
                if (col < 0 || row[col].Length == 0) return false;
                var mode = id == HotKeys.FilterContains ? ColumnFilterMode.Contains : id == HotKeys.FilterNotContains ? ColumnFilterMode.NotContains : ColumnFilterMode.Equals;
                _vm.AddCellFilter(HeaderName(col), mode, row[col]);
                return true;
        }
        return false;
    }

    /// <summary>Ctrl+0 .. Ctrl+9 (main row or keypad): 0 = clear, 1..9 = the Nth flag in Options order.</summary>
    private static int? FlagKey(Key k) => k switch
    {
        >= Key.D0 and <= Key.D9 => k - Key.D0,
        >= Key.NumPad0 and <= Key.NumPad9 => k - Key.NumPad0,
        _ => null,
    };

    // ------------------------------------------------------------ flag filter dropdown

    private bool _flagPopupScaled;

    private void FlagFilterPopup_Opened(object? sender, EventArgs e)
    {
        if (!_flagPopupScaled) { Scaled(FlagFilterPopupRoot); _flagPopupScaled = true; }
        SyncFlagModeChecks();
    }

    private bool _syncingModes;

    private void SyncFlagModeChecks()
    {
        if (_vm == null || FlagModeAll == null) return;
        bool colours = _vm.FlagOptions.Any(o => o.IsSelected);
        _syncingModes = true;
        try
        {
            FlagModeAll.IsChecked = _vm.FlagFilter == null;
            FlagModeFlagged.IsChecked = _vm.FlagFilter == true && !colours;
            FlagModeUnflagged.IsChecked = _vm.FlagFilter == false;
        }
        finally { _syncingModes = false; }
    }

    /// <summary>All rows / All flagged / Not flagged (mouse, keyboard or UIA toggle). Unticking the current mode returns to all rows.</summary>
    private void FlagMode_Changed(object sender, RoutedEventArgs e)
    {
        if (_syncingModes || _vm == null || sender is not CheckBox { Tag: string mode } cb) return;
        _vm.SetFlagMode(cb.IsChecked != true ? null : mode switch { "flagged" => true, "unflagged" => false, _ => null });
        SyncFlagModeChecks();
    }

    private void FlagOnly_Click(object sender, RoutedEventArgs e)
    {
        if (_vm == null || sender is not FrameworkElement { DataContext: FlagOption o }) return;
        _vm.SetFlagFilter(o.Id);
        SyncFlagModeChecks();
    }

    /// <summary>"Flag" sub-menu: a swatch item per flag (Ctrl+1..9) and Clear flag (Ctrl+0), applied to the selected rows.</summary>
    private MenuItem FlagColorMenu(IReadOnlyCollection<RowItem> sel)
    {
        var sub = new MenuItem { Header = new TextBlock { Text = $"Flag ({sel.Count:N0})" } };
        AutomationProperties.SetAutomationId(sub, "Results.Menu.Flag");
        AutomationProperties.SetName(sub, "Flag");
        var swatchStyle = (Style)FindResource("FlagSwatch");
        var current = sel.Select(r => r.FlagId).Distinct().ToList();
        foreach (var o in _vm!.FlagOptions)
        {
            var opt = o;
            var mi = Item("Flag." + opt.Id, $"{opt.Display}   ({opt.Count:N0} in file)", () => _vm?.SetFlagId(sel, opt.Id));
            mi.Icon = new Border { Style = swatchStyle, Background = opt.Brush };
            if (opt.Number <= 9) mi.InputGestureText = "Ctrl+" + opt.Number;
            if (current.Count == 1 && current[0] == opt.Id) mi.FontWeight = FontWeights.SemiBold;
            sub.Items.Add(mi);
        }
        sub.Items.Add(new Separator());
        var clear = Item("Flag.Clear", "Clear flag", () => _vm?.SetFlagId(sel, null), sel.Any(r => r.IsFlagged));
        clear.InputGestureText = "Ctrl+0";
        sub.Items.Add(clear);
        return sub;
    }

    // ------------------------------------------------------------ cell viewer

    private void Grid_MouseDoubleClick(object sender, MouseButtonEventArgs e)
    {
        if (e.OriginalSource is not DependencyObject src) return;
        var cell = FindAncestor<DataGridCell>(src);
        if (cell?.DataContext is not RowItem row) return;
        int col = DataIndex(cell.Column);
        if (col < 0) return;
        e.Handled = true;
        OpenViewer(row, col);
    }

    private void OpenViewer(RowItem row, int col)
    {
        if (_vm?.Rows is not { } rows || col < 0) return;
        if (_viewer == null || !_viewer.IsLoaded)
        {
            _viewer = new CellViewerWindow { Owner = Window.GetWindow(this) };
            _viewer.Closed += (_, _) => _viewer = null;
            _viewer.ShowCell(rows, _vm.Headers, row.Position, col);
            _viewer.Show();
        }
        else
        {
            _viewer.ShowCell(rows, _vm.Headers, row.Position, col);
            _viewer.Activate();
        }
    }

    // ------------------------------------------------------------ context menu

    private void Grid_PreviewMouseRightButtonDown(object sender, MouseButtonEventArgs e)
    {
        _menuRow = null;
        _menuCol = -1;
        _menuGroup = null;
        if (e.OriginalSource is not DependencyObject src) return;
        if (FindAncestor<DataGridColumnHeader>(src) != null) return;
        if (FindAncestor<DataGridRow>(src) is { Item: GroupRowItem g } gr)
        {
            _menuGroup = g;
            gr.Focus();
            return;
        }
        var cell = FindAncestor<DataGridCell>(src);
        if (cell?.DataContext is not RowItem row) return;
        _menuRow = row;
        _menuCol = DataIndex(cell.Column);
        if (!cell.IsSelected)
        {
            Grid.UnselectAllCells();
            cell.IsSelected = true;
        }
        Grid.CurrentCell = new DataGridCellInfo(row, cell.Column);
        cell.Focus();
    }

    private void Grid_ContextMenuOpening(object sender, ContextMenuEventArgs e)
    {
        var menu = Grid.ContextMenu;
        if (menu == null || _vm == null) { e.Handled = true; return; }
        if (e.OriginalSource is DependencyObject src && FindAncestor<DataGridColumnHeader>(src) is { } header)
        {
            if (_vm.Headers.Count == 0) { e.Handled = true; return; }
            menu.Items.Clear();
            BuildHeaderMenu(menu, header);
            return;
        }
        menu.Items.Clear();
        var group = _menuGroup ?? (e.OriginalSource is DependencyObject s2 ? FindAncestor<DataGridRow>(s2)?.Item as GroupRowItem : null)
            ?? (Keyboard.FocusedElement as DataGridRow)?.Item as GroupRowItem;
        if (group != null)
        {
            BuildGroupMenu(menu, group);
            return;
        }
        var row = _menuRow ?? Grid.CurrentCell.Item as RowItem;
        int col = _menuRow != null ? _menuCol : DataIndex(Grid.CurrentCell.Column);
        if (row == null) { e.Handled = true; return; }
        var name = HeaderName(col);
        var value = col >= 0 ? row[col] : "";

        var sel = SelectedRows();
        if (col < 0)
        {
            // right-click on the flag cell: the colour choices first
            var fm = FlagColorMenu(sel);
            fm.Icon = MenuUi.Icon(MenuUi.Flag, "Warn");
            menu.Items.Add(fm);
        }
        if (col >= 0)
        {
            var shown = Shorten(value);
            bool hasValue = value.Length > 0;
            MenuUi.Group(menu.Items, "View");
            menu.Items.Add(Item("ViewValue", "View full value…", () => OpenViewer(row, col), true, MenuUi.View, "Accent", HotKeyText(HotKeys.ViewValue)));
            MenuUi.Group(menu.Items, "Filter");
            menu.Items.Add(Item("Contains", $"Contains \"{shown}\"", () => _vm.AddCellFilter(name, ColumnFilterMode.Contains, value), hasValue, MenuUi.Filter, "Info", HotKeyText(HotKeys.FilterContains)));
            menu.Items.Add(Item("NotContains", $"Does not contain \"{shown}\"", () => _vm.AddCellFilter(name, ColumnFilterMode.NotContains, value), hasValue, MenuUi.FilterOff, "Info", HotKeyText(HotKeys.FilterNotContains)));
            var more = new MenuItem { Header = new TextBlock { Text = "More filters" }, Icon = MenuUi.Icon(MenuUi.More, "Info") };
            AutomationProperties.SetAutomationId(more, "Results.Menu.MoreFilters");
            AutomationProperties.SetName(more, "More filters");
            more.Items.Add(Item("Equals", $"Equals \"{shown}\"", () => _vm.AddCellFilter(name, ColumnFilterMode.Equals, value), hasValue, MenuUi.Filter, "Info", HotKeyText(HotKeys.FilterEquals)));
            more.Items.Add(Item("NotEquals", $"Does not equal \"{shown}\"", () => _vm.AddCellFilter(name, ColumnFilterMode.NotEquals, value), hasValue, MenuUi.Filter, "Info"));
            more.Items.Add(Item("BeginsWith", $"Begins with \"{shown}\"", () => _vm.AddCellFilter(name, ColumnFilterMode.BeginsWith, value), hasValue, MenuUi.Filter, "Info"));
            more.Items.Add(Item("EndsWith", $"Ends with \"{shown}\"", () => _vm.AddCellFilter(name, ColumnFilterMode.EndsWith, value), hasValue, MenuUi.Filter, "Info"));
            more.Items.Add(new Separator());
            more.Items.Add(Item("IsEmpty", $"Is empty  [{Shorten(name)}]", () => _vm.AddCellFilter(name, ColumnFilterMode.IsEmpty, ""), true, MenuUi.Filter, "Info"));
            more.Items.Add(Item("IsNotEmpty", $"Is not empty  [{Shorten(name)}]", () => _vm.AddCellFilter(name, ColumnFilterMode.IsNotEmpty, ""), true, MenuUi.Filter, "Info"));
            menu.Items.Add(more);
        }
        MenuUi.Group(menu.Items, "Copy");
        if (col >= 0) menu.Items.Add(Item("CopyCell", "Copy cell", () => SetClipboard(value), true, MenuUi.Copy, "Accent"));
        menu.Items.Add(Item("CopyRow", "Copy row (tab-separated)", () => SetClipboard(string.Join("\t", row.Cells)), true, MenuUi.Copy, "Accent", HotKeyText(HotKeys.CopyRow)));
        var moreCopy = new MenuItem { Header = new TextBlock { Text = "More copy" }, Icon = MenuUi.Icon(MenuUi.More, "Accent") };
        AutomationProperties.SetAutomationId(moreCopy, "Results.Menu.MoreCopy");
        AutomationProperties.SetName(moreCopy, "More copy");
        moreCopy.Items.Add(Item("CopySelection", "Copy selection", CopySelection, true, MenuUi.Copy, "Accent", "Ctrl+C"));
        if (col >= 0) moreCopy.Items.Add(Item("CopyColumn", $"Copy column name  ({Shorten(name)})", () => SetClipboard(name), true, MenuUi.Copy, "Accent"));
        menu.Items.Add(moreCopy);
        MenuUi.Group(menu.Items, "Flag");
        menu.Items.Add(Item("FlagSelected", $"Flag selected ({sel.Count:N0}) – {_vm.LastFlagLabel}", () => _vm.SetFlags(sel, true), true, MenuUi.Flag, "Warn", "Space"));
        if (col >= 0)
        {
            var fm = FlagColorMenu(sel);
            fm.Icon = MenuUi.Icon(MenuUi.Flag, "Warn");
            menu.Items.Add(fm);
        }
        menu.Items.Add(Item("UnflagSelected", $"Unflag selected ({sel.Count:N0})", () => _vm.SetFlags(sel, false), true, MenuUi.Flag, "Warn", "Ctrl+0"));
        MenuUi.Group(menu.Items, "Clear");
        if (col >= 0)
        {
            var cf = _vm.CellFor(name);
            menu.Items.Add(Item("ClearColumn", $"Clear filters on this column  [{Shorten(name)}]", () => _vm.ClearColumnFilters(name), cf?.IsFiltered == true, MenuUi.Clear, "Error"));
        }
        menu.Items.Add(Item("ClearAll", "Clear all filters", () => _vm.ClearFiltersCommand.Execute(null), _vm.HasAnyFilter, MenuUi.Clear, "Error", HotKeyText(HotKeys.ClearFilters)));
    }

    private static string Shorten(string v)
    {
        v = v.Replace("\r", " ").Replace("\n", " ");
        return v.Length > MenuValueMax ? v[..(MenuValueMax - 1)] + "…" : v;
    }

    /// <summary>Menu item with a plain-text header (no access-key underscores) and an AutomationId Results.Menu.&lt;id&gt;.</summary>
    private static MenuItem Item(string id, string header, Action action, bool enabled = true, string? glyph = null, string iconKind = "", string? gesture = null)
    {
        var mi = new MenuItem { Header = new TextBlock { Text = header }, IsEnabled = enabled };
        if (glyph != null) mi.Icon = MenuUi.Icon(glyph, iconKind);
        if (!string.IsNullOrEmpty(gesture)) mi.InputGestureText = gesture;
        AutomationProperties.SetAutomationId(mi, "Results.Menu." + id);
        AutomationProperties.SetName(mi, header);
        mi.Click += (_, _) => action();
        return mi;
    }

    private void CopySelection()
    {
        var rows = Grid.SelectedCells.Select(c => c.Item).OfType<RowItem>().Distinct().OrderBy(r => r.Position).Take(100_000).ToList();
        if (rows.Count == 0) return;
        var cols = Grid.SelectedCells.Select(c => DataIndex(c.Column)).Where(i => i >= 0).Distinct().OrderBy(i => i).ToList();
        if (cols.Count == 0) return;
        var sb = new StringBuilder();
        foreach (var r in rows) sb.AppendLine(string.Join("\t", cols.Select(i => r[i])));
        SetClipboard(sb.ToString());
    }

    private void SetClipboard(string text)
    {
        try { Clipboard.SetDataObject(text, true); }
        catch (Exception ex) { _vm?.ReportCopyError(ex); }
    }

    // ------------------------------------------------------------ filter row / funnel / builder

    private void FilterOp_Click(object sender, RoutedEventArgs e)
    {
        if (sender is not Button b || b.DataContext is not ColumnFilterCell cell) return;
        e.Handled = true;
        var menu = new ContextMenu { PlacementTarget = b, Placement = PlacementMode.Bottom };
        AutomationProperties.SetAutomationId(menu, "Results.FilterRow.OpMenu");
        foreach (var op in FilterOps.All)
        {
            var o = op;
            var mi = Item("Op." + o.Op, $"{o.Symbol}   {o.Label}", () => cell.Op = o.Op);
            if (o.IsComparison) mi.ToolTip = "Numbers and dates compare by value";
            if (o.Op == cell.Op) mi.FontWeight = FontWeights.SemiBold;
            menu.Items.Add(mi);
        }
        menu.IsOpen = true;
    }

    private async void Funnel_Click(object sender, RoutedEventArgs e)
    {
        if (sender is not Button b || b.DataContext is not ColumnFilterCell cell || _vm?.Index is not { } idx) return;
        e.Handled = true;
        if (_funnelPopup != null) _funnelPopup.IsOpen = false;
        try { cell.IsDateColumn = await Task.Run(() => DistinctValueCounter.LooksLikeDateColumn(idx, cell.Column)); }
        catch (Exception) { cell.IsDateColumn = false; }
        if (_vm?.Index != idx) return;
        var pvm = new ColumnValuesViewModel(_vm, idx, cell);
        var popup = new Popup
        {
            Child = Scaled(new ColumnFilterPopup { DataContext = pvm }),
            PlacementTarget = b,
            Placement = PlacementMode.Bottom,
            StaysOpen = false,
            AllowsTransparency = true,
        };
        pvm.CloseRequested += () => popup.IsOpen = false;
        popup.Closed += (_, _) => { pvm.Dispose(); if (_funnelPopup == popup) _funnelPopup = null; };
        _funnelPopup = popup;
        popup.IsOpen = true;
        await pvm.LoadAsync();
    }

    // ---- search help

    private sealed record SearchExample(int N, string Query, string Description)
    {
        public string AutomationId => "Results.SearchHelp.Example." + N;
    }

    private static readonly SearchExample[] SearchExamples =
    {
        new(1, "mimikatz", "mimikatz anywhere"),
        new(2, "powershell encodedcommand", "both words"),
        new(3, "\"failed logon\"", "that exact phrase"),
        new(4, "psexec OR wmic", "either word"),
        new(5, "powershell NOT svchost", "powershell but not svchost"),
        new(6, "(psexec OR wmic) NOT admin", "psexec or wmic, but not admin"),
        new(7, "\"OR\"", "the literal text OR"),
    };

    private bool _searchHelpScaled;

    private void SearchHelpPopup_Opened(object? sender, EventArgs e)
    {
        if (_searchHelpScaled) return;
        SearchExamplesList.ItemsSource = SearchExamples;
        Scaled(SearchHelpPopupRoot);
        _searchHelpScaled = true;
    }

    private System.Windows.Threading.DispatcherTimer? _searchHelpTimer;
    private bool _searchHelpPinned;
    private long _searchHelpClosedAt;

    private void SearchHelp_MouseEnter(object sender, MouseEventArgs e)
    {
        _searchHelpTimer?.Stop();
        if (!SearchHelpPopup.IsOpen) { SearchHelpPopup.IsOpen = true; SearchHelpButton.IsChecked = true; }
    }

    private void SearchHelp_MouseLeave(object sender, MouseEventArgs e)
    {
        if (_searchHelpPinned || !SearchHelpPopup.IsOpen) return;
        _searchHelpTimer ??= new System.Windows.Threading.DispatcherTimer { Interval = TimeSpan.FromMilliseconds(300) };
        _searchHelpTimer.Tick -= SearchHelpTimer_Tick;
        _searchHelpTimer.Tick += SearchHelpTimer_Tick;
        _searchHelpTimer.Stop();
        _searchHelpTimer.Start();
    }

    private void SearchHelpTimer_Tick(object? sender, EventArgs e)
    {
        _searchHelpTimer?.Stop();
        if (!_searchHelpPinned) SearchHelpPopup.IsOpen = false;
    }

    private void SearchHelpButton_Click(object sender, RoutedEventArgs e)
    {
        _searchHelpTimer?.Stop();
        if (SearchHelpPopup.IsOpen && !_searchHelpPinned)
        {
            // hover-opened: a click pins it (click-away then closes it)
            _searchHelpPinned = true;
            SearchHelpPopup.StaysOpen = false;
            SearchHelpButton.IsChecked = true;
        }
        else if (SearchHelpPopup.IsOpen || Environment.TickCount64 - _searchHelpClosedAt < 250)
        {
            // pinned popup was just closed by this very click (click-away handling), or still open: stay closed
            SearchHelpPopup.IsOpen = false;
            SearchHelpButton.IsChecked = false;
        }
        else
        {
            _searchHelpPinned = true;
            SearchHelpPopup.StaysOpen = false;
            SearchHelpPopup.IsOpen = true;
            SearchHelpButton.IsChecked = true;
        }
    }

    private void SearchHelpPopup_Closed(object? sender, EventArgs e)
    {
        _searchHelpTimer?.Stop();
        _searchHelpClosedAt = Environment.TickCount64;
        _searchHelpPinned = false;
        SearchHelpPopup.StaysOpen = true;
        SearchHelpButton.IsChecked = false;
    }

    private void SearchHelp_KeyDown(object sender, KeyEventArgs e)
    {
        if (e.Key != Key.Escape || !SearchHelpPopup.IsOpen) return;
        SearchHelpPopup.IsOpen = false;
        e.Handled = true;
    }

    private void SearchExample_Click(object sender, RoutedEventArgs e)
    {
        if (_vm == null || sender is not Button { Tag: string query }) return;
        SearchHelpPopup.IsOpen = false;
        _vm.GlobalText = query;
        _vm.ApplyNow();
    }
}
