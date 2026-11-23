using System.Globalization;
using System.Windows;
using System.Windows.Automation;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Media.Animation;
using EzForensics.App.Services;
using EzForensics.App.ViewModels.Findings;
using EzForensics.Core.Settings;
using EzForensics.Core.Spectre;

namespace EzForensics.App.Controls;

/// <summary>
/// The Findings story in one track, for any reader: a time axis made of the key events (story scale: moments seconds apart share a point with
/// a "×N" badge, longer gaps get more room on a log scale and a label like "3 days" or "15 yrs"; other findings are faint dots, and those outside
/// the moments are counted as "N earlier" / "N later"), numbered cards for the key events above it (colour = severity only, and the word is written too), a line from each card to its point or range on the axis. Geometry comes from <see cref="StoryTimelineLayout"/>; this control only paints it.
/// <see cref="ViewStart"/>/<see cref="ViewEnd"/> (axis x, animatable) pick the part shown; unset shows all. With <see cref="FollowFocus"/> the
/// view glides to <see cref="FocusMomentId"/> (moments before it dim, later ones are faint). Hover shows the technical terms; click selects.
/// </summary>
public sealed class StoryTimeline : FrameworkElement
{
    internal const double BadgeR = 8.5, BadgeGap = 14;
    internal const double Pad = 14, CardW = 190, CardH = 92, ConnectorGap = 18, TickRowH = 18, Bottom = 6;
    /// <summary>Room at both ends of the story scale for the "N earlier" / "N later" captions.</summary>
    private const double EdgePad = 64;
    private static readonly Duration GlideTime = new(TimeSpan.FromMilliseconds(450));

    public static readonly DependencyProperty TimelineProperty = DependencyProperty.Register(nameof(Timeline), typeof(SpectreTimeline), typeof(StoryTimeline),
        new FrameworkPropertyMetadata(null, FrameworkPropertyMetadataOptions.AffectsRender | FrameworkPropertyMetadataOptions.AffectsMeasure,
            (d, _) => ((StoryTimeline)d).OnTimelineChanged()));
    public static readonly DependencyProperty SelectedMomentIdProperty = DependencyProperty.Register(nameof(SelectedMomentId), typeof(string), typeof(StoryTimeline),
        new FrameworkPropertyMetadata(null, FrameworkPropertyMetadataOptions.AffectsRender | FrameworkPropertyMetadataOptions.BindsTwoWayByDefault));
    public static readonly DependencyProperty FocusMomentIdProperty = DependencyProperty.Register(nameof(FocusMomentId), typeof(string), typeof(StoryTimeline),
        new FrameworkPropertyMetadata(null, FrameworkPropertyMetadataOptions.AffectsRender, (d, _) => ((StoryTimeline)d).OnFocusChanged()));
    public static readonly DependencyProperty FollowFocusProperty = DependencyProperty.Register(nameof(FollowFocus), typeof(bool), typeof(StoryTimeline),
        new FrameworkPropertyMetadata(false, FrameworkPropertyMetadataOptions.AffectsRender, (d, _) => ((StoryTimeline)d).OnFocusChanged()));
    public static readonly DependencyProperty FocusZoomProperty = DependencyProperty.Register(nameof(FocusZoom), typeof(double), typeof(StoryTimeline),
        new FrameworkPropertyMetadata(2.5));
    public static readonly DependencyProperty ViewStartProperty = DependencyProperty.Register(nameof(ViewStart), typeof(double), typeof(StoryTimeline),
        new FrameworkPropertyMetadata(double.NaN, FrameworkPropertyMetadataOptions.AffectsRender));
    public static readonly DependencyProperty ViewEndProperty = DependencyProperty.Register(nameof(ViewEnd), typeof(double), typeof(StoryTimeline),
        new FrameworkPropertyMetadata(double.NaN, FrameworkPropertyMetadataOptions.AffectsRender));

    /// <summary>Playback position 0..1 along the axis; NaN (the default) shows everything (the end state).</summary>
    public static readonly DependencyProperty PlayProgressProperty = DependencyProperty.Register(nameof(PlayProgress), typeof(double), typeof(StoryTimeline),
        new FrameworkPropertyMetadata(double.NaN, FrameworkPropertyMetadataOptions.AffectsRender));
    public static readonly DependencyProperty IsPlayingProperty = DependencyProperty.Register(nameof(IsPlaying), typeof(bool), typeof(StoryTimeline),
        new FrameworkPropertyMetadata(false));
    public double PlayProgress { get => (double)GetValue(PlayProgressProperty); set => SetValue(PlayProgressProperty, value); }
    /// <summary>True while the story plays (the page's Play button reads Stop).</summary>
    public bool IsPlaying { get => (bool)GetValue(IsPlayingProperty); private set => SetValue(IsPlayingProperty, value); }

    public SpectreTimeline? Timeline { get => (SpectreTimeline?)GetValue(TimelineProperty); set => SetValue(TimelineProperty, value); }
    /// <summary>The selected moment (ringed). A click on a card sets it.</summary>
    public string? SelectedMomentId { get => (string?)GetValue(SelectedMomentIdProperty); set => SetValue(SelectedMomentIdProperty, value); }
    /// <summary>The slide's moment: earlier moments are dimmed, later ones faint. Null = no focus (everything at full strength).</summary>
    public string? FocusMomentId { get => (string?)GetValue(FocusMomentIdProperty); set => SetValue(FocusMomentIdProperty, value); }
    /// <summary>When true, a change of <see cref="FocusMomentId"/> glides the view to centre that moment (or back to the whole when null).</summary>
    public bool FollowFocus { get => (bool)GetValue(FollowFocusProperty); set => SetValue(FollowFocusProperty, value); }
    /// <summary>How far the focus view zooms in (1 = the whole axis).</summary>
    public double FocusZoom { get => (double)GetValue(FocusZoomProperty); set => SetValue(FocusZoomProperty, value); }
    /// <summary>Left end of the view in axis x; NaN (with <see cref="ViewEnd"/>) shows everything.</summary>
    public double ViewStart { get => (double)GetValue(ViewStartProperty); set => SetValue(ViewStartProperty, value); }
    public double ViewEnd { get => (double)GetValue(ViewEndProperty); set => SetValue(ViewEndProperty, value); }

    /// <summary>Raised when a card is clicked (the moment id).</summary>
    public event Action<string>? MomentClicked;

    private enum HitKind { Card, Marker, Cluster, Break }
    private sealed record Hit(HitKind Kind, Rect Bounds, string? MomentId = null, AxisBreak? Break = null, StoryCluster? Cluster = null);

    private readonly List<Hit> _hits = new();
    private Dictionary<string, SpectreTimelineFinding> _byId = new(StringComparer.OrdinalIgnoreCase);
    private Dictionary<string, SpectreMoment> _moments = new();
    private StoryTimelineLayout? _layout;
    private double _layoutWidth = -1, _layoutZoom = 1;
    private Hit? _hover;
    private Point _hoverPoint;

    // zoom and pan: the layout is rebuilt at Width x zoom (so points that shared a pixel split) and the view shows one plot width of it
    private const double MinMaxZoom = 20, WheelStep = 1.2;
    internal const double TopH = 28;
    /// <summary>The furthest the timeline zooms in at the moment: grows with the time the story covers (see <see cref="MaxZoomFor"/>).</summary>
    private double _maxZoom = MinMaxZoom;
    private double _zoom = 1, _centerFrac = 0.5;
    private enum DragMode { None, Pan, Select }
    private DragMode _dragMode;
    private Point? _dragFrom;
    private double _dragViewStart;
    private bool _dragged;
    private double _selFromX, _selToX;

