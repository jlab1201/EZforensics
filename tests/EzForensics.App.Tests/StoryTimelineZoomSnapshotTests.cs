using System.IO;
using System.Text.Json.Nodes;
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

/// <summary>The timeline at zoom 1, 4 and 20: groups split and the axis gains finer time labels. PNGs go to artifacts/snapshots.</summary>
[Collection("WpfApp")]
public sealed class StoryTimelineZoomSnapshotTests
{
    private static SpectreTimeline Sample()
    {
        var b = new DateTime(2026, 1, 2, 22, 0, 0, DateTimeKind.Utc);
        var offs = new[] { 0, 1, 40, 41, 90, 400, 405, 900, 3600, 3640, 7200, 20000 };
        var moments = new JsonArray();
        for (int i = 0; i < offs.Length; i++)
            moments.Add(new JsonObject { ["id"] = "m" + i, ["rank"] = i + 1, ["headline"] = "Event " + i, ["time"] = b.AddSeconds(offs[i]).ToString("yyyy-MM-dd'T'HH:mm:ss'Z'") });
        return SpectreTimeline.From(new JsonObject { ["moments"] = moments });
    }

    [Fact]
    public void Snapshots_at_zoom_1_4_and_20()
    {
        var outDir = FindingsSnapshotTests.SnapshotDir();
        var written = new List<string>();
        var counts = new List<int>();
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
                Motion.ForceOff = true;
                ThemeManager.Apply(AppTheme.Light);
                foreach (var z in new[] { 1, 4, 20 })
                {
                    var tl = new StoryTimeline { Timeline = Sample() };
                    var host = new Border { Child = tl, Padding = new Thickness(16) };
                    host.SetResourceReference(Border.BackgroundProperty, "Brush.Window");
                    host.SetResourceReference(TextElement.ForegroundProperty, "Brush.Text");
                    int w = 1300, h = 560;
                    void Lay() { host.Measure(new Size(w, h)); host.Arrange(new Rect(0, 0, w, h)); host.UpdateLayout(); }
                    Lay();
                    if (z > 1) { tl.SetZoom(z, 40); Lay(); }
                    tl.Zoom.Should().BeApproximately(z, 1e-6);
                    counts.Add(tl.LayoutFor(w - 32 - 2 * StoryTimeline.Pad)!.Clusters.Count);
                    var bmp = new RenderTargetBitmap(w, h, 96, 96, PixelFormats.Pbgra32);
                    bmp.Render(host);
                    var enc = new PngBitmapEncoder();
                    enc.Frames.Add(BitmapFrame.Create(bmp));
                    var path = Path.Combine(outDir, $"timeline-zoom-{z}-light.png");
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
        written.Should().HaveCount(3);
        written.ForEach(p => new FileInfo(p).Length.Should().BeGreaterThan(5000));
        counts[0].Should().BeLessThan(counts[1]);
        counts[1].Should().BeLessThanOrEqualTo(counts[2]);
    }
}
