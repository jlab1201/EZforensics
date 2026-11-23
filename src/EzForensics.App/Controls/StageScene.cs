using System.Globalization;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Media.Animation;
using System.Windows.Shapes;
using Path = System.Windows.Shapes.Path;
using EzForensics.Core.Spectre;

namespace EzForensics.App.Controls;

/// <summary>
/// A small looping illustration of a story stage for the walk-through slide (a download for "Ingress Tool Transfer", turning gears for
/// "Execution", ...). It shows the kind of activity, not what happened. The frame and accent take the severity colour (the slide
/// also names the severity). The loop runs only while the control is loaded and visible and <see cref="Motion.Enabled"/>; otherwise a
/// still frame of the loop shows. Each scene is a few shapes and Segoe Fluent glyphs moved by one Storyboard (no per-frame code).
/// </summary>
public sealed class StageScene : Decorator
{
    public const double SceneWidth = 220, SceneHeight = 160;

    public static readonly DependencyProperty StageProperty = DependencyProperty.Register(nameof(Stage), typeof(string), typeof(StageScene),
        new PropertyMetadata(null, OnSceneChanged));
    /// <summary>Stage (ATT&amp;CK tactic) name (<see cref="StoryGraphics.StageOrder"/>); unknown names show "Unmapped".</summary>
    public string? Stage { get => (string?)GetValue(StageProperty); set => SetValue(StageProperty, value); }

    public static readonly DependencyProperty SeverityProperty = DependencyProperty.Register(nameof(Severity), typeof(string), typeof(StageScene),
        new PropertyMetadata(null, OnSceneChanged));
    /// <summary>Severity word (Critical, High, Medium, Low, Info): picks the frame and accent colour.</summary>
    public string? Severity { get => (string?)GetValue(SeverityProperty); set => SetValue(SeverityProperty, value); }

    public static readonly DependencyProperty TechniqueProperty = DependencyProperty.Register(nameof(Technique), typeof(string), typeof(StageScene),
        new PropertyMetadata(null, OnSceneChanged));
    /// <summary>ATT&amp;CK technique id(s), "T1105" or "T1027, T1105": T1105 under Command and Control shows the download scene.</summary>
    public string? Technique { get => (string?)GetValue(TechniqueProperty); set => SetValue(TechniqueProperty, value); }

    private Storyboard? _story;
    private IReadOnlyList<(IAnimatable Target, DependencyProperty Prop)> _targets = Array.Empty<(IAnimatable, DependencyProperty)>();
    private Canvas? _canvas;
    private bool _running;

    public StageScene()
    {
        Focusable = false;
        Loaded += (_, _) => Refresh();
        Unloaded += (_, _) => Halt();
        IsVisibleChanged += (_, _) => Refresh();
        Build();
    }

    /// <summary>The scene shown for <paramref name="stage"/> (unknown or empty names give "Unmapped").</summary>
    internal static string SceneFor(string? stage, string? technique = null) => StoryGraphics.SceneKey(stage, technique == null ? null : new[] { technique });

    /// <summary>A scene is drawn for this exact stage name.</summary>
    internal static bool HasScene(string stage) => Scenes.ContainsKey(stage);

    /// <summary>The loop is running.</summary>
    internal bool IsAnimating => _running;

    /// <summary>Number of animated tracks of the current scene.</summary>
    internal int TrackCount => _story?.Children.Count ?? 0;

    /// <summary>Some element of the scene still has an animation applied (none should after <see cref="Halt"/>).</summary>
    internal bool AnyAnimated()
    {
        if (_canvas == null) return false;
        bool Walk(DependencyObject d)
        {
            if (d is UIElement u && (u.HasAnimatedProperties || (u.RenderTransform is Animatable a && a.HasAnimatedProperties))) return true;
            if (d is UIElement { RenderTransform: TransformGroup g } && g.Children.Any(c => c.HasAnimatedProperties)) return true;
            if (d is Panel p) foreach (UIElement c in p.Children) if (Walk(c)) return true;
            return false;
        }
        return Walk(_canvas);
    }

    private static void OnSceneChanged(DependencyObject d, DependencyPropertyChangedEventArgs e)
    {
        var s = (StageScene)d;
        s.Build();
        s.Refresh();
    }

    private void Refresh()
    {
        if (IsLoaded && IsVisible) Run(); else Halt();
    }

    /// <summary>Starts the loop (no-op when animations are off: the still frame stays).</summary>
    internal void Run()
    {
        if (_running || _story == null || _canvas == null || !Motion.Enabled) return;
        _story.Begin(_canvas, HandoffBehavior.SnapshotAndReplace, true);
        _running = true;
    }

    /// <summary>Stops and removes the loop; the still frame shows again.</summary>
    internal void Halt()
    {
        if (_running && _story != null && _canvas != null)
        {
            _story.Remove(_canvas);
            foreach (var (target, prop) in _targets) target.BeginAnimation(prop, null);   // the clocks go now, not on the next tick
        }
        _running = false;
    }

    private void Build()
    {
        Halt();
        var b = new SceneBuilder(AccentKey(Severity));
        Scenes[SceneFor(Stage, Technique)](b);
        b.Finish();
        _story = b.Story;
        _targets = b.Targets;
        _canvas = b.Canvas;

        var root = new Grid { Width = SceneWidth, Height = SceneHeight };
        var frame = new Border { CornerRadius = new CornerRadius(24), BorderThickness = new Thickness(3) };
        frame.SetResourceReference(Border.BackgroundProperty, "Brush.Surface");
        frame.SetResourceReference(Border.BorderBrushProperty, RingKey(Severity));
        var tint = new Border { CornerRadius = new CornerRadius(21), Margin = new Thickness(3), Opacity = 0.07 };
        tint.SetResourceReference(Border.BackgroundProperty, AccentKey(Severity));
        b.Canvas.Clip = new RectangleGeometry(new Rect(3, 3, SceneWidth - 6, SceneHeight - 6), 21, 21);
        root.Children.Add(frame);
        root.Children.Add(tint);
        root.Children.Add(b.Canvas);
        Child = root;
    }