    /// <summary>
    /// How far the timeline may zoom in for a story covering <paramref name="span"/>: far enough that the whole width can show about a minute
    /// (never less than 20x, never more than 5000x), so a case of days reaches minutes and seconds while a short one keeps the old limit.
    /// </summary>
    public static double MaxZoomFor(TimeSpan span) => Math.Clamp(Math.Floor(span.TotalSeconds / 60.0), MinMaxZoom, 5000);

    /// <summary>The current zoom limit.</summary>
    public double MaxZoom => _maxZoom;

    /// <summary>Raised when a dragged time range was zoomed to (start and end, UTC).</summary>
    public event Action<DateTimeOffset, DateTimeOffset>? RangeSelected;
    /// <summary>Raised when the user resets the view (Fit): a range filter set from the selection should go.</summary>
    public event Action? RangeCleared;
    private readonly StackPanel _zoomBar;
    private Window? _keyWindow;

    public StoryTimeline()
    {
        Focusable = false;
        ClipToBounds = true;
        SnapsToDevicePixels = true;
        _zoomBar = new StackPanel { Orientation = Orientation.Horizontal };
        var info = new TextBlock { Text = "\uE946", VerticalAlignment = VerticalAlignment.Center, Margin = new Thickness(0, 0, 4, 0),
            ToolTip = "Mouse wheel zooms around the pointer. Drag across the timeline to select a time range and zoom to it. Shift + wheel, middle-drag, or dragging the axis strip pans. Fit shows everything again. Times are UTC." };
        info.SetResourceReference(StyleProperty, "InfoGlyph");
        AutomationProperties.SetAutomationId(info, "Findings.Timeline.Help");
        _zoomBar.Children.Add(info);
        foreach (var (glyph, id, name, act) in new (string, string, string, Action)[]
        {
            ("−", "Findings.Timeline.ZoomOut", "Zoom out", ZoomOut), ("+", "Findings.Timeline.ZoomIn", "Zoom in", ZoomIn), ("Fit", "Findings.Timeline.Fit", "Fit the whole story", FitFromUser),
        })
        {
            var b = new Button { Content = glyph, MinWidth = 26, Height = 22, Padding = new Thickness(6, 0, 6, 0), Margin = new Thickness(3, 0, 0, 0), Focusable = false,
                ToolTip = name };
            b.SetResourceReference(StyleProperty, "SubtleButton");
            AutomationProperties.SetAutomationId(b, id);
            AutomationProperties.SetName(b, name);
            b.Click += (_, e) => { act(); e.Handled = true; };
            _zoomBar.Children.Add(b);
        }
        AddVisualChild(_zoomBar);
        AddLogicalChild(_zoomBar);
        Loaded += (_, _) =>
        {
            ThemeManager.Changed += OnThemeChanged;
            _keyWindow = Window.GetWindow(this);
            if (_keyWindow != null) _keyWindow.PreviewKeyDown += OnWindowKeyDown;
        };
        Unloaded += (_, _) =>
        {
            ThemeManager.Changed -= OnThemeChanged;
            if (_keyWindow != null) _keyWindow.PreviewKeyDown -= OnWindowKeyDown;
            _keyWindow = null;
        };
    }

    protected override int VisualChildrenCount => 1;
    protected override Visual GetVisualChild(int index) => _zoomBar;
    protected override System.Collections.IEnumerator LogicalChildren => new object[] { _zoomBar }.GetEnumerator();

    private void OnThemeChanged(AppTheme _) => InvalidateVisual();

    private void OnTimelineChanged()
    {
        var tl = Timeline;
        _byId = tl == null ? new(StringComparer.OrdinalIgnoreCase) : StoryText.Index(tl);
        _moments = tl == null ? new() : tl.Moments.GroupBy(m => m.Id).ToDictionary(g => g.Key, g => g.First());
        _layout = null; _layoutWidth = -1; _hover = null;
        _zoom = 1; _centerFrac = 0.5;
        var times = tl?.Moments.Where(m => m.Time != null).SelectMany(m => new[] { m.Time!.Value, m.End ?? m.Time!.Value }).ToList();
        _maxZoom = times is { Count: > 0 } ? MaxZoomFor(times.Max() - times.Min()) : MinMaxZoom;
        if (IsPlaying) Stop();
        ResetView();
    }

    /// <summary>The layout for a plot width (at the current zoom), rebuilt only when the width, the zoom or the timeline changes.</summary>
    internal StoryTimelineLayout? LayoutFor(double plotWidth)
    {
        if (Timeline is not { } tl || plotWidth < 40) return null;
        plotWidth = Math.Round(plotWidth);
        if (_layout != null && Math.Abs(_layoutWidth - plotWidth) < 0.5 && _layoutZoom == _zoom) return _layout;
        _layout = StoryTimelineLayout.Build(tl, new StoryTimelineOptions(Width: plotWidth * _zoom, CardWidth: CardW, CardHeight: CardH, RowGap: BadgeGap, CardGap: BadgeGap, EdgePad: EdgePad,
            ClusterSeconds: StoryTimelineLayout.ClusterSecondsFor(_zoom)));
        _layoutWidth = plotWidth; _layoutZoom = _zoom;
        return _layout;
    }

    internal static double HeightFor(StoryTimelineLayout lay) => lay.IsEmpty ? 0 : TopH + lay.CalloutAreaHeight + ConnectorGap + TickRowH + Bottom + 4;

    protected override Size MeasureOverride(Size availableSize)
    {
        double w = double.IsInfinity(availableSize.Width) ? 800 : availableSize.Width;
        var lay = LayoutFor(w - 2 * Pad);
        _zoomBar.Visibility = lay == null || lay.IsEmpty ? Visibility.Collapsed : Visibility.Visible;
        _zoomBar.Measure(new Size(double.PositiveInfinity, double.PositiveInfinity));
        return new Size(double.IsInfinity(availableSize.Width) ? 0 : w, lay == null ? 0 : HeightFor(lay));
    }

    protected override Size ArrangeOverride(Size finalSize)
    {
        var d = _zoomBar.DesiredSize;
        _zoomBar.Arrange(new Rect(Math.Max(0, finalSize.Width - d.Width - Pad), 3, d.Width, d.Height));
        return finalSize;
    }

    // ------------------------------------------------------------------ zoom and pan

    /// <summary>The current zoom (1 = the whole story fits).</summary>
    public double Zoom => _zoom;

    public void ZoomIn() => SetZoom(_zoom * 1.5);
    private void FitFromUser() { Fit(); RangeCleared?.Invoke(); }
    public void ZoomOut() => SetZoom(_zoom / 1.5);

    /// <summary>Back to zoom 1 (the slide's focus window when following).</summary>
    public void Fit()
    {
        if (_zoom == 1 && double.IsNaN(ViewStart) && !FollowFocus) return;
        _zoom = 1; _centerFrac = 0.5;
        InvalidateMeasure();
        ResetView();
        InvalidateVisual();
    }

    /// <summary>How much the view magnifies the axis built at Width x zoom: the slide focus can ask for more than the user's zoom.</summary>
    private double Magnification => FollowFocus && FocusMomentId != null ? Math.Max(FocusZoom, _zoom) : _zoom;

    /// <summary>Zooms to <paramref name="z"/> (1..<see cref="MaxZoom"/>) keeping the time under <paramref name="screenX"/> (default: the middle) where it is.</summary>
    internal void SetZoom(double z, double? screenX = null)
    {
        z = Math.Clamp(double.IsFinite(z) ? z : 1, 1, _maxZoom);
        if (Math.Abs(z - _zoom) < 1e-6) return;
        double plotW = ActualWidth - 2 * Pad;
        var lay = LayoutFor(plotW);
        if (lay == null || lay.IsEmpty) { _zoom = z; return; }
        if (z <= 1 && !FollowFocus) { Fit(); return; }
        var (vs, ve) = EffectiveView(lay);
        double frac = Math.Clamp(((screenX ?? Pad + plotW / 2) - Pad) / plotW, 0, 1);
        var t = lay.Time(vs + frac * (ve - vs));
        _zoom = z;
        var nl = LayoutFor(plotW)!;
        double width = nl.TotalWidth / Magnification;
        double start = Math.Clamp(nl.X(t) - frac * width, 0, Math.Max(0, nl.TotalWidth - width));
        ShowWindow(nl, start, width);
        InvalidateMeasure();
    }

