using System.Diagnostics;
using System.Windows;
using System.Windows.Automation;
using System.Windows.Media;
using EzForensics.App.ViewModels;
using EzForensics.App.Views.Analyze;
using EzForensics.Core.Processes;
using FluentAssertions;

namespace EzForensics.App.Tests;

/// <summary>The process tree pane parses, lays out and binds without errors (floating chips, View popover, left rail, toast), in Tree and Focus mode.</summary>
public sealed class ProcessTreePaneSmokeTests
{
    private sealed class Collector : TraceListener
    {
        public readonly List<string> Lines = new();
        public override void Write(string? message) { }
        public override void WriteLine(string? message) { if (message != null) lock (Lines) Lines.Add(message); }
    }

    private static async Task<ProcessTreeViewModel> LoadAsync()
    {
        var g = new ProcessGraph();
        ProcessNode N(int pid, string name, ProcessNode? parent)
        {
            var n = new ProcessNode { Pid = pid, Ppid = parent?.Pid ?? 0, Name = name, CreateTime = new DateTime(2026, 1, 1, 0, 0, pid, DateTimeKind.Utc), Parent = parent };
            parent?.Children.Add(n);
            g.AllNodes.Add(n);
            if (parent == null) g.Roots.Add(n);
            return n;
        }
        var a = N(1, "a.exe", null); N(2, "b.exe", null);
        var a1 = N(10, "a1.exe", a); N(11, "a2.exe", a);
        N(20, "a1a.exe", a1);
        var vm = new ProcessTreeViewModel("host", "HOST", (_, _) => { }, () => { }, (_, _) => Task.FromResult<string?>(null));
        await vm.ShowGraphAsync(g, CancellationToken.None);
        return vm;
    }

    private static IEnumerable<DependencyObject> Logical(DependencyObject root)
    {
        yield return root;
        foreach (var c in LogicalTreeHelper.GetChildren(root).OfType<DependencyObject>())
            foreach (var d in Logical(c)) yield return d;
    }

    private static IEnumerable<DependencyObject> VisualTree(DependencyObject root)
    {
        yield return root;
        if (root is not System.Windows.Media.Visual) yield break;
        for (int i = 0; i < VisualTreeHelper.GetChildrenCount(root); i++)
            foreach (var d in VisualTree(VisualTreeHelper.GetChild(root, i))) yield return d;
    }

    /// <summary>A left press on the row's text, as the input system raises it (MouseDown, re-raised as MouseLeftButtonDown on every element of the route).</summary>
    private static void Press(DependencyObject target)
    {
        var e = new System.Windows.Input.MouseButtonEventArgs(System.Windows.Input.Mouse.PrimaryDevice, 0, System.Windows.Input.MouseButton.Left)
        {
            RoutedEvent = System.Windows.Input.Mouse.MouseDownEvent,
        };
        typeof(System.Windows.Input.MouseButtonEventArgs).GetProperty("ClickCount")!.SetValue(e, 1);
        ((UIElement)target).RaiseEvent(e);
    }

