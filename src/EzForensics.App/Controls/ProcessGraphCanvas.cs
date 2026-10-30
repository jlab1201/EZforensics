using System.Windows;
using System.Windows.Automation;
using System.Windows.Automation.Peers;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Shapes;
using EzForensics.App.ViewModels;
using EzForensics.Core.Processes;

namespace EzForensics.App.Controls;

/// <summary>
/// Process tree on an isometric floor (Tanium style, bird's-eye view): one templated node per expanded process (small diamond on a pedestal + name pill), thin
/// curly-brace connectors from the parent's to the children's diamond centres (<see cref="BuildConnectors"/>) (dashed from a ghost parent: the parent was not captured), a viewport-wide isometric
/// grid that pans and zooms with the graph, mouse-wheel zoom (around the cursor), <see cref="Zoom"/> for a slider, drag to pan, <see cref="Fit"/>.
/// Layout comes from <see cref="ProcessTreeLayout"/> over the source's <see cref="IProcessGraphSource.LayoutRoots"/> / <see cref="IProcessGraphSource.LayoutChildren"/>
/// (processes and same-name stacks); only drawn items get a presenter. Each layout point (x = depth, y = row) is the item's diamond centre
/// and is projected by x' = (x − y)·cos θ, y' = (x + y)·sin θ (θ = <see cref="ElevationDegrees"/>, a low camera: grid lines run 20° from horizontal): a child
/// steps right-and-down from its parent, a sibling left-and-down. Presenters are not transformed (text stays crisp); only their diamond centre is placed on the
/// projected point.
/// </summary>
public sealed class ProcessGraphCanvas : Border
{
    /// <summary>Presenter size of every item (node, stack): diamond column + name pill + the row under it.</summary>
    public const double NodeWidth = 210, NodeHeight = 72;
    /// <summary>Where the diamond centre (the layout anchor, where edges meet) sits inside a presenter.</summary>
    public const double DiamondCenterX = 24, DiamondCenterY = 27;
    /// <summary>
    /// Layout pitch (not the presenter size; everything 1.5x since Oct 27): a column step is <see cref="LayoutNodeWidth"/> + <see cref="HorizontalGap"/> = 198, a
    /// row step <see cref="LayoutNodeHeight"/> + <see cref="VerticalGap"/> = 186. An item's box around its anchor is about x −19.5..+177 (diamond, pill ≤ 156 px
    /// from +21), y −21..+40.5 (badge dot, the "N children" row). Two items with layout delta (dx, dy) clear each other when |(dx + dy)·sin20| ≥ 61.5 or
    /// |(dx − dy)·cos20| ≥ 196.5, i.e. |dx + dy| ≥ 179.8 or |dx − dy| ≥ 209.1. A sibling (0, 186) clears vertically (63.6 px), and a child (198, s) for any row
    /// offset s clears one way or the other (2·198 = 396 ≥ 179.8 + 209.1 = 388.9); items two or more columns apart always clear.
    /// </summary>
    public const double LayoutNodeWidth = 165, HorizontalGap = 33, LayoutNodeHeight = 150, VerticalGap = 36, RootGap = 18, Margin0 = 36;
    public const double MinZoom = 0.1, MaxZoom = 3;
    /// <summary>Camera elevation of the floor projection: the angle of the grid lines (both layout axes) from horizontal. 30° is true isometric; lower is flatter.</summary>
    public const double ElevationDegrees = 20;
    /// <summary>Projection factors (cos θ, sin θ) for <see cref="ElevationDegrees"/>.</summary>
    public static readonly double IsoX = Math.Cos(ElevationDegrees * Math.PI / 180), IsoY = Math.Sin(ElevationDegrees * Math.PI / 180);
    /// <summary>Floor grid: one cell is this many layout units (half a row step), at least <see cref="MinGridCellPx"/> high on screen (doubled when zoomed out).</summary>
    public const double GridCell = 93, MinGridCellPx = 18, GridLinePx = 0.7, GridOpacity = 0.4;

    public static readonly DependencyProperty ShowGridProperty = DependencyProperty.Register(nameof(ShowGrid), typeof(bool), typeof(ProcessGraphCanvas),
        new PropertyMetadata(true, (d, e) => ((ProcessGraphCanvas)d).ApplyShowGrid((bool)e.NewValue)));

    /// <summary>Draw the floor grid (View › Show grid); off removes the floor brush.</summary>
    public bool ShowGrid { get => (bool)GetValue(ShowGridProperty); set => SetValue(ShowGridProperty, value); }

    public static readonly DependencyProperty ZoomProperty = DependencyProperty.Register(nameof(Zoom), typeof(double), typeof(ProcessGraphCanvas),
        new FrameworkPropertyMetadata(1.0, FrameworkPropertyMetadataOptions.BindsTwoWayByDefault, (d, e) => ((ProcessGraphCanvas)d).OnZoomChanged((double)e.NewValue),
            (_, v) => Math.Clamp((double)v, MinZoom, MaxZoom)));

    /// <summary>Current zoom (bind a slider to it); set from outside it zooms around the viewport centre.</summary>
    public double Zoom { get => (double)GetValue(ZoomProperty); set => SetValue(ZoomProperty, value); }
    private bool _settingZoom;

    public static readonly DependencyProperty SourceProperty = DependencyProperty.Register(nameof(Source), typeof(IProcessGraphSource), typeof(ProcessGraphCanvas),
        new PropertyMetadata(null, (d, e) => ((ProcessGraphCanvas)d).OnSourceChanged(e.OldValue as IProcessGraphSource, e.NewValue as IProcessGraphSource)));

    public static readonly DependencyProperty NodeTemplateProperty = DependencyProperty.Register(nameof(NodeTemplate), typeof(DataTemplate), typeof(ProcessGraphCanvas),
        new PropertyMetadata(null, (d, _) => ((ProcessGraphCanvas)d).Rebuild(clear: true)));

