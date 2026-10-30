using System.ComponentModel;
using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Input;
using System.Windows.Interop;
using System.Windows.Media.Animation;
using System.Windows.Threading;
using EzForensics.App.Services;
using EzForensics.App.ViewModels;
using EzForensics.Core.Execution;
using EzForensics.Core.Settings;

namespace EzForensics.App.Views;

public partial class MainWindow : Window
{
    private static readonly TimeSpan NavAnimation = TimeSpan.FromMilliseconds(170);
    private readonly DispatcherTimer _navEnterTimer = new() { Interval = TimeSpan.FromMilliseconds(120) };
    private readonly DispatcherTimer _navLeaveTimer = new() { Interval = TimeSpan.FromMilliseconds(280) };
    private ShellViewModel? _hooked;

    public MainWindow()
    {
        InitializeComponent();
        Appearance.Attach(this);
        TitleBarTheme.Attach(this); // follows ThemeManager (startup, toggle, settings preview); other windows via TitleBarTheme.Register
        _navEnterTimer.Tick += (_, _) =>
        {
            _navEnterTimer.Stop();
            if (Shell is { NavPinned: false } s && NavPanel.IsMouseOver) s.NavExpanded = true;
        };
        _navLeaveTimer.Tick += (_, _) =>
        {
            // Stay open while the pointer is back over the drawer, or a menu / captured control from it is still open.
            if (Shell is not { NavPinned: false, NavExpanded: true } s) { _navLeaveTimer.Stop(); return; }
            if (NavPanel.IsMouseOver || NavHasOpenPopup()) return;
            _navLeaveTimer.Stop();
            s.NavExpanded = false;
        };
        DataContextChanged += (_, _) => HookShell();
        Loaded += (_, _) => { HookShell(); HookHotKeys(); };
    }

    private ShellViewModel? Shell => DataContext as ShellViewModel;

    // ---- Back: customizable hot key (App.Back, default Alt+Left) and the mouse back button
    private IReadOnlyDictionary<string, string> _hotKeyMap = HotKeys.Resolve(null);
    private AppSettings? _hookedSettings;

    private void HookHotKeys()
    {
        var settings = AppServices.Current.Settings;
        if (_hookedSettings == settings) return;
        if (_hookedSettings != null) _hookedSettings.PropertyChanged -= OnSettingsChanged;
        _hookedSettings = settings;
        settings.PropertyChanged += OnSettingsChanged;
        _hotKeyMap = HotKeys.Resolve(settings.HotKeys);
    }

    private void OnSettingsChanged(object? sender, PropertyChangedEventArgs e)
    {
        if (e.PropertyName == nameof(AppSettings.HotKeys)) _hotKeyMap = HotKeys.Resolve(AppServices.Current.Settings.HotKeys);
    }

    protected override void OnPreviewKeyDown(KeyEventArgs e)
    {
        base.OnPreviewKeyDown(e);
        if (e.Handled || Shell is not { } s) return;
        if (Keyboard.FocusedElement is System.Windows.Controls.Primitives.TextBoxBase or System.Windows.Controls.PasswordBox or System.Windows.Controls.ComboBox { IsEditable: true }) return;
        if (HotKeyInput.FromEvent(e) is not { } g) return;
        if (HotKeys.ActionFor(_hotKeyMap, g) != HotKeys.Back) return;
        if (s.GoBackCommand.CanExecute(null)) s.GoBackCommand.Execute(null);
        e.Handled = true;
    }

    protected override void OnPreviewMouseDown(MouseButtonEventArgs e)
    {
        base.OnPreviewMouseDown(e);
        if (e.ChangedButton != MouseButton.XButton1 || Shell is not { } s) return;
        if (s.GoBackCommand.CanExecute(null)) s.GoBackCommand.Execute(null);
        e.Handled = true;
    }

    private void HookShell()
    {
        if (_hooked == Shell) return;
        if (_hooked != null) _hooked.PropertyChanged -= OnShellPropertyChanged;
        _hooked = Shell;
        if (_hooked == null) return;
        _hooked.PropertyChanged += OnShellPropertyChanged;
        UpdateNavWidth(animate: false);
        ShowPage();
    }

    // The Analyze view is built once and reused: re-creating it on every visit (Back included) rebuilt the whole grid and tree.
    private ResultsView? _resultsView;

    private void ShowPage()
    {
        var page = Shell?.CurrentPage;
        if (page is ResultsViewModel rvm)
        {
            _resultsView ??= new ResultsView { DataContext = rvm };
            PageHost.Content = _resultsView;
        }
        else PageHost.Content = page;
    }

