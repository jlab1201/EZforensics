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

/// <summary>Case folder row, tree column header and the case files tree (DataContext: ResultsViewModel).</summary>
public partial class CaseTreePanel : UserControl
{
    private ResultsViewModel? _vm;

    public CaseTreePanel()
    {
        InitializeComponent();
        // Tree: bring items into view vertically only; selecting or keyboard navigation never changes the horizontal offset.
        Tree.AddHandler(FrameworkElement.RequestBringIntoViewEvent, new RequestBringIntoViewEventHandler(Tree_RequestBringIntoView), true);
        Tree.AddHandler(TreeViewItem.SelectedEvent, new RoutedEventHandler(TreeItem_Selected));
        // a collapsed folder's descendants leave the multi-selection (FileNode.OnIsExpandedChanged); refresh the selection bar
        Tree.AddHandler(TreeViewItem.CollapsedEvent, new RoutedEventHandler((_, _) => _vm?.OnTreeFolderCollapsed()));
        // The tree's own horizontal scroll bar (shown when the rows are wider than the pane) drives the column header.
        Tree.AddHandler(ScrollViewer.ScrollChangedEvent, new ScrollChangedEventHandler(Tree_ScrollChanged));
        // Column headers: follow the tree horizontally, never scroll on their own, start where the tree's items start.
        TreeHeaderScroll.RequestBringIntoView += (_, e) => e.Handled = true;
        Tree.LayoutUpdated += (_, _) => AlignTreeHeader();
        DataContextChanged += (_, e) =>
        {
            Detach();
            _vm = e.NewValue as ResultsViewModel;
            Attach();
        };
        Unloaded += (_, _) => Detach();
        Loaded += (_, _) =>
        {
            Detach();
            Attach();
        };
    }

    /// <summary>The case tree, for focus handling and tests.</summary>
    public TreeView CaseTree => Tree;

    private void Attach()
    {
        if (_vm == null) return;
        _vm.RevealRequested += OnRevealRequested;
        _vm.Tree.CollectionChanged += OnTreeRebuilt;
    }

    private void Detach()
    {
        if (_vm == null) return;
        _vm.RevealRequested -= OnRevealRequested;
        _vm.Tree.CollectionChanged -= OnTreeRebuilt;
    }

    private void Tree_SelectedItemChanged(object sender, RoutedPropertyChangedEventArgs<object> e)
    {
        if (_vm != null) _vm.SelectedNode = e.NewValue as FileNode;
    }

    private bool _resetHScrollQueued;

    /// <summary>A loaded or refreshed case starts at the left edge (offset 0).</summary>
    private void OnTreeRebuilt(object? sender, System.Collections.Specialized.NotifyCollectionChangedEventArgs e)
    {
        if (_resetHScrollQueued) return;
        _resetHScrollQueued = true;
        Dispatcher.BeginInvoke(() =>
        {
            _resetHScrollQueued = false;
            TreeScroll?.ScrollToHorizontalOffset(0);
            TreeHeaderScroll.ScrollToHorizontalOffset(0);
        }, System.Windows.Threading.DispatcherPriority.Loaded);
    }

    private void Tree_ScrollChanged(object sender, ScrollChangedEventArgs e)
    {
        if (e.OriginalSource is not ScrollViewer sv || !ReferenceEquals(sv, TreeScroll)) return;
        if (TreeHeaderScroll.HorizontalOffset != sv.HorizontalOffset)
            TreeHeaderScroll.ScrollToHorizontalOffset(sv.HorizontalOffset);
    }

    // ------------------------------------------------------------ tree columns

    /// <summary>Keeps the header row's left edge on the tree items' left edge (the TreeView template adds padding).</summary>
    private void AlignTreeHeader()
    {
        var sv = TreeScroll;
        if (sv == null || Tree.Items.Count == 0 || Tree.ItemContainerGenerator.ContainerFromIndex(0) is not TreeViewItem first || !first.IsVisible) return;
        double x;
        try { x = first.TransformToAncestor(Tree).Transform(new Point(0, 0)).X + sv.HorizontalOffset; }
        catch (InvalidOperationException) { return; }
        var m = TreeHeaderScroll.Margin;
        if (Math.Abs(m.Left - x) > 0.1) TreeHeaderScroll.Margin = new Thickness(x, m.Top, m.Right, m.Bottom);
    }