    /// <summary>Template of a <see cref="ProcessStackViewModel"/> (same size as a node).</summary>
    public static readonly DependencyProperty StackTemplateProperty = DependencyProperty.Register(nameof(StackTemplate), typeof(DataTemplate), typeof(ProcessGraphCanvas),
        new PropertyMetadata(null, (d, _) => ((ProcessGraphCanvas)d).Rebuild(clear: true)));

    public IProcessGraphSource? Source { get => (IProcessGraphSource?)GetValue(SourceProperty); set => SetValue(SourceProperty, value); }
    public DataTemplate? NodeTemplate { get => (DataTemplate?)GetValue(NodeTemplateProperty); set => SetValue(NodeTemplateProperty, value); }
    public DataTemplate? StackTemplate { get => (DataTemplate?)GetValue(StackTemplateProperty); set => SetValue(StackTemplateProperty, value); }

    private readonly Grid _host = new();
    private readonly Canvas _canvas = new();
    /// <summary>Viewport-wide isometric floor: Brush.Border lines through a tiled mask (theme follows the Fill resource; the mask is theme-free).</summary>
    private readonly Rectangle _floor = new() { IsHitTestVisible = false, Opacity = GridOpacity };
    private readonly DrawingBrush _floorMask;
    private readonly Pen _floorPen = new(Brushes.Black, GridLinePx);
    private readonly System.Windows.Shapes.Path _edges = new() { StrokeThickness = 1, IsHitTestVisible = false };
    /// <summary>Edges from a ghost parent (not captured): dashed.</summary>
    private readonly System.Windows.Shapes.Path _ghostEdges = new() { StrokeThickness = 1, IsHitTestVisible = false, StrokeDashArray = new DoubleCollection { 4, 3 } };
    /// <summary>The traced route of a PID click (<see cref="IProcessGraphSource.TrailNodes"/>): drawn over the normal connectors, thicker, in the trail accent.</summary>
    private readonly System.Windows.Shapes.Path _trail = new() { StrokeThickness = TrailThickness, IsHitTestVisible = false, StrokeLineJoin = PenLineJoin.Round };
    private IReadOnlyList<(object Parent, object Child)> _layoutEdges = Array.Empty<(object, object)>();
    private readonly ScaleTransform _scale = new(1, 1);
    private readonly TranslateTransform _translate = new(Margin0, Margin0);
    private readonly Dictionary<object, ContentPresenter> _presenters = new();
    /// <summary>
    /// Presenters of the items off the boot chain (same coordinates as the canvas): solid and drawn at their true (shrinking) size at every zoom, only not
    /// hit-testable far out (region clicks win there), see <see cref="UpdateSemanticZoom"/>.
    /// </summary>
    private readonly Canvas _nodeLayer = new();
    /// <summary>Presenters of the boot chain (<see cref="ProcessRegions.BootChain{T}"/>): always shown, kept at a readable screen size far out (<see cref="_bootScale"/>).</summary>
    private readonly Canvas _bootLayer = new();
    private readonly HashSet<object> _boot = new(ReferenceEqualityComparer.Instance);
    /// <summary>Shared inverse scale of the boot-chain presenters around their diamond centre.</summary>
    private readonly ScaleTransform _bootScale = new(1, 1, DiamondCenterX, DiamondCenterY);
    /// <summary>Connectors out of boot-chain parents (the others are <see cref="_edges"/> / <see cref="_ghostEdges"/>; none fade).</summary>
    private readonly System.Windows.Shapes.Path _bootEdges = new() { StrokeThickness = 1, IsHitTestVisible = false };
    private readonly System.Windows.Shapes.Path _bootGhostEdges = new() { StrokeThickness = 1, IsHitTestVisible = false, StrokeDashArray = new DoubleCollection { 4, 3 } };
    /// <summary>Region fills in canvas coordinates, behind the connectors and nodes.</summary>
    private readonly Canvas _regionLayer = new();
    private readonly List<RegionVisual> _regions = new();
    /// <summary>A region: its ground-plane <see cref="Shape"/> (canvas coordinates), the shape's canvas bounding box and its top (back) corner.</summary>
    private sealed record RegionVisual(object Parent, Geometry Shape, Rect Bounds, Point Apex, System.Windows.Shapes.Path Fill);
    /// <summary>The floor projection as a matrix: (x, y) → ((x − y)·cos θ, (x + y)·sin θ), so a layout rectangle becomes the parallelogram under its nodes.</summary>
    public static readonly Matrix ProjectionMatrix = new(IsoX, IsoY, -IsoX, IsoY, 0, 0);
    /// <summary>Region padding around its items' anchors (layout units), extra room for the name pill (it runs +x / −y in layout terms), corner radius.</summary>
    public const double RegionPad = 80, RegionPillPad = 40, RegionCorner = 40;