    [Fact]
    public async Task Navigator_row_press_runs_its_action_once()
    {
        var g = new ProcessGraph();
        ProcessNode N(int pid, string name, ProcessNode? parent)
        {
            var n = new ProcessNode { Pid = pid, Ppid = parent?.Pid ?? 0, Name = name, CreateTime = new DateTime(2026, 1, 1, 0, 0, pid, DateTimeKind.Utc), Parent = parent };
            parent?.Children.Add(n);
            g.AllNodes.Add(n);
            if (parent == null) g.Roots.Add(n);
            return n;
        }
        var a = N(1, "a.exe", null);
        N(10, "cmd.exe", a); N(11, "cmd.exe", a);
        ProcessIndicators.Annotate(g);
        var vm = new ProcessTreeViewModel("host", "HOST", (_, _) => { }, () => { }, (_, _) => Task.FromResult<string?>(null));
        await vm.ShowGraphAsync(g, CancellationToken.None);
        vm.IsNavigatorOpen = true;
        var roles = vm.NavigatorSections.Single(s => s.Section == NavigatorSection.Roles);
        roles.IsExpanded = true;
        var group = roles.Groups.Single(x => x.Title == "Shells & scripts");

        Exception? failure = null;
        var t = new Thread(() =>
        {
            try
            {
                if (Application.Current == null) new App().InitializeComponent();
                var pane = new ProcessTreePane { DataContext = vm };
                void Layout() { pane.Measure(new Size(1000, 700)); pane.Arrange(new Rect(0, 0, 1000, 700)); pane.UpdateLayout(); }
                // the row's label (not the chevron glyph inside the expander ToggleButton)
                System.Windows.Controls.TextBlock Text(NavigatorTreeItem row)
                {
                    Layout();
                    var label = row switch { NavigatorSectionViewModel s => s.HeaderText, NavigatorGroupViewModel gr => gr.Title, NavigatorMemberViewModel m => m.Name, _ => "" };
                    var item = VisualTree(pane).OfType<System.Windows.Controls.TreeViewItem>().First(i => ReferenceEquals(i.DataContext, row));
                    return VisualTree(item).OfType<System.Windows.Controls.TextBlock>().First(tb => tb.DataContext == row && tb.Text == label);
                }

                // role colour: the diamond of a cmd.exe node and the Roles group swatch use Brush.Role.Shells
                Layout();
                var shells = pane.FindResource("Brush.Role.Shells");
                var canvas = (EzForensics.App.Controls.ProcessGraphCanvas)pane.FindName("Canvas");
                var cmd = VisualTree(canvas).OfType<System.Windows.Controls.ContentPresenter>().First(p => p.Content is ProcessNodeViewModel { Pid: 10 });
                VisualTree(cmd).OfType<System.Windows.Shapes.Path>().Should().Contain(p => ReferenceEquals(p.Fill, shells));
                var groupItem = VisualTree(pane).OfType<System.Windows.Controls.TreeViewItem>().First(i => ReferenceEquals(i.DataContext, group));
                VisualTree(groupItem).OfType<System.Windows.Shapes.Path>()
                    .Should().Contain(p => AutomationProperties.GetAutomationId(p) == "Results.ProcessTree.Navigator.RoleSwatch.Shells" && ReferenceEquals(p.Fill, shells));

                // group row (nested in the Roles section row): selected once, not selected-then-cleared
                int changes = 0;
                vm.PropertyChanged += (_, e) => { if (e.PropertyName == nameof(vm.SelectedGroup)) changes++; };
                Press(Text(group));
                changes.Should().Be(1);
                vm.SelectedGroup.Should().BeSameAs(group);
                roles.IsExpanded.Should().BeTrue("the section row under the group must not run its fold");
                // member row (nested two deep)
                var member = group.Members.Single(m => m.Node.Pid == 11);
                Press(Text(member));
                vm.Selected!.Pid.Should().Be(11);
                vm.SelectedGroup.Should().BeSameAs(group);
                // section row: folds once
                Press(Text(roles));
                roles.IsExpanded.Should().BeFalse();
                // group again (unfold first): clears
                roles.IsExpanded = true;
                Press(Text(group));
                vm.SelectedGroup.Should().BeNull();
            }
            catch (Exception ex) { failure = ex; }
        });
        t.SetApartmentState(ApartmentState.STA);
        t.Start();
        t.Join();
        failure.Should().BeNull(failure?.ToString());
    }

    private static int _tick = Environment.TickCount; // raw reports 1 s apart: consecutive clicks never count as a double-click

    /// <summary>
    /// A real left click at a window point, fed in as the window's mouse input provider does it: raw mouse reports (activate + move, left down, left up) go into
    /// InputManager.ProcessInput, so the whole pipeline runs (hit test, DirectlyOver, click count, tunnel / bubble, left-button promotion, capture) without moving
    /// the user's cursor. Returns the element under the point.
    /// </summary>
    private static IInputElement? ClickAt(Window w, Point p)
    {
        var source = PresentationSource.FromVisual(w)!;
        var dev = source.CompositionTarget!.TransformToDevice.Transform(p);
        var asm = typeof(System.Windows.Input.InputManager).Assembly;
        const System.Reflection.BindingFlags Any = System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.Static | System.Reflection.BindingFlags.Public | System.Reflection.BindingFlags.NonPublic;
        var reportType = asm.GetType("System.Windows.Input.RawMouseInputReport")!;
        var actionsType = asm.GetType("System.Windows.Input.RawMouseActions")!;
        object Actions(params string[] names) => Enum.ToObject(actionsType, names.Aggregate(0, (acc, n) => acc | (int)Enum.Parse(actionsType, n)));
        void Send(object actions)
        {
            var report = Activator.CreateInstance(reportType, Any, null,
                new object[] { System.Windows.Input.InputMode.Foreground, _tick += 1000, source, actions, (int)Math.Round(dev.X), (int)Math.Round(dev.Y), 0, IntPtr.Zero }, null)!;
            var args = (System.Windows.Input.InputEventArgs)Activator.CreateInstance(asm.GetType("System.Windows.Input.InputReportEventArgs")!, Any, null,
                new object[] { System.Windows.Input.Mouse.PrimaryDevice, report }, null)!;
            args.RoutedEvent = (RoutedEvent)typeof(System.Windows.Input.InputManager).GetField("PreviewInputReportEvent", Any)!.GetValue(null)!;
            System.Windows.Input.InputManager.Current.ProcessInput(args);
        }
        Send(Actions("Activate", "AbsoluteMove"));
        Send(Actions("AbsoluteMove", "Button1Press"));
        Send(Actions("AbsoluteMove", "Button1Release"));
        return w.InputHitTest(p);
    }