    /// <summary>The zoom at which a point with several moments splits (grouping window and pixel merge both let go), at least a step above the current one; capped at the limit.</summary>
    internal double ZoomToSplit(StoryCluster cl)
    {
        var ids = cl.MomentIds;
        double z = Math.Clamp(_zoom * 1.5, 1, _maxZoom);
        if (ids.Count < 2 || Timeline is not { } tl || _maxZoom <= _zoom) return z;
        var times = ids.Select(i => _moments.TryGetValue(i, out var m) ? m.Time : null).Where(t => t != null).Select(t => t!.Value).OrderBy(t => t).ToList();
        double widest = 0;
        for (int i = 1; i < times.Count; i++) widest = Math.Max(widest, (times[i] - times[i - 1]).TotalSeconds);
        if (widest <= 0) return z;   // the same instant: never splits
        z = Math.Max(z, Math.Min(_maxZoom, StoryTimelineLayout.BaseClusterSeconds / widest * 1.02));
        double plotW = Math.Round(ActualWidth - 2 * Pad);
        var first = ids[0];
        for (int guard = 0; guard < 40 && z < _maxZoom; guard++)
        {
            var l = StoryTimelineLayout.Build(tl, new StoryTimelineOptions(Width: plotW * z, CardWidth: CardW, CardHeight: CardH, RowGap: BadgeGap, CardGap: BadgeGap, EdgePad: EdgePad,
                ClusterSeconds: StoryTimelineLayout.ClusterSecondsFor(z)));
            if (l.ClusterOf(first) is { } c && c.MomentIds.Count < ids.Count) return z;
            z = Math.Min(_maxZoom, z * 1.4);
        }
        return Math.Min(z, _maxZoom);
    }

    /// <summary>Zooms in on a point with several moments until they split (or the zoom is at its limit), centred on it.</summary>
    private void ZoomToCluster(StoryCluster cl)
    {
        var t = cl.Start + (cl.End - cl.Start) / 2;
        _zoom = ZoomToSplit(cl);
        var nl = LayoutFor(ActualWidth - 2 * Pad);
        if (nl == null || nl.IsEmpty) return;
        var w = nl.CenterOnX(nl.X(t), Magnification);
        ShowWindow(nl, w.Start, w.Width);
        InvalidateMeasure();
    }

    private void ShowWindow(StoryTimelineLayout lay, double start, double width)
    {
        BeginAnimation(ViewStartProperty, null);
        BeginAnimation(ViewEndProperty, null);
        ViewStart = start; ViewEnd = start + width;
        _centerFrac = lay.TotalWidth > 0 ? (start + width / 2) / lay.TotalWidth : 0.5;
        InvalidateVisual();
    }

    private bool IsZoomedIn(StoryTimelineLayout lay)
    {
        var (vs, ve) = EffectiveView(lay);
        return ve - vs < lay.TotalWidth - 0.5;
    }

    private void OnWindowKeyDown(object sender, KeyEventArgs e)
    {
        if (!IsMouseOver || !IsVisible || Keyboard.FocusedElement is System.Windows.Controls.Primitives.TextBoxBase) return;
        switch (e.Key)
        {
            case Key.OemPlus or Key.Add: ZoomIn(); e.Handled = true; break;
            case Key.OemMinus or Key.Subtract: ZoomOut(); e.Handled = true; break;
            case Key.D0 or Key.NumPad0: FitFromUser(); e.Handled = true; break;
        }
    }

    protected override void OnMouseWheel(MouseWheelEventArgs e)
    {
        var lay = LayoutFor(ActualWidth - 2 * Pad);
        if (lay == null || lay.IsEmpty) { base.OnMouseWheel(e); return; }
        if ((Keyboard.Modifiers & ModifierKeys.Shift) != 0)
        {
            // Shift + wheel pans by a tenth of the view per notch
            if (IsZoomedIn(lay))
            {
                var (vs, ve) = EffectiveView(lay);
                double width = ve - vs, step = width * 0.1 * (e.Delta > 0 ? -1 : 1);
                ShowWindow(lay, Math.Clamp(vs + step, 0, Math.Max(0, lay.TotalWidth - width)), width);
            }
        }
        else SetZoom(_zoom * Math.Pow(WheelStep, e.Delta / 120.0), e.GetPosition(this).X);
        e.Handled = true;   // the page's own scroll must not move too
    }

    /// <summary>True when a point is on the axis strip (the axis and its tick row): dragging there pans.</summary>
    private bool OnAxisStrip(StoryTimelineLayout lay, Point p)
    {
        double axisY = TopH + lay.CalloutAreaHeight + ConnectorGap;
        return p.Y >= axisY - 4 && p.Y <= axisY + TickRowH + 2;
    }

    protected override void OnMouseDown(MouseButtonEventArgs e)
    {
        base.OnMouseDown(e);
        if (e.ChangedButton != MouseButton.Middle) return;
        var lay = LayoutFor(ActualWidth - 2 * Pad);
        if (lay == null || lay.IsEmpty || !IsZoomedIn(lay)) return;
        BeginDrag(DragMode.Pan, e.GetPosition(this), lay);
        e.Handled = true;
    }

    protected override void OnMouseUp(MouseButtonEventArgs e)
    {
        base.OnMouseUp(e);
        if (e.ChangedButton == MouseButton.Middle && _dragMode == DragMode.Pan) EndDrag();
    }

    private void BeginDrag(DragMode mode, Point from, StoryTimelineLayout lay)
    {
        _dragged = false;
        _dragMode = mode;
        _dragFrom = from;
        _dragViewStart = EffectiveView(lay).Start;
        _selFromX = _selToX = Math.Clamp(from.X, Pad, ActualWidth - Pad);
        CaptureMouse();
    }

    private void EndDrag()
    {
        if (IsMouseCaptured) ReleaseMouseCapture();
        _dragFrom = null; _dragMode = DragMode.None; _dragged = false;
        Cursor = null;
        InvalidateVisual();
    }

    protected override void OnMouseLeftButtonDown(MouseButtonEventArgs e)
    {
        base.OnMouseLeftButtonDown(e);
        _dragged = false;
        var lay = LayoutFor(ActualWidth - 2 * Pad);
        if (lay == null || lay.IsEmpty || IsPlaying) { _dragFrom = null; _dragMode = DragMode.None; return; }
        var p = e.GetPosition(this);
        BeginDrag(IsZoomedIn(lay) && OnAxisStrip(lay, p) ? DragMode.Pan : DragMode.Select, p, lay);
    }

    /// <summary>Zooms so the time range between two screen x positions fills the view (a drag-selected range); times in UTC.</summary>
    internal void ZoomToRange(double screenX0, double screenX1)
    {
        double plotW = ActualWidth - 2 * Pad;
        var lay = LayoutFor(plotW);
        if (lay == null || lay.IsEmpty) return;
        var (vs, ve) = EffectiveView(lay);
        double scale = plotW / Math.Max(1, ve - vs);
        double a = Math.Min(screenX0, screenX1), b = Math.Max(screenX0, screenX1);
        var t0 = lay.Time(vs + (a - Pad) / scale);
        var t1 = lay.Time(vs + (b - Pad) / scale);
        double f = Math.Max(1e-6, (b - a) / plotW);   // the share of the screen the selection covers
        _zoom = Math.Clamp(_zoom / f, 1, _maxZoom);
        var nl = LayoutFor(plotW)!;
        double xa = nl.X(t0), xb = nl.X(t1);
        double width = Math.Max(Math.Max(1, xb - xa), nl.TotalWidth / _maxZoom);
        double start = Math.Clamp((xa + xb) / 2 - width / 2, 0, Math.Max(0, nl.TotalWidth - width));
        ShowWindow(nl, start, width);
        InvalidateMeasure();
        RangeSelected?.Invoke(t0, t1);
    }