    /// <summary>
    /// Ground-plane outline of a region: one layout rectangle per run of adjacent groups (rows at most a row step apart; a boot sibling in between splits
    /// them), padded and rounded, projected by <see cref="ProjectionMatrix"/> and unioned. <paramref name="apex"/> is the top screen corner of the topmost piece.
    /// </summary>
    public static Geometry RegionShape(IEnumerable<IEnumerable<TreeLayoutPoint>> groups, double rowStep, out Point apex)
    {
        var rects = groups.Select(g => g.ToList()).Where(g => g.Count > 0)
            .Select(g => new Rect(new Point(g.Min(p => p.X), g.Min(p => p.Y)), new Point(g.Max(p => p.X), g.Max(p => p.Y))))
            .OrderBy(r => r.Y).ToList();
        var runs = new List<Rect>();
        foreach (var r in rects)
        {
            if (runs.Count > 0 && r.Y - runs[^1].Bottom <= rowStep + 1) runs[^1] = Rect.Union(runs[^1], r);
            else runs.Add(r);
        }
        apex = new Point(0, double.PositiveInfinity);
        Geometry? shape = null;
        var transform = new MatrixTransform(ProjectionMatrix);
        transform.Freeze();
        foreach (var r in runs)
        {
            var padded = new Rect(r.X - RegionPad, r.Y - RegionPad - RegionPillPad, r.Width + 2 * RegionPad + RegionPillPad, r.Height + 2 * RegionPad + RegionPillPad);
            var top = Project(padded.X, padded.Y);
            if (top.Y < apex.Y) apex = top;
            var piece = new RectangleGeometry(padded, RegionCorner, RegionCorner, transform);
            shape = shape == null ? piece : Geometry.Combine(shape, piece, GeometryCombineMode.Union, null);
        }
        shape ??= Geometry.Empty;
        shape.Freeze();
        return shape;
    }
    private RegionVisual? _downRegion;
    /// <summary>Projected content bounds in canvas coordinates (presenter boxes included); X / Y can be negative (rows step left).</summary>
    private Rect _content = Rect.Empty;
    private bool _fitPending = true;
    /// <summary>
    /// Layout cells of the drawn items (per tree: cleared when the source changes). An expand / collapse keeps every cell unless an expanded subtree
    /// would overlap it, then pushes it (and everything below) down; a collapse leaves gaps (<see cref="ProcessTreeLayout.Update{T}"/>); a full re-layout runs when <see cref="IProcessGraphSource.LayoutGeneration"/> changes.
    /// </summary>
    private readonly Dictionary<object, TreeLayoutPoint> _slots = new();
    /// <summary>Base row of every item: its row in the last full layout, or where it was first placed; a collapse puts items pushed down by an expand back on it.</summary>
    private readonly Dictionary<object, double> _baseRows = new();
    private int? _generation;

    // drag state
    private Point _downPos;
    private Point _downTranslate;
    private bool _dragging, _panned;
    private ProcessNodeViewModel? _downNode;

    public ProcessGraphCanvas()
    {
        ClipToBounds = true;
        Focusable = true;
        SetResourceReference(BackgroundProperty, "Brush.Input");
        _edges.SetResourceReference(Shape.StrokeProperty, "Brush.BorderStrong");
        _ghostEdges.SetResourceReference(Shape.StrokeProperty, "Brush.TextDisabled");
        _trail.SetResourceReference(Shape.StrokeProperty, "Brush.ProcessTree.Trail");
        _floor.SetResourceReference(Shape.FillProperty, "Brush.Border");

        // one 2·cosθ × 2·sinθ tile (per layout cell) with its two diagonals: tiled, they draw lines along both projected layout axes
        double w = 2 * IsoX, h = 2 * IsoY;
        var lines = new GeometryGroup();
        lines.Children.Add(new LineGeometry(new Point(0, 0), new Point(w, h)));
        lines.Children.Add(new LineGeometry(new Point(w, 0), new Point(0, h)));
        _floorMask = new DrawingBrush(new GeometryDrawing(null, _floorPen, lines))
        {
            TileMode = TileMode.Tile,
            Stretch = Stretch.Fill,
            Viewbox = new Rect(0, 0, w, h),
            ViewboxUnits = BrushMappingMode.Absolute,
            ViewportUnits = BrushMappingMode.Absolute,
        };
        _floor.OpacityMask = _floorMask;

        var g = new TransformGroup();
        g.Children.Add(_scale);
        g.Children.Add(_translate);
        _canvas.RenderTransform = g;
        _bootEdges.SetResourceReference(Shape.StrokeProperty, "Brush.BorderStrong");
        _bootGhostEdges.SetResourceReference(Shape.StrokeProperty, "Brush.TextDisabled");
        _canvas.Children.Add(_regionLayer);
        _canvas.Children.Add(_edges);
        _canvas.Children.Add(_ghostEdges);
        _canvas.Children.Add(_bootEdges);
        _canvas.Children.Add(_bootGhostEdges);
        _canvas.Children.Add(_trail);
        _canvas.Children.Add(_nodeLayer);
        _canvas.Children.Add(_bootLayer);
        _host.Children.Add(_floor);
        _host.Children.Add(_canvas);
        Child = _host;
        UpdateFloor();
        SizeChanged += (_, _) => { if (_fitPending && ActualWidth > 0) Fit(); UpdateSemanticZoom(); };
    }

    private void OnZoomChanged(double z)
    {
        if (_settingZoom) return;
        double old = _scale.ScaleX;
        if (Math.Abs(old - z) < 1e-9) return;
        double px = ActualWidth / 2, py = ActualHeight / 2;
        double cx = (px - _translate.X) / old, cy = (py - _translate.Y) / old;
        SetView(z, px - cx * z, py - cy * z);
    }

    /// <summary>Show grid off: the floor rectangle (and its tiled brush) leaves the visual tree's rendering.</summary>
    private void ApplyShowGrid(bool show) => _floor.Visibility = show ? Visibility.Visible : Visibility.Collapsed;

    /// <summary>Floor-projection map of a layout point (the item's diamond centre) to canvas coordinates.</summary>
    public static Point Project(double x, double y) => new((x - y) * IsoX, (x + y) * IsoY);

    /// <summary>Zoom-in / zoom-out buttons next to the slider.</summary>
    public void ZoomBy(double factor) => Zoom = Zoom * factor;

    private void OnSourceChanged(IProcessGraphSource? old, IProcessGraphSource? now)
    {
        if (old != null)
        {
            old.LayoutInvalidated -= OnLayoutInvalidated;
            old.CenterRequested -= CenterOn;
            old.FitRequested -= Fit;
            old.TrailChanged -= RedrawTrail;
        }
        if (now != null)
        {
            now.LayoutInvalidated += OnLayoutInvalidated;
            now.CenterRequested += CenterOn;
            now.FitRequested += Fit;
            now.TrailChanged += RedrawTrail;
        }
        _fitPending = true;
        _slots.Clear();
        _baseRows.Clear();
        _generation = null;
        Rebuild(clear: true);
    }