    [Fact]
    public async Task Navigator_rows_respond_to_a_real_click_in_a_shown_window()
    {
        var g = new ProcessGraph();
        ProcessNode N(int pid, string name, ProcessNode? parent)
        {
            var n = new ProcessNode { Pid = pid, Ppid = parent?.Pid ?? 0, Name = name, CreateTime = new DateTime(2026, 1, 1, 0, 0, pid, DateTimeKind.Utc), Parent = parent };
            parent?.Children.Add(n);
            g.AllNodes.Add(n);
            if (parent == null) g.Roots.Add(n);
            return n;
        }
        var a = N(1, "a.exe", null);
        N(10, "cmd.exe", a); N(11, "cmd.exe", a);
        ProcessIndicators.Annotate(g);
        var vm = new ProcessTreeViewModel("host", "HOST", (_, _) => { }, () => { }, (_, _) => Task.FromResult<string?>(null));
        await vm.ShowGraphAsync(g, CancellationToken.None);
        vm.IsNavigatorOpen = true;
        var roles = vm.NavigatorSections.Single(s => s.Section == NavigatorSection.Roles);
        var group = roles.Groups.Single(x => x.Title == "Shells & scripts");

        Exception? failure = null;
        var t = new Thread(() =>
        {
            Window? w = null;
            try
            {
                if (Application.Current == null) new App().InitializeComponent();
                var pane = new ProcessTreePane { DataContext = vm };
                w = new Window { Width = 1100, Height = 760, Content = pane, ShowActivated = false, WindowStyle = WindowStyle.None, Left = -3000, Top = -3000, ShowInTaskbar = false };
                w.Show();
                // a click on a row's label; no dispatcher pumping (a queued test-host Application shutdown would close the window)
                IInputElement? Click(NavigatorTreeItem row, bool chevron = false)
                {
                    w.UpdateLayout();
                    var label = row switch { NavigatorSectionViewModel s => s.HeaderText, NavigatorGroupViewModel gr => gr.Title, NavigatorMemberViewModel m => m.Name, _ => "" };
                    var item = VisualTree(w).OfType<System.Windows.Controls.TreeViewItem>().First(i => ReferenceEquals(i.DataContext, row));
                    FrameworkElement target = chevron
                        ? VisualTree(item).OfType<System.Windows.Controls.Primitives.ToggleButton>().First()
                        : VisualTree(item).OfType<System.Windows.Controls.TextBlock>().First(x => x.DataContext == row && x.Text == label);
                    var p = target.TranslatePoint(new Point(Math.Min(10, target.ActualWidth / 2), target.ActualHeight / 2), w);
                    var hit = ClickAt(w, p);
                    w.UpdateLayout();
                    return hit;
                }

                int changes = 0;
                vm.PropertyChanged += (_, e) => { if (e.PropertyName == nameof(vm.SelectedGroup)) changes++; };
                // section row: unfolds
                var hit = Click(roles);
                hit.Should().BeOfType<System.Windows.Controls.TextBlock>("nothing overlays the Navigator rows");
                roles.IsExpanded.Should().BeTrue("a click on the Roles section row unfolds it");
                // group row: highlights once
                Click(group);
                vm.SelectedGroup.Should().BeSameAs(group, "a click on the group row selects it");
                changes.Should().Be(1);
                roles.IsExpanded.Should().BeTrue();
                // member row: selects that process
                var member = group.Members.Single(m => m.Node.Pid == 11);
                Click(member);
                vm.Selected?.Pid.Should().Be(11, "a click on the member row selects it");
                vm.SelectedGroup.Should().BeSameAs(group);
                // the group's chevron folds without running the row action
                bool open = group.IsExpanded;
                Click(group, chevron: true);
                group.IsExpanded.Should().Be(!open);
                vm.SelectedGroup.Should().BeSameAs(group);
                // group again: clears
                Click(group);
                vm.SelectedGroup.Should().BeNull();

                // bottom bar: centred at the bottom, same place with the Navigator open or closed; the View popover opens above it; the toast sits above the bar
                FrameworkElement Find(string id) => VisualTree(w).OfType<FrameworkElement>().First(f => AutomationProperties.GetAutomationId(f) == id);
                Rect BarRect() { w.UpdateLayout(); var b = Find("Results.ProcessTree.ZoomBar"); return new Rect(b.TranslatePoint(new Point(0, 0), pane), b.RenderSize); }
                var open1 = BarRect();
                vm.IsNavigatorOpen = false;
                var closed1 = BarRect();
                closed1.Should().Be(open1, "the bar does not move when the Navigator opens");
                (pane.ActualWidth / 2 - (open1.Left + open1.Width / 2)).Should().BeApproximately(0, 2, "the bar is centred");
                (pane.ActualHeight - open1.Bottom).Should().BeInRange(4, 20, "the bar sits at the bottom");
                open1.Width.Should().BeGreaterThan(open1.Height * 3, "the bar is horizontal");
                ((System.Windows.Controls.Primitives.Popup)pane.FindName("ViewPopup")).Placement.Should().Be(System.Windows.Controls.Primitives.PlacementMode.Top);
                var toast = Find("Results.ProcessTree.Toast");
                (pane.ActualHeight - toast.TranslatePoint(new Point(0, toast.ActualHeight), pane).Y).Should().BeGreaterThan(pane.ActualHeight - open1.Top, "the toast is above the bar");
            }
            catch (Exception ex) { failure = ex; }
            finally { w?.Close(); }
        });
        t.SetApartmentState(ApartmentState.STA);
        t.Start();
        t.Join();
        failure.Should().BeNull(failure?.ToString());
    }