    private void OnShellPropertyChanged(object? sender, PropertyChangedEventArgs e)
    {
        if (e.PropertyName == nameof(ShellViewModel.CurrentPage)) ShowPage();
        else if (e.PropertyName == nameof(ShellViewModel.NavWidth))
            UpdateNavWidth(animate: Shell is { NavPinned: false });
        else if (e.PropertyName == nameof(ShellViewModel.IsFocusMode))
        {
            _navEnterTimer.Stop();
            _navLeaveTimer.Stop();
            NavPanel.Visibility = Shell is { IsFocusMode: true } ? Visibility.Collapsed : Visibility.Visible;
            // a restored overlay (unpinned, expanded before focus mode) closes again like after a hover once the mouse is elsewhere
            if (Shell is { IsFocusMode: false, NavPinned: false, NavExpanded: true } && !NavPanel.IsMouseOver) _navLeaveTimer.Start();
        }
        else if (e.PropertyName == nameof(ShellViewModel.NavPinned))
        {
            _navEnterTimer.Stop();
            _navLeaveTimer.Stop();
            UpdateNavWidth(animate: false);
        }
    }

    /// <summary>
    /// Only the drawer's Width changes (content is laid out at full width and clipped), so the rail icons never move.
    /// Unpinned hover/hamburger: ease-out width animation; pinning/unpinning: immediate.
    /// </summary>
    private void UpdateNavWidth(bool animate)
    {
        if (Shell is not { } s) return;
        var target = s.NavWidth;
        if (!animate)
        {
            NavPanel.BeginAnimation(WidthProperty, null);
            NavPanel.Width = target;
            return;
        }
        var anim = new DoubleAnimation(target, NavAnimation)
        {
            EasingFunction = new CubicEase { EasingMode = EasingMode.EaseOut },
        };
        NavPanel.BeginAnimation(WidthProperty, anim, HandoffBehavior.SnapshotAndReplace);
    }

    private bool NavHasOpenPopup() =>
        Shell is { IsSettingsExpanded: true } ||
        Mouse.Captured is DependencyObject c && (c is System.Windows.Controls.ContextMenu || NavPanel.IsAncestorOf(c));

    // Settings flyout. StaysOpen=False closes it on the mouse-down that also lands on the Settings button; ignore that click's reopen.
    private DateTime _flyoutClosedAt = DateTime.MinValue;

    private void Gear_Click(object sender, RoutedEventArgs e)
    {
        if (Shell is not { } s) return;
        if (DateTime.UtcNow - _flyoutClosedAt < TimeSpan.FromMilliseconds(250)) return;
        s.IsSettingsExpanded = !s.IsSettingsExpanded;
    }

    private void SettingsFlyout_Opened(object? sender, EventArgs e) =>
        Dispatcher.BeginInvoke(new Action(() => FlyoutToolsButton.Focus()), DispatcherPriority.Input);

    private void SettingsFlyout_Closed(object? sender, EventArgs e)
    {
        _flyoutClosedAt = DateTime.UtcNow;
        if (Shell is { NavPinned: false, NavExpanded: true } && !NavPanel.IsMouseOver) _navLeaveTimer.Start();
    }

    private void SettingsFlyout_PreviewKeyDown(object sender, KeyEventArgs e)
    {
        if (e.Key != Key.Escape) return;
        if (Shell is { } s) s.IsSettingsExpanded = false;
        GearButton.Focus();
        e.Handled = true;
    }

    // Unpinned nav: hover (after a short delay) or keyboard focus expands the rail as an overlay; leaving collapses it after a delay.
    private void Nav_MouseEnter(object sender, MouseEventArgs e)
    {
        _navLeaveTimer.Stop();
        if (Shell is { NavPinned: false, NavExpanded: false }) _navEnterTimer.Start();
    }

    private void Nav_MouseLeave(object sender, MouseEventArgs e)
    {
        _navEnterTimer.Stop();
        if (Shell is { NavPinned: false, NavExpanded: true }) _navLeaveTimer.Start();
    }

    private void Nav_GotKeyboardFocus(object sender, KeyboardFocusChangedEventArgs e)
    {
        if (Shell is { NavPinned: false, NavExpanded: false } s && InputManager.Current.MostRecentInputDevice is KeyboardDevice)
            s.NavExpanded = true;
    }

    private void Nav_FocusWithinChanged(object sender, DependencyPropertyChangedEventArgs e)
    {
        if (e.NewValue is false && Shell is { NavPinned: false } s && !NavPanel.IsMouseOver && !NavHasOpenPopup()) s.NavExpanded = false;
    }

    protected override void OnClosing(CancelEventArgs e)
    {
        var active = AppServices.Current.Queue.Jobs.Count(j => j.State is JobState.Running or JobState.Queued);
        if (active > 0)
        {
            if (!EzForensics.App.Services.Dialogs.Confirm("Jobs still running", $"{active} job(s) are running or queued. Cancel them and exit?", "Cancel jobs and exit", "Stay", EzForensics.App.Views.EzDialogSeverity.Warning, defaultNo: true, owner: this)) { e.Cancel = true; return; }
        }
        Appearance.FlushPendingSave();
        base.OnClosing(e);
    }

}