    private void OnLayoutInvalidated()
    {
        bool wasEmpty = _presenters.Count == 0;
        Rebuild(clear: false);
        // an expand / collapse never moves the view (no fit, no pan): only a new tree or an explicit Fit does
        if (wasEmpty || _fitPending) Fit();
    }

    /// <summary>Lays out the expanded part of the graph and syncs the node presenters (reused across layouts).</summary>
    private void Rebuild(bool clear)
    {
        if (clear)
        {
            foreach (var p in _presenters.Values) (p.Parent as Panel)?.Children.Remove(p);
            _presenters.Clear();
        }
        var src = Source;
        if (src == null || NodeTemplate == null)
        {
            _edges.Data = null;
            _ghostEdges.Data = null;
            _bootEdges.Data = null;
            _bootGhostEdges.Data = null;
            _trail.Data = null;
            ClearRegions();
            _boot.Clear();
            _bootAnchors.Clear();
            _bootSpacing = double.PositiveInfinity;
            UpdateSemanticZoom();
            _layoutEdges = Array.Empty<(object, object)>();
            _content = Rect.Empty;
            return;
        }
        // full: a new tree / template, or an explicit re-layout (Fit, Show whole tree, a new path, Show exited); else drawn items stay put unless pushed down
        bool full = clear || _generation != src.LayoutGeneration || _slots.Count == 0;
        _generation = src.LayoutGeneration;
        // the boot tree first, then every orphaned tree (root not boot chain) together in one block, a row step apart from it (one Orphaned region)
        static bool IsBootRoot(object r) => ProcessRegions.IsBootName(BootName(r));
        var roots = ProcessRegions.BootFirst(src.LayoutRoots(), IsBootRoot);
        object? firstOrphan = roots.Count > 0 && IsBootRoot(roots[0]) ? roots.FirstOrDefault(r => !IsBootRoot(r)) : null;
        bool GroupStart(object r) => ReferenceEquals(r, firstOrphan);
        const double orphanGap = LayoutNodeHeight + VerticalGap;
        var layout = full
            ? ProcessTreeLayout.Compute(roots, src.LayoutChildren, LayoutNodeWidth, LayoutNodeHeight, HorizontalGap, VerticalGap, RootGap, GroupStart, orphanGap)
            : ProcessTreeLayout.Update(roots, src.LayoutChildren, _slots, LayoutNodeWidth + HorizontalGap, LayoutNodeHeight + VerticalGap, RootGap, _baseRows, GroupStart, orphanGap);
        if (full) _baseRows.Clear();
        foreach (var (item, pos) in layout.Positions) if (full || !_baseRows.ContainsKey(item)) _baseRows[item] = pos.Y;
        _slots.Clear();
        foreach (var (item, pos) in layout.Positions) _slots[item] = pos;

        foreach (var stale in _presenters.Keys.Where(k => !layout.Positions.ContainsKey(k)).ToList())
        {
            (_presenters[stale].Parent as Panel)?.Children.Remove(_presenters[stale]);
            _presenters.Remove(stale);
        }
        BuildRegions(layout.Positions, layout.Edges);
        // canvas coordinates are the projected points themselves (the first root's anchor is 0,0), so nodes above an expansion do not move
        var content = Rect.Empty;
        foreach (var (node, pos) in layout.Positions)
        {
            if (!_presenters.TryGetValue(node, out var cp))
            {
                var template = node switch { ProcessStackViewModel => StackTemplate, _ => NodeTemplate };
                cp = new ContentPresenter { Content = node, ContentTemplate = template, Width = NodeWidth, Height = NodeHeight };
                _presenters[node] = cp;
            }
            // boot chain: its own layer (above every other node) and the shared inverse scale; everything else keeps its true size
            bool boot = _boot.Contains(node);
            var layer = boot ? _bootLayer : _nodeLayer;
            if (!ReferenceEquals(cp.Parent, layer))
            {
                (cp.Parent as Panel)?.Children.Remove(cp);
                layer.Children.Add(cp);
            }
            cp.RenderTransform = boot ? _bootScale : Transform.Identity;
            // the presenter stays upright and untransformed: its diamond centre lands on the projected anchor
            var at = Project(pos.X, pos.Y);
            var box = new Rect(at.X - DiamondCenterX, at.Y - DiamondCenterY, NodeWidth, NodeHeight);
            Canvas.SetLeft(cp, box.X);
            Canvas.SetTop(cp, box.Y);
            content.Union(box);
        }
        _content = content;
        _bootAnchors.Clear();
        foreach (var b in _boot) if (layout.Positions.TryGetValue(b, out var bp)) _bootAnchors.Add(Project(bp.X, bp.Y));
        _bootSpacing = BootSpacing(_bootAnchors);

        static bool IsGhostItem(object p) => p is ProcessNodeViewModel { IsGhost: true };
        var (solid, ghost) = BuildConnectors(layout.Positions, layout.Edges.Where(e => !_boot.Contains(e.Parent)), IsGhostItem);
        _edges.Data = solid;
        _ghostEdges.Data = ghost;
        var (bootSolid, bootGhost) = BuildConnectors(layout.Positions, layout.Edges.Where(e => _boot.Contains(e.Parent)), IsGhostItem);
        _bootEdges.Data = bootSolid;
        _bootGhostEdges.Data = bootGhost;
        _layoutEdges = layout.Edges.ToList();
        RedrawTrail();
        UpdateSemanticZoom();
    }

    // ------------------------------------------------------------ semantic zoom (boot chain + regions, Google-Maps style)