    [Fact]
    public void Every_role_has_a_key_and_a_brush_in_both_themes()
    {
        var roles = ProcessRoles.Images.Values.Select(i => i.Role).Append(ProcessRoles.OtherRole).Distinct().ToList();
        roles.Should().Contain(new[] { "Admin tools & LOLBins", "Remote access" });
        var keys = roles.Select(ProcessNodeViewModel.RoleKeyOf).ToList();
        keys.Should().OnlyHaveUniqueItems("each role gets its own colour");
        ProcessNodeViewModel.RoleKeyOf("").Should().Be("Other");
        ProcessNodeViewModel.RoleKeyOf("something new").Should().Be("Other");

        Exception? failure = null;
        var t = new Thread(() =>
        {
            try
            {
                if (Application.Current == null) new App().InitializeComponent();
                foreach (var theme in new[] { "Light", "Dark" })
                {
                    var d = new ResourceDictionary { Source = new Uri($"pack://application:,,,/{typeof(App).Assembly.GetName().Name};component/Themes/{theme}.xaml") };
                    foreach (var k in keys) d.Contains("Brush.Role." + k).Should().BeTrue($"{theme}.xaml defines Brush.Role.{k}");
                }
            }
            catch (Exception ex) { failure = ex; }
        });
        t.SetApartmentState(ApartmentState.STA);
        t.Start();
        t.Join();
        failure.Should().BeNull(failure?.ToString());
    }

    [Fact]
    public void Node_tooltip_names_the_role()
    {
        var n = new ProcessNodeViewModel(new ProcessNode { Pid = 7, Name = "certutil.exe", Role = "Admin tools & LOLBins" }, null, _ => { });
        n.RoleKey.Should().Be("Admin");
        n.ToolTipText.Should().Contain("Role: Admin tools & LOLBins");
        new ProcessNodeViewModel(new ProcessNode { Pid = 8, Name = "x.exe" }, null, _ => { }).ToolTipText.Should().Contain("Role: Other");
    }

    [Fact]
    public void Node_tooltip_caps_a_long_command_line_and_says_exited()
    {
        string cmd = "powershell.exe -enc " + new string('A', 5000);
        var n = new ProcessNodeViewModel(new ProcessNode { Pid = 9, Name = "powershell.exe", CmdLine = cmd }, null, _ => { });
        int max = ProcessNodeViewModel.MaxToolTipCommandLine;
        n.ToolTipText.Should().Contain(cmd[..max] + "…").And.NotContain(cmd[..(max + 1)], "the tooltip must fit the screen");
        n.ToolTipText.Should().NotContain("Exited");
        new ProcessNodeViewModel(new ProcessNode { Pid = 10, Name = "a.exe", CmdLine = "a.exe /x" }, null, _ => { }).ToolTipText.Should().Contain("a.exe /x").And.NotContain("…");

        var exited = new ProcessNodeViewModel(new ProcessNode { Pid = 11, Name = "b.exe", Markers = ProcessMarkers.Exited,
            ExitTime = new DateTime(2026, 1, 2, 3, 4, 5, DateTimeKind.Utc) }, null, _ => { });
        exited.IsExited.Should().BeTrue();
        exited.ToolTipText.Should().Contain("Exited 2026-01-02 03:04:05 UTC");
    }

    [Fact]
    public void Projection_runs_the_layout_axes_at_the_camera_elevation()
    {
        double Deg(Point p) => Math.Atan2(p.Y, p.X) * 180 / Math.PI;
        var child = EzForensics.App.Controls.ProcessGraphCanvas.Project(100, 0);
        var sibling = EzForensics.App.Controls.ProcessGraphCanvas.Project(0, 100);
        EzForensics.App.Controls.ProcessGraphCanvas.ElevationDegrees.Should().Be(20);
        Deg(child).Should().BeApproximately(20, 1e-6);            // right-and-down, 20Â° below horizontal
        Deg(sibling).Should().BeApproximately(180 - 20, 1e-6);    // left-and-down
    }