    /// <summary>Same colours as the page's SeverityRing style.</summary>
    internal static string RingKey(string? severity) => severity switch
    {
        "Critical" or "High" => "Brush.Error",
        "Medium" => "Brush.Warning",
        "Low" => "Brush.Info",
        _ => "Brush.BorderStrong",
    };

    /// <summary>Accent of the moving parts: the severity colour; Info and unknown use the secondary text colour.</summary>
    internal static string AccentKey(string? severity) => severity switch
    {
        "Critical" or "High" => "Brush.Error",
        "Medium" => "Brush.Warning",
        "Low" => "Brush.Info",
        _ => "Brush.TextSecondary",
    };

    // ------------------------------------------------------------------ scenes

    private const string Sec = "Brush.TextSecondary", Txt = "Brush.Text", Alt = "Brush.SurfaceAlt", Surf = "Brush.Surface", Bdr = "Brush.Border";

    private static readonly Dictionary<string, Action<SceneBuilder>> Scenes = new(StringComparer.OrdinalIgnoreCase)
    {
        ["Ingress Tool Transfer"] = BringingInTools,
        ["Execution"] = RunningPrograms,
        ["Initial Access"] = GettingIn,
        ["Persistence"] = StayingIn,
        ["Privilege Escalation"] = GainingControl,
        ["Defense Evasion"] = CoveringTracks,
        ["Credential Access"] = StealingPasswords,
        ["Discovery"] = LookingAround,
        ["Lateral Movement"] = Spreading,
        ["Collection"] = GatheringData,
        ["Exfiltration"] = TakingDataOut,
        ["Command and Control"] = RemoteControl,
        ["Impact"] = Damage,
        [PlainLanguage.OtherActivity] = Other,
    };

    /// <summary>A cloud at the top, dots streaming down into a computer, a progress bar filling and starting over.</summary>
    private static void BringingInTools(SceneBuilder b)
    {
        b.StaticAt = 1.3;
        b.Glyph("", 52, 110, 34, Sec);                  // cloud
        b.Glyph("", 16, 110, 38, b.Accent);             // download arrow
        var scr = b.Computer(110, 84, 84);
        b.Glyph("", 18, 110, scr.Y + 14, b.Accent);
        b.Box(scr.X + 9, scr.Y + 28, scr.Width - 18, 7, 3.5, Bdr);
        var fill = b.Box(scr.X + 9, scr.Y + 28, scr.Width - 18, 7, 3.5, b.Accent);
        b.Pivot(fill, scr.X + 9, scr.Y + 31);
        b.Track(fill, P.SX, 2.8, D(0, 0.04), O(2.2, 1), L(2.55, 1), D(2.6, 0.04), L(2.8, 0.04));
        for (int i = 0; i < 3; i++)
        {
            var dot = b.Dot(110, 58, 4, b.Accent);
            b.Track(dot, P.Y, 0.9, i * 0.3, D(0, 0), L(0.9, 26));
            b.Track(dot, P.Op, 0.9, i * 0.3, D(0, 0), L(0.15, 1), L(0.7, 1), L(0.9, 0));
        }
    }

    /// <summary>A computer with a terminal and a blinking cursor, a program window popping open, two meshing gears.</summary>
    private static void RunningPrograms(SceneBuilder b)
    {
        b.StaticAt = 1.0;
        var scr = b.Computer(80, 38, 112);
        double x = scr.X + 8, y = scr.Y + 9;
        b.Chevron(x, y + 4, b.Accent);
        b.Box(x + 12, y + 2, 32, 4, 2, Bdr);
        b.Box(x + 12, y + 12, 22, 4, 2, Bdr);
        b.Box(x + 12, y + 22, 28, 4, 2, Bdr);
        b.Chevron(x, y + 38, b.Accent);
        var cursor = b.Box(x + 12, y + 33, 7, 12, 1, b.Accent);
        b.Track(cursor, P.Op, 1.0, D(0, 1), D(0.55, 0), L(1.0, 0));

        var win = b.Group(scr.Right - 48, scr.Y + 6, 42, 32);
        b.Box(win, 0, 0, 42, 32, 3, Surf, b.Accent, 1.5);
        b.Box(win, 0, 0, 42, 8, 3, b.Accent);
        b.Box(win, 6, 14, 26, 3, 1.5, Bdr);
        b.Box(win, 6, 21, 18, 3, 1.5, Bdr);
        b.Track(win, P.S, 3.0, D(0, 0), L(0.3, 0), Bk(0.7, 1), L(2.4, 1), I(2.8, 0), L(3.0, 0));
        b.Track(win, P.Op, 3.0, D(0, 0), L(0.3, 0), L(0.55, 1), L(2.5, 1), L(2.8, 0), L(3.0, 0));

        // gears: big 12 teeth, small 8 (1.5x faster, the other way); the small gear's gap faces the big gear's tooth
        double bx = 168, by = 62, sx = bx + 35 * Math.Cos(Math.PI / 3), sy = by + 35 * Math.Sin(Math.PI / 3);
        var big = b.Draw(Gear(bx, by, 24, 19, 12, 0, 6.5), b.Accent);
        var small = b.Draw(Gear(sx, sy, 16, 11, 8, -7.5, 4.5), Sec);
        b.Pivot(big, bx, by);
        b.Pivot(small, sx, sy);
        b.Track(big, P.Rot, 6, D(0, 0), L(6, 360));
        b.Track(small, P.Rot, 6, D(0, 0), L(6, -540));
    }

