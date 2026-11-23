using System.Windows;
using System.Windows.Automation;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Input;
using System.ComponentModel;
using System.Windows.Media;
using System.Windows.Media.Animation;
using System.Windows.Threading;
using EzForensics.App.ViewModels.Findings;

namespace EzForensics.App.Views.Findings;

/// <summary>
/// Findings page: status line, the three tabs (Overview, Story slides, Details with the editor beside the list) and the report flyout. The view model does the work; this wires
/// the page keys (J/K next/previous key event, A confirm, R reject, E edit, Esc closes the report; not while typing in a text box).
/// </summary>
public partial class FindingsView : UserControl
{
    private FindingsViewModel? _vm;

    /// <summary>Extended selection: the page groups the selected findings into one key event.</summary>
    private void FindingList_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (DataContext is FindingsViewModel vm) vm.SetSelectedRows(FindingList.SelectedItems.OfType<FindingRowViewModel>().ToList());
    }

    /// <summary>The dropdown under "Add to key event": the key events that exist, then "New key event…" (same pattern as Actions on Analyze).</summary>
    private void AddToKeyEventButton_Click(object sender, RoutedEventArgs e)
    {
        if (DataContext is not FindingsViewModel vm || sender is not Button b) return;
        var menu = new ContextMenu { PlacementTarget = b, Placement = PlacementMode.Bottom };
        AutomationProperties.SetAutomationId(menu, "Findings.AddToKeyEvent.Menu");
        foreach (var k in vm.KeyEventRows)
        {
            var mi = new MenuItem { Header = k.Title, Command = k.AddHereCommand };
            AutomationProperties.SetAutomationId(mi, "Findings.AddToKeyEvent.Item");
            menu.Items.Add(mi);
        }
        if (menu.Items.Count > 0) menu.Items.Add(new Separator());
        var created = new MenuItem { Header = "New key event…", Command = vm.NewKeyEventCommand };
        AutomationProperties.SetAutomationId(created, "Findings.AddToKeyEvent.New");
        menu.Items.Add(created);
        menu.IsOpen = true;
    }

    private void FindingMenu_Opened(object sender, RoutedEventArgs e) =>
        EditFindingItem.IsEnabled = DataContext is FindingsViewModel vm && vm.CanEditWorkspaceFinding(FindingList.SelectedItem as FindingRowViewModel);

    private void EditFinding_Click(object sender, RoutedEventArgs e)
    {
        if (DataContext is not FindingsViewModel vm || FindingList.SelectedItem is not FindingRowViewModel row || vm.WorkspaceDraftOf(row) is not { } draft) return;
        if (Analyze.KeyEventEditorWindow.Edit("Edit finding", draft, Window.GetWindow(this)) is { } result) vm.SaveWorkspaceDraft(row, result);
    }

    public FindingsView()
    {
        InitializeComponent();
        DataContextChanged += (_, _) => Hook(DataContext as FindingsViewModel);
        PreviewKeyDown += OnPreviewKeyDown;
        // coming back to the page (e.g. from Tools after setting Spectre up) re-checks the engine
        IsVisibleChanged += (_, e) => { if (e.NewValue is true && DataContext is FindingsViewModel { ShowSetup: true } v) v.RecheckEngineCommand.Execute(null); };
        SlideScroll.SizeChanged += (_, e) => FitSlideScene(e.NewSize.Height);
        PageBody.SizeChanged += PageBody_SizeChanged;
        EvidenceSplitter.DragCompleted += EvidenceSplitter_DragCompleted;
        OverviewScroll.SizeChanged += (_, e) => { if (_vm != null) _vm.IsWideLayout = e.NewSize.Width >= WideOverviewWidth; };
    }

    /// <summary>The Overview scroll width from which the tiles sit side by side and the key event cards use two columns.</summary>
    internal const double WideOverviewWidth = 900;

    /// <summary>The scene sits on top of the moment slide (about 210 px of words and buttons below it): below this slide height it
    /// shrinks (220 x 160 to 150 x 110), below <see cref="TinySlideHeight"/> further (110 x 80), so the actions fit without scrolling.</summary>
    internal const double ShortSlideHeight = 400;
    internal const double TinySlideHeight = 320;

    private void FitSlideScene(double slideHeight)
    {
        var (w, h) = slideHeight < TinySlideHeight ? (110, 80) : slideHeight < ShortSlideHeight ? (150, 110) : (220, 160);
        SlideIcon.Width = w;
        SlideIcon.Height = h;
    }

    private void Hook(FindingsViewModel? vm)
    {
        if (_vm != null)
        {
            _vm.KeyEventScrollRequested -= OnKeyEventScroll; _vm.FindingRowsSelectRequested -= OnFindingRowsSelect;
            _vm.EditRequested -= OnEditRequested; _vm.Walkthrough.PropertyChanged -= OnWalkthroughChanged; _vm.EvidencePanel.PropertyChanged -= OnEvidencePanelChanged; _vm.PropertyChanged -= OnVmPropertyChanged;
        }
        _vm = vm;
        if (vm != null)
        {
            vm.KeyEventScrollRequested += OnKeyEventScroll; vm.FindingRowsSelectRequested += OnFindingRowsSelect;
            vm.EditRequested += OnEditRequested; vm.Walkthrough.PropertyChanged += OnWalkthroughChanged; vm.EvidencePanel.PropertyChanged += OnEvidencePanelChanged; vm.PropertyChanged += OnVmPropertyChanged;
        }
    }

    // The lists sit in tabs that may not be on screen yet: wait for the layout, then scroll.
    private void OnKeyEventScroll(KeyEventRowViewModel row) =>
        Dispatcher.BeginInvoke(DispatcherPriority.Loaded, () => { KeyEventList.UpdateLayout(); KeyEventList.ScrollIntoView(row); });

    /// <summary>Selects all the key event's findings in the list; the first (earliest) stays the current one shown in the editor.</summary>
    private void OnFindingRowsSelect(IReadOnlyList<FindingRowViewModel> rows) =>
        Dispatcher.BeginInvoke(DispatcherPriority.Loaded, () =>
        {
            if (rows.Count == 0) return;
            FindingList.UpdateLayout();
            if (!ReferenceEquals(FindingList.SelectedItem, rows[0])) FindingList.SelectedItem = rows[0];
            foreach (var r in rows.Skip(1)) if (!FindingList.SelectedItems.Contains(r)) FindingList.SelectedItems.Add(r);
            FindingList.ScrollIntoView(rows[0]);
        });

    // Banner notifications (results of an action) fade out after 4 s; the timer waits while the mouse is over the banner.
    // Errors stay until dismissed (the X in the banner).
    private DispatcherTimer? _bannerTimer;

    private void OnVmPropertyChanged(object? sender, PropertyChangedEventArgs e)
    {
        if (e.PropertyName != nameof(FindingsViewModel.Banner)) return;
        _bannerTimer?.Stop();
        BannerHost.BeginAnimation(OpacityProperty, null);
        BannerHost.Opacity = 1;
        if (_vm?.Banner is not { } b || b.Severity == ViewModels.InfoSeverity.Error) return;
        _bannerTimer ??= NewBannerTimer();
        _bannerTimer.Start();
    }

    private DispatcherTimer NewBannerTimer()
    {
        var t = new DispatcherTimer { Interval = TimeSpan.FromSeconds(4) };
        t.Tick += (_, _) =>
        {
            if (BannerHost.IsMouseOver) return;   // paused while hovered: check again next tick
            t.Stop();
            var banner = _vm?.Banner;
            var fade = new DoubleAnimation(0, TimeSpan.FromMilliseconds(400));
            fade.Completed += (_, _) => { if (ReferenceEquals(_vm?.Banner, banner)) _vm!.Banner = null; BannerHost.BeginAnimation(OpacityProperty, null); BannerHost.Opacity = 1; };
            BannerHost.BeginAnimation(OpacityProperty, fade);
        };
        return t;
    }

    private const double EvidenceMin = 360;
    private static double _evidenceWidth;   // this session's width, 0 = not set yet

    /// <summary>The evidence panel is docked: it takes its own column (the page reflows to its left), opens at the saved width or 45% of
    /// the page, and slides in (skipped when Windows animations are off). Closing collapses the column.</summary>
    private void OnEvidencePanelChanged(object? sender, PropertyChangedEventArgs e)
    {
        if (e.PropertyName != nameof(EvidencePanelViewModel.IsOpen)) return;
        if (_vm?.EvidencePanel.IsOpen != true) { CloseEvidenceColumn(); return; }

        var settings = EzForensics.App.Services.AppServices.Current?.Settings;
        var total = PageBody.ActualWidth;
        var saved = _evidenceWidth > 0 ? _evidenceWidth : settings?.EvidencePanelWidth ?? 0;
        _evidenceDefaultPending = saved <= 0 && total <= 0;   // not laid out yet: take 45% once it is
        var w = ClampEvidence(saved > 0 ? saved : total * 0.45, total);
        EvidenceCol.MinWidth = EvidenceMin;
        EvidenceCol.Width = new GridLength(w);
        EvidenceSplitCol.Width = GridLength.Auto;
        EvidenceSplitter.Visibility = Visibility.Visible;
        if (EzForensics.App.Controls.Motion.Enabled && IsLoaded)
        {
            var ease = new CubicEase { EasingMode = EasingMode.EaseOut };
            var time = new Duration(TimeSpan.FromMilliseconds(240));
            EvidenceShift.BeginAnimation(TranslateTransform.XProperty, new DoubleAnimation(80, 0, time) { EasingFunction = ease });
            EvidenceSlide.BeginAnimation(OpacityProperty, new DoubleAnimation(0, 1, time) { EasingFunction = ease });
        }
        Dispatcher.BeginInvoke(DispatcherPriority.Input, () => EvidencePanelView.Grid.Focus());
    }

    private bool _evidenceDefaultPending;

    /// <summary>At least the panel minimum; at most what leaves the page its own minimum (when both do not fit, the panel keeps its minimum).</summary>
    private static double ClampEvidence(double w, double total) =>
        Math.Max(EvidenceMin, total > 0 ? Math.Min(w, total - 8 - 420) : w);

    private void PageBody_SizeChanged(object sender, SizeChangedEventArgs e)
    {
        if (!_evidenceDefaultPending || e.NewSize.Width <= 0 || _vm?.EvidencePanel.IsOpen != true) return;
        _evidenceDefaultPending = false;
        EvidenceCol.Width = new GridLength(ClampEvidence(e.NewSize.Width * 0.45, e.NewSize.Width));
    }

    private void CloseEvidenceColumn()
    {
        EvidenceSplitter.Visibility = Visibility.Collapsed;
        EvidenceSplitCol.Width = new GridLength(0);
        EvidenceCol.MinWidth = 0;
        EvidenceCol.Width = new GridLength(0);
    }

    private void EvidenceSplitter_DragCompleted(object sender, DragCompletedEventArgs e)
    {
        var w = EvidenceCol.ActualWidth;
        if (w < EvidenceMin) return;
        _evidenceWidth = w;
        var settings = EzForensics.App.Services.AppServices.Current?.Settings;
        if (settings == null) return;
        settings.EvidencePanelWidth = w;
        try { EzForensics.App.Services.AppServices.Current?.SaveSettings(); } catch { /* the width is also kept for the session */ }
    }

    private void OnWalkthroughChanged(object? sender, PropertyChangedEventArgs e)
    {
        if (e.PropertyName == nameof(WalkthroughViewModel.IsOpen) && _vm?.Walkthrough.IsOpen == true)
            Dispatcher.BeginInvoke(DispatcherPriority.Input, () => WalkthroughOverlay.Focus());
        else if (e.PropertyName == nameof(WalkthroughViewModel.Index)) AnimateSlideIn();
    }

    /// <summary>The slide's words fade and slide in (short; skipped when Windows animations are off).</summary>
    private void AnimateSlideIn()
    {
        if (!EzForensics.App.Controls.Motion.Enabled || !IsLoaded) return;
        var ease = new CubicEase { EasingMode = EasingMode.EaseOut };
        var time = new Duration(TimeSpan.FromMilliseconds(220));
        SlidePanel.BeginAnimation(OpacityProperty, new DoubleAnimation(0, 1, time) { EasingFunction = ease });
        SlideShift.BeginAnimation(TranslateTransform.XProperty, new DoubleAnimation(24, 0, time) { EasingFunction = ease });
        if (_vm?.Walkthrough.IsMoment == true) EzForensics.App.Controls.Motion.Pop(SlideIcon, 80, overshoot: true, durationMs: 420);
    }

    private void Flow_NodeClicked(string id) => _vm?.SelectFlowNode(id);
    private void Flow_NodeDoubleClicked(string id) => _vm?.OpenFlowNode(id);
    private void Flow_Replay(object sender, RoutedEventArgs e) => AttackFlow.Replay();

    /// <summary>Any click on the slides pauses autoplay (except on the Autoplay button itself).</summary>
    private void Walkthrough_PreviewMouseDown(object sender, MouseButtonEventArgs e)
    {
        for (var d = e.OriginalSource as DependencyObject; d != null; d = d is Visual ? VisualTreeHelper.GetParent(d) : LogicalTreeHelper.GetParent(d))
            if (ReferenceEquals(d, AutoplayButton)) return;
        _vm?.Walkthrough.PauseAutoplay();
        // A click on plain slide content leaves the focus where it was (a Border does not take it), e.g. in the nav or the chat,
        // and the arrow keys never reach this page. Take the focus unless a text box, button or other control just got it.
        if (!IsTyping(e.OriginalSource as DependencyObject) && !IsInsideControl(e.OriginalSource as DependencyObject)) WalkthroughOverlay.Focus();
    }

    private static bool IsInsideControl(DependencyObject? d)
    {
        for (; d != null; d = d is Visual or System.Windows.Media.Media3D.Visual3D ? VisualTreeHelper.GetParent(d) : LogicalTreeHelper.GetParent(d))
            if (d is System.Windows.Controls.Primitives.ButtonBase or Selector or Expander or ScrollBar) return true;
        return false;
    }

    private void OnEditRequested() => Dispatcher.BeginInvoke(DispatcherPriority.Input, () => { TitleBox.Focus(); TitleBox.SelectAll(); });

    private void ReportBackdrop_MouseDown(object sender, MouseButtonEventArgs e)
    {
        if (_vm != null) _vm.IsReportOpen = false;
    }

    private void OnPreviewKeyDown(object sender, KeyEventArgs e)
    {
        if (_vm is not { } vm) return;
        if (vm.EvidencePanel.IsOpen)
        {
            // the panel has its own keys (C, N, A, M, Del, J, K); Esc closes the note or the form first, then the panel
            if (e.Key != Key.Escape) return;
            var p = vm.EvidencePanel;
            if (p.IsNoteOpen) p.IsNoteOpen = false;
            else if (p.IsNewFindingOpen) p.IsNewFindingOpen = false;
            else p.Close();
            e.Handled = true;
            return;
        }
        if (e.Key == Key.Escape && vm.IsReportOpen) { vm.IsReportOpen = false; e.Handled = true; return; }
        if (vm.Walkthrough.IsOpen && !vm.IsReportOpen) { OnWalkthroughKey(vm.Walkthrough, e); return; }
        if (Keyboard.Modifiers != ModifierKeys.None || vm.IsReportOpen || IsTyping(Keyboard.FocusedElement as DependencyObject)) return;
        switch (e.Key)
        {
            case Key.J: vm.NextMoment(); break;
            case Key.K: vm.PreviousMoment(); break;
            case Key.A when vm.ApproveCommand.CanExecute(null): vm.ApproveCommand.Execute(null); break;
            case Key.R when vm.RejectCommand.CanExecute(null): vm.RejectCommand.Execute(null); break;
            case Key.E when vm.HasSelection: vm.RequestEdit(); break;
            default: return;
        }
        e.Handled = true;
    }

    /// <summary>Slides (the Timeline view of the Story tab): Left/Right, Page Up/Down, Home/End move; Space toggles autoplay (any other key pauses it).
    /// Does nothing in the Attack flow view or while typing.</summary>
    private static void OnWalkthroughKey(WalkthroughViewModel w, KeyEventArgs e)
    {
        if (!w.Page.ShowTimelineView || Keyboard.Modifiers != ModifierKeys.None || IsTyping(Keyboard.FocusedElement as DependencyObject)) return;
        if (e.Key == Key.Space) { w.ToggleAutoplay(); e.Handled = true; return; }
        if (w.HandleKey(e.Key)) e.Handled = true;
    }

    /// <summary>A text box (or an editable combo box) has the focus: letters are typed, not shortcuts.</summary>
    internal static bool IsTyping(DependencyObject? focused)
    {
        for (var d = focused; d != null; d = d is Visual or System.Windows.Media.Media3D.Visual3D ? VisualTreeHelper.GetParent(d) : LogicalTreeHelper.GetParent(d))
        {
            if (d is TextBoxBase or PasswordBox) return true;
            if (d is ComboBox { IsEditable: true }) return true;
        }
        return false;
    }
}