    [Fact]
    public void Connectors_are_one_brace_per_parent_with_curves_to_each_child()
    {
        var pos = new Dictionary<string, TreeLayoutPoint>
        {
            ["p"] = new(0, 0), ["a"] = new(198, -186), ["b"] = new(198, 0), ["c"] = new(198, 372), ["g"] = new(0, 750), ["gk"] = new(198, 750),
        };
        var (solid, ghost) = EzForensics.App.Controls.ProcessGraphCanvas.BuildConnectors(pos,
            new[] { ("p", "a"), ("p", "b"), ("p", "c"), ("g", "gk") }, n => n == "g");
        solid.IsEmpty().Should().BeFalse();
        ghost.IsEmpty().Should().BeFalse();
        // the curves stay within the parent â†’ children span (no overshoot) and reach every child
        var bounds = solid.Bounds;
        bounds.Inflate(0.01, 0.01);
        var top = EzForensics.App.Controls.ProcessGraphCanvas.Project(198, -186);
        var bottom = EzForensics.App.Controls.ProcessGraphCanvas.Project(198, 372);
        bounds.Contains(top).Should().BeTrue();
        bounds.Contains(bottom).Should().BeTrue();
        // straight legs (p->b line, stub, spine, a leg per off-row child) and rounded junctions
        var segs = PathGeometry.CreateFromGeometry(solid).Figures.SelectMany(fg => fg.Segments).ToList();
        segs.OfType<LineSegment>().Count().Should().BeGreaterThanOrEqualTo(4);
        segs.OfType<BezierSegment>().Count().Should().BeGreaterThanOrEqualTo(2);
    }

    [Fact]
    public async Task Pane_lays_out_and_binds_without_errors()
    {
        var vm = await LoadAsync();
        Exception? failure = null;
        var errors = new Collector();
        var t = new Thread(() =>
        {
            try
            {
                if (Application.Current == null) new App().InitializeComponent();
                PresentationTraceSources.Refresh();
                PresentationTraceSources.DataBindingSource.Listeners.Add(errors);
                PresentationTraceSources.DataBindingSource.Switch.Level = SourceLevels.Error;

                var pane = new ProcessTreePane { DataContext = vm };
                pane.Measure(new Size(1000, 400));
                pane.Arrange(new Rect(0, 0, 1000, 400));
                pane.UpdateLayout();

                // isometric floor: a child steps right-and-down from its parent, a sibling left-and-down from the one before it
                var canvas = (EzForensics.App.Controls.ProcessGraphCanvas)pane.FindName("Canvas");
                Point At(int pid)
                {
                    var cp = VisualTree(canvas).OfType<System.Windows.Controls.ContentPresenter>().First(p => p.Content is ProcessNodeViewModel n && n.Pid == pid);
                    return new Point(System.Windows.Controls.Canvas.GetLeft(cp), System.Windows.Controls.Canvas.GetTop(cp));
                }
                Point parent = At(1), child = At(10), sibling = At(11);
                child.X.Should().BeGreaterThan(parent.X);
                child.Y.Should().BeGreaterThan(parent.Y);
                sibling.X.Should().BeLessThan(child.X);
                sibling.Y.Should().BeGreaterThan(child.Y);

                // View â€º Show grid: bound to the canvas, remembered by the next tree of the session
                canvas.ShowGrid.Should().BeTrue();
                vm.ShowGrid = false;
                canvas.ShowGrid.Should().BeFalse();
                new ProcessTreeViewModel("h2", "H2", (_, _) => { }, () => { }, (_, _) => Task.FromResult<string?>(null)).ShowGrid.Should().BeFalse();
                vm.ShowGrid = true;

                vm.SelectPid(11, null, null, null).Should().Be(PidResolutionKind.Found);
                vm.IsFocusMode.Should().BeTrue();
                pane.UpdateLayout();

                var ids = Logical(pane).Concat(VisualTree(pane)).Select(d => AutomationProperties.GetAutomationId(d)).Where(s => !string.IsNullOrEmpty(s)).ToHashSet();
                ids.Should().Contain(new[]
                {
                    "Results.ProcessTree.HostChip", "Results.ProcessTree.Title",
                    "Results.ProcessTree.ViewMenu", "Results.ProcessTree.ShowExited", "Results.ProcessTree.ShowGrid",
                    "Results.ProcessTree.ShowWholeTree", "Results.ProcessTree.Toast", "Results.ProcessTree.Status",
                    "Results.ProcessTree.ZoomIn", "Results.ProcessTree.ZoomOut", "Results.ProcessTree.ZoomSlider", "Results.ProcessTree.Fit", "Results.ProcessTree.Close",
                    "Results.ProcessTree.Navigator.Toggle", "Results.ProcessTree.Canvas", "Results.ProcessTree.ZoomBar",
                });
                ids.Should().Contain(new[] { "Results.ProcessTree.Detail.Card", "Results.ProcessTree.Detail.Copy", "Results.ProcessTree.Detail.Field.StartTime.Copy" });
                ids.Should().NotContain(new[]
                {
                    "Results.ProcessTree.Dock", "Results.ProcessTree.Dock.Minimize", "Results.ProcessTree.Dock.Folded", "Results.ProcessTree.Dock.Expand",
                    "Results.ProcessTree.Detail.ShowPath", "Results.ProcessTree.Detail.AddToEvidence", "Results.ProcessTree.Detail.TabCounts", "Results.ProcessTree.Detail.Count.Network",
                    "Results.ProcessTree.Detail.Rail.Timeline", "Results.ProcessTree.Detail.Rail.Threats", "Results.ProcessTree.Detail.Rail.Scripts",
                    "Results.ProcessTree.Detail.Rail.Persistence", "Results.ProcessTree.Detail.Rail.Execution", "Results.ProcessTree.Detail.Rail.Sources",
                });
                PresentationTraceSources.DataBindingSource.Listeners.Remove(errors);
            }
            catch (Exception ex) { failure = ex; }
        });
        t.SetApartmentState(ApartmentState.STA);
        t.Start();
        t.Join();
        failure.Should().BeNull(failure?.ToString());
        errors.Lines.Should().BeEmpty();
    }

