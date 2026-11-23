using System.Diagnostics;
using System.IO;
using System.Windows;
using EzForensics.App.ViewModels.Findings;
using EzForensics.App.Views.Findings;
using EzForensics.Core.Spectre;
using FluentAssertions;

namespace EzForensics.App.Tests;

/// <summary>The Findings page parses, binds and lays out through all three steps and the detail tabs without binding errors.</summary>
[Collection("WpfApp")]
public sealed class FindingsViewSmokeTests
{
    private sealed class Collector : TraceListener
    {
        public readonly List<string> Lines = new();
        public override void Write(string? message) { }
        public override void WriteLine(string? message) { if (message != null) lock (Lines) Lines.Add(message); }
    }

    // A plain queue instead of the WPF dispatcher (see ExploreViewSmokeTests): continuations run on this thread when pumped.
    private sealed class QueueContext : SynchronizationContext
    {
        private readonly System.Collections.Concurrent.ConcurrentQueue<(SendOrPostCallback, object?)> _q = new();
        public override void Post(SendOrPostCallback d, object? state) => _q.Enqueue((d, state));
        public override void Send(SendOrPostCallback d, object? state) => d(state);
        public void Drain() { while (_q.TryDequeue(out var x)) x.Item1(x.Item2); }
    }

    private static void Pump(QueueContext ctx, Func<bool> until, int timeoutMs = 10000)
    {
        var sw = Stopwatch.StartNew();
        while (sw.ElapsedMilliseconds < timeoutMs) { ctx.Drain(); DoEvents(); if (until()) return; Thread.Sleep(10); }
        ctx.Drain(); DoEvents();
    }

    // The view models' OnUi calls (from other test classes running in parallel) Invoke on this thread's dispatcher: keep it served.
    private static void DoEvents()
    {
        var frame = new System.Windows.Threading.DispatcherFrame();
        System.Windows.Threading.Dispatcher.CurrentDispatcher.BeginInvoke(System.Windows.Threading.DispatcherPriority.Background, () => frame.Continue = false);
        System.Windows.Threading.Dispatcher.PushFrame(frame);
    }

    [Fact]
    public void Findings_view_binds_through_every_step_and_tab()
    {
        var dir = FindingsViewModelTests.TempDir();
        Directory.CreateDirectory(dir);
        SpectreCaseLink.Save(dir, new SpectreCaseLink("case1", 5, DateTime.UtcNow));
        Exception? failure = null;
        var listener = new Collector();
        var t = new Thread(() =>
        {
            try
            {
                if (Application.Current == null) new App().InitializeComponent();
                var ctx = new QueueContext(); SynchronizationContext.SetSynchronizationContext(ctx);
                foreach (var d in Application.Current.Resources.MergedDictionaries) foreach (var k in d.Keys.Cast<object>().ToList()) _ = d[k];   // realize deferred resources
                PresentationTraceSources.DataBindingSource.Listeners.Add(listener);
                PresentationTraceSources.DataBindingSource.Switch.Level = SourceLevels.Error;
                var engine = new FindingsViewModelTests.FakeEngine();
                var vm = new FindingsViewModel(() => dir, new SpectreFindingsClient(engine, () => dir, null), _ => Task.FromResult(true));
                var view = new FindingsView { DataContext = vm };
                void Layout() { view.Measure(new Size(1400, 800)); view.Arrange(new Rect(0, 0, 1400, 800)); view.UpdateLayout(); }
                Layout();
                                var open = vm.OpenCaseAsync(dir);
                Pump(ctx, () => open.IsCompleted);
                open.IsCompleted.Should().BeTrue();
                Layout();
                vm.Rows.Should().HaveCount(2);
                vm.Moments.Should().HaveCount(2);
                vm.NextMoment(); Layout(); Pump(ctx, () => false, 150); Layout();
                vm.Walkthrough.Open(); Layout();
                vm.Walkthrough.Next(); Layout(); Pump(ctx, () => false, 150); Layout();
                vm.Walkthrough.Last(); Layout();
                vm.Walkthrough.Close(); Layout();
                foreach (var tab in Enum.GetValues<FindingsTab>()) { vm.SelectedTab = tab; Layout(); Pump(ctx, () => false, 100); Layout(); }
                vm.SelectedTab = FindingsTab.Overview; Layout();
                vm.LaneMode = EzForensics.Core.Spectre.TimelineLaneMode.Host; Layout();
                vm.ShowAllFindings = true; Layout();
                vm.Selected = vm.Rows[0];
                vm.AddSuggestionCore("F-1", new SpectreFindingChanges().Severity("Critical"), "why");
                vm.DraftTitle = "changed";
                Layout();
                foreach (var tab in new[] { FindingsViewModel.TabEvidence, FindingsViewModel.TabHistory, FindingsViewModel.TabDetails }) { vm.SelectedDetailTab = tab; Layout(); Pump(ctx, () => false, 150); Layout(); }
                vm.IsReportOpen = true; Layout();
                var gen = vm.GenerateReportCommand.ExecuteAsync(null);
                Pump(ctx, () => gen.IsCompleted);
                Layout();
                vm.ReportPreview.Should().Contain("Key events report");
                vm.IsReportOpen = false; vm.ShowAllFindings = false; Layout();
            }
            catch (Exception ex) { failure = ex; }
        });
        t.SetApartmentState(ApartmentState.STA);
        t.Start();
        t.Join();
        try { Directory.Delete(dir, true); } catch (IOException) { }
        if (failure != null) throw new Exception("Findings smoke failed: " + failure, failure);
        listener.Lines.Should().BeEmpty("binding errors: " + string.Join("\n", listener.Lines));
    }