    /// <summary>Font size of a node name pill; the zoom at which it renders under ~8 px is where the names stop being readable.</summary>
    private const double NameFontPx = 16.5;
    /// <summary>On-screen name size (px) at which "far" (<see cref="UpdateSemanticZoom"/>) is 1 / 0; between them the boot chain grows to its far scale.</summary>
    private const double FarNamePx = 6, NearNamePx = 10;
    /// <summary>On-screen scale of the boot-chain presenters when fully zoomed out (they are inverse-scaled around their diamond to stay readable).</summary>
    private const double BootScreenScale = 0.7;
    /// <summary>Region fill opacity: the same plain tint of the role colour at every zoom.</summary>
    private const double RegionFillFar = 0.05;
    private const double MinEdgePx = 1, MinTrailPx = 2;
    /// <summary>Smallest on-screen name size the boot chain shrinks to when its pills would overlap.</summary>
    private const double MinBootNamePx = 9;
    /// <summary>A presenter's visible box (diamond, badge, pill, children row) relative to its anchor (presenter units).</summary>
    public const double PillTop = -21, PillWidth = 197, PillHeight = 62;
    /// <summary>Canvas anchors of the boot chain, and the largest presenter scale at which none of their pill boxes overlap (<see cref="BootSpacing"/>).</summary>
    private readonly List<Point> _bootAnchors = new();
    private double _bootSpacing = double.PositiveInfinity;

    /// <summary>
    /// The largest scale s at which no two pill boxes (<see cref="PillWidth"/> × <see cref="PillHeight"/>, scaled by s around their anchors) overlap: boxes
    /// overlap iff |dx| &lt; W·s and |dy| &lt; H·s, so s = min over pairs of max(|dx| / W, |dy| / H). A sweep over the anchors sorted by y.
    /// </summary>
    public static double BootSpacing(IEnumerable<Point> anchors)
    {
        var pts = anchors.OrderBy(p => p.Y).ToList();
        double best = double.PositiveInfinity;
        for (int i = 0; i < pts.Count; i++)
            for (int j = i + 1; j < pts.Count && (pts[j].Y - pts[i].Y) / PillHeight < best; j++)
                best = Math.Min(best, Math.Max(Math.Abs(pts[j].X - pts[i].X) / PillWidth, (pts[j].Y - pts[i].Y) / PillHeight));
        return best;
    }

    /// <summary>A nameless ghost (an uncaptured parent nothing names) above a boot-chain name is treated as the top of the boot chain; any other is an orphan root.</summary>
    private static string? BootName(object item) => item switch
    {
        ProcessNodeViewModel { IsGhost: true } g when string.IsNullOrEmpty(g.Node.Name) =>
            g.AllChildren.Any(c => ProcessRegions.IsBootName(c.Node.Name)) ? "System" : null,
        ProcessNodeViewModel n => n.Node.Name,
        ProcessStackViewModel s => s.Name,
        _ => null,
    };

    private void ClearRegions()
    {
        foreach (var r in _regions)
        {
            _regionLayer.Children.Remove(r.Fill);
        }
        _regions.Clear();
    }

    /// <summary>
    /// With every layout (never on zoom / pan): the boot chain of the drawn tree and one region per boot-chain parent around the subtrees of its off-chain
    /// children (<see cref="ProcessRegions"/>): a rounded rectangle (padding one node height) in the dominant role's colour.
    /// </summary>
    private void BuildRegions(IReadOnlyDictionary<object, TreeLayoutPoint> positions, IEnumerable<(object Parent, object Child)> edges)
    {
        ClearRegions();
        _boot.Clear();
        var kids = new Dictionary<object, List<object>>(ReferenceEqualityComparer.Instance);
        var hasParent = new HashSet<object>(ReferenceEqualityComparer.Instance);
        foreach (var (parent, child) in edges)
        {
            if (!kids.TryGetValue(parent, out var l)) kids[parent] = l = new List<object>();
            l.Add(child);
            hasParent.Add(child);
        }
        IReadOnlyList<object> Kids(object i) => kids.TryGetValue(i, out var l) ? l : Array.Empty<object>();
        var roots = positions.Keys.Where(i => !hasParent.Contains(i)).ToList();
        foreach (var b in ProcessRegions.BootChain(roots, Kids, BootName)) _boot.Add(b);
        static int Weight(object i) => i switch { ProcessNodeViewModel { IsGhost: true } => 0, ProcessStackViewModel s => Math.Max(1, s.Count), _ => 1 };
        foreach (var region in ProcessRegions.Build(roots, Kids, _boot, Weight).OrderByDescending(r => r.Processes))
        {
            var parent = region.Parent as ProcessNodeViewModel;
            if (parent == null && !region.Orphaned) continue;
            var roles = new Dictionary<(string Text, string Key), int>();
            void Count(string text, string key, int n) { if (key.Length > 0) roles[(text, key)] = roles.GetValueOrDefault((text, key)) + n; }
            foreach (var item in region.Items)
            {
                if (item is ProcessNodeViewModel n) Count(n.RoleText, n.RoleKey, 1);
                else if (item is ProcessStackViewModel s) foreach (var m in s.Members) Count(m.RoleText, m.RoleKey, 1);
            }
            var shape = RegionShape(region.Groups.Select(g => g.Where(positions.ContainsKey).Select(i => positions[i])), LayoutNodeHeight + VerticalGap, out var apex);
            var bounds = shape.Bounds;
            if (bounds.IsEmpty) continue;
            var roleList = roles.Select(kv => new RegionRole(kv.Key.Text, kv.Key.Key, kv.Value)).ToList();
            var dominant = ProcessRegions.Dominant(roleList);
            string key = dominant.Count > 0 ? dominant[0].Key : "Other";

            // the ground plane under the nodes: same projection as the grid and the anchors, so it lies flat like a plot of land on the floor
            var fill = new System.Windows.Shapes.Path { Data = shape, Cursor = Cursors.Hand };
            fill.SetResourceReference(Shape.FillProperty, "Brush.Role." + key);
            string pid = region.Orphaned ? "Orphaned" : parent!.Pid.ToString(System.Globalization.CultureInfo.InvariantCulture);
            AutomationProperties.SetAutomationId(fill, "Results.ProcessTree.Region." + pid);
            var visual = new RegionVisual(region.Parent, shape, bounds, apex, fill);
            fill.Tag = visual;
            _regionLayer.Children.Add(fill);
            _regions.Add(visual);
        }
    }