    private void TreeHeader_Click(object sender, RoutedEventArgs e)
    {
        if (_vm != null && sender is FrameworkElement { Tag: string column }) _vm.SortTreeBy(column);
    }

    private void TreeColumnSizer_DragDelta(object sender, DragDeltaEventArgs e)
    {
        if (_vm == null || sender is not FrameworkElement { Tag: string column }) return;
        _vm.Columns.SetWidth(column, _vm.Columns.GetWidth(column) + e.HorizontalChange);
    }

    private void TreeColumnSizer_DragCompleted(object sender, DragCompletedEventArgs e) => _vm?.SaveTreeColumns();

    private void TreeColumns_Click(object sender, RoutedEventArgs e)
    {
        if (sender is not Button b) return;
        var menu = new ContextMenu { PlacementTarget = b, Placement = PlacementMode.Bottom };
        AutomationProperties.SetAutomationId(menu, "Results.Tree.ColumnsMenu");
        FillColumnsMenu(menu);
        menu.IsOpen = true;
    }

    private void TreeHeader_ContextMenuOpening(object sender, ContextMenuEventArgs e)
    {
        if (TreeHeaderScroll.ContextMenu is not { } menu || _vm == null) { e.Handled = true; return; }
        FillColumnsMenu(menu);
    }

    /// <summary>
    /// Column picker: a check box per column (Name is always shown, so it is checked and greyed out). Items stay open on click, so
    /// several columns can be toggled in one go; each is bound to the column's visibility and saved at once. Show all / Reset below.
    /// </summary>
    private void FillColumnsMenu(ContextMenu menu)
    {
        menu.Items.Clear();
        if (_vm == null) return;
        var name = new MenuItem
        {
            Header = new TextBlock { Text = TreeColumnsViewModel.Name + "  (always shown)" }, IsCheckable = true, IsChecked = true, IsEnabled = false,
        };
        AutomationProperties.SetAutomationId(name, "Results.Tree.Columns.Name");
        AutomationProperties.SetName(name, TreeColumnsViewModel.Name);
        menu.Items.Add(name);
        foreach (var col in TreeColumnsViewModel.Optional)
        {
            var c = col;
            var mi = new MenuItem { Header = new TextBlock { Text = c }, IsCheckable = true, StaysOpenOnClick = true };
            mi.SetBinding(MenuItem.IsCheckedProperty, new Binding("Show" + c) { Source = _vm.Columns, Mode = BindingMode.TwoWay });
            AutomationProperties.SetAutomationId(mi, "Results.Tree.Columns." + c);
            AutomationProperties.SetName(mi, c);
            // saved on any change (mouse, keyboard or UIA toggle; a toggle does not raise Click)
            mi.Checked += (_, _) => _vm?.SaveTreeColumns();
            mi.Unchecked += (_, _) => _vm?.SaveTreeColumns();
            menu.Items.Add(mi);
        }
        menu.Items.Add(new Separator());
        var all = new MenuItem { Header = new TextBlock { Text = "Show all" }, StaysOpenOnClick = true };
        AutomationProperties.SetAutomationId(all, "Results.Tree.Columns.ShowAll");
        AutomationProperties.SetName(all, "Show all");
        all.Click += (_, _) => _vm?.ShowAllTreeColumns();
        menu.Items.Add(all);
        var reset = new MenuItem { Header = new TextBlock { Text = "Reset (default columns and widths)" }, StaysOpenOnClick = true };
        AutomationProperties.SetAutomationId(reset, "Results.Tree.Columns.Reset");
        AutomationProperties.SetName(reset, "Reset");
        reset.Click += (_, _) => _vm?.ResetTreeColumns();
        menu.Items.Add(reset);
    }

    // ------------------------------------------------------------ search box

    private void TreeSearchBox_PreviewKeyDown(object sender, KeyEventArgs e)
    {
        if (_vm == null) return;
        if (e.Key == Key.Escape)
        {
            if (_vm.TreeSearchText.Length > 0) _vm.TreeSearchText = "";
            else Tree.Focus();
            e.Handled = true;
        }
        else if (e.Key == Key.Down || e.Key == Key.Enter)
        {
            // into the (filtered) tree: the first visible file
            var first = _vm.VisibleTreeOrder().FirstOrDefault(n => n.IsFile) ?? _vm.VisibleTreeOrder().FirstOrDefault();
            if (first != null && FindVisualChildren<TreeViewItem>(Tree).FirstOrDefault(i => i.DataContext == first) is { } item) item.Focus();
            else Tree.Focus();
            e.Handled = true;
        }
    }