    /// <summary>An envelope opening and a figure moving out of it onto a computer.</summary>
    private static void GettingIn(SceneBuilder b)
    {
        b.StaticAt = 1.55;
        b.Draw(Geometry.Parse("M 51,58 Q 105,14 160,70"), null, Bdr, 2, dash: true);
        // envelope: the open flap behind, a letter rising out, the body, the closed flap in front
        var back = b.Draw(Geometry.Parse("M 22,74 L 80,74 L 51,96 Z"), Alt, Sec, 2);
        b.Pivot(back, 51, 74);
        b.Track(back, P.SY, 3.6, D(0, 0), L(0.25, 0), O(0.5, -0.8), L(3.05, -0.8), I(3.3, 0), L(3.6, 0));
        var letter = b.Group(31, 78, 40, 30);
        b.Box(letter, 0, 0, 40, 30, 2, Surf, Sec, 1.5);
        b.Box(letter, 7, 7, 26, 3, 1.5, b.Accent);
        b.Box(letter, 7, 13, 18, 3, 1.5, Bdr);
        b.Track(letter, P.Y, 3.6, D(0, 0), L(0.45, 0), O(0.8, -15), L(2.8, -15), I(3.1, 0), L(3.6, 0));
        b.Box(22, 74, 58, 40, 3, Alt, Sec, 2);
        b.Draw(Geometry.Parse("M 22,114 L 46,96 M 80,114 L 56,96"), null, Bdr, 1.5);
        var flap = b.Draw(Geometry.Parse("M 22,74 L 80,74 L 51,96 Z"), Alt, Sec, 2);
        b.Pivot(flap, 51, 74);
        b.Track(flap, P.SY, 3.6, D(0, 1), I(0.25, 0), L(3.3, 0), O(3.55, 1), L(3.6, 1));

        var scr = b.Computer(160, 48, 84);
        var glow = b.Box(scr.X, scr.Y, scr.Width, scr.Height, 2, b.Accent);
        b.Track(glow, P.Op, 3.6, D(0, 0), L(2.1, 0), L(2.4, 0.28), L(3.0, 0.28), L(3.3, 0), L(3.6, 0));

        var who = b.Glyph("", 28, 51, 92, b.Accent);
        b.Track(who, P.Op, 3.6, D(0, 0), L(0.45, 0), L(0.8, 1), L(2.9, 1), L(3.2, 0), L(3.6, 0));
        b.Track(who, P.X, 3.6, D(0, 0), L(0.8, 0), E(2.2, 109), L(3.6, 109));
        b.Track(who, P.Y, 3.6, D(0, 0), O(0.8, -34), O(1.5, -52), I(2.2, -16), L(3.6, -16));
    }

    /// <summary>A restart arrow circling the computer and a pin dropping onto it each round.</summary>
    private static void StayingIn(SceneBuilder b)
    {
        b.StaticAt = 1.3;
        b.Computer(110, 64, 74);
        double cx = 110, cy = 90, r = 58;
        var ring = b.Group(0, 0, SceneWidth, SceneHeight);
        b.Draw(ring, Arc(cx, cy, r, -60, 230), null, b.Accent, 4);
        double a1 = 230 * Math.PI / 180;
        var e = new Point(cx + r * Math.Cos(a1), cy + r * Math.Sin(a1));
        var t = new Vector(-Math.Sin(a1), Math.Cos(a1));
        var n = new Vector(Math.Cos(a1), Math.Sin(a1));
        var head = new PathGeometry(new[] { new PathFigure(e + t * 10, new PathSegment[] { new PolyLineSegment(new[] { e + n * 8, e - n * 8 }, true) }, true) });
        b.Draw(ring, head, b.Accent);
        b.Pivot(ring, cx, cy);
        b.Track(ring, P.Rot, 4, D(0, 0), L(4, 360));

        var pin = b.Glyph("", 26, 136, 62, b.Accent);
        b.Track(pin, P.Y, 4, D(0, -38), L(0.2, -38), Bn(1.0, 0), L(4, 0));
        b.Track(pin, P.Op, 4, D(0, 0), L(0.2, 0), L(0.45, 1), L(3.3, 1), L(3.8, 0), L(4, 0));
    }

    /// <summary>A user badge climbing steps up to a shield.</summary>
    private static void GainingControl(SceneBuilder b)
    {
        b.StaticAt = 1.8;
        b.Draw(Geometry.Parse("M 24,142 L 24,124 L 64,124 L 64,104 L 104,104 L 104,84 L 144,84 L 144,142 Z"), Alt, Sec, 2);
        var halo = b.Dot(178, 62, 34, b.Accent);
        b.Pivot(halo, 178, 62);
        b.Track(halo, P.Op, 4, D(0, 0), L(2.4, 0), O(2.7, 0.3), L(3.6, 0), L(4, 0));
        b.Track(halo, P.S, 4, D(0, 0.8), L(2.4, 0.8), O(3.6, 1.25), L(4, 1.25));
        b.Glyph("", 60, 178, 62, b.Accent);

        var badge = b.Group(44 - 15, 109 - 15, 30, 30);
        var ring = new Ellipse { Width = 30, Height = 30, StrokeThickness = 2.5 };
        ring.SetResourceReference(Shape.FillProperty, Surf);
        ring.SetResourceReference(Shape.StrokeProperty, b.Accent);
        badge.Children.Add(ring);
        b.Draw(badge, GlyphGeometry("", 16, 15, 15), Txt);
        b.Track(badge, P.X, 4, D(0, 0), L(0.4, 0), E(0.8, 40), L(1.2, 40), E(1.6, 80), L(2.0, 80), E(2.5, 134), L(3.5, 134), D(3.55, 0), L(4, 0));
        b.Track(badge, P.Y, 4, D(0, 0), L(0.4, 0), O(0.6, -30), I(0.8, -20), L(1.2, -20), O(1.4, -50), I(1.6, -40), L(2.0, -40),
            O(2.25, -64), I(2.5, -47), L(3.5, -47), D(3.55, 0), L(4, 0));
        b.Track(badge, P.Op, 4, D(0, 1), L(3.1, 1), L(3.45, 0), L(3.6, 0), L(3.95, 1), L(4, 1));
    }