    private static double Lerp(double a, double b, double t) => a + (b - a) * t;

    /// <summary>
    /// Zoom / pan (screen maths only: no layout, the view never moves). "far" goes 0 → 1 as the names drop below ~8 px. Nothing fades: every node and connector
    /// stays solid and visible at every zoom (off-chain nodes just shrink with the canvas, unreadable far out); off-chain nodes stop being hit-testable from halfway
    /// (region clicks win), the boot chain is inverse-scaled to stay readable (never overlapping), region fills keep one plain tint at every zoom and become clickable.
    /// </summary>
    private void UpdateSemanticZoom()
    {
        double z = _scale.ScaleX;
        double far = 1 - Math.Clamp((NameFontPx * z - FarNamePx) / (NearNamePx - FarNamePx), 0, 1);
        // connectors stay visible at every zoom: a minimum on-screen stroke (the canvas is scaled by z)
        _edges.StrokeThickness = _ghostEdges.StrokeThickness = _bootEdges.StrokeThickness = _bootGhostEdges.StrokeThickness = Math.Max(1, MinEdgePx / z);
        _trail.StrokeThickness = Math.Max(TrailThickness, MinTrailPx / z);
        _nodeLayer.IsHitTestVisible = far <= 0.5;
        // boot pills never overlap each other: the inverse scale is capped by the tightest pair (down to a still readable name), at least 1 (zoom 1 clears)
        double bootTarget = Lerp(1, BootScreenScale / z, far);
        _bootScale.ScaleX = _bootScale.ScaleY = Math.Max(1, Math.Min(bootTarget, Math.Max(_bootSpacing, MinBootNamePx / (NameFontPx * z))));

        bool clickable = far >= 0.3;
        foreach (var r in _regions)
        {
            r.Fill.Opacity = RegionFillFar;
            r.Fill.Visibility = Visibility.Visible;
            r.Fill.IsHitTestVisible = clickable;
        }
    }

    /// <summary>A click on a region (far / middle zoom): zooms so its bounds fill the viewport (at most 100 %).</summary>
    private void FitRegion(Rect r)
    {
        if (ActualWidth <= 0 || ActualHeight <= 0 || r.IsEmpty) return;
        double h = Math.Max(1, ActualHeight - FitBottomInset);
        double z = Math.Clamp(Math.Min((ActualWidth - 2 * Margin0) / r.Width, (h - 2 * Margin0) / r.Height), MinZoom, 1);
        SetView(z, ActualWidth / 2 - (r.X + r.Width / 2) * z, h / 2 - (r.Y + r.Height / 2) * z);
    }

    /// <summary>Stroke width of the trail connectors (the normal ones are 1).</summary>
    public const double TrailThickness = 1.6;

    /// <summary>Redraws the trail over the current layout (connectors only: nothing is laid out, the view does not move).</summary>
    private void RedrawTrail()
    {
        var nodes = Source?.TrailNodes;
        if (nodes == null || nodes.Count < 2) { _trail.Data = null; return; }
        var inTrail = new HashSet<object>(nodes, ReferenceEqualityComparer.Instance);
        // a trail edge is a drawn parent -> child edge whose two ends are consecutive trail nodes (real parent link)
        var edges = _layoutEdges.Where(e => e.Parent is ProcessNodeViewModel p && e.Child is ProcessNodeViewModel c
            && inTrail.Contains(p) && inTrail.Contains(c) && ReferenceEquals(c.Parent, p)).ToList();
        _trail.Data = edges.Count == 0 ? null : BuildTrail(_slots, edges);
    }

    /// <summary>Length of the shared trunk out of a parent, as a share of a column step (layout units), before it fans out to the children.</summary>
    public const double TrunkShare = 0.5;

    /// <summary>Corner radius (layout units) of the rounded junctions of a connector brace; clamped to half the available distance.</summary>
    public const double ConnectorRadius = 15;

