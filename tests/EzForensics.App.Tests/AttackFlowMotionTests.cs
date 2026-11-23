using System.Windows;
using System.Windows.Controls;
using System.Windows.Documents;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using System.IO;
using EzForensics.App.Controls;
using EzForensics.App.Services;
using EzForensics.Core.Settings;
using EzForensics.Core.Spectre;
using FluentAssertions;

namespace EzForensics.App.Tests;

/// <summary>The attack flow's pulse math (no UI) and a snapshot of the graph with the pulse frozen mid-edge.</summary>
[Collection("WpfApp")]
public sealed class AttackFlowMotionTests
{
    [Fact]
    public void Pulse_starts_at_the_first_edge_start()
    {
        var p = AttackFlowGraph.PulseAt(0, 4)!.Value;
        p.Edge.Should().Be(0);
        p.Along.Should().Be(0);
    }

    [Fact]
    public void Pulse_is_halfway_along_the_edge_mid_edge_and_moves_on_at_the_edge_end()
    {
        var mid = AttackFlowGraph.PulseAt(AttackFlowGraph.EdgeSeconds * 0.5, 4)!.Value;
        mid.Edge.Should().Be(0);
        mid.Along.Should().BeApproximately(0.5, 1e-9);

        var second = AttackFlowGraph.PulseAt(AttackFlowGraph.EdgeSeconds * 1.5, 4)!.Value;
        second.Edge.Should().Be(1);
        second.Along.Should().BeApproximately(0.5, 1e-9);

        var nearEnd = AttackFlowGraph.PulseAt(AttackFlowGraph.EdgeSeconds - 1e-6, 4)!.Value;
        nearEnd.Edge.Should().Be(0);
        nearEnd.Along.Should().BeGreaterThan(0.999);

        AttackFlowGraph.PulseAt(AttackFlowGraph.EdgeSeconds, 4)!.Value.Edge.Should().Be(1);
    }

    [Fact]
    public void Pulse_rests_after_the_last_edge_then_loops()
    {
        AttackFlowGraph.PulseAt(AttackFlowGraph.EdgeSeconds * 4 + 0.1, 4).Should().BeNull();
        AttackFlowGraph.PulseAt(AttackFlowGraph.CycleSeconds(4) + 0.45, 4)!.Value.Edge.Should().Be(0);
    }

    [Fact]
    public void Arrival_glow_peaks_on_arrival_and_fades()
    {
        double at = AttackFlowGraph.EdgeSeconds;   // card 1
        AttackFlowGraph.GlowAt(1, at, 4).Should().BeApproximately(1, 1e-9);
        AttackFlowGraph.GlowAt(1, at + AttackFlowGraph.GlowSeconds / 2, 4).Should().BeApproximately(0.5, 1e-9);
        AttackFlowGraph.GlowAt(1, at + AttackFlowGraph.GlowSeconds + 0.01, 4).Should().Be(0);
        AttackFlowGraph.GlowAt(1, at - 0.1, 4).Should().Be(0);
    }

    [Fact]
    public void Point_along_follows_the_polyline_by_length()
    {
        var pts = new[] { new Point(0, 0), new Point(10, 0), new Point(10, 10) };
        AttackFlowGraph.PointAlong(pts, 0).Should().Be(new Point(0, 0));
        AttackFlowGraph.PointAlong(pts, 0.5).Should().Be(new Point(10, 0));
        AttackFlowGraph.PointAlong(pts, 0.75).Should().Be(new Point(10, 5));
        AttackFlowGraph.PointAlong(pts, 1).Should().Be(new Point(10, 10));
    }

    [Theory]
    [InlineData(0, true, false)]
    [InlineData(1, true, false)]
    [InlineData(2, true, true)]
    [InlineData(5, false, false)]
    public void Motion_needs_two_nodes_and_animations_on(int nodes, bool animationsOn, bool expected)
        => AttackFlowGraph.MotionPossible(nodes, animationsOn).Should().Be(expected);

    private static FlowNode N(string id, int num, int minute, string tactic, string sev) =>
        new(id, num, "Event " + id, new DateTimeOffset(2026, 1, 1, 10, minute, 0, TimeSpan.Zero), tactic, sev, StoryGraphics.Glyph(tactic), "PC1");

    [Fact]
    public void Snapshot_with_the_pulse_frozen_mid_edge()
    {
        var outDir = FindingsSnapshotTests.SnapshotDir();
        var written = new List<string>();
        Exception? failure = null;
        var t = new Thread(() =>
        {
            try
            {
                if (Application.Current == null)
                {
                    var posted = new List<System.Windows.Threading.DispatcherOperation>();
                    var hooks = System.Windows.Threading.Dispatcher.CurrentDispatcher.Hooks;
                    void OnPosted(object? s, System.Windows.Threading.DispatcherHookEventArgs e) => posted.Add(e.Operation);
                    hooks.OperationPosted += OnPosted;
                    try { new App().InitializeComponent(); } finally { hooks.OperationPosted -= OnPosted; }
                    foreach (var op in posted) op.Abort();
                }
                var nodes = new[]
                {
                    N("a", 1, 1, "Initial Access", "Medium"), N("b", 2, 3, "Execution", "High"), N("c", 3, 8, "Persistence", "High"),
                    N("d", 4, 15, "Credential Access", "Critical"), N("e", 5, 30, "Lateral Movement", "High"),
                };
                foreach (var theme in new[] { AppTheme.Light, AppTheme.Dark })
                {
                    ThemeManager.Apply(theme);
                    var g = new AttackFlowGraph { Nodes = nodes, ShowHost = true };
                    g.SetAnimationClock(AttackFlowGraph.EdgeSeconds * 1.55);   // mid edge 1 (card b to card c); card b glow already faded, card c not reached
                    var host = new Border { Child = g, Padding = new Thickness(16) };
                    host.SetResourceReference(Border.BackgroundProperty, "Brush.Window");
                    host.SetResourceReference(TextElement.ForegroundProperty, "Brush.Text");
                    int w = 1100, h = 520;
                    host.Measure(new Size(w, h)); host.Arrange(new Rect(0, 0, w, h)); host.UpdateLayout();
                    foreach (var (name, clock) in new[] { ("mid", AttackFlowGraph.EdgeSeconds * 1.55), ("arrive", AttackFlowGraph.EdgeSeconds * 2 + 0.15) })
                    {
                        g.SetAnimationClock(clock);
                        host.UpdateLayout();
                        var bmp = new RenderTargetBitmap(w, h, 96, 96, PixelFormats.Pbgra32);
                        bmp.Render(host);
                        var enc = new PngBitmapEncoder();
                        enc.Frames.Add(BitmapFrame.Create(bmp));
                        var path = Path.Combine(outDir, $"attackflow-motion-{name}-{theme.ToString().ToLowerInvariant()}.png");
                        Directory.CreateDirectory(outDir);
                        using (var fs = File.Create(path)) enc.Save(fs);
                        written.Add(path);
                    }
                    host.Child = null;
                }
            }
            catch (Exception ex) { failure = ex; }
            finally { try { ThemeManager.Apply(AppTheme.Dark); } catch (Exception) { } }
        });
        t.SetApartmentState(ApartmentState.STA);
        t.Start();
        t.Join();
        failure.Should().BeNull();
        written.Should().HaveCount(4);
        written.Should().OnlyContain(p => new FileInfo(p).Length > 5000);
    }
}
