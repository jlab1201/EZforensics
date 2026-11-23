using System.Windows;
using System.Windows.Controls;
using System.Windows.Data;
using System.Windows.Input;
using System.Windows.Threading;
using EzForensics.App.ViewModels;
using EzForensics.App.ViewModels.Findings;

namespace EzForensics.App.Views.Findings;

/// <summary>
/// The Findings page's evidence panel: the grid of one evidence file with the Spectre / analyst marks in a gutter. Columns are built here like
/// Analyze's EventsPane (one text column per CSV header, bound to <see cref="RowItem"/>'s indexer). Keys (when not typing): C confirm, N not relevant,
/// A add, M note, Del clear, J / K next / previous evidence row; Esc is handled by the page.
/// </summary>
public partial class EvidencePanelView : UserControl
{
    private EvidencePanelViewModel? _vm;
    private bool _syncing;

    public EvidencePanelView()
    {
        InitializeComponent();
        DataContextChanged += (_, _) => Hook(DataContext as EvidencePanelViewModel);
        PreviewKeyDown += OnPreviewKeyDown;
    }

    private void Hook(EvidencePanelViewModel? vm)
    {
        if (_vm != null)
        {
            _vm.ColumnsChanged -= BuildColumns; _vm.GoToPositionRequested -= GoTo; _vm.PropertyChanged -= OnVmChanged;
        }
        _vm = vm;
        if (vm != null)
        {
            vm.ColumnsChanged += BuildColumns; vm.GoToPositionRequested += GoTo; vm.PropertyChanged += OnVmChanged;
        }
        BuildColumns();
    }

    private void OnVmChanged(object? sender, System.ComponentModel.PropertyChangedEventArgs e)
    {
        if (e.PropertyName == nameof(EvidencePanelViewModel.IsNoteOpen) && _vm?.IsNoteOpen == true)
            Dispatcher.BeginInvoke(DispatcherPriority.Input, () => { NoteBox.Focus(); NoteBox.SelectAll(); });
        else if (e.PropertyName == nameof(EvidencePanelViewModel.IsNewFindingOpen) && _vm?.IsNewFindingOpen == true)
            Dispatcher.BeginInvoke(DispatcherPriority.Input, () => NewTitleBox.Focus());
    }

    private CellViewerWindow? _viewer;

    /// <summary>Double-click a cell: its full content in the cell viewer (the same window as Analyze).</summary>
    private void Grid_MouseDoubleClick(object sender, MouseButtonEventArgs e)
    {
        if (e.OriginalSource is not DependencyObject src || _vm?.Rows is not { } rows) return;
        var cell = Analyze.ViewHelpers.FindAncestor<DataGridCell>(src);
        if (cell?.DataContext is not RowItem row) return;
        int col = Grid.Columns.IndexOf(cell.Column) - 2;   // after the marks gutter and the row number
        if (col < 0 || col >= _vm.Headers.Count) return;
        e.Handled = true;
        if (_viewer == null || !_viewer.IsLoaded)
        {
            _viewer = new CellViewerWindow { Owner = Window.GetWindow(this) };
            _viewer.Closed += (_, _) => _viewer = null;
            _viewer.ShowCell(rows, _vm.Headers, row.Position, col);
            _viewer.Show();
        }
        else { _viewer.ShowCell(rows, _vm.Headers, row.Position, col); _viewer.Activate(); }
    }

    /// <summary>The gutter (marks), the file row number, then one column per CSV header (Analyze's default widths).</summary>
    private void BuildColumns()
    {
        Grid.Columns.Clear();
        if (_vm == null || _vm.Headers.Count == 0) return;
        Grid.Columns.Add(new DataGridTemplateColumn
        {
            Header = "", CellTemplate = (DataTemplate)FindResource("MarkCellTemplate"), Width = new DataGridLength(80),
            CanUserResize = false, CanUserReorder = false, CanUserSort = false,
        });
        Grid.Columns.Add(new DataGridTemplateColumn
        {
            Header = "Row", CellTemplate = (DataTemplate)FindResource("RowNumberTemplate"), Width = new DataGridLength(64),
            CanUserReorder = false, CanUserSort = false,
        });
        var cellStyle = (Style)FindResource("EvidenceCellText");
        for (int i = 0; i < _vm.Headers.Count; i++)
        {
            var h = _vm.Headers[i];
            Grid.Columns.Add(new DataGridTextColumn
            {
                Header = h, Binding = new Binding($"[{i}]") { Mode = BindingMode.OneTime }, ElementStyle = cellStyle,
                Width = new DataGridLength(Analyze.EventsPane.DefaultWidth(h)), MinWidth = 40, CanUserSort = false,
            });
        }
    }

    private void GoTo(int position)
    {
        if (_vm?.Rows is not { } rows || position < 0 || position >= rows.Count) return;
        Dispatcher.BeginInvoke(DispatcherPriority.Background, () =>
        {
            if (_vm?.Rows is not { } r || position >= r.Count) return;
            var item = r[position];
            _syncing = true;
            try { Grid.SelectedItem = item; }
            finally { _syncing = false; }
            Grid.ScrollIntoView(item);
        });
    }

    private void Grid_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (_vm == null || _syncing) return;
        var rows = Grid.SelectedItems.OfType<RowItem>().Select(r => r.Source).Distinct().OrderBy(r => r).ToList();
        _vm.SelectedRows = rows;
        if (Grid.CurrentItem is RowItem cur && rows.Contains(cur.Source)) _vm.CurrentRow = cur.Source;
    }

    private void OnPreviewKeyDown(object sender, KeyEventArgs e)
    {
        if (_vm is not { } vm || Keyboard.Modifiers != ModifierKeys.None || FindingsView.IsTyping(Keyboard.FocusedElement as DependencyObject)) return;
        System.Windows.Input.ICommand? cmd = e.Key switch
        {
            Key.C => vm.ConfirmCommand,
            Key.N => vm.NotRelevantCommand,
            Key.A => vm.AddCommand,
            Key.M => vm.NoteCommand,
            Key.Delete => vm.ClearCommand,
            Key.J => vm.NextRowCommand,
            Key.K => vm.PreviousRowCommand,
            _ => null,
        };
        if (cmd == null) return;
        if (cmd.CanExecute(null)) cmd.Execute(null);
        e.Handled = true;
    }
}