    /// <summary>
    /// Curly-brace connectors, one per parent: a short straight trunk out of the parent's diamond along the child axis, then one smooth cubic Bézier per child
    /// that leaves the trunk end and arrives at the child's diamond, both tangents along the child axis (a brace / bracket fan). Built in layout space and
    /// projected point by point (the floor projection is affine, so the projected control points draw the projected curve). Solid: the parent PID matched a
    /// captured process; dashed (second geometry): the parent is a ghost (not captured). A child left of (or in) its parent's column gets a straight line.
    /// </summary>
    public static (StreamGeometry Solid, StreamGeometry Ghost) BuildConnectors<T>(IReadOnlyDictionary<T, TreeLayoutPoint> positions,
        IEnumerable<(T Parent, T Child)> edges, Func<T, bool> isGhost) where T : notnull
    {
        var geo = new StreamGeometry();
        var ghostGeo = new StreamGeometry();
        var byParent = new Dictionary<T, List<T>>();
        var parents = new List<T>();
        foreach (var (parent, child) in edges)
        {
            if (!byParent.TryGetValue(parent, out var kids)) { byParent[parent] = kids = new List<T>(); parents.Add(parent); }
            kids.Add(child);
        }
        using (var ctx = geo.Open())
        using (var gctx = ghostGeo.Open())
        {
            foreach (var parent in parents)
            {
                var a = positions[parent];
                var c = isGhost(parent) ? gctx : ctx;
                double sx = a.X + (LayoutNodeWidth + HorizontalGap) * TrunkShare;
                var ups = new List<TreeLayoutPoint>();
                var downs = new List<TreeLayoutPoint>();
                foreach (var child in byParent[parent])
                {
                    var b = positions[child];
                    if (b.X <= sx + 1 || Math.Abs(b.Y - a.Y) < 1)
                    {
                        c.BeginFigure(Project(a.X, a.Y), false, false);
                        c.LineTo(Project(b.X, b.Y), true, false);
                    }
                    else if (b.Y < a.Y) ups.Add(b);
                    else downs.Add(b);
                }
                if (ups.Count == 0 && downs.Count == 0) continue;
                double R(TreeLayoutPoint b) => Math.Min(ConnectorRadius, Math.Min((b.X - sx) / 2, Math.Abs(b.Y - a.Y) / 2));
                const double K = 0.5523;
                double topEnd = ups.Count > 0 ? ups.Min(b => b.Y + R(b)) : a.Y;
                double botEnd = downs.Count > 0 ? downs.Max(b => b.Y - R(b)) : a.Y;
                if (ups.Count > 0 && downs.Count > 0)
                {
                    c.BeginFigure(Project(a.X, a.Y), false, false);
                    c.LineTo(Project(sx, a.Y), true, false);
                    c.BeginFigure(Project(sx, topEnd), false, false);
                    c.LineTo(Project(sx, botEnd), true, false);
                }
                else
                {
                    int s = downs.Count > 0 ? 1 : -1;
                    double r0 = Math.Min(ConnectorRadius, Math.Min((sx - a.X) / 2, Math.Abs((s > 0 ? botEnd : topEnd) - a.Y)));
                    c.BeginFigure(Project(a.X, a.Y), false, false);
                    c.LineTo(Project(sx - r0, a.Y), true, false);
                    c.BezierTo(Project(sx - r0 + K * r0, a.Y), Project(sx, a.Y + s * (r0 - K * r0)), Project(sx, a.Y + s * r0), true, false);
                    c.LineTo(Project(sx, s > 0 ? botEnd : topEnd), true, false);
                }
                foreach (var (list, sg) in new[] { (ups, -1), (downs, 1) })
                    foreach (var b in list)
                    {
                        double r = R(b);
                        c.BeginFigure(Project(sx, b.Y - sg * r), false, false);
                        c.BezierTo(Project(sx, b.Y - sg * (r - K * r)), Project(sx + r - K * r, b.Y), Project(sx + r, b.Y), true, false);
                        c.LineTo(Project(b.X, b.Y), true, false);
                    }
            }
        }        geo.Freeze();
        ghostGeo.Freeze();
        return (geo, ghostGeo);
    }

    /// <summary>
    /// The trail: for each given parent -> child edge only that child's own path (the parent's stub, the spine up to the child's leg, the leg), same "{" shape
    /// as <see cref="BuildConnectors"/> (straight segments, rounded junctions only), so siblings off the trail are not touched. Empty when there are no edges.
    /// </summary>
    public static StreamGeometry BuildTrail<T>(IReadOnlyDictionary<T, TreeLayoutPoint> positions, IEnumerable<(T Parent, T Child)> edges) where T : notnull
    {
        var geo = new StreamGeometry();
        const double K = 0.5523;
        using (var c = geo.Open())
            foreach (var (parent, child) in edges)
            {
                if (!positions.TryGetValue(parent, out var a) || !positions.TryGetValue(child, out var b)) continue;
                double sx = a.X + (LayoutNodeWidth + HorizontalGap) * TrunkShare;
                if (b.X <= sx + 1 || Math.Abs(b.Y - a.Y) < 1)
                {
                    c.BeginFigure(Project(a.X, a.Y), false, false);
                    c.LineTo(Project(b.X, b.Y), true, false);
                    continue;
                }
                int s = b.Y > a.Y ? 1 : -1;
                double dy = Math.Abs(b.Y - a.Y);
                double r0 = Math.Min(ConnectorRadius, Math.Min((sx - a.X) / 2, dy / 2));
                double r = Math.Min(ConnectorRadius, Math.Min((b.X - sx) / 2, dy / 2));
                c.BeginFigure(Project(a.X, a.Y), false, false);
                c.LineTo(Project(sx - r0, a.Y), true, false);
                c.BezierTo(Project(sx - r0 + K * r0, a.Y), Project(sx, a.Y + s * (r0 - K * r0)), Project(sx, a.Y + s * r0), true, false);
                c.LineTo(Project(sx, b.Y - s * r), true, false);
                c.BezierTo(Project(sx, b.Y - s * (r - K * r)), Project(sx + r - K * r, b.Y), Project(sx + r, b.Y), true, false);
                c.LineTo(Project(b.X, b.Y), true, false);
            }
        geo.Freeze();
        return geo;
    }

    /// <summary>
    /// Keeps the floor attached to the graph: the tile is one projected layout cell (doubled until it is at least <see cref="MinGridCellPx"/> high on
    /// screen), anchored at the canvas origin through the current scale / translation; the pen is rescaled so lines stay <see cref="GridLinePx"/> wide.
    /// </summary>
    private void UpdateFloor()
    {
        double z = _scale.ScaleX;
        double cell = GridCell;
        while (cell * 2 * IsoY * z < MinGridCellPx && cell < 1e6) cell *= 2;
        double tw = cell * 2 * IsoX * z, th = cell * 2 * IsoY * z;
        // tile corners (and centres) are where the diagonals cross: put a corner on the canvas origin, where layout (0,0) projects
        _floorMask.Viewport = new Rect(_translate.X, _translate.Y, tw, th);
        // the drawing is stretched from a (2·cosθ × 2·sinθ) viewbox to tw × th: undo that so the line stays GridLinePx on screen
        _floorPen.Thickness = GridLinePx * 2 * IsoY / th;
    }

    // ------------------------------------------------------------ zoom / pan

    /// <summary>Height kept free at the bottom by <see cref="Fit"/> (the pane's floating bottom bar sits there).</summary>
    public double FitBottomInset { get; set; }