    [Fact]
    public void Trail_geometry_draws_only_the_given_edges()
    {
        var pos = new Dictionary<string, TreeLayoutPoint> { ["p"] = new(0, 0), ["a"] = new(198, -186), ["b"] = new(198, 0), ["c"] = new(198, 372) };
        EzForensics.App.Controls.ProcessGraphCanvas.BuildTrail(pos, Array.Empty<(string, string)>()).IsEmpty().Should().BeTrue();
        var one = EzForensics.App.Controls.ProcessGraphCanvas.BuildTrail(pos, new[] { ("p", "c") });
        one.IsEmpty().Should().BeFalse();
        var b = one.Bounds;
        b.Inflate(0.01, 0.01);
        b.Contains(EzForensics.App.Controls.ProcessGraphCanvas.Project(198, 372)).Should().BeTrue();
        // the sibling a is not touched
        b.Contains(EzForensics.App.Controls.ProcessGraphCanvas.Project(198, -186)).Should().BeFalse();
    }

    [Fact]
    public async Task Long_detail_value_clamps_and_more_less_respond_to_a_real_click()
    {
        var g = new ProcessGraph();
        var n = new ProcessNode { Pid = 5, Name = "long.exe", CmdLine = "long.exe " + string.Join(" ", Enumerable.Range(0, 120).Select(i => "--option" + i)), CreateTime = new DateTime(2026, 1, 1, 0, 0, 5, DateTimeKind.Utc) };
        g.AllNodes.Add(n);
        g.Roots.Add(n);
        var vm = new ProcessTreeViewModel("host", "HOST", (_, _) => { }, () => { }, (_, _) => Task.FromResult<string?>(null));
        await vm.ShowGraphAsync(g, CancellationToken.None);
        vm.SelectNode(vm.VisibleRoots.Single());
        var field = vm.Detail!.Fields.Single(f => f.Id == "CmdLine");
        field.IsLong.Should().BeTrue();

        Exception? failure = null;
        var t = new Thread(() =>
        {
            try
            {
                if (Application.Current == null) new App().InitializeComponent();
                var pane = new ProcessTreePane { DataContext = vm };
                var w = new Window { Width = 1100, Height = 760, Content = pane, ShowActivated = false, WindowStyle = WindowStyle.None, Left = -3000, Top = -3000, ShowInTaskbar = false };
                w.Show();
                w.UpdateLayout();
                FrameworkElement Find(string id) => VisualTree(w).OfType<FrameworkElement>().First(f => AutomationProperties.GetAutomationId(f) == id);
                double ValueHeight() { w.UpdateLayout(); return VisualTree(Find("Results.ProcessTree.Detail.Field.CmdLine")).OfType<System.Windows.Controls.Border>().First(x => x.Name == "valClip").ActualHeight; }
                double line = VisualTree(w).OfType<System.Windows.Controls.TextBox>().First(x => x.DataContext is ProcessDetailField { IsLong: false }).ActualHeight;
                var collapsed = ValueHeight();
                collapsed.Should().BeInRange(line * 3 - 4, line * 3 + 6, "a long value shows about 3 lines");
                // the raw-input harness cannot press a ButtonBase (the physical button state stays released, so the button drops its capture), so the
                // click is the UIA Invoke a screen reader sends, after checking that the point on the link really hits the button
                void ClickMore(bool checkHit = true)
                {
                    w.UpdateLayout();
                    var more = (System.Windows.Controls.Button)Find("Results.ProcessTree.Detail.Field.CmdLine.More");
                    more.ActualWidth.Should().BeGreaterThan(0, "the link shows for a long value");
                    // expanded, the link can sit below the scrolled card
                    if (checkHit) VisualTree(more).Should().Contain((DependencyObject)w.InputHitTest(more.TranslatePoint(new Point(more.ActualWidth / 2, more.ActualHeight / 2), w)), "the link text is what the click lands on");
                    ((System.Windows.Automation.Provider.IInvokeProvider)new System.Windows.Automation.Peers.ButtonAutomationPeer(more)).Invoke();
                    w.Dispatcher.Invoke(System.Windows.Threading.DispatcherPriority.Background, () => { });   // Invoke is queued on the dispatcher
                }
                ClickMore();
                field.IsExpanded.Should().BeTrue("Invoke on 'more…' expands");
                ValueHeight().Should().BeGreaterThan(collapsed + line);
                ClickMore(checkHit: false);
                field.IsExpanded.Should().BeFalse();
                ValueHeight().Should().BeApproximately(collapsed, 1);
                w.Close();
            }
            catch (Exception ex) { failure = ex; }
        });
        t.SetApartmentState(ApartmentState.STA);
        t.Start();
        t.Join();
        failure.Should().BeNull(failure?.ToString());
    }