    /// <summary>Log pages sliding into a bin and fading.</summary>
    private static void CoveringTracks(SceneBuilder b)
    {
        b.StaticAt = 1.0;
        b.Doc(44, 82, 38, 48, false);
        b.Doc(52, 76, 38, 48, false);
        for (int i = 0; i < 2; i++)
        {
            var page = b.Doc(52, 76, 38, 48, true);
            double o = i * 1.5;
            b.Track(page, P.X, 3, o, D(0, 0), L(0.3, 0), E(1.6, 114), L(3, 114));
            b.Track(page, P.Y, 3, o, D(0, 0), L(0.3, 0), O(0.9, -26), I(1.6, 28), L(3, 28));
            b.Track(page, P.S, 3, o, D(0, 1), L(1.2, 1), I(1.7, 0.35), L(3, 0.35));
            b.Track(page, P.Rot, 3, o, D(0, 0), L(0.3, 0), L(1.6, 28), L(3, 28));
            b.Track(page, P.Op, 3, o, D(0, 0), L(0.3, 1), L(1.35, 1), L(1.7, 0), L(3, 0));
        }
        b.Glyph("", 58, 166, 108, Sec);
    }

    /// <summary>A key lifting out of a lock and drifting away.</summary>
    private static void StealingPasswords(SceneBuilder b)
    {
        b.StaticAt = 1.8;
        var shackle = b.Draw(Geometry.Parse("M 58,86 L 58,72 A 16,16 0 0 1 90,72 L 90,86"), null, Sec, 7);
        b.Track(shackle, P.Y, 3.6, D(0, 0), L(0.9, 0), O(1.15, -7), L(3.0, -7), E(3.5, 0), L(3.6, 0));
        b.Box(46, 82, 56, 46, 7, Alt, Sec, 2);
        b.Dot(74, 100, 5.5, Sec);
        b.Box(71.5, 101, 5, 13, 2, Sec);

        // key, tip in the keyhole, bow to the right
        var key = b.Group(72, 100 - 12, 58, 24);
        b.Draw(key, new EllipseGeometry(new Point(46, 12), 9, 9), null, b.Accent, 4.5);
        b.Box(key, 2, 10, 36, 4.5, 2, b.Accent);
        b.Box(key, 7, 13, 4, 7, 1, b.Accent);
        b.Box(key, 15, 13, 4, 5, 1, b.Accent);
        b.Track(key, P.X, 3.6, D(0, 0), L(0.6, 0), E(1.2, 24), O(2.9, 90), D(3.0, 0), L(3.6, 0));
        b.Track(key, P.Y, 3.6, D(0, 0), L(1.2, 0), E(2.9, -64), D(3.0, 0), L(3.6, 0));
        b.Track(key, P.Rot, 3.6, D(0, 0), L(1.2, 0), E(2.9, -28), D(3.0, 0), L(3.6, 0));
        b.Track(key, P.Op, 3.6, D(0, 1), L(2.3, 1), L(2.9, 0), L(3.15, 0), L(3.6, 1));
    }

    /// <summary>A magnifier sweeping over folders and a computer.</summary>
    private static void LookingAround(SceneBuilder b)
    {
        b.StaticAt = 0.9;
        b.Glyph("", 40, 44, 108, Sec);
        b.Computer(110, 90, 50);
        b.Glyph("", 40, 176, 108, Sec);
        var lens = b.Glyph("", 48, 116, 82, b.Accent);
        b.Track(lens, P.X, 3.6, D(0, -66), E(1.8, 64), E(3.6, -66));
        b.Track(lens, P.Y, 3.6, D(0, 0), E(0.9, -12), E(1.8, 0), E(2.7, -12), E(3.6, 0));
    }

    /// <summary>A packet hopping from computer to computer.</summary>
    private static void Spreading(SceneBuilder b)
    {
        b.StaticAt = 0.75;
        b.Draw(Geometry.Parse("M 38,106 L 110,60 L 182,106"), null, Sec, 2, dash: true);
        var a = b.Computer(38, 92, 46);
        var m = b.Computer(110, 46, 46);
        var c = b.Computer(182, 92, 46);
        b.Box(a.X, a.Y, a.Width, a.Height, 2, b.Accent).Opacity = 0.3;
        var gm = b.Box(m.X, m.Y, m.Width, m.Height, 2, b.Accent);
        b.Track(gm, P.Op, 3.6, D(0, 0), L(1.1, 0), L(1.3, 0.3), L(3.1, 0.3), L(3.4, 0), L(3.6, 0));
        var gc = b.Box(c.X, c.Y, c.Width, c.Height, 2, b.Accent);
        b.Track(gc, P.Op, 3.6, D(0, 0), L(2.5, 0), L(2.7, 0.3), L(3.1, 0.3), L(3.4, 0), L(3.6, 0));

        var packet = b.Box(38 - 8, a.Y + a.Height / 2 - 8, 16, 16, 4, b.Accent);
        b.Track(packet, P.X, 3.6, D(0, 0), L(0.2, 0), E(1.2, 72), L(1.6, 72), E(2.6, 144), L(3.25, 144), D(3.3, 0), L(3.6, 0));
        b.Track(packet, P.Y, 3.6, D(0, 0), L(0.2, 0), E(1.2, -46), L(1.6, -46), E(2.6, 0), L(3.6, 0));
        b.Track(packet, P.Op, 3.6, D(0, 1), L(2.9, 1), L(3.2, 0), L(3.3, 0), L(3.6, 1));
    }

