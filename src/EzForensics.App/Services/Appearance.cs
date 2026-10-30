using System.Windows;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Threading;
using EzForensics.Core.Settings;

namespace EzForensics.App.Services;

/// <summary>
/// App-wide UI zoom and fonts. Zoom is the dynamic resource "Ui.Scale" (a frozen <see cref="ScaleTransform"/>, replaced on every
/// change) used as the LayoutTransform of every window's root, context menus, tool tips and popups; fonts are the dynamic resources Font.UI / FontSize.Body / Font.Mono / FontSize.Mono / Grid.RowHeight.
/// Text uses Display formatting at 100 % and Ideal formatting when scaled (Display text is blurry under a transform).
/// </summary>
public static class Appearance
{
    private static AppSettings? _settings;
    private static DispatcherTimer? _saveTimer;
    private static int _wheelAccumulator;

    /// <summary>
    /// Set on an element whose own Ctrl+0 shortcut wins while keyboard focus is inside it (Results grid: Ctrl+0 clears flags);
    /// the zoom reset stays on Ctrl+0 everywhere else and on the status bar button.
    /// </summary>
    public static readonly DependencyProperty HandlesCtrlZeroProperty = DependencyProperty.RegisterAttached(
        "HandlesCtrlZero", typeof(bool), typeof(Appearance), new PropertyMetadata(false));
    public static bool GetHandlesCtrlZero(DependencyObject d) => (bool)d.GetValue(HandlesCtrlZeroProperty);
    public static void SetHandlesCtrlZero(DependencyObject d, bool v) => d.SetValue(HandlesCtrlZeroProperty, v);

    private static bool FocusWantsCtrlZero(object? source)
    {
        if (source is System.Windows.Controls.Primitives.TextBoxBase) return false; // e.g. the Results filter row: zoom as usual
        for (var d = source as DependencyObject; d != null; d = d is Visual ? VisualTreeHelper.GetParent(d) : LogicalTreeHelper.GetParent(d))
            if (GetHandlesCtrlZero(d)) return true;
        return false;
    }

    /// <summary>Current zoom factor (1.0 = 100 %).</summary>
    public static double Zoom { get; private set; } = UiZoom.Default;

    /// <summary>Applies the saved zoom and fonts; call once after the settings load, before the first window.</summary>
    public static void Initialize(AppSettings settings)
    {
        _settings = settings;
        ApplyZoom(settings.UiZoom);
        ApplyFonts(settings.UiFontFamily, settings.UiFontSize, settings.MonoFontFamily, settings.MonoFontSize);
        settings.PropertyChanged += (_, e) =>
        {
            if (e.PropertyName == nameof(AppSettings.UiZoom)) ApplyZoom(settings.UiZoom);
        };
    }

    public static void ZoomIn() => SetZoom(UiZoom.In(Zoom));
    public static void ZoomOut() => SetZoom(UiZoom.Out(Zoom));
    public static void ZoomReset() => SetZoom(UiZoom.Default);