    protected override void OnRenderSizeChanged(SizeChangedInfo sizeInfo)
    {
        base.OnRenderSizeChanged(sizeInfo);
        if (sizeInfo.WidthChanged) ResetView();
    }

    // ------------------------------------------------------------------ view window and glide

    private (double Start, double End) EffectiveView(StoryTimelineLayout lay)
    {
        double s = ViewStart, e = ViewEnd;
        if (double.IsNaN(s) || double.IsNaN(e) || e - s < 1) return (0, Math.Max(1, lay.TotalWidth));
        return (s, e);
    }

    /// <summary>The window for the current focus: centred on it when following, else everything.</summary>
    private ViewWindow? TargetWindow()
    {
        var lay = LayoutFor(ActualWidth - 2 * Pad);
        if (lay == null || lay.IsEmpty) return null;
        if (FollowFocus && FocusMomentId is { } id) return lay.CenterOn(id, Magnification);
        return _zoom > 1 ? lay.CenterOnX(_centerFrac * lay.TotalWidth, _zoom) : new ViewWindow(0, lay.TotalWidth);
    }

    private void OnFocusChanged()
    {
        if (FollowFocus) Glide(animate: true);
    }

    /// <summary>Jumps (no glide) to the view the focus asks for; used when the size or the data changes.</summary>
    private void ResetView()
    {
        BeginAnimation(ViewStartProperty, null);
        BeginAnimation(ViewEndProperty, null);
        if ((FollowFocus || _zoom > 1) && TargetWindow() is { } w) { ViewStart = w.Start; ViewEnd = w.End; }
        else { ClearValue(ViewStartProperty); ClearValue(ViewEndProperty); }
    }

    /// <summary>Moves the view to the focus window, gliding about 450 ms with an ease-out unless Windows animations are off.</summary>
    public void Glide(bool animate)
    {
        if (TargetWindow() is not { } target) { ResetView(); return; }
        var lay = LayoutFor(ActualWidth - 2 * Pad)!;
        var (cs, ce) = EffectiveView(lay);
        if (!animate || !Motion.Enabled || !IsLoaded)
        {
            BeginAnimation(ViewStartProperty, null);
            BeginAnimation(ViewEndProperty, null);
            ViewStart = target.Start; ViewEnd = target.End;
            return;
        }
        var ease = new CubicEase { EasingMode = EasingMode.EaseOut };
        BeginAnimation(ViewStartProperty, new DoubleAnimation(cs, target.Start, GlideTime) { EasingFunction = ease });
        BeginAnimation(ViewEndProperty, new DoubleAnimation(ce, target.End, GlideTime) { EasingFunction = ease });
    }

    // ------------------------------------------------------------------ playback

    /// <summary>
    /// Plays the story: the axis draws in behind a playhead that crosses it, and each moment pops in as the playhead passes. With Windows
    /// animations off it jumps to the end state. Playing again replays from the start.
    /// </summary>
    public void Play()
    {
        Stop();
        var lay = LayoutFor(ActualWidth - 2 * Pad);
        if (lay == null || lay.IsEmpty || !Motion.Enabled || !IsLoaded) return;
        var ms = Math.Min(12000, 1500 + 700 * lay.Callouts.Count);
        var a = new DoubleAnimation(0, 1.08, new Duration(TimeSpan.FromMilliseconds(ms))) { EasingFunction = new CubicEase { EasingMode = EasingMode.EaseInOut } };
        a.Completed += (_, _) => { if (IsPlaying) Stop(); };
        IsPlaying = true;
        BeginAnimation(PlayProgressProperty, a);
    }

    /// <summary>Stops playback and shows everything.</summary>
    public void Stop()
    {
        BeginAnimation(PlayProgressProperty, null);
        ClearValue(PlayProgressProperty);
        IsPlaying = false;
    }

    /// <summary>How far a marker at axis x has popped in (0 hidden .. 1 shown, with a slight overshoot on the way); 1 when not playing.</summary>
    private double PopOf(double x, double totalWidth)
    {
        var p = PlayProgress;
        if (double.IsNaN(p)) return 1;
        double head = p * totalWidth, span = Math.Max(1, totalWidth * 0.06);
        double t = Math.Clamp((head - x) / span, 0, 1);
        return t <= 0 ? 0 : new BackEase { Amplitude = 0.5, EasingMode = EasingMode.EaseOut }.Ease(t);
    }

    // ------------------------------------------------------------------ painting

    private Brush Res(string key, Brush fallback) => TryFindResource(key) as Brush ?? fallback;

    private static Brush Faded(Brush b, double opacity)
    {
        var c = b.Clone();
        c.Opacity = opacity;
        c.Freeze();
        return c;
    }

    private static Color ColorOf(Brush b) => b is SolidColorBrush s ? s.Color : Colors.Gray;

    /// <summary>A soft radial glow in the brush's colour, <paramref name="strength"/> alpha at the centre fading to nothing.</summary>
    private static Brush Glow(Brush b, double strength)
    {
        var c = ColorOf(b);
        var g = new RadialGradientBrush(Color.FromArgb((byte)(255 * strength), c.R, c.G, c.B), Color.FromArgb(0, c.R, c.G, c.B));
        g.Freeze();
        return g;
    }

    private Brush SeverityBrush(string? severity) => SeverityBrushes.Of(severity, Res);

    private FormattedText Text(string s, double size, Brush brush, bool bold = false, double maxWidth = 0, int lines = 1, bool italic = false)
    {
        var family = TryFindResource("Font.UI") as FontFamily ?? new FontFamily("Segoe UI");
        var ft = new FormattedText(s, CultureInfo.CurrentUICulture, FlowDirection.LeftToRight,
            new Typeface(family, italic ? FontStyles.Italic : FontStyles.Normal, bold ? FontWeights.SemiBold : FontWeights.Normal, FontStretches.Normal), size, brush,
            VisualTreeHelper.GetDpi(this).PixelsPerDip);
        if (maxWidth > 1) { ft.MaxTextWidth = maxWidth; ft.MaxLineCount = lines; ft.Trimming = TextTrimming.CharacterEllipsis; }
        return ft;
    }