    /// <summary>Files flying into one folder.</summary>
    private static void GatheringData(SceneBuilder b)
    {
        b.StaticAt = 0.65;   // a page a third of the way into the folder, fully opaque
        var starts = new[] { (x: 38.0, y: 40.0, rot: -18.0), (x: 110.0, y: 28.0, rot: 0.0), (x: 182.0, y: 40.0, rot: 18.0) };
        for (int i = 0; i < 3; i++)
        {
            var (x, y, rot) = starts[i];
            var doc = b.Doc(x, y, 24, 30, true);
            double o = -i * 0.8;
            b.Track(doc, P.X, 2.4, o, D(0, 0), L(0.1, 0), E(1.4, 110 - x), L(2.4, 110 - x));
            b.Track(doc, P.Y, 2.4, o, D(0, 0), L(0.1, 0), E(1.4, 104 - y), L(2.4, 104 - y));
            b.Track(doc, P.Rot, 2.4, o, D(0, rot), L(0.1, rot), E(1.4, 0), L(2.4, 0));
            b.Track(doc, P.S, 2.4, o, D(0, 1), L(0.9, 1), L(1.4, 0.4), L(2.4, 0.4));
            b.Track(doc, P.Op, 2.4, o, D(0, 0), L(0.25, 1), L(1.15, 1), L(1.4, 0), L(2.4, 0));
        }
        b.Glyph("", 66, 110, 112, b.Accent);
    }

    /// <summary>Files flowing up from a computer to a cloud.</summary>
    private static void TakingDataOut(SceneBuilder b)
    {
        b.StaticAt = 1.1;    // a page half-way up to the cloud, another leaving the screen, both opaque
        var scr = b.Computer(110, 90, 76);
        for (int i = 0; i < 3; i++)
        {
            var doc = b.Doc(110, scr.Y + scr.Height / 2, 18, 23, true);
            double o = -i * 0.8;
            b.Track(doc, P.Y, 2.4, o, D(0, 0), L(1.6, -60), L(2.4, -60));
            b.Track(doc, P.X, 2.4, o, D(0, 0), E(0.6, -9), E(1.2, 7), E(1.6, 0), L(2.4, 0));
            b.Track(doc, P.S, 2.4, o, D(0, 0.6), O(0.4, 1), L(1.2, 1), L(1.6, 0.5), L(2.4, 0.5));
            b.Track(doc, P.Op, 2.4, o, D(0, 0), L(0.3, 1), L(1.2, 1), L(1.55, 0), L(2.4, 0));
        }
        b.Glyph("", 58, 110, 36, Sec);
        b.Glyph("", 16, 110, 40, b.Accent);
    }

    /// <summary>Signal waves pulsing from a distant computer to a near one, whose pointer moves.</summary>
    private static void RemoteControl(SceneBuilder b)
    {
        b.StaticAt = 0.9;
        var far = b.Computer(184, 34, 40);
        var scr = b.Computer(62, 66, 84);
        double cx = far.X + far.Width / 2, cy = far.Y + far.Height / 2 + 4;
        double dir = Math.Atan2(scr.Y + 6 - cy, scr.Right + 6 - cx) * 180 / Math.PI;
        for (int i = 0; i < 3; i++)
        {
            double r = 26 + i * 20;
            var wave = b.Draw(Arc(cx, cy, r, dir - 32, dir + 32), null, b.Accent, 3.5);
            b.Track(wave, P.Op, 1.8, -i * 0.3, D(0, 0.15), L(0.3, 1), L(0.9, 0.15), L(1.8, 0.15));
        }
        var ptr = b.Draw(Geometry.Parse("M 0,0 L 0,15 L 4,11 L 7,17 L 9.5,16 L 6.5,10 L 11.5,10 Z"), b.Accent, Surf, 1);
        Canvas.SetLeft(ptr, scr.X + scr.Width / 2 - 5);
        Canvas.SetTop(ptr, scr.Y + scr.Height / 2 - 9);
        b.Track(ptr, P.X, 2.4, D(0, 0), E(0.8, 16), E(1.6, -12), E(2.4, 0));
        b.Track(ptr, P.Y, 2.4, D(0, 0), E(0.8, -9), E(1.6, 7), E(2.4, 0));
    }

    /// <summary>A crack spreading across files, then a lock closing over them.</summary>
    private static void Damage(SceneBuilder b)
    {
        b.StaticAt = 2.6;
        b.Doc(80, 80, 42, 54, false).RenderTransform = new RotateTransform(-10);
        b.Doc(140, 80, 42, 54, false).RenderTransform = new RotateTransform(10);
        b.Doc(110, 74, 42, 54, false);

        var pts = new[] { new Point(58, 52), new Point(80, 68), new Point(72, 80), new Point(98, 88), new Point(92, 100), new Point(126, 94), new Point(120, 106), new Point(160, 110) };
        double len = 0;
        for (int i = 1; i < pts.Length; i++) len += (pts[i] - pts[i - 1]).Length;
        const double th = 3;
        var crack = b.Draw(new PathGeometry(new[] { new PathFigure(pts[0], new[] { new PolyLineSegment(pts.Skip(1), true) }, false) }), null, b.Accent, th);
        crack.StrokeLineJoin = PenLineJoin.Miter;
        crack.StrokeDashArray = new DoubleCollection { len / th + 1, len / th + 1 };
        double full = len / th + 1;
        b.Track(crack, P.Dash, 4, D(0, full), L(0.2, full), O(1.4, 0), L(3.8, 0), D(3.85, full), L(4, full));
        b.Track(crack, P.Op, 4, D(0, 1), L(3.3, 1), L(3.7, 0), L(3.85, 0), L(3.9, 1), L(4, 1));

        var lockG = b.Group(110 - 20, 88, 40, 50);
        var shackle = b.Draw(lockG, Geometry.Parse("M 10,26 L 10,16 A 10,10 0 0 1 30,16 L 30,26"), null, b.Accent, 5);
        b.Box(lockG, 2, 22, 36, 27, 5, b.Accent);
        var hole = new Ellipse { Width = 7, Height = 7 };
        hole.SetResourceReference(Shape.FillProperty, Surf);
        Canvas.SetLeft(hole, 16.5); Canvas.SetTop(hole, 30);
        lockG.Children.Add(hole);
        b.Track(lockG, P.S, 4, D(0, 0), L(1.4, 0), Bk(1.9, 1), L(3.3, 1), L(3.7, 0.6), L(4, 0.6));
        b.Track(lockG, P.Op, 4, D(0, 0), L(1.4, 0), L(1.7, 1), L(3.3, 1), L(3.7, 0), L(4, 0));
        b.Track(shackle, P.Y, 4, D(0, -8), L(2.0, -8), I(2.35, 0), L(4, 0));
    }

