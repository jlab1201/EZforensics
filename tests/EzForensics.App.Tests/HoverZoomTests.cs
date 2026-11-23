using System.IO;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Documents;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using EzForensics.App.Controls;
using EzForensics.App.Services;
using EzForensics.Core.Settings;
using EzForensics.Core.Spectre;
using FluentAssertions;

namespace EzForensics.App.Tests;

/// <summary>Hover zoom easing math (no UI) and snapshots of the radar and donut with one item hovered at full zoom.</summary>
[Collection("WpfApp")]
public sealed class HoverZoomTests
{
    [Fact]
    public void Ease_runs_from_rest_to_full_and_is_fast_at_the_start()
    {
        HoverZoom.Ease(0).Should().Be(0);
        HoverZoom.Ease(1).Should().Be(1);
        HoverZoom.Ease(0.5).Should().BeGreaterThan(0.5);
        HoverZoom.Ease(-1).Should().Be(0);
        HoverZoom.Ease(3).Should().Be(1);
    }

    [Fact]
    public void Scale_grows_from_one_to_the_max_over_the_duration()
    {
        HoverZoom.ScaleAt(0).Should().Be(1);
        HoverZoom.ScaleAt(HoverZoom.DurationMs).Should().BeApproximately(1.35, 1e-9);
        HoverZoom.ScaleAt(HoverZoom.DurationMs * 5).Should().BeApproximately(1.35, 1e-9);
        HoverZoom.ScaleAt(HoverZoom.DurationMs / 2).Should().BeInRange(1.2, 1.35);
        TacticRadar.ChipScale(1).Should().BeApproximately(1.35, 1e-9);
        TacticRadar.ChipScale(0).Should().Be(1);
    }

    [Fact]
    public void Step_moves_toward_the_target_and_clamps()
    {
        HoverZoom.Step(0, true, HoverZoom.DurationMs / 2).Should().BeApproximately(0.5, 1e-9);
        HoverZoom.Step(0.9, true, 100).Should().Be(1);
        HoverZoom.Step(0.5, false, HoverZoom.DurationMs / 2).Should().BeApproximately(0, 1e-9);
        HoverZoom.Step(0.1, false, 100).Should().Be(0);
    }

    [Fact]
    public void Snapshots_of_radar_chip_and_donut_segment_at_full_zoom()
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
                var axes = new[] { "Initial Access", "Execution", "Persistence", "Privilege Escalation", "Defense Evasion", "Credential Access",
                    "Discovery", "Lateral Movement", "Collection", "Command and Control", "Exfiltration", "Impact" };
                var data = new TacticRadarData(axes, new[] { 3, 5, 2, 0, 4, 6, 1, 2, 0, 3, 1, 0 }, 0);
                foreach (var theme in new[] { AppTheme.Light, AppTheme.Dark })
                {
                    ThemeManager.Apply(theme);
                    var radar = new TacticRadar { Data = data, Width = 520, Height = 340 };
                    radar.SetHoverForTest(5);
                    var donut = new SeverityDonut { Counts = new[] { 2, 4, 6, 3, 1 }, Width = 140, Height = 140 };
                    donut.SetHoverForTest(2);
                    var row = new StackPanel { Orientation = Orientation.Horizontal };
                    row.Children.Add(radar); row.Children.Add(new Border { Width = 24 }); row.Children.Add(donut);
                    var host = new Border { Child = row, Padding = new Thickness(16) };
                    host.SetResourceReference(Border.BackgroundProperty, "Brush.Window");
                    host.SetResourceReference(TextElement.ForegroundProperty, "Brush.Text");
                    int w = 740, h = 380;
                    host.Measure(new Size(w, h)); host.Arrange(new Rect(0, 0, w, h)); host.UpdateLayout();
                    var bmp = new RenderTargetBitmap(w, h, 96, 96, PixelFormats.Pbgra32);
                    bmp.Render(host);
                    var enc = new PngBitmapEncoder();
                    enc.Frames.Add(BitmapFrame.Create(bmp));
                    var path = Path.Combine(outDir, $"hover-zoom-{theme.ToString().ToLowerInvariant()}.png");
                    Directory.CreateDirectory(outDir);
                    using (var fs = File.Create(path)) enc.Save(fs);
                    written.Add(path);
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
        written.Should().HaveCount(2);
        written.ForEach(p => new FileInfo(p).Length.Should().BeGreaterThan(5000));
    }
}