    protected override void OnRender(DrawingContext dc)
    {
        _hits.Clear();
        double w = ActualWidth, h = ActualHeight;
        dc.DrawRectangle(Brushes.Transparent, null, new Rect(0, 0, w, h));   // hit-testable
        var lay = LayoutFor(w - 2 * Pad);
        if (lay == null || lay.IsEmpty || w < 80) return;   // nothing dated: the page says so in words

        var text = Res("Brush.Text", Brushes.White);
        var dim = Res("Brush.TextSecondary", Brushes.Gray);
        var border = Res("Brush.Border", Brushes.DimGray);
        var strong = Res("Brush.BorderStrong", border);
        var surface = Res("Brush.SurfaceAlt", Brushes.Black);
        double plotW = w - 2 * Pad;
        var (vs, ve) = EffectiveView(lay);
        double scale = plotW / Math.Max(1, ve - vs);
        double Sx(double x) => Pad + (x - vs) * scale;

        double calloutTop = TopH, axisY = calloutTop + lay.CalloutAreaHeight + ConnectorGap;
        double tickY = axisY + 5, plotBottom = axisY + TickRowH + 2;

        bool playing = !double.IsNaN(PlayProgress);
        double headX = playing ? Sx(Math.Min(1, PlayProgress) * lay.TotalWidth) : w;
        if (playing) dc.PushClip(new RectangleGeometry(new Rect(0, 0, Math.Max(0, headX), h)));

        // axis: real-time segments, then the breaks as a short faded zig-zag with the gap's length
        var axisPen = new Pen(strong, 2); axisPen.Freeze();
        foreach (var s in lay.Segments)
        {
            double x0 = Sx(s.X0), x1 = Sx(s.X1);
            if (x1 - x0 < 2) { x0 -= 1; x1 += 1; }
            dc.DrawLine(axisPen, new Point(x0, axisY), new Point(x1, axisY));
        }
        var breakPen = new Pen(Faded(strong, 0.6), 1.5); breakPen.Freeze();
        var used = new List<(double L, double R)>();

        // story scale: findings before the first / after the last key event are counted at the ends, not drawn
        if (lay.IsStoryScale && lay.Clusters.Count > 0)
        {
            if (lay.EarlierCount > 0)
            {
                var cap = Text($"← {lay.EarlierCount} earlier", 10, dim, italic: true);
                double right = Sx(lay.Clusters[0].AnchorX) - 10;
                if (right > 4) dc.DrawText(cap, new Point(Math.Max(2, right - cap.Width), axisY - cap.Height / 2));
            }
            if (lay.LaterCount > 0)
            {
                var cap = Text($"{lay.LaterCount} later →", 10, dim, italic: true);
                double left = Sx(lay.Clusters[^1].AnchorX) + 10;
                if (left < w - 4) dc.DrawText(cap, new Point(Math.Min(left, w - cap.Width - 2), axisY - cap.Height / 2));
            }
        }

        foreach (var b in lay.Breaks)
        {
            double x0 = Sx(b.X0), x1 = Sx(b.X1);
            if (b.Squiggle)
            {
                var zig = new StreamGeometry();
                using (var g = zig.Open())
                {
                    g.BeginFigure(new Point(x0, axisY), false, false);
                    int n = 4; double step = (x1 - x0) / n;
                    for (int i = 1; i <= n; i++) g.LineTo(new Point(x0 + i * step, axisY + (i == n ? 0 : (i % 2 == 1 ? -4 : 4))), true, false);
                }
                zig.Freeze();
                dc.DrawGeometry(null, breakPen, zig);
            }
            var lbl = Text(b.Label, 10.5, dim, italic: true);
            double lx = (x0 + x1) / 2 - lbl.Width / 2;
            dc.DrawText(lbl, new Point(lx, tickY));
            used.Add((lx - 4, lx + lbl.Width + 4));
            _hits.Add(new Hit(HitKind.Break, new Rect(Math.Min(x0, lx), axisY - 6, Math.Max(x1 - x0, lbl.Width), TickRowH + 6), Break: b));
        }

        // ticks under the axis, skipping labels that would collide
        var tickPen = new Pen(strong, 1); tickPen.Freeze();
        foreach (var t in lay.MajorTicks(90 / scale, vs - 40 / scale, ve + 40 / scale))
        {
            double x = Sx(t.X);
            if (x < Pad - 1 || x > w - Pad + 1) continue;
            dc.DrawLine(tickPen, new Point(x, axisY), new Point(x, axisY + 4));
            var lbl = Text(t.Label, 10, dim);
            double lx = Math.Clamp(x - lbl.Width / 2, 2, w - lbl.Width - 2);
            if (used.Any(u => lx < u.R && lx + lbl.Width > u.L)) continue;
            dc.DrawText(lbl, new Point(lx, tickY));
            used.Add((lx - 6, lx + lbl.Width + 6));
        }

        // Splunk-style time buckets between the points: fainter ticks at a round interval that gets finer as the view zooms in
        {
            var minorPen = new Pen(Faded(strong, 0.8), 1); minorPen.Freeze();
            var minorText = Faded(dim, 0.8);
            foreach (var t in lay.MinorTicks(90 / scale, vs - 40 / scale, ve + 40 / scale))
            {
                double x = Sx(t.X);
                if (x < Pad - 1 || x > w - Pad + 1) continue;
                var lbl = Text(t.Label, 9.5, minorText);
                double lx = Math.Clamp(x - lbl.Width / 2, 2, w - lbl.Width - 2);
                if (used.Any(u => lx < u.R && lx + lbl.Width > u.L)) continue;
                dc.DrawLine(minorPen, new Point(x, axisY), new Point(x, axisY + 3));
                dc.DrawText(lbl, new Point(lx, tickY));
                used.Add((lx - 4, lx + lbl.Width + 4));
            }
        }

        // other findings between the first and last key event: faint dots on the line (one per few pixels)
        if (lay.IsStoryScale && lay.Clusters.Count > 0 && Timeline is { } story)
        {
            var dot = Faded(text, 0.35);
            DateTimeOffset first = lay.Clusters[0].Start, last = lay.Clusters[^1].End;
            var seen = new HashSet<long>();
            foreach (var f in story.Findings)
            {
                if (f.Start is not { } fs || fs < first || fs > last) continue;
                double x = Sx(lay.X(fs));
                if (x < -3 || x > w + 3 || !seen.Add((long)Math.Round(x / 3))) continue;
                dc.DrawEllipse(dot, null, new Point(x, axisY), 2.4, 2.4);
            }
        }

        if (playing)
        {
            dc.Pop();
            var accent = Res("Brush.Accent", Brushes.DodgerBlue);
            if (PlayProgress <= 1)
            {
                // a fading trail behind the playhead
                double trail = Math.Min(90, Math.Max(0, headX - Pad));
                if (trail > 1)
                {
                    var c = ColorOf(accent);
                    var g = new LinearGradientBrush(Color.FromArgb(0, c.R, c.G, c.B), Color.FromArgb(70, c.R, c.G, c.B), new Point(0, 0), new Point(1, 0));
                    g.Freeze();
                    dc.DrawRectangle(g, null, new Rect(headX - trail, 4, trail, plotBottom - 4));
                }
                var headPen = new Pen(accent, 2); headPen.Freeze();
                dc.DrawLine(headPen, new Point(headX, 4), new Point(headX, plotBottom));
            }
        }

        // compact: too little room for text cards, so each moment is its stage pictogram
        bool compact = plotW * _zoom < lay.Callouts.Count * (CardW + 8) * 0.75;

        // focus order: the callouts are in time order
        int focusIndex = -1;
        if (FocusMomentId is { } fid) focusIndex = lay.Callouts.ToList().FindIndex(c => c.MomentId == fid);
        double OpacityOf(int i) => focusIndex < 0 || i == focusIndex ? 1 : i < focusIndex ? 0.5 : 0.22;

        // connectors and anchors first, cards over them
        var connPen = new Pen(Faded(strong, 0.8), 1); connPen.Freeze();
        var cardRects = new Rect[lay.Callouts.Count];
        double cardW = CardW * Math.Min(1, scale);
        for (int i = 0; i < lay.Callouts.Count; i++)
        {
            var c = lay.Callouts[i];
            double cx = Sx(c.Rect.X + c.Rect.Width / 2);
            cardRects[i] = new Rect(cx - cardW / 2, calloutTop + c.Rect.Y, cardW, c.Rect.Height);
            var sev = SeverityBrush(MomentSeverity(c.MomentId));
            double pop = PopOf(c.AnchorX, lay.TotalWidth);
            if (pop <= 0) continue;
            dc.PushOpacity(OpacityOf(i) * Math.Min(1, pop));
            double ax = Sx(c.AnchorX);
            if (compact) cardRects[i] = new Rect(cx - 17, cardRects[i].Y + Math.Max(0, cardRects[i].Height - 50), 34, 34);
            dc.PushTransform(new ScaleTransform(pop, pop, ax, axisY));
            dc.DrawLine(connPen, new Point(cx, cardRects[i].Bottom), new Point(ax, axisY - 4));
            int rank = SpectreFinding.Rank(MomentSeverity(c.MomentId));
            Rect marker;
            if (c.IsRange)
            {
                marker = new Rect(Sx(c.BarX0), axisY - 3.5, Math.Max(6, Sx(c.BarX1) - Sx(c.BarX0)), 7);
                if (rank >= 3) dc.DrawRoundedRectangle(Glow(sev, 0.32), null, Rect.Inflate(marker, 6, 6), 9, 9);   // soft halo on High / Critical
                dc.DrawRoundedRectangle(sev, null, marker, 3.5, 3.5);
            }
            else
            {
                double halo = rank >= 4 ? 17 : 14;
                if (rank >= 3) dc.DrawEllipse(Glow(sev, rank >= 4 ? 0.55 : 0.4), null, new Point(ax, axisY), halo, halo);   // soft halo on High / Critical
                var ring = new Pen(Res("Brush.Surface", Brushes.Black), 2); ring.Freeze();
                dc.DrawEllipse(sev, ring, new Point(ax, axisY), 5.5, 5.5);
                marker = new Rect(ax - 5.5, axisY - 5.5, 11, 11);
            }
            dc.Pop();
            dc.Pop();
            if (pop >= 1) _hits.Add(new Hit(HitKind.Marker, Rect.Inflate(marker, 4, 4), MomentId: c.MomentId));
        }

        // several moments on one point: a "×N" badge (hover lists them, click zooms in)
        foreach (var cl in lay.Clusters)
        {
            if (!cl.IsGroup || PopOf(cl.AnchorX, lay.TotalWidth) < 1) continue;
            double ax = Sx(cl.AnchorX);
            if (ax < -30 || ax > w + 30) continue;
            var lbl = Text("×" + cl.MomentIds.Count.ToString(CultureInfo.InvariantCulture), 10, Res("Brush.Surface", Brushes.Black), bold: true);
            var r = new Rect(ax + 7, axisY - 21, lbl.Width + 10, 14);
            dc.DrawRoundedRectangle(text, null, r, 7, 7);
            dc.DrawText(lbl, new Point(r.X + 5, r.Y + (r.Height - lbl.Height) / 2));
            _hits.Add(new Hit(HitKind.Cluster, Rect.Inflate(r, 3, 3), Cluster: cl));
        }

        var selPen = new Pen(text, 2); selPen.Freeze();
        var cardPen = new Pen(border, 1); cardPen.Freeze();
        for (int i = 0; i < lay.Callouts.Count; i++)
        {
            var c = lay.Callouts[i];
            var r = cardRects[i];
            if (r.Right < 0 || r.Left > w) continue;
            _moments.TryGetValue(c.MomentId, out var m);
            var severity = MomentSeverity(c.MomentId);
            var sev = SeverityBrush(severity);
            bool selected = c.MomentId == SelectedMomentId || i == focusIndex;
            double pop = PopOf(c.AnchorX, lay.TotalWidth);
            if (pop <= 0) continue;
            dc.PushOpacity(OpacityOf(i) * Math.Min(1, pop));
            dc.PushTransform(new ScaleTransform(pop, pop, r.X + r.Width / 2, r.Bottom));
            if (compact)
            {
                DrawPictogram(dc, r, m, severity, sev, selected ? selPen : null, text);
                dc.Pop(); dc.Pop();
                _hits.Add(new Hit(HitKind.Card, r, MomentId: c.MomentId));
                continue;
            }
            dc.DrawRoundedRectangle(surface, selected ? selPen : cardPen, r, 5, 5);
            dc.PushClip(new RectangleGeometry(r, 5, 5));
            dc.DrawRectangle(sev, null, new Rect(r.X, r.Y, 4, r.Height));
            dc.Pop();
            double tx = r.X + 14, tw = Math.Max(10, r.Width - 22);
            var lines = m != null ? HoverLines(m, _byId) : new[] { "", "No time", PlainLanguage.Tactic(null) };
            var title = Text(lines[0], 12, text, bold: true, maxWidth: tw, lines: 2);
            title.MaxTextHeight = 34;
            dc.DrawText(title, new Point(tx, r.Y + 5));
            double ly = r.Y + 5 + Math.Max(title.Height, 16) + 2;
            var dimText = Res("Brush.TextSecondary", text);
            string[] glyphs = { ClockGlyph, StoryGraphics.Glyph(lines[2]) };
            for (int k = 0; k < 2; k++)
            {
                var ft = Text(lines[k + 1], 10.5, dimText, maxWidth: Math.Max(10, tw - GlyphGap));
                DrawGlyph(dc, glyphs[k], new Point(tx, ly), ft.Height, dimText);
                dc.DrawText(ft, new Point(tx + GlyphGap, ly)); ly += ft.Height + 1;
            }
            // number badge in neutral colours, just outside the card's top-left corner (the layout leaves a gap for it)
            var badgeC = new Point(r.X, r.Y);
            dc.DrawEllipse(text, new Pen(Res("Brush.Surface", Brushes.Black), 1.5), badgeC, BadgeR, BadgeR);
            var num = Text(c.Number.ToString(CultureInfo.InvariantCulture), 10, Res("Brush.Surface", Brushes.Black), bold: true);
            dc.DrawText(num, new Point(badgeC.X - num.Width / 2, badgeC.Y - num.Height / 2));
            dc.Pop();
            dc.Pop();
            _hits.Add(new Hit(HitKind.Card, r, MomentId: c.MomentId));
        }

        if (_dragMode == DragMode.Select && _dragged)
        {
            double x0 = Math.Min(_selFromX, _selToX), x1 = Math.Max(_selFromX, _selToX);
            var accent = Res("Brush.Accent", Brushes.DodgerBlue);
            var band = new Rect(x0, 2, Math.Max(1, x1 - x0), plotBottom - 2);
            dc.DrawRectangle(Faded(accent, 0.18), null, band);
            var edge = new Pen(accent, 1.5); edge.Freeze();
            dc.DrawLine(edge, new Point(x0, band.Top), new Point(x0, band.Bottom));
            dc.DrawLine(edge, new Point(x1, band.Top), new Point(x1, band.Bottom));
            double scaleNow = plotW / Math.Max(1, ve - vs);
            string Stamp(double sx) => lay.Time(vs + (sx - Pad) / scaleNow).UtcDateTime.ToString("yyyy-MM-dd HH:mm:ss", CultureInfo.InvariantCulture);
            var lbl = Text($"{Stamp(x0)} – {Stamp(x1)} (UTC)", 10.5, text, bold: true);
            double lx = Math.Clamp((x0 + x1) / 2 - lbl.Width / 2, 2, Math.Max(2, w - lbl.Width - 2));
            dc.DrawRoundedRectangle(Res("Brush.Surface", Brushes.Black), null, new Rect(lx - 4, 4, lbl.Width + 8, lbl.Height + 2), 3, 3);
            dc.DrawText(lbl, new Point(lx, 5));
        }

        DrawTooltip(dc, w, h, text, border);
    }