    private void TreeSearchClear_Click(object sender, RoutedEventArgs e)
    {
        if (_vm != null) _vm.TreeSearchText = "";
        TreeSearchBox.Focus();
    }

    private void FocusSearch()
    {
        TreeSearchBox.Focus();
        TreeSearchBox.SelectAll();
    }

    // ------------------------------------------------------------ multi-selection actions, context menu, shortcuts

    private void OpenCombined_Click(object sender, RoutedEventArgs e) => _ = _vm?.OpenCombinedAsync(_vm.SelectedFiles.Select(f => f.FullPath).ToList());

    private void MergeSelection_Click(object sender, RoutedEventArgs e) => _ = _vm?.MergeSelectionAsync();

    private FileNode? FocusNode => _vm?.TreeSelection.FirstOrDefault();

    private void Tree_PreviewKeyDown(object sender, KeyEventArgs e)
    {
        if (_vm == null || e.OriginalSource is TextBox) return;
        var mods = Keyboard.Modifiers;
        bool handled = true;
        switch (e.Key)
        {
            case Key.F when mods == ModifierKeys.Control: FocusSearch(); break;
            case Key.F2 when mods == ModifierKeys.None: RenameSelected(); break;
            case Key.Delete when mods == ModifierKeys.None: _ = _vm.DeleteSelectionAsync(); break;
            case Key.C when mods == ModifierKeys.Control: _vm.CopySelection(); break;
            case Key.C when mods == (ModifierKeys.Control | ModifierKeys.Shift): _vm.CopySelectionPaths(); break;
            case Key.X when mods == ModifierKeys.Control: _vm.CutSelection(); break;
            case Key.V when mods == ModifierKeys.Control: _ = _vm.PasteAsync(FocusNode); break;
            case Key.D when mods == ModifierKeys.Control: _ = _vm.DuplicateSelectionAsync(); break;
            case Key.N when mods == (ModifierKeys.Control | ModifierKeys.Shift): NewFolder(FocusNode); break;
            case Key.Enter when mods == ModifierKeys.None: _ = _vm.OpenSelectionAsync(); break;
            case Key.Escape when mods == ModifierKeys.None && _vm.IsTreeFiltered: _vm.TreeSearchText = ""; break;
            default: handled = false; break;
        }
        if (handled) e.Handled = true;
    }

    private void RenameSelected()
    {
        if (_vm == null) return;
        var sel = _vm.TreeSelection;
        if (sel.Count != 1) { _vm.ShowMessage("Select one item to rename.", InfoSeverity.Warning); return; }
        var node = sel[0];
        bool isCsv = node.IsFile && node.Name.EndsWith(".csv", StringComparison.OrdinalIgnoreCase);
        var name = TextPromptWindow.Ask(this, "Rename", node.IsFile ? "New file name (a CSV keeps .csv; flags and sidecars are renamed with it):" : "New folder name:",
            node.Name, t =>
            {
                try { EzForensics.Core.Case.CaseFileOperations.ValidateName(t, isCsv, !node.IsFile); return null; }
                catch (EzForensics.Core.Case.CaseFileOperationException ex) { return ex.Message; }
            }, isCsv ? node.Name.Length - 4 : -1);
        if (name != null && name != node.Name) _ = _vm.RenameAsync(node, name);
    }

    private void NewFolder(FileNode? at)
    {
        if (_vm == null || _vm.CaseFolder == null) return;
        var name = TextPromptWindow.Ask(this, "New folder", "Folder name:", "New folder", t =>
        {
            try { EzForensics.Core.Case.CaseFileOperations.ValidateName(t, false, true); return null; }
            catch (EzForensics.Core.Case.CaseFileOperationException ex) { return ex.Message; }
        });
        if (name != null) _ = _vm.NewFolderAsync(at, name);
    }