    /// <summary>A gently pulsing info mark.</summary>
    private static void Other(SceneBuilder b)
    {
        b.StaticAt = 0.8;
        for (int i = 0; i < 2; i++)
        {
            var halo = new Ellipse { Width = 76, Height = 76, StrokeThickness = 3 };
            halo.SetResourceReference(Shape.StrokeProperty, b.Accent);
            Canvas.SetLeft(halo, 110 - 38); Canvas.SetTop(halo, 80 - 38);
            halo.RenderTransformOrigin = new Point(0.5, 0.5);
            b.Canvas.Children.Add(halo);
            b.Track(halo, P.S, 2.4, -i * 1.2, D(0, 0.9), O(2.0, 1.75), L(2.4, 1.75));
            b.Track(halo, P.Op, 2.4, -i * 1.2, D(0, 0.6), O(2.0, 0), L(2.4, 0));
        }
        var mark = b.Glyph("", 64, 110, 80, b.Accent);
        b.Track(mark, P.S, 2.4, D(0, 1), E(1.2, 1.07), E(2.4, 1));
    }

    // ------------------------------------------------------------------ geometry

    private static Geometry Gear(double cx, double cy, double rOut, double rRoot, int teeth, double phaseDeg, double hole)
    {
        double p = 2 * Math.PI / teeth, ph = phaseDeg * Math.PI / 180;
        Point At(double a, double r) => new(cx + r * Math.Cos(a), cy + r * Math.Sin(a));
        var pts = new List<Point>();
        for (int i = 0; i < teeth; i++)
        {
            double a = ph + i * p;
            pts.Add(At(a - 0.5 * p, rRoot));
            pts.Add(At(a - 0.27 * p, rRoot));
            pts.Add(At(a - 0.14 * p, rOut));
            pts.Add(At(a + 0.14 * p, rOut));
            pts.Add(At(a + 0.27 * p, rRoot));
        }
        var body = new PathGeometry(new[] { new PathFigure(pts[0], new[] { new PolyLineSegment(pts.Skip(1), true) }, true) });
        var g = new GeometryGroup { FillRule = FillRule.EvenOdd };
        g.Children.Add(body);
        g.Children.Add(new EllipseGeometry(new Point(cx, cy), hole, hole));
        return g;
    }

    /// <summary>Clockwise arc (screen angles in degrees, 0 = right, 90 = down).</summary>
    private static Geometry Arc(double cx, double cy, double r, double a0, double a1)
    {
        Point At(double a) => new(cx + r * Math.Cos(a * Math.PI / 180), cy + r * Math.Sin(a * Math.PI / 180));
        var seg = new ArcSegment(At(a1), new Size(r, r), 0, a1 - a0 > 180, SweepDirection.Clockwise, true);
        return new PathGeometry(new[] { new PathFigure(At(a0), new[] { seg }, false) });
    }

    private static FontFamily IconFont()
    {
        try
        {
            if (Application.Current?.TryFindResource("Font.Icon") is FontFamily f) return f;
        }
        catch (InvalidOperationException) { }
        return new FontFamily("Segoe Fluent Icons, Segoe MDL2 Assets");
    }

    /// <summary>Outline of an icon-font glyph of em size <paramref name="size"/>, its ink centred on (cx, cy).</summary>
    internal static Geometry GlyphGeometry(string ch, double size, double cx, double cy)
    {
        var ft = new FormattedText(ch, CultureInfo.InvariantCulture, FlowDirection.LeftToRight,
            new Typeface(IconFont(), FontStyles.Normal, FontWeights.Normal, FontStretches.Normal), size, Brushes.Black, 1.0);
        var g = ft.BuildGeometry(new Point(0, 0));
        var bounds = g.Bounds;
        if (bounds.IsEmpty) return g;
        g.Transform = new TranslateTransform(cx - (bounds.Left + bounds.Width / 2), cy - (bounds.Top + bounds.Height / 2));
        return g;
    }

    // ------------------------------------------------------------------ key frames

    private enum P { X, Y, SX, SY, S, Rot, Op, Dash }

    private readonly record struct K(double T, double V, bool Discrete, IEasingFunction? Ease);

    private static readonly IEasingFunction SineInOut = Frozen(new SineEase { EasingMode = EasingMode.EaseInOut });
    private static readonly IEasingFunction CubicOut = Frozen(new CubicEase { EasingMode = EasingMode.EaseOut });
    private static readonly IEasingFunction CubicIn = Frozen(new CubicEase { EasingMode = EasingMode.EaseIn });
    private static readonly IEasingFunction BackOut = Frozen(new BackEase { Amplitude = 0.5, EasingMode = EasingMode.EaseOut });
    private static readonly IEasingFunction BounceOut = Frozen(new BounceEase { Bounces = 2, Bounciness = 3, EasingMode = EasingMode.EaseOut });