    [Fact]
    public void NavLabel_IsReview() => EzForensics.App.Services.PageNames.Findings.Should().Be("Review");

    private static IEnumerable<T> Descendants<T>(DependencyObject root) where T : DependencyObject
    {
        for (var i = 0; i < System.Windows.Media.VisualTreeHelper.GetChildrenCount(root); i++)
        {
            var c = System.Windows.Media.VisualTreeHelper.GetChild(root, i);
            if (c is T t) yield return t;
            foreach (var d in Descendants<T>(c)) yield return d;
        }
    }

    [Fact]
    public void DetailsHeader_IsAnalyst()
    {
        var dir = FindingsViewModelTests.TempDir();
        Directory.CreateDirectory(dir);
        Exception? failure = null;
        var t = new Thread(() =>
        {
            try
            {
                if (Application.Current == null) new App().InitializeComponent();
                var vm = new FindingsViewModel(() => dir, new SpectreFindingsClient(new FindingsViewModelTests.FakeEngine(), () => dir, null), _ => Task.FromResult(true));
                var view = new FindingsView { DataContext = vm };
                vm.SelectedTab = FindingsTab.Details;
                view.Measure(new Size(1400, 800)); view.Arrange(new Rect(0, 0, 1400, 800)); view.UpdateLayout();
                var texts = Descendants<System.Windows.Controls.TextBlock>(view).Select(x => x.Text).ToList();
                texts.Should().Contain("Analyst");
                texts.Should().NotContain("ID");
            }
            catch (Exception ex) { failure = ex; }
        });
        t.SetApartmentState(ApartmentState.STA);
        t.Start();
        t.Join();
        try { Directory.Delete(dir, true); } catch (IOException) { }
        if (failure != null) throw new Exception("Header test failed: " + failure, failure);
    }

    [Fact]
    public void SeverityText_UsesSeverityBrushes_NoSemiBold()
    {
        Exception? failure = null;
        var t = new Thread(() =>
        {
            try
            {
                if (Application.Current == null) new App().InitializeComponent();
                var view = new FindingsView();
                var style = (Style)view.Resources["SeverityText"];
                foreach (var level in new[] { "Critical", "High", "Medium", "Low", "Info" })
                {
                    foreach (var theme in new[] { EzForensics.Core.Settings.AppTheme.Light, EzForensics.Core.Settings.AppTheme.Dark })
                    {
                        EzForensics.App.Services.ThemeManager.Apply(theme);
                        var tb = new System.Windows.Controls.TextBlock { Style = style, DataContext = new { Severity = level } };
                        view.Content = tb;
                        view.Measure(new Size(300, 100)); view.Arrange(new Rect(0, 0, 300, 100)); view.UpdateLayout();
                        tb.FontWeight.Should().Be(FontWeights.Normal);
                        var expected = (System.Windows.Media.SolidColorBrush)Application.Current.FindResource("Brush.Severity." + level);
                        ((System.Windows.Media.SolidColorBrush)tb.Foreground).Color.Should().Be(expected.Color, level + " in " + theme);
                    }
                }
            }
            catch (Exception ex) { failure = ex; }
        });
        t.SetApartmentState(ApartmentState.STA);
        t.Start();
        t.Join();
        if (failure != null) throw new Exception("Severity test failed: " + failure, failure);
    }
}