    private void Tree_ContextMenuOpening(object sender, ContextMenuEventArgs e)
    {
        if (_vm == null || Tree.ContextMenu is not { } menu) { e.Handled = true; return; }
        menu.Items.Clear();
        var sel = _vm.TreeSelection;
        var files = sel.Where(n => n.IsFile).ToList();
        bool any = sel.Count > 0, single = sel.Count == 1;
        var at = sel.FirstOrDefault();

        MenuItem Item(string id, string header, string? gesture, string? glyph, bool enabled, Action click)
        {
            var mi = new MenuItem { Header = header, InputGestureText = gesture ?? "", IsEnabled = enabled };
            if (glyph != null) mi.Icon = new TextBlock { Text = glyph, FontFamily = (FontFamily)FindResource("Font.Icon"), FontSize = 12 };
            AutomationProperties.SetAutomationId(mi, "Results.Tree.Menu." + id);
            AutomationProperties.SetName(mi, header.TrimEnd('…'));
            mi.Click += (_, _) => click();
            menu.Items.Add(mi);
            return mi;
        }

        if (files.Count == 1 && sel.Count == 1 && files[0].FullPath.EndsWith(".csv", StringComparison.OrdinalIgnoreCase))
        {
            MenuUi.Group(menu.Items, "Filters");
            AddFilterMenu(menu, files[0].FullPath);
            MenuUi.Group(menu.Items, "View");
            AddViewMenu(menu, files[0].FullPath);
        }
        MenuUi.Group(menu.Items, "Open");
        Item("Open", files.Count > 1 ? "Open combined view" : "Open", "Enter", "", files.Count > 0, () => _ = _vm.OpenSelectionAsync());
        Item("OpenCombined", "Open in new combined view", null, "", files.Count > 1,
            () => _ = _vm.OpenCombinedAsync(files.Select(f => f.FullPath).ToList()));
        Item("Merge", "Merge into new file…", null, "", files.Count > 1, () => _ = _vm.MergeSelectionAsync());
        if (single && ProcessTreeHostOf(at) is { } host)
        {
            // Volatility pslist / psscan / pstree and/or EvtxECmd process events of the host drawn as a graph (replaces the grid until Back)
            MenuUi.Group(menu.Items, "Process");
            bool can = ResultsViewModel.CanShowProcessTree(host);
            var pt = Item("ProcessTree", "Process tree", null, "", can, () => _vm.OpenProcessTree(host));
            pt.ToolTip = can ? "Parent / child graph of the host's processes (memory image and/or Security 4688 / Sysmon 1 events)" : "Run windows.pslist, windows.psscan or windows.pstree (Tools › Volatility), or ingest the event logs, for this host first";
            ToolTipService.SetShowOnDisabled(pt, true);
            // the Ingest correlator's Processes\Processes.csv + Processes_Activity.csv, built again now (the tree reloads from them when it is open)
            var rb = Item("RebuildProcessCorrelation", "Rebuild process tree data", null, "", can, () => _vm.RebuildProcessCorrelation(host));
            rb.ToolTip = can ? "Correlate the host's process evidence again into Processes\\Processes.csv and Processes_Activity.csv (a job in the Jobs list on Ingest)"
                : "Run windows.pslist, windows.psscan or windows.pstree (Tools › Volatility), or ingest the event logs, for this host first";
            ToolTipService.SetShowOnDisabled(rb, true);
        }
        MenuUi.Group(menu.Items, "Edit");
        Item("Cut", "Cut", "Ctrl+X", "", any, _vm.CutSelection);
        Item("Copy", "Copy", "Ctrl+C", "", any, _vm.CopySelection);
        Item("Paste", "Paste", "Ctrl+V", "", _vm.CanPaste, () => _ = _vm.PasteAsync(at));
        Item("Duplicate", "Duplicate", "Ctrl+D", "", any, () => _ = _vm.DuplicateSelectionAsync());
        Item("Rename", "Rename…", "F2", "", single, RenameSelected);
        Item("Delete", "Delete…", "Del", "", any, () => _ = _vm.DeleteSelectionAsync());
        MenuUi.Group(menu.Items, "Folder");
        Item("NewFolder", "New folder…", "Ctrl+Shift+N", "", _vm.CaseFolder != null, () => NewFolder(at));
        Item("ShowInExplorer", "Show in Explorer", null, "", any, _vm.ShowSelectionInExplorer);
        Item("CopyPath", "Copy full path", "Ctrl+Shift+C", "", any, _vm.CopySelectionPaths);
    }

