using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using EzForensics.App.Services;

namespace EzForensics.App.Views;

public partial class ResultsView : UserControl
{
    private GridLength _savedLeft = new(680);
    private double _savedLeftMin = 180;
    private GridLength _savedSplitter = new(12);
    private bool _focusApplied;
    /// <summary>Width of the case pane the examiner chose (splitter drag); the pane shrinks below it only while the events side would get narrower than its minimum.</summary>
    private double _preferredLeft = 860;
    private bool _draggingPane;

    /// <summary>Tree / events star heights of the split right pane, kept for the app session (restored when the tree is opened again, also after leaving the page).</summary>
    private static double s_treeStar = 55, s_eventsStar = 45;
    private ViewModels.ResultsViewModel? _vm;

    public ResultsView()
    {
        InitializeComponent();
        Loaded += (_, _) =>
        {
            AppServices.Current.FocusModeChanged += OnFocusMode;
            ApplyFocus(AppServices.Current.IsFocusMode);
        };
        Unloaded += (_, _) =>
        {
            AppServices.Current.FocusModeChanged -= OnFocusMode;
            ApplyFocus(false);
            AppServices.Current.SetFocusMode(false); // leaving Analyze exits focus mode
        };
        PreviewKeyDown += OnPreviewKeyDown;
        DataContextChanged += (_, _) => HookVm();
        TreeSplitter.DragCompleted += (_, _) => RememberSplit();
        PaneSplitter.DragStarted += (_, _) => _draggingPane = true;
        PaneSplitter.DragCompleted += (_, _) =>
        {
            _draggingPane = false;
            if (!_focusApplied && LeftColumn.ActualWidth > 0) _preferredLeft = LeftColumn.ActualWidth;
        };
        Loaded += (_, _) => { HookVm(); ApplySplit(); };
    }

    private void HookVm()
    {
        if (_vm != null) _vm.PropertyChanged -= OnVmChanged;
        _vm = DataContext as ViewModels.ResultsViewModel;
        if (_vm != null) _vm.PropertyChanged += OnVmChanged;
        ApplySplit();
    }

    private void OnVmChanged(object? sender, System.ComponentModel.PropertyChangedEventArgs e)
    {
        if (e.PropertyName == nameof(ViewModels.ResultsViewModel.IsProcessTreeOpen)) ApplySplit();
    }

    /// <summary>Open tree: top / splitter / bottom rows with the remembered split; closed: the top rows collapse and the grid fills the pane.</summary>
    private void ApplySplit()
    {
        if (_vm?.IsProcessTreeOpen == true)
        {
            TreeRow.Height = new GridLength(s_treeStar, GridUnitType.Star);
            TreeSplitterRow.Height = GridLength.Auto;
            EventsRow.Height = new GridLength(s_eventsStar, GridUnitType.Star);
            TreeSplitter.Visibility = Visibility.Visible;
        }
        else
        {
            if (TreeSplitter.Visibility == Visibility.Visible) RememberSplit();
            TreeSplitter.Visibility = Visibility.Collapsed;
            TreeRow.Height = new GridLength(0);
            TreeSplitterRow.Height = new GridLength(0);
            EventsRow.Height = new GridLength(1, GridUnitType.Star);
        }
    }

    private void RememberSplit()
    {
        var t = TreeRow.ActualHeight; var e = EventsRow.ActualHeight;
        if (t > 20 && e > 20) { s_treeStar = t; s_eventsStar = e; }
    }

    private void OnFocusMode(bool on) => ApplyFocus(on);

    /// <summary>Focus mode: hides the case tree card and splitter (the events grid gets the whole page); column widths are kept.</summary>
    private void ApplyFocus(bool on)
    {
        if (on == _focusApplied) return;
        _focusApplied = on;
        if (on)
        {
            _savedLeft = LeftColumn.Width;
            _savedLeftMin = LeftColumn.MinWidth;
            _savedSplitter = SplitterColumn.Width;
            LeftColumn.MinWidth = 0;
            LeftColumn.Width = new GridLength(0);
            SplitterColumn.Width = new GridLength(0);
            LeftCard.Visibility = Visibility.Collapsed;
            PaneSplitter.Visibility = Visibility.Collapsed;
        }
        else
        {
            LeftCard.Visibility = Visibility.Visible;
            PaneSplitter.Visibility = Visibility.Visible;
            LeftColumn.MinWidth = _savedLeftMin;
            LeftColumn.Width = _savedLeft;
            SplitterColumn.Width = _savedSplitter;
        }
    }

    /// <summary>
    /// The case pane keeps the examiner's width while there is room and gives way when the window (or the docked Spectre) leaves the
    /// events grid less than its MinWidth. Done here because the grid itself only reports its desired width (a fixed-pixel column plus
    /// the grid's minimum), which would push the grid off the page instead of shrinking the pane.
    /// </summary>
    protected override Size MeasureOverride(Size constraint)
    {
        if (!_focusApplied && !_draggingPane && !double.IsInfinity(constraint.Width) && constraint.Width > 0)
        {
            var max = constraint.Width - SplitterColumn.Width.Value - MainColumn.MinWidth;
            var w = Math.Max(LeftColumn.MinWidth, Math.Min(_preferredLeft, max));
            if (Math.Abs(LeftColumn.Width.Value - w) > 0.5 || !LeftColumn.Width.IsAbsolute) LeftColumn.Width = new GridLength(w);
        }
        return base.MeasureOverride(constraint);
    }

    private void OnPreviewKeyDown(object sender, KeyEventArgs e)
    {
        var s = AppServices.Current;
        if (e.Key == Key.F11 && Keyboard.Modifiers == ModifierKeys.None)
        {
            s.SetFocusMode(!s.IsFocusMode);
            e.Handled = true;
        }
        else if (e.Key == Key.Escape && s.IsFocusMode)
        {
            s.SetFocusMode(false);
            e.Handled = true;
        }
    }
}