    /// <summary>Zooms (at most 100 %) so the whole expanded graph fits, centred with a margin above <see cref="FitBottomInset"/> (top-left aligned when it does not fit).</summary>
    public void Fit()
    {
        if (ActualWidth <= 0 || ActualHeight <= 0) { _fitPending = true; return; }
        _fitPending = false;
        if (_content.IsEmpty || _content.Width <= 0 || _content.Height <= 0) { SetView(1, Margin0, Margin0); return; }
        double h = Math.Max(1, ActualHeight - FitBottomInset);
        double z = Math.Min((ActualWidth - 2 * Margin0) / _content.Width, (h - 2 * Margin0) / _content.Height);
        z = Math.Clamp(z, MinZoom, 1);
        double x = Math.Max(Margin0, (ActualWidth - _content.Width * z) / 2) - _content.X * z;
        double y = Math.Max(Margin0, (h - _content.Height * z) / 2) - _content.Y * z;
        SetView(z, x, y);
    }

    /// <summary>Puts the node in the middle of the viewport (at no less than 60 % zoom so it is readable).</summary>
    public void CenterOn(ProcessNodeViewModel node)
    {
        // the node may have just been revealed: lay out first
        if (!_presenters.ContainsKey(node)) Rebuild(clear: false);
        if (!_presenters.TryGetValue(node, out var cp)) return;
        double z = Math.Max(_scale.ScaleX, 0.6);
        double cx = Canvas.GetLeft(cp) + NodeWidth / 2, cy = Canvas.GetTop(cp) + DiamondCenterY;
        SetView(z, ActualWidth / 2 - cx * z, ActualHeight / 2 - cy * z);
    }

    private void SetView(double zoom, double x, double y)
    {
        _scale.ScaleX = _scale.ScaleY = zoom;
        _settingZoom = true;
        try { Zoom = zoom; }
        finally { _settingZoom = false; }
        _translate.X = x;
        _translate.Y = y;
        UpdateFloor();
        UpdateSemanticZoom();
    }

    protected override void OnMouseWheel(MouseWheelEventArgs e)
    {
        base.OnMouseWheel(e);
        var p = e.GetPosition(this);
        double old = _scale.ScaleX;
        double z = Math.Clamp(old * (e.Delta > 0 ? 1.15 : 1 / 1.15), MinZoom, MaxZoom);
        // keep the content point under the cursor fixed
        double cx = (p.X - _translate.X) / old, cy = (p.Y - _translate.Y) / old;
        SetView(z, p.X - cx * z, p.Y - cy * z);
        e.Handled = true;
    }

    protected override void OnMouseLeftButtonDown(MouseButtonEventArgs e)
    {
        base.OnMouseLeftButtonDown(e);
        if (e.Handled) return; // the chevron button handled it
        Focus();
        _downPos = e.GetPosition(this);
        _downTranslate = new Point(_translate.X, _translate.Y);
        _downNode = NodeFrom(e.OriginalSource as DependencyObject);
        _downRegion = _downNode == null ? RegionFrom(e.OriginalSource as DependencyObject) : null;
        _dragging = true;
        _panned = false;
        CaptureMouse();
        e.Handled = true;
    }

    protected override void OnMouseMove(MouseEventArgs e)
    {
        base.OnMouseMove(e);
        if (!_dragging) return;
        var p = e.GetPosition(this);
        var d = p - _downPos;
        if (!_panned && Math.Abs(d.X) + Math.Abs(d.Y) < 4) return;
        _panned = true;
        Cursor = Cursors.SizeAll;
        _translate.X = _downTranslate.X + d.X;
        _translate.Y = _downTranslate.Y + d.Y;
        UpdateFloor();
        UpdateSemanticZoom();
    }

    protected override void OnMouseLeftButtonUp(MouseButtonEventArgs e)
    {
        base.OnMouseLeftButtonUp(e);
        if (!_dragging) return;
        _dragging = false;
        ReleaseMouseCapture();
        Cursor = null;
        if (!_panned && _downNode != null) Source?.SelectNode(_downNode);
        else if (!_panned && _downRegion != null) FitRegion(_downRegion.Bounds);
        _downNode = null;
        _downRegion = null;
        e.Handled = true;
    }

    protected override void OnLostMouseCapture(MouseEventArgs e)
    {
        base.OnLostMouseCapture(e);
        _dragging = false;
        Cursor = null;
    }

    private ProcessNodeViewModel? NodeFrom(DependencyObject? d)
    {
        while (d != null && d != this)
        {
            if (d is ContentPresenter { Content: ProcessNodeViewModel n } cp && _presenters.TryGetValue(n, out var mine) && mine == cp) return n;
            if (d is ButtonBase) return null;
            d = d is Visual or System.Windows.Media.Media3D.Visual3D ? VisualTreeHelper.GetParent(d) : LogicalTreeHelper.GetParent(d);
        }
        return null;
    }

    /// <summary>The region whose fill was clicked (only hit-testable at far / middle zoom).</summary>
    private RegionVisual? RegionFrom(DependencyObject? d)
    {
        while (d != null && d != this)
        {
            if (d is FrameworkElement { Tag: RegionVisual r } && _regions.Contains(r)) return r;
            if (d is ButtonBase) return null;
            d = d is Visual or System.Windows.Media.Media3D.Visual3D ? VisualTreeHelper.GetParent(d) : LogicalTreeHelper.GetParent(d);
        }
        return null;
    }

    protected override AutomationPeer OnCreateAutomationPeer() => new Peer(this);

    private sealed class Peer : FrameworkElementAutomationPeer
    {
        public Peer(ProcessGraphCanvas owner) : base(owner) { }
        protected override AutomationControlType GetAutomationControlTypeCore() => AutomationControlType.Pane;
        protected override string GetClassNameCore() => nameof(ProcessGraphCanvas);
        protected override bool IsControlElementCore() => true;
        protected override bool IsContentElementCore() => false;
    }
}