    /// <summary>Host folder the Process tree item opens for: a host row, or the Memory folder directly under a host (same enable rule as the host menu).</summary>
    private static string? ProcessTreeHostOf(FileNode? n)
    {
        if (n == null || n.IsFile) return null;
        if (n.IsHost) return n.FullPath;
        if (string.Equals(n.Name, "Memory", StringComparison.OrdinalIgnoreCase) && n.Parent is { IsHost: true } h) return h.FullPath;
        return null;
    }

    private static readonly System.Collections.Concurrent.ConcurrentDictionary<string, (long Size, long Ticks, string[] Headers)> HeaderCache = new(StringComparer.OrdinalIgnoreCase);

    /// <summary>Column names of a CSV (first record only, same parser rules as the grid); cached by path, size and write time. Null when unreadable.</summary>
    private static string[]? ReadHeaders(string path)
    {
        try
        {
            var fi = new System.IO.FileInfo(path);
            if (!fi.Exists) return null;
            if (HeaderCache.TryGetValue(path, out var c) && c.Size == fi.Length && c.Ticks == fi.LastWriteTimeUtc.Ticks) return c.Headers;
            var h = CsvIndex.ReadHeaderNames(path);
            if (h.Length == 0) return null;
            HeaderCache[path] = (fi.Length, fi.LastWriteTimeUtc.Ticks, h);
            return h;
        }
        catch (Exception) { return null; }
    }

    /// <summary>"Filter" submenu of a single CSV in the tree: the saved filters of the file's category (click applies). "Open filter builder" and "Manage saved filters" follow as siblings.</summary>
    private void AddFilterMenu(ContextMenu menu, string path)
    {
        var vm = _vm;
        if (vm == null) return;
        var root = new MenuItem { Header = "Filter", Icon = MenuUi.Icon("") };
        AutomationProperties.SetAutomationId(root, "Results.Tree.Menu.Filter");
        AutomationProperties.SetName(root, "Filter");
        menu.Items.Add(root);
        var headers = ReadHeaders(path);
        if (headers == null)
        {
            var bad = new MenuItem { Header = "Could not read the columns", IsEnabled = false };
            AutomationProperties.SetAutomationId(bad, "Results.Tree.Menu.Filter.Error");
            root.Items.Add(bad);
        }
        else
        {
            var sig = EzForensics.Core.Settings.ColumnLayoutStore.Signature(headers);
            var catName = Services.SavedFilters.Instance.CategoryName(sig);
            if (string.IsNullOrWhiteSpace(catName))
            {
                try { catName = ArtifactCategoryNamer.Name(headers, System.IO.Path.GetFileName(path)); }
                catch (Exception) { catName = "Custom layout"; }
            }
            var active = string.Equals(path, vm.CurrentFile, StringComparison.OrdinalIgnoreCase) ? vm.ActiveSavedFilter : null;
            SavedFilterUi.AddFilterItems(root, Services.SavedFilters.Instance.ForSignature(sig), catName, active, "Results.Tree.Menu.Filter.Item",
                f => _ = vm.OpenAndApplySavedFilterAsync(path, f), "Results.Tree.Menu.Filter");
        }

        MenuItem Builder(string header, string glyph, string id, bool savedTab)
        {
            var mi = new MenuItem { Header = header, Icon = MenuUi.Icon(glyph) };
            AutomationProperties.SetAutomationId(mi, "Results.Tree.Menu.Filter." + id);
            AutomationProperties.SetName(mi, header);
            mi.Click += async (_, _) =>
            {
                if (!string.Equals(path, vm.CurrentFile, StringComparison.OrdinalIgnoreCase)) await vm.OpenCsvAsync(path);
                if (string.Equals(path, vm.CurrentFile, StringComparison.OrdinalIgnoreCase) && vm.Rows != null) EditFilter(vm, this, savedTab);
            };
            menu.Items.Add(mi);
            return mi;
        }
        Builder("Open filter builder", "", "Builder", false);
        Builder("Manage saved filters", "", "Manage", true);
    }