    /// <summary>Changes the zoom and saves it (debounced, so Ctrl+wheel bursts write once).</summary>
    public static void SetZoom(double zoom)
    {
        zoom = UiZoom.Clamp(zoom);
        if (_settings == null) { ApplyZoom(zoom); return; }
        _settings.UiZoom = zoom; // PropertyChanged applies it
        if (_saveTimer == null)
        {
            _saveTimer = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(400) };
            _saveTimer.Tick += (_, _) =>
            {
                _saveTimer.Stop();
                try { AppServices.Current.SaveSettings(); } catch (Exception ex) { AppLog.Error("Could not save settings", ex); }
            };
        }
        _saveTimer.Stop();
        _saveTimer.Start();
    }

    /// <summary>Writes a pending zoom change now (on exit).</summary>
    public static void FlushPendingSave()
    {
        if (_saveTimer is not { IsEnabled: true }) return;
        _saveTimer.Stop();
        try { AppServices.Current.SaveSettings(); } catch (Exception ex) { AppLog.Error("Could not save settings", ex); }
    }

    private static void ApplyZoom(double zoom)
    {
        zoom = UiZoom.Clamp(zoom);
        Zoom = zoom;
        // Resource dictionaries freeze Freezables, so a new transform replaces the old one (DynamicResource picks it up).
        var scale = new ScaleTransform(zoom, zoom);
        scale.Freeze();
        Application.Current.Resources["Ui.Scale"] = scale;
        Application.Current.Resources["Ui.TextFormatting"] =
            Math.Abs(zoom - 1.0) < 0.001 ? TextFormattingMode.Display : TextFormattingMode.Ideal;
    }

    /// <summary>Live font update (Settings page preview and startup).</summary>
    public static void ApplyFonts(string uiFamily, double uiSize, string monoFamily, double monoSize)
    {
        var res = Application.Current.Resources;
        res["Font.UI"] = new FontFamily(UiFonts.Family(uiFamily, UiFonts.DefaultUiFamily));
        res["FontSize.Body"] = UiFonts.ClampSize(uiSize, UiFonts.DefaultUiSize);
        res["Font.Mono"] = new FontFamily(UiFonts.Family(monoFamily, UiFonts.DefaultMonoFamily));
        res["FontSize.Mono"] = UiFonts.ClampSize(monoSize, UiFonts.DefaultMonoSize);
        res["Grid.RowHeight"] = UiFonts.GridRowHeight(monoSize);
    }

    /// <summary>Zoom shortcuts for a window: Ctrl+= / Ctrl++ / Ctrl+- / Ctrl+0 and Ctrl+wheel (handled in the tunnelling phase, so a grid never scrolls instead).</summary>
    public static void Attach(Window w)
    {
        w.PreviewKeyDown += OnPreviewKeyDown;
        w.PreviewMouseWheel += OnPreviewMouseWheel;
    }

    /// <summary>For popups that live outside a window's visual tree.</summary>
    public static void ApplyTo(FrameworkElement popupChild)
    {
        popupChild.SetResourceReference(FrameworkElement.LayoutTransformProperty, "Ui.Scale");
        popupChild.SetResourceReference(TextOptions.TextFormattingModeProperty, "Ui.TextFormatting");
    }

    private static void OnPreviewKeyDown(object sender, KeyEventArgs e)
    {
        if ((Keyboard.Modifiers & (ModifierKeys.Control | ModifierKeys.Alt)) != ModifierKeys.Control) return;
        var key = e.Key == Key.System ? e.SystemKey : e.Key;
        switch (key)
        {
            case Key.OemPlus: case Key.Add: ZoomIn(); e.Handled = true; break;
            case Key.OemMinus: case Key.Subtract: ZoomOut(); e.Handled = true; break;
            case Key.D0: case Key.NumPad0:
                if (FocusWantsCtrlZero(e.OriginalSource)) break;
                ZoomReset(); e.Handled = true; break;
        }
    }

    private static void OnPreviewMouseWheel(object sender, MouseWheelEventArgs e)
    {
        if ((Keyboard.Modifiers & ModifierKeys.Control) == 0) return;
        e.Handled = true;
        // Precision touchpads send small deltas: one zoom step per notch (120).
        if (Math.Sign(_wheelAccumulator) != Math.Sign(e.Delta)) _wheelAccumulator = 0;
        _wheelAccumulator += e.Delta;
        while (_wheelAccumulator >= Mouse.MouseWheelDeltaForOneLine) { ZoomIn(); _wheelAccumulator -= Mouse.MouseWheelDeltaForOneLine; }
        while (_wheelAccumulator <= -Mouse.MouseWheelDeltaForOneLine) { ZoomOut(); _wheelAccumulator += Mouse.MouseWheelDeltaForOneLine; }
    }
}