    /// <summary>
    /// The lines every card shows, in this order and nothing else: the title, the time as "yyyy-MM-dd HH:mm:ss UTC" and the tactic name (the
    /// primary finding's). The severity is the card's colour, not a line.
    /// </summary>
    internal static IReadOnlyList<string> HoverLines(SpectreMoment m, IReadOnlyDictionary<string, SpectreTimelineFinding> byId) => StoryText.CardLines(m, byId);

    /// <summary>A moment as its stage pictogram: a circle ringed in the severity colour, and the number badge (the ring colour is the severity).</summary>
    private void DrawPictogram(DrawingContext dc, Rect r, SpectreMoment? m, string severity, Brush sev, Pen? selPen, Brush text)
    {
        var centre = new Point(r.X + r.Width / 2, r.Y + r.Height / 2);
        var ring = new Pen(sev, 3); ring.Freeze();
        dc.DrawEllipse(Res("Brush.Surface", Brushes.Black), ring, centre, r.Width / 2, r.Height / 2);
        if (selPen != null) dc.DrawEllipse(null, selPen, centre, r.Width / 2 + 3, r.Height / 2 + 3);
        var family = TryFindResource("Font.Icon") as FontFamily ?? new FontFamily("Segoe MDL2 Assets");
        var glyph = new FormattedText(StoryGraphics.Icon(m?.Phase, m == null ? null : StoryGraphics.MomentTechniques(m, _byId)), CultureInfo.CurrentUICulture, FlowDirection.LeftToRight,
            new Typeface(family, FontStyles.Normal, FontWeights.Normal, FontStretches.Normal), 15, text, VisualTreeHelper.GetDpi(this).PixelsPerDip);
        dc.DrawText(glyph, new Point(centre.X - glyph.Width / 2, centre.Y - glyph.Height / 2));
        if (m != null)
        {
            var bx = r.Right - 2; var by = r.Y + 2;
            dc.DrawEllipse(text, null, new Point(bx, by), 8, 8);
            var num = Text(m.Number.ToString(CultureInfo.InvariantCulture), 9.5, Res("Brush.Surface", Brushes.Black), bold: true);
            dc.DrawText(num, new Point(bx - num.Width / 2, by - num.Height / 2));
        }
    }