    /// <summary>"Views" submenu of a single CSV in the tree (saved column layouts of the file's category; click opens the file if needed and applies) and "Manage views".</summary>
    private void AddViewMenu(ContextMenu menu, string path)
    {
        var vm = _vm;
        if (vm == null) return;
        var root = new MenuItem { Header = "Views", Icon = MenuUi.Icon("") };
        AutomationProperties.SetAutomationId(root, "Results.Tree.Menu.Views");
        AutomationProperties.SetName(root, "Views");
        menu.Items.Add(root);
        var headers = ReadHeaders(path);
        if (headers == null)
        {
            var bad = new MenuItem { Header = "Could not read the columns", IsEnabled = false };
            AutomationProperties.SetAutomationId(bad, "Results.Tree.Menu.Views.Error");
            root.Items.Add(bad);
        }
        else
        {
            var sig = EzForensics.Core.Settings.ColumnLayoutStore.Signature(headers);
            var catName = Services.SavedViews.Instance.CategoryName(sig);
            if (string.IsNullOrWhiteSpace(catName))
            {
                try { catName = ArtifactCategoryNamer.Name(headers, System.IO.Path.GetFileName(path)); }
                catch (Exception) { catName = "Custom layout"; }
            }
            var active = string.Equals(path, vm.CurrentFile, StringComparison.OrdinalIgnoreCase) ? vm.ActiveSavedView : null;
            SavedViewUi.AddViewItems(root, Services.SavedViews.Instance.ForSignature(sig), catName, active, "Results.Tree.Menu.Views.Item",
                v => _ = vm.OpenAndApplySavedViewAsync(path, v), "Results.Tree.Menu.Views");
        }

        var manage = new MenuItem { Header = "Manage views", Icon = MenuUi.Icon("") };
        AutomationProperties.SetAutomationId(manage, "Results.Tree.Menu.Views.Manage");
        AutomationProperties.SetName(manage, "Manage views");
        manage.Click += async (_, _) =>
        {
            if (!string.Equals(path, vm.CurrentFile, StringComparison.OrdinalIgnoreCase)) await vm.OpenCsvAsync(path);
            if (string.Equals(path, vm.CurrentFile, StringComparison.OrdinalIgnoreCase) && vm.Rows != null)
            {
                await System.Windows.Threading.Dispatcher.Yield(System.Windows.Threading.DispatcherPriority.Loaded);
                SavedViewUi.Edit(vm, this, savedTab: true);
            }
        };
        menu.Items.Add(manage);
    }

    private ScrollViewer? _treeScroll;
    private ScrollViewer? TreeScroll => _treeScroll ??= FindVisualChildren<ScrollViewer>(Tree).FirstOrDefault();

    private void TreeItem_Selected(object sender, RoutedEventArgs e)
    {
        if (e.OriginalSource is TreeViewItem item)
            Dispatcher.BeginInvoke(() => ScrollTreeItemIntoView(item), System.Windows.Threading.DispatcherPriority.Loaded);
    }

    private void OnRevealRequested(FileNode node)
    {
        // Containers of freshly expanded folders exist only after layout.
        Dispatcher.BeginInvoke(() =>
        {
            if (FindVisualChildren<TreeViewItem>(Tree).FirstOrDefault(i => i.DataContext == node) is { } item)
                ScrollTreeItemIntoView(item);
        }, System.Windows.Threading.DispatcherPriority.Loaded);
    }

    private void Tree_RequestBringIntoView(object sender, RequestBringIntoViewEventArgs e)
    {
        var d = e.TargetObject as DependencyObject;
        while (d != null && d is not TreeViewItem && d != Tree) d = VisualTreeHelper.GetParent(d);
        if (d is not TreeViewItem item) return;
        e.Handled = true; // the default would also scroll horizontally to the item's (indented) edge; keep the offset
        ScrollTreeItemIntoView(item);
    }

    /// <summary>Scrolls the tree vertically so the item's header row is visible; the horizontal offset is left alone.</summary>
    private void ScrollTreeItemIntoView(TreeViewItem item)
    {
        var sv = TreeScroll;
        if (sv == null || !item.IsVisible) return;
        var header = item.Template?.FindName("Bd", item) as FrameworkElement ?? item;
        if (header.ActualHeight <= 0) return;
        try
        {
            var top = header.TransformToAncestor(sv).Transform(new Point(0, 0)).Y;
            var bottom = top + header.ActualHeight;
            if (top < 0) sv.ScrollToVerticalOffset(sv.VerticalOffset + top);
            else if (bottom > sv.ViewportHeight) sv.ScrollToVerticalOffset(sv.VerticalOffset + Math.Min(top, bottom - sv.ViewportHeight));
        }
        catch (InvalidOperationException) { } // not connected to the scroll viewer (yet)
    }
}