    private static IEasingFunction Frozen(EasingFunctionBase e) { e.Freeze(); return e; }

    private static K D(double t, double v) => new(t, v, true, null);
    private static K L(double t, double v) => new(t, v, false, null);
    private static K E(double t, double v) => new(t, v, false, SineInOut);
    private static K O(double t, double v) => new(t, v, false, CubicOut);
    private static K I(double t, double v) => new(t, v, false, CubicIn);
    private static K Bk(double t, double v) => new(t, v, false, BackOut);
    private static K Bn(double t, double v) => new(t, v, false, BounceOut);

    /// <summary>The value of a key-frame track at local time <paramref name="t"/> (same rules as the WPF key frames).</summary>
    private static double ValueAt(K[] frames, double t)
    {
        if (t <= frames[0].T) return frames[0].V;
        for (int i = 1; i < frames.Length; i++)
        {
            if (t >= frames[i].T) continue;
            var a = frames[i - 1];
            var b = frames[i];
            if (b.Discrete) return a.V;
            double u = (t - a.T) / (b.T - a.T);
            if (b.Ease != null) u = b.Ease.Ease(u);
            return a.V + (b.V - a.V) * u;
        }
        return frames[^1].V;
    }

    // ------------------------------------------------------------------ builder

    private sealed class SceneBuilder
    {
        public readonly Canvas Canvas = new() { Width = SceneWidth, Height = SceneHeight, IsHitTestVisible = false };
        public readonly Storyboard Story = new();
        public readonly string Accent;
        /// <summary>Loop time (seconds) shown as the still frame.</summary>
        public double StaticAt;
        private readonly List<(UIElement El, P Prop, double Period, double Phase, K[] Frames)> _tracks = new();
        /// <summary>The animated objects and properties (to clear the loop at once on stop).</summary>
        public readonly List<(IAnimatable Target, DependencyProperty Prop)> Targets = new();

        public SceneBuilder(string accent) => Accent = accent;

        /// <summary>Sets every animated property's base value to the loop at <see cref="StaticAt"/> (the still frame).</summary>
        public void Finish()
        {
            foreach (var (el, prop, period, phase, frames) in _tracks)
                SetBase(el, prop, ValueAt(frames, (StaticAt + phase) % period));
        }

        // ---- animation

        public void Track(UIElement el, P prop, double period, params K[] frames) => Track(el, prop, period, 0, frames);

        /// <summary>A looping key-frame track; <paramref name="offset"/> shifts its phase (seconds ahead, may be negative).</summary>
        public void Track(UIElement el, P prop, double period, double offset, params K[] frames)
        {
            if (prop == P.S)
            {
                Track(el, P.SX, period, offset, frames);
                Track(el, P.SY, period, offset, frames);
                return;
            }
            Xf(el);
            double phase = ((offset % period) + period) % period;
            var anim = new DoubleAnimationUsingKeyFrames { Duration = TimeSpan.FromSeconds(period), RepeatBehavior = RepeatBehavior.Forever };
            if (phase > 0) anim.BeginTime = TimeSpan.FromSeconds(-phase);
            foreach (var k in frames)
            {
                var kt = KeyTime.FromTimeSpan(TimeSpan.FromSeconds(k.T));
                anim.KeyFrames.Add(k.Discrete ? new DiscreteDoubleKeyFrame(k.V, kt)
                    : k.Ease != null ? new EasingDoubleKeyFrame(k.V, kt, k.Ease)
                    : new LinearDoubleKeyFrame(k.V, kt));
            }
            Storyboard.SetTarget(anim, el);
            Storyboard.SetTargetProperty(anim, PathFor(prop));
            Story.Children.Add(anim);
            _tracks.Add((el, prop, period, phase, frames));
            Targets.Add(prop switch
            {
                P.Op => (el, UIElement.OpacityProperty),
                P.Dash => (el, Shape.StrokeDashOffsetProperty),
                _ => (((TransformGroup)el.RenderTransform).Children[Index(prop)], Dp(prop)),
            });
        }

        private static PropertyPath PathFor(P prop) => prop switch
        {
            P.Op => new PropertyPath(UIElement.OpacityProperty),
            P.Dash => new PropertyPath(Shape.StrokeDashOffsetProperty),
            _ => new PropertyPath("(0).(1)[" + Index(prop) + "].(2)", UIElement.RenderTransformProperty, TransformGroup.ChildrenProperty, Dp(prop)),
        };

        private static int Index(P prop) => prop switch { P.SX or P.SY => 0, P.Rot => 1, _ => 2 };

        private static DependencyProperty Dp(P prop) => prop switch
        {
            P.SX => ScaleTransform.ScaleXProperty,
            P.SY => ScaleTransform.ScaleYProperty,
            P.Rot => RotateTransform.AngleProperty,
            P.X => TranslateTransform.XProperty,
            _ => TranslateTransform.YProperty,
        };

        private static void SetBase(UIElement el, P prop, double v)
        {
            switch (prop)
            {
                case P.Op: el.Opacity = v; break;
                case P.Dash: ((Shape)el).StrokeDashOffset = v; break;
                default: ((TransformGroup)el.RenderTransform).Children[Index(prop)].SetValue(Dp(prop), v); break;
            }
        }

        /// <summary>Gives the element a Scale, Rotate, Translate group (scale and rotation about its centre unless <see cref="Pivot"/> says otherwise).</summary>
        private static TransformGroup Xf(UIElement el)
        {
            if (el.RenderTransform is TransformGroup { Children.Count: 3 } g && !g.IsFrozen) return g;
            g = new TransformGroup();
            g.Children.Add(new ScaleTransform(1, 1));
            g.Children.Add(new RotateTransform(0));
            g.Children.Add(new TranslateTransform());
            el.RenderTransform = g;
            if (el.RenderTransformOrigin == default) el.RenderTransformOrigin = new Point(0.5, 0.5);
            return g;
        }

