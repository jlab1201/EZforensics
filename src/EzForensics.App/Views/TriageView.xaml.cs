using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;

namespace EzForensics.App.Views;

public partial class TriageView : UserControl
{
    private const double MinListAreaHeight = 420;

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
        PageScroll.SizeChanged += (_, _) => FitPage();
        // the viewport settles after the size change (maximize / restore): fit again once it did, or the page keeps its old height and floats
        PageScroll.ScrollChanged += (_, e) => { if (e.ViewportHeightChange != 0) FitPage(); };
        PageHeader.SizeChanged += (_, _) => FitPage();
        ScanDetails.SizeChanged += (_, _) => FitPage();
        DataContextChanged += (_, e) =>
        {
            if (e.OldValue is ViewModels.TriageViewModel o) o.AdminWarningRequested -= ShowRunHint;
            if (e.NewValue is ViewModels.TriageViewModel n) n.AdminWarningRequested += ShowRunHint;
        };
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

    /// <summary>A click on a planned row (not on its checkbox or the Result badge button) that has a job opens that job on the Jobs page.</summary>
    private void PlannedRow_MouseUp(object sender, System.Windows.Input.MouseButtonEventArgs e)
    {
        for (var o = e.OriginalSource as DependencyObject; o != null && o != sender; o = o is System.Windows.Media.Visual or System.Windows.Media.Media3D.Visual3D ? System.Windows.Media.VisualTreeHelper.GetParent(o) : LogicalTreeHelper.GetParent(o))
            if (o is System.Windows.Controls.Primitives.ButtonBase or System.Windows.Controls.Primitives.ScrollBar or System.Windows.Documents.Hyperlink) return;
        if (sender is DataGridRow { DataContext: ViewModels.PlannedJobItem { HasJob: true } row } && Vm is { } vm && vm.OpenRowJobCommand.CanExecute(row))
            vm.OpenRowJobCommand.Execute(row);
    }

    /// <summary>The run hint below the planned jobs carries the Administrator choice: scroll it into view when Run selected needs that choice.</summary>
    private void ShowRunHint() =>
        Dispatcher.BeginInvoke(System.Windows.Threading.DispatcherPriority.Loaded, new Action(() => RunHintBar.BringIntoView()));

    /// <summary>Page height = the viewport, but never less than the header (with the detected artifacts), a usable planned-jobs area and the
    /// archived evidence (then the page scrolls).</summary>
    private void FitPage()
    {
        // ActualHeight is already the new height inside SizeChanged; ViewportHeight still holds the old one then (that left the page at its
        // normal-size height, vertically centred in the maximized window, a large gap above the title)
        var viewport = PageScroll.ActualHeight - PageScroll.Padding.Top - PageScroll.Padding.Bottom;
        if (viewport <= 0) return;
        PageRoot.Height = Math.Max(viewport, PageHeader.ActualHeight + Math.Max(MinListAreaHeight, ScanDetails.ActualHeight + 260));
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