    [Fact]
    public async Task Zoomed_far_out_regions_stand_in_for_the_subtrees_and_the_boot_chain_stays()
    {
        var g = new ProcessGraph();
        ProcessNode N(int pid, string name, ProcessNode? parent)
        {
            var n = new ProcessNode { Pid = pid, Ppid = parent?.Pid ?? 0, Name = name, CreateTime = new DateTime(2026, 1, 1, 0, 0, pid % 60, DateTimeKind.Utc), Parent = parent };
            parent?.Children.Add(n);
            g.AllNodes.Add(n);
            if (parent == null) g.Roots.Add(n);
            return n;
        }
        var services = N(500, "services.exe", null);
        var svc = N(600, "svchost.exe", services);
        N(601, "a.exe", svc); N(602, "b.exe", svc);
        N(700, "spoolsv.exe", services);
        // two orphaned trees (their roots are not boot chain): one shared region, laid out apart below the boot tree
        var o1 = N(900, "orphan1.exe", null); N(901, "o1kid.exe", o1);
        N(950, "orphan2.exe", null);
        var vm = new ProcessTreeViewModel("host", "HOST", (_, _) => { }, () => { }, (_, _) => Task.FromResult<string?>(null));
        await vm.ShowGraphAsync(g, CancellationToken.None);
        Exception? failure = null;
        var t = new Thread(() =>
        {
            try
            {
                if (Application.Current == null) new App().InitializeComponent();
                var pane = new ProcessTreePane { DataContext = vm };
                var w = new Window { Width = 1100, Height = 760, Content = pane, ShowActivated = false, WindowStyle = WindowStyle.None, Left = -3000, Top = -3000, ShowInTaskbar = false };
                w.Show();
                var canvas = (EzForensics.App.Controls.ProcessGraphCanvas)pane.FindName("Canvas");
                // effective on-screen opacity (0 when an ancestor is hidden)
                Func<UIElement, double> Shown = e =>
                {
                    double o = 1;
                    for (DependencyObject? d = e; d != null && d != canvas; d = VisualTreeHelper.GetParent(d))
                        if (d is UIElement u) { if (u.Visibility != Visibility.Visible) return 0; o *= u.Opacity; }
                    return o;
                };
                List<System.Windows.Controls.ContentPresenter> Presenters() => VisualTree(canvas).OfType<System.Windows.Controls.ContentPresenter>().Where(p => p.Content is ProcessNodeViewModel).ToList();
                List<FrameworkElement> Regions() => VisualTree(w).OfType<FrameworkElement>().Where(f => (AutomationProperties.GetAutomationId(f) ?? "").StartsWith("Results.ProcessTree.Region.")).ToList();
                List<System.Windows.Shapes.Path> RegionShapes() => VisualTree(canvas).OfType<System.Windows.Shapes.Path>().Where(p => p.Tag?.GetType().Name == "RegionVisual").ToList();
                double FillOpacity() => RegionShapes().Select(r => { Shown(r).Should().BeGreaterThan(0); return r.Opacity; }).Distinct().Single();

                canvas.Zoom = 1;
                w.UpdateLayout();
                Presenters().Should().HaveCountGreaterThan(2).And.OnlyContain(p => Shown(p) > 0.99, "close in, the full graph");
                Regions().Select(f => AutomationProperties.GetAutomationId(f)).Should().BeEquivalentTo("Results.ProcessTree.Region.500", "Results.ProcessTree.Region.Orphaned");
                RegionShapes().Should().OnlyContain(r => r.Stroke == null, "regions have no outline");
                VisualTree(canvas).OfType<System.Windows.Controls.TextBlock>().Where(x => x.Text.Contains("processes")).Should().BeEmpty("regions have no titles");
                FillOpacity().Should().Be(0.05, "one plain fill at zoom 1");
                // the region lies on the ground plane: un-projected, its shape is an (almost, rounded corners) axis-aligned layout rectangle
                var ground = RegionShapes()[0].Data.Clone();
                var inverse = EzForensics.App.Controls.ProcessGraphCanvas.ProjectionMatrix;
                inverse.Invert();
                ground.Transform = new MatrixTransform(Matrix.Multiply(ground.Transform.Value, inverse));
                var layoutBox = ground.Bounds;
                (ground.GetArea() / (layoutBox.Width * layoutBox.Height)).Should().BeGreaterThan(0.95, "a projected parallelogram, not a screen rectangle");
                RegionShapes()[0].Data.GetArea().Should().BeLessThan(0.8 * RegionShapes()[0].Data.Bounds.Width * RegionShapes()[0].Data.Bounds.Height, "on screen it is slanted");

                canvas.Zoom = 0.15;
                w.UpdateLayout();
                Presenters().Where(p => ((ProcessNodeViewModel)p.Content).Pid == 500).Should().ContainSingle().Which.Should().Match<UIElement>(p => Shown(p) > 0.99, "the boot chain stays");
                Presenters().Where(p => ((ProcessNodeViewModel)p.Content).Pid == 500).Single().RenderTransform.Should().BeOfType<ScaleTransform>()
                    .Which.ScaleX.Should().BeGreaterThan(1, "far out the boot chain is inverse-scaled to stay readable");
                var offChain = Presenters().Where(p => ((ProcessNodeViewModel)p.Content).Pid != 500).ToList();
                offChain.Should().NotBeEmpty().And.OnlyContain(p => Shown(p) > 0.99 && p.IsVisible, "far out, every node stays drawn and solid (no fade)");
                ((UIElement)VisualTreeHelper.GetParent(offChain[0])).IsHitTestVisible.Should().BeFalse("far out, region clicks win over the tiny nodes");
                var edges = VisualTree(canvas).OfType<System.Windows.Shapes.Path>()
                    .Where(x => x.Data != null && !x.Data.IsEmpty() && x.Stroke != null && x.StrokeThickness * 0.15 >= 0.99 && Shown(x) > 0.99).ToList();
                edges.Should().NotBeEmpty("connectors out of the boot chain keep a minimum on-screen stroke");
                FillOpacity().Should().Be(0.05, "same fill at zoom 0.15");

                canvas.Zoom = 0.3;
                w.UpdateLayout();
                FillOpacity().Should().Be(0.05, "same fill at zoom 0.3");
                Regions().Count(f => AutomationProperties.GetAutomationId(f) == "Results.ProcessTree.Region.Orphaned").Should().Be(1, "orphaned trees share one region");
                var fill = Regions().Single(f => AutomationProperties.GetAutomationId(f) == "Results.ProcessTree.Region.500");
                fill.IsHitTestVisible.Should().BeTrue("far out a region fill zooms to the region");

                canvas.Zoom = EzForensics.App.Controls.ProcessGraphCanvas.MinZoom;
                w.UpdateLayout();
                var nodeLayer = (UIElement)VisualTreeHelper.GetParent(Presenters().First(p => ((ProcessNodeViewModel)p.Content).Pid == 601));
                nodeLayer.Opacity.Should().Be(1, "at MinZoom the node layer is opaque");
                nodeLayer.Visibility.Should().Be(Visibility.Visible, "at MinZoom the node layer is visible");
                Presenters().Should().OnlyContain(p => Shown(p) > 0.99 && p.IsVisible, "at MinZoom every node is still drawn");
                // node tooltips: an explicit capped-width ToolTip with a wrapping TextBlock bound to ToolTipText (diamond and pill alike)
                var kid = Presenters().First(p => ((ProcessNodeViewModel)p.Content).Pid == 601);
                var tips = VisualTree(kid).OfType<FrameworkElement>().Select(f => f.ToolTip).OfType<System.Windows.Controls.ToolTip>().ToList();
                tips.Should().HaveCount(2);
                foreach (var owner in VisualTree(kid).OfType<FrameworkElement>().Where(f => f.ToolTip is System.Windows.Controls.ToolTip))
                {
                    var tip = (System.Windows.Controls.ToolTip)owner.ToolTip;
                    tip.PlacementTarget = owner; // what ToolTipService sets when it opens the tooltip
                    tip.MaxWidth.Should().BeLessThanOrEqualTo(500);
                    var tb = tip.Content.Should().BeOfType<System.Windows.Controls.TextBlock>().Subject;
                    tb.TextWrapping.Should().Be(TextWrapping.Wrap);
                    tip.IsOpen = true;
                    tip.UpdateLayout();
                    tb.Text.Should().Be(((ProcessNodeViewModel)kid.Content).ToolTipText, "the ToolTip takes the node's DataContext from its PlacementTarget");
                    tip.IsOpen = false;
                }
                w.Close();
            }
            catch (Exception ex) { failure = ex; }
        });
        t.SetApartmentState(ApartmentState.STA);
        t.Start();
        t.Join();
        failure.Should().BeNull(failure?.ToString());
    }
}
