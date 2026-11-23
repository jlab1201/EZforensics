using System.ComponentModel;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using EzForensics.App.ViewModels;
using EzForensics.App.ViewModels.Explore;

namespace EzForensics.App.Views.Explore;

/// <summary>Explore page code-behind: owns the CyberChef host (a popup window), feeds the list's lazy facts and paging. No evidence is ever opened or run from here.</summary>
public partial class ExploreView : UserControl
{
    private ExploreViewModel? _vm;
    private CyberChefHost? _host;
    // One host for the whole session: leaving and re-entering the page keeps the CyberChef recipe. Released in ShutdownHost (app exit).
    private static CyberChefHost? s_host;

    /// <summary>App exit: closes the kept-alive CyberChef window.</summary>
    public static void ShutdownHost() { try { s_host?.Detach(); } catch { } s_host = null; try { ExploreSearchWindow.Shutdown(); } catch { } }

    public ExploreView()
    {
        InitializeComponent();
        Loaded += OnLoaded;
        Unloaded += OnUnloaded;
    }

    private void OnLoaded(object sender, RoutedEventArgs e)
    {
        _vm = DataContext as ExploreViewModel;
        if (_vm == null) return;
        _vm.PropertyChanged += OnVmChanged;
        _vm.RowRevealed += OnRowRevealed;

        bool chefInstalled = _vm.Tools.IsInstalled(EzForensics.Core.Explore.ExploreTool.CyberChef);
        _host = s_host ??= new CyberChefHost(_vm.Tools.CyberChefFolder);
        _host.Refresh(_vm.Tools.CyberChefFolder, chefInstalled);
        _host.InputTruncated += OnHostInputTruncated;
        _vm.CyberChefSender = _host.SendInputAsync;
        _vm.CyberChefOpener = _host.Open;
        _vm.AdvancedSearchOpener = vm => ExploreSearchWindow.Open(vm);
        _vm.OnNavigatedTo();
    }

    private void OnUnloaded(object sender, RoutedEventArgs e)
    {
        if (_vm != null)
        {
            _vm.PropertyChanged -= OnVmChanged;
            _vm.RowRevealed -= OnRowRevealed;
            _vm.CyberChefSender = null;
            _vm.CyberChefOpener = null;
            _vm.AdvancedSearchOpener = null;
        }
        if (_host != null) { _host.InputTruncated -= OnHostInputTruncated; _host = null; }
        _vm = null;
    }

    // ------------------------------------------------------------------------------ CyberChef

    private void OnVmChanged(object? s, PropertyChangedEventArgs e)
    {
        if (e.PropertyName == nameof(ExploreViewModel.IsFiltering)) PathColumn.Visibility = _vm is { IsFiltering: true } ? Visibility.Visible : Visibility.Collapsed;
    }

    private void OnRowRevealed(FileRow row) =>
        Dispatcher.BeginInvoke(new Action(() => { try { FileGrid.ScrollIntoView(row); } catch (InvalidOperationException) { } }), System.Windows.Threading.DispatcherPriority.Loaded);

    private void OnHostInputTruncated(long len) => Dispatcher.BeginInvoke(() =>
        _vm?.ShowMessage($"CyberChef input was cut to 10 MB (the original is {len:N0} bytes).", InfoSeverity.Warning));

    // ------------------------------------------------------------------------------ sources

    private void Browse_Click(object sender, RoutedEventArgs e)
    {
        if (_vm == null || sender is not Button b) return;
        var menu = new ContextMenu { PlacementTarget = b, Placement = System.Windows.Controls.Primitives.PlacementMode.Bottom, DataContext = _vm };
        menu.Items.Add(new MenuItem { Header = "Add folder...", Command = _vm.AddFolderSourceCommand });
        menu.Items.Add(new MenuItem { Header = "Add file...", Command = _vm.AddFileSourceCommand });
        menu.IsOpen = true;
    }

    private void TreeItem_Selected(object sender, RoutedEventArgs e)
    {
        // Keep the explored folder in view (the tree follows double-clicks and Back/Forward/Up).
        if (sender is TreeViewItem item && ReferenceEquals(e.OriginalSource, item))
            Dispatcher.BeginInvoke(new Action(item.BringIntoView), System.Windows.Threading.DispatcherPriority.Loaded);
    }

    private void RemoveSource_Click(object sender, RoutedEventArgs e)
    {
        if (_vm != null && sender is FrameworkElement { DataContext: ExploreNode node }) _vm.RemoveSourceCommand.Execute(node);
    }

    // ------------------------------------------------------------------------------ list

    private void FileGrid_LoadingRow(object? sender, DataGridRowEventArgs e)
    {
        if (e.Row.Item is FileRow r) _vm?.RequestFacts(r);
    }

    private void FileGrid_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (_vm != null) _vm.SelectedRows = FileGrid.SelectedItems.OfType<FileRow>().ToList();
    }

    private void FileGrid_MouseDoubleClick(object sender, MouseButtonEventArgs e)
    {
        // Only folders are entered. A double-click on a file just keeps it selected: evidence is never opened. (A filter result of either kind is shown in Browse.)
        if (_vm != null && FileGrid.SelectedItem is FileRow r && (r.IsDirectory || _vm.IsFiltering) && IsOverRow(e)) _vm.ActivateRow(r);
    }

    private static bool IsOverRow(MouseButtonEventArgs e)
    {
        for (var d = e.OriginalSource as DependencyObject; d != null; d = VisualTreeHelper.GetParent(d))
            if (d is DataGridRow) return true;
        return false;
    }

    private void Filter_PreviewKeyDown(object sender, KeyEventArgs e)
    {
        if (e.Key == Key.Escape && _vm != null) { _vm.ClearNameFilter(); e.Handled = true; }
    }

    private void FileGrid_PreviewKeyDown(object sender, KeyEventArgs e)
    {
        if (_vm == null) return;
        if (e.Key == Key.Enter && FileGrid.SelectedItem is FileRow r && (r.IsDirectory || _vm.IsFiltering)) { _vm.ActivateRow(r); e.Handled = true; }
        else if (e.Key == Key.Back && _vm.CanUp) { _vm.UpCommand.Execute(null); e.Handled = true; }
    }

    private void FileGrid_PreviewMouseRightButtonDown(object sender, MouseButtonEventArgs e)
    {
        for (var d = e.OriginalSource as DependencyObject; d != null; d = VisualTreeHelper.GetParent(d))
            if (d is DataGridRow row)
            {
                if (!row.IsSelected) { FileGrid.SelectedItems.Clear(); row.IsSelected = true; }
                return;
            }
    }

    private void FileGrid_ScrollChanged(object sender, ScrollChangedEventArgs e)
    {
        if (_vm == null || e.OriginalSource is not ScrollViewer sv || e.VerticalChange == 0 && e.ExtentHeightChange == 0) return;
        if (sv.ScrollableHeight > 0 && sv.VerticalOffset >= sv.ScrollableHeight - 3) _ = _vm.LoadMoreAsync();
    }
}