    private string MomentSeverity(string momentId) =>
        _moments.TryGetValue(momentId, out var m) ? StoryText.Severity(m, _byId) : "Info";

    private void DrawTooltip(DrawingContext dc, double w, double h, Brush text, Brush border)
    {
        if (_hover == null) return;
        if (_hover is { Kind: HitKind.Cluster, Cluster: { } hc }) { DrawClusterCard(dc, w, h, hc, _hover.Bounds, text, border); return; }
        if (_hover is { Kind: HitKind.Marker, MomentId: { } markerId })
        {
            if (_layout?.ClusterOf(markerId) is { IsGroup: true } mc) DrawClusterCard(dc, w, h, mc, _hover.Bounds, text, border);
            else DrawMiniCard(dc, w, h, markerId, _hover.Bounds, text, border);
            return;
        }
        var lines = TooltipLines(_hover);
        if (lines.Count == 0) return;
        bool details = _hover is { Kind: HitKind.Card };   // the details of a key event: wrapped lines, none bold
        var dimTip = Res("Brush.TextSecondary", text);
        var fts = lines.Select((l, i) => details
            ? Text(l, 11, i == 0 ? text : dimTip, maxWidth: 320, lines: 2)
            : Text(l, i == 0 ? 12 : 11, i == 0 ? text : dimTip, bold: i == 0, maxWidth: 380)).ToList();
        double tw = fts.Max(f => f.Width) + 16, th = fts.Sum(f => f.Height) + 10;
        double x = _hoverPoint.X + 14, y = _hoverPoint.Y + 16;
        if (x + tw > w - 2) x = Math.Max(2, _hoverPoint.X - tw - 8);
        if (y + th > h - 2) y = Math.Max(2, _hoverPoint.Y - th - 8);
        var pen = new Pen(border, 1); pen.Freeze();
        dc.DrawRoundedRectangle(Res("Brush.Surface", Brushes.Black), pen, new Rect(x, y, tw, th), 4, 4);
        double yy = y + 5;
        foreach (var ft in fts) { dc.DrawText(ft, new Point(x + 8, yy)); yy += ft.Height; }
    }

    private const string ClockGlyph = "";
    private const double GlyphGap = 15;

    /// <summary>A small icon-font glyph in front of a card line, centred on the line.</summary>
    private void DrawGlyph(DrawingContext dc, string glyph, Point at, double lineHeight, Brush brush)
    {
        var family = TryFindResource("Font.Icon") as FontFamily ?? new FontFamily("Segoe MDL2 Assets");
        var g = new FormattedText(glyph, CultureInfo.CurrentUICulture, FlowDirection.LeftToRight,
            new Typeface(family, FontStyles.Normal, FontWeights.Normal, FontStretches.Normal), 10.5, brush, VisualTreeHelper.GetDpi(this).PixelsPerDip);
        dc.DrawText(g, new Point(at.X, at.Y + (lineHeight - g.Height) / 2));
    }

    /// <summary>Hovering a marker on the axis: a small card above it with the stage pictogram and the <see cref="HoverLines"/>.</summary>
    private void DrawMiniCard(DrawingContext dc, double w, double h, string momentId, Rect marker, Brush text, Brush border)
    {
        if (!_moments.TryGetValue(momentId, out var m)) return;
        var severity = MomentSeverity(momentId);
        var sev = SeverityBrush(severity);
        var dim = Res("Brush.TextSecondary", text);
        var lines = HoverLines(m, _byId);
        var title = Text(lines[0], 12, text, bold: true, maxWidth: 260);
        var when = Text(lines[1], 10.5, dim, maxWidth: 260);
        var tactic = Text(lines[2], 10.5, dim, maxWidth: 260);
        const double icon = 30, pad = 8;
        double tw = pad + icon + 10 + new[] { title.Width, tactic.Width + GlyphGap, when.Width + GlyphGap }.Max() + pad;
        double th = Math.Max(icon, title.Height + tactic.Height + when.Height + 2) + 2 * pad;
        double x = Math.Clamp(marker.X + marker.Width / 2 - tw / 2, 2, Math.Max(2, w - tw - 2));
        double y = marker.Y - th - 8;
        if (y < 2) y = Math.Min(h - th - 2, marker.Bottom + 8);
        var r = new Rect(x, y, tw, th);
        var pen = new Pen(border, 1); pen.Freeze();
        dc.DrawRoundedRectangle(Res("Brush.Surface", Brushes.Black), pen, r, 6, 6);
        dc.PushClip(new RectangleGeometry(r, 6, 6));
        dc.DrawRectangle(sev, null, new Rect(r.X, r.Y, 4, r.Height));
        dc.Pop();
        var ic = new Rect(r.X + pad + 2, r.Y + pad, icon, icon);
        var ring = new Pen(sev, 2.5); ring.Freeze();
        dc.DrawEllipse(Res("Brush.SurfaceAlt", Brushes.Black), ring, new Point(ic.X + icon / 2, ic.Y + icon / 2), icon / 2, icon / 2);
        var family = TryFindResource("Font.Icon") as FontFamily ?? new FontFamily("Segoe MDL2 Assets");
        var glyph = new FormattedText(StoryGraphics.Icon(m.Phase, StoryGraphics.MomentTechniques(m, _byId)), CultureInfo.CurrentUICulture, FlowDirection.LeftToRight,
            new Typeface(family, FontStyles.Normal, FontWeights.Normal, FontStretches.Normal), 14, text, VisualTreeHelper.GetDpi(this).PixelsPerDip);
        dc.DrawText(glyph, new Point(ic.X + (icon - glyph.Width) / 2, ic.Y + (icon - glyph.Height) / 2));
        double tx = ic.Right + 10, ty = r.Y + pad;
        dc.DrawText(title, new Point(tx, ty)); ty += title.Height;
        DrawGlyph(dc, ClockGlyph, new Point(tx, ty), when.Height, dim);
        dc.DrawText(when, new Point(tx + GlyphGap, ty)); ty += when.Height;
        DrawGlyph(dc, StoryGraphics.Glyph(lines[2]), new Point(tx, ty), tactic.Height, dim);
        dc.DrawText(tactic, new Point(tx + GlyphGap, ty));
    }