        /// <summary>Scale and rotate about (cx, cy) in the element's own coordinates (for shapes drawn in scene coordinates).</summary>
        public void Pivot(UIElement el, double cx, double cy)
        {
            var g = Xf(el);
            el.RenderTransformOrigin = new Point(0, 0);
            var s = (ScaleTransform)g.Children[0];
            var r = (RotateTransform)g.Children[1];
            s.CenterX = cx; s.CenterY = cy;
            r.CenterX = cx; r.CenterY = cy;
        }

        // ---- drawing

        public T Add<T>(Panel into, T el) where T : UIElement { into.Children.Add(el); return el; }

        public Rectangle Box(double x, double y, double w, double h, double r, string fill, string? stroke = null, double th = 0) =>
            Box(Canvas, x, y, w, h, r, fill, stroke, th);

        public Rectangle Box(Panel into, double x, double y, double w, double h, double r, string fill, string? stroke = null, double th = 0)
        {
            var rc = new Rectangle { Width = w, Height = h, RadiusX = r, RadiusY = r, StrokeThickness = th };
            rc.SetResourceReference(Shape.FillProperty, fill);
            if (stroke != null) rc.SetResourceReference(Shape.StrokeProperty, stroke);
            Canvas.SetLeft(rc, x);
            Canvas.SetTop(rc, y);
            return Add(into, rc);
        }

        public Ellipse Dot(double cx, double cy, double r, string fill)
        {
            var e = new Ellipse { Width = 2 * r, Height = 2 * r };
            e.SetResourceReference(Shape.FillProperty, fill);
            Canvas.SetLeft(e, cx - r);
            Canvas.SetTop(e, cy - r);
            return Add(Canvas, e);
        }

        public Path Draw(Geometry g, string? fill, string? stroke = null, double th = 0, bool dash = false) => Draw(Canvas, g, fill, stroke, th, dash);

        public Path Draw(Panel into, Geometry g, string? fill, string? stroke = null, double th = 0, bool dash = false)
        {
            var p = new Path { Data = g, StrokeThickness = th, StrokeStartLineCap = PenLineCap.Round, StrokeEndLineCap = PenLineCap.Round, StrokeLineJoin = PenLineJoin.Round };
            if (fill != null) p.SetResourceReference(Shape.FillProperty, fill);
            if (stroke != null) p.SetResourceReference(Shape.StrokeProperty, stroke);
            if (dash) { p.StrokeDashArray = new DoubleCollection { 1.5, 2.5 }; p.StrokeDashCap = PenLineCap.Round; }
            return Add(into, p);
        }

        /// <summary>An icon-font glyph centred on (cx, cy); scales and turns about that point.</summary>
        public Path Glyph(string ch, double size, double cx, double cy, string fill)
        {
            var p = Draw(GlyphGeometry(ch, size, cx, cy), fill);
            Pivot(p, cx, cy);
            return p;
        }

        /// <summary>A sub-canvas at (x, y) that moves, scales and fades as one.</summary>
        public Canvas Group(double x, double y, double w, double h)
        {
            var c = new Canvas { Width = w, Height = h, RenderTransformOrigin = new Point(0.5, 0.5) };
            Canvas.SetLeft(c, x);
            Canvas.SetTop(c, y);
            return Add(Canvas, c);
        }

        /// <summary>A monitor on a stand, <paramref name="w"/> wide, top edge at <paramref name="top"/>; returns its screen.</summary>
        public Rect Computer(double cx, double top, double w)
        {
            double h = Math.Round(w * 0.64), x = cx - w / 2, inset = Math.Max(3.5, w * 0.055);
            Box(x, top, w, h, Math.Max(3, w * 0.05), Alt, Sec, w < 60 ? 1.5 : 2);
            var scr = new Rect(x + inset, top + inset, w - 2 * inset, h - 2 * inset);
            Box(scr.X, scr.Y, scr.Width, scr.Height, 2, Surf);
            Box(cx - w * 0.07, top + h, w * 0.14, w * 0.1, 0, Sec);
            Box(cx - w * 0.24, top + h + w * 0.1, w * 0.48, Math.Max(3, w * 0.045), 1.5, Sec);
            return scr;
        }

        /// <summary>A page with a folded corner and text lines (accent lines when <paramref name="accent"/>), centred on (cx, cy).</summary>
        public Canvas Doc(double cx, double cy, double w, double h, bool accent)
        {
            var c = Group(cx - w / 2, cy - h / 2, w, h);
            double fold = Math.Round(w * 0.3);
            var page = Geometry.Parse(FormattableString.Invariant($"M 0,0 L {w - fold},0 L {w},{fold} L {w},{h} L 0,{h} Z"));
            Draw(c, page, Surf, accent ? Accent : Sec, w < 30 ? 1.5 : 2);
            Draw(c, Geometry.Parse(FormattableString.Invariant($"M {w - fold},0 L {w - fold},{fold} L {w},{fold}")), null, accent ? Accent : Sec, w < 30 ? 1.2 : 1.5);
            double lh = Math.Max(2, h * 0.07);
            Box(c, w * 0.18, h * 0.42, w * 0.64, lh, lh / 2, accent ? Accent : Bdr);
            Box(c, w * 0.18, h * 0.6, w * 0.64, lh, lh / 2, accent ? Accent : Bdr);
            Box(c, w * 0.18, h * 0.78, w * 0.42, lh, lh / 2, accent ? Accent : Bdr);
            return c;
        }

        /// <summary>A terminal prompt chevron (8 x 9) with its top-left at (x, y).</summary>
        public void Chevron(double x, double y, string stroke) =>
            Draw(Geometry.Parse(FormattableString.Invariant($"M {x},{y} L {x + 6},{y + 4.5} L {x},{y + 9}")), null, stroke, 2.2);
    }
}