    /// <summary>Hovering a point with several moments: each moment's <see cref="HoverLines"/> under its number, above the point.</summary>
    private void DrawClusterCard(DrawingContext dc, double w, double h, StoryCluster cluster, Rect anchor, Brush text, Brush border)
    {
        const double pad = 8, maxRows = 6;
        var dim = Res("Brush.TextSecondary", text);
        var rows = new List<(SpectreMoment M, FormattedText[] Lines)>();
        foreach (var id in cluster.MomentIds.Take((int)maxRows))
        {
            if (!_moments.TryGetValue(id, out var m)) continue;
            var l = HoverLines(m, _byId);
            rows.Add((m, new[]
            {
                Text(l[0], 12, text, bold: true, maxWidth: 260), Text(l[1], 10.5, dim, maxWidth: 260),
                Text(l[2], 10.5, dim, maxWidth: 260),
            }));
        }
        if (rows.Count == 0) return;
        var more = cluster.MomentIds.Count > rows.Count ? Text($"+{cluster.MomentIds.Count - rows.Count} more", 10.5, dim, italic: true) : null;
        const double badge = 22;
        double rowH = rows.Max(r => r.Lines.Sum(x => x.Height)) + 8;
        double tw = pad + badge + 8 + rows.Max(r => Math.Max(r.Lines[0].Width, Math.Max(r.Lines[1].Width, r.Lines[2].Width) + GlyphGap)) + pad;
        double th = 2 * pad + rows.Count * rowH + (more?.Height ?? 0);
        double x = Math.Clamp(anchor.X + anchor.Width / 2 - tw / 2, 2, Math.Max(2, w - tw - 2));
        double y = anchor.Y - th - 8;
        if (y < 2) y = Math.Max(2, Math.Min(h - th - 2, anchor.Bottom + 8));
        var pen = new Pen(border, 1); pen.Freeze();
        dc.DrawRoundedRectangle(Res("Brush.Surface", Brushes.Black), pen, new Rect(x, y, tw, th), 6, 6);
        double yy = y + pad;
        foreach (var (m, lines) in rows)
        {
            var c = new Point(x + pad + badge / 2, yy + badge / 2);
            dc.DrawEllipse(SeverityBrush(MomentSeverity(m.Id)), null, c, 9, 9);   // the severity is the badge colour (no word)
            var num = Text(m.Number.ToString(CultureInfo.InvariantCulture), 10.5, Res("Brush.Surface", Brushes.Black), bold: true);
            dc.DrawText(num, new Point(c.X - num.Width / 2, c.Y - num.Height / 2));
            double tx = x + pad + badge + 8, ly = yy;
            dc.DrawText(lines[0], new Point(tx, ly)); ly += lines[0].Height;
            var rowTactic = HoverLines(m, _byId)[2];
            DrawGlyph(dc, ClockGlyph, new Point(tx, ly), lines[1].Height, dim);
            dc.DrawText(lines[1], new Point(tx + GlyphGap, ly)); ly += lines[1].Height;
            DrawGlyph(dc, StoryGraphics.Glyph(rowTactic), new Point(tx, ly), lines[2].Height, dim);
            dc.DrawText(lines[2], new Point(tx + GlyphGap, ly)); ly += lines[2].Height;
            yy += rowH;
        }
        if (more != null) dc.DrawText(more, new Point(x + pad, yy));
    }

    /// <summary>The details behind a card (hover only): see <see cref="MomentDetailLines"/>.</summary>
    private List<string> TooltipLines(Hit hit)
    {
        var lines = new List<string>();
        switch (hit)
        {
            case { Kind: HitKind.Card, MomentId: { } mid } when _moments.TryGetValue(mid, out var m):
                lines.AddRange(MomentDetailLines.Build(m, _byId));
                break;
            case { Kind: HitKind.Break, Break: { } b }:
                lines.Add(_layout?.IsStoryScale == true ? $"No key events for {b.Label}" : $"Nothing recorded for {b.Label}");
                lines.Add($"{StoryText.Tile(b.From)} to {StoryText.Tile(b.To)}");
                break;
        }
        return lines;
    }

    // ------------------------------------------------------------------ input

    private Hit? HitAt(Point p)
    {
        foreach (var kind in new[] { HitKind.Cluster, HitKind.Marker, HitKind.Card, HitKind.Break })
            for (int i = _hits.Count - 1; i >= 0; i--)
                if (_hits[i].Kind == kind && _hits[i].Bounds.Contains(p)) return _hits[i];
        return null;
    }

    protected override void OnMouseMove(MouseEventArgs e)
    {
        base.OnMouseMove(e);
        var p = e.GetPosition(this);
        if (_dragFrom is { } from && IsMouseCaptured && (e.LeftButton == MouseButtonState.Pressed || e.MiddleButton == MouseButtonState.Pressed) && LayoutFor(ActualWidth - 2 * Pad) is { } lay)
        {
            double dx = p.X - from.X;
            if (!_dragged && Math.Abs(dx) < 4) return;
            _dragged = true;
            if (_hover != null) _hover = null;
            if (_dragMode == DragMode.Pan)
            {
                Cursor = Cursors.SizeWE;
                var (vs, ve) = EffectiveView(lay);
                double width = ve - vs, scale = (ActualWidth - 2 * Pad) / Math.Max(1, width);
                ShowWindow(lay, Math.Clamp(_dragViewStart - dx / scale, 0, Math.Max(0, lay.TotalWidth - width)), width);
            }
            else
            {
                Cursor = Cursors.Cross;
                _selToX = Math.Clamp(p.X, Pad, ActualWidth - Pad);
                InvalidateVisual();
            }
            return;
        }
        var hit = HitAt(p);
        _hoverPoint = p;
        if (!Equals(hit, _hover) || hit != null)
        {
            _hover = hit;
            Cursor = hit is { Kind: HitKind.Card or HitKind.Marker or HitKind.Cluster } ? Cursors.Hand : null;
            InvalidateVisual();
        }
    }

    /// <summary>Shows a moment's marker card as if hovered (snapshot tests); false when the marker was not drawn.</summary>
    internal bool HoverMarker(string momentId)
    {
        var hit = _hits.FirstOrDefault(x => x.Kind == HitKind.Marker && x.MomentId == momentId);
        if (hit == null) return false;
        _hover = hit;
        _hoverPoint = new Point(hit.Bounds.X + hit.Bounds.Width / 2, hit.Bounds.Y + hit.Bounds.Height / 2);
        InvalidateVisual();
        return true;
    }

    /// <summary>Shows a moment's card details as if hovered (snapshot tests); false when the card was not drawn.</summary>
    internal bool HoverCard(string momentId)
    {
        var hit = _hits.FirstOrDefault(x => x.Kind == HitKind.Card && x.MomentId == momentId);
        if (hit == null) return false;
        _hover = hit;
        _hoverPoint = new Point(hit.Bounds.X + hit.Bounds.Width * 0.5, hit.Bounds.Y + hit.Bounds.Height * 0.6);
        InvalidateVisual();
        return true;
    }

    protected override void OnMouseLeave(MouseEventArgs e)
    {
        base.OnMouseLeave(e);
        if (_hover != null) { _hover = null; InvalidateVisual(); }
    }

    protected override void OnMouseLeftButtonUp(MouseButtonEventArgs e)
    {
        base.OnMouseLeftButtonUp(e);
        bool wasDrag = _dragged;
        var mode = _dragMode;
        double selA = _selFromX, selB = _selToX;
        EndDrag();
        if (wasDrag)
        {
            if (mode == DragMode.Select && Math.Abs(selB - selA) >= 6) ZoomToRange(selA, selB);
            e.Handled = true;
            return;
        }
        var hit = HitAt(e.GetPosition(this));
        if (hit is { Kind: HitKind.Cluster, Cluster: { } cl })
        {
            ZoomToCluster(cl);
            e.Handled = true;
            return;
        }
        if (hit is { Kind: HitKind.Card or HitKind.Marker, MomentId: { } id })
        {
            SelectedMomentId = id;
            MomentClicked?.Invoke(id);
            e.Handled = true;
        }
    }
}
