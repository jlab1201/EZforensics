using System.Diagnostics;
using System.IO;
using System.Text.Json.Nodes;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Documents;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using EzForensics.App.Controls;
using EzForensics.App.Services;
using EzForensics.App.ViewModels.Findings;
using EzForensics.App.Views.Findings;
using EzForensics.Core.Settings;
using EzForensics.Core.Spectre;
using FluentAssertions;

namespace EzForensics.App.Tests;

/// <summary>
/// Renders the Findings story with a realistic sample case (7 key moments, the last undated, importance rank not in time order, 3 computers, mixed severities, one undated finding) to PNG in the
/// light and dark theme at 1400 and 900 px wide, with animations off (end state). The PNGs go to <c>artifacts/snapshots</c> at the repository
/// root (gitignored) so a person can look at them.
/// </summary>
[Collection("WpfApp")]
public sealed class FindingsSnapshotTests
{
    // A plain queue instead of the WPF dispatcher (see FindingsViewSmokeTests).
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

    private static void DoEvents()
    {
        var frame = new System.Windows.Threading.DispatcherFrame();
        System.Windows.Threading.Dispatcher.CurrentDispatcher.BeginInvoke(System.Windows.Threading.DispatcherPriority.Background, () => frame.Continue = false);
        System.Windows.Threading.Dispatcher.PushFrame(frame);
    }

    /// <summary>
    /// Creates the App (for its resources) without ever running its OnStartup: Application's constructor queues the startup on this thread's
    /// dispatcher, and pumping it would run the real start (single-instance check, which shuts the app down while EZ Forensics is open).
    /// </summary>
    private static void NewAppWithoutStartup()
    {
        var posted = new List<System.Windows.Threading.DispatcherOperation>();
        var hooks = System.Windows.Threading.Dispatcher.CurrentDispatcher.Hooks;
        void OnPosted(object? s, System.Windows.Threading.DispatcherHookEventArgs e) => posted.Add(e.Operation);
        hooks.OperationPosted += OnPosted;
        try { new App().InitializeComponent(); }
        finally { hooks.OperationPosted -= OnPosted; }
        foreach (var op in posted) op.Abort();
    }

    // ------------------------------------------------------------ sample case

    private static readonly (string Id, string Title, string Plain, string Severity, string Tactic, string Host, string? Start, string? Approval)[] Sample =
    {
        ("F-1", "Phishing attachment opened", "Phishing attachment opened", "Medium", "Initial Access", "WS-01", "2026-01-02T08:14:00Z", "APPROVED"),
        ("F-2", "PowerShell encoded command", "Encoded PowerShell command executed", "High", "Execution", "WS-01", "2026-01-02T08:16:30Z", null),
        ("F-3", "New service installed", "Service installed (Event 7045)", "High", "Persistence", "WS-01", "2026-01-02T09:02:00Z", null),
        ("F-4", "Mimikatz sekurlsa", "Credential dumping tool executed (mimikatz.exe)", "Critical", "Credential Access", "WS-02", "2026-01-03T22:41:00Z", null),
        ("F-5", "PsExec to domain controller", "Remote execution on DC-01", "High", "Lateral Movement", "DC-01", "2026-01-04T01:05:00Z", null),
        ("F-6", "Event log cleared", "Security event log cleared (Event 1102)", "Low", "Defense Evasion", "DC-01", "2026-01-05T03:30:00Z", null),
        ("F-7", "Suspicious scheduled task", "Scheduled task created (no timestamp)", "Medium", "Persistence", "WS-02", null, null),
    };

    internal static string SampleTimeline()
    {
        var findings = new JsonArray();
        var undated = new JsonArray();
        foreach (var f in Sample)
        {
            var o = new JsonObject
            {
                ["id"] = f.Id, ["finding_id"] = f.Id, ["title"] = f.Title, ["plain_title"] = f.Plain, ["severity"] = f.Severity, ["tactic"] = f.Tactic,
                ["host"] = f.Host, ["artifact_type"] = "evtx", ["approval"] = f.Approval,
            };
            if (f.Id switch { "F-2" => "T1059.001", "F-3" => "T1543.003", "F-7" => "T1053.005", _ => null } is { } tq) o["technique"] = tq;
            if (f.Start != null) { o["start"] = f.Start; findings.Add(o); }
            else undated.Add(o);
        }
        var moments = new JsonArray();
        // importance rank is deliberately not the time order (the Critical one is third in time, the first-in-time one is fifth in importance)
        var importance = new Dictionary<string, int> { ["F-1"] = 5, ["F-2"] = 3, ["F-3"] = 4, ["F-4"] = 1, ["F-5"] = 2, ["F-6"] = 6, ["F-7"] = 7 };
        foreach (var f in Sample.Where(s => s.Start != null || s.Id == "F-7"))
        {
            moments.Add(new JsonObject
            {
                ["id"] = "M-" + f.Id[2..], ["rank"] = importance[f.Id], ["time"] = f.Start, ["primary_finding_id"] = f.Id, ["finding_ids"] = new JsonArray(f.Id),
                ["headline"] = f.Severity + ": " + f.Title, ["plain_title"] = f.Plain, ["reason"] = f.Severity + " severity",
                ["why"] = "Spectre picked it because it is " + f.Severity.ToLowerInvariant() + " severity.",
            });
        }
        return new JsonObject
        {
            ["case"] = "case1", ["time_basis"] = "UTC", ["lanes"] = new JsonObject { ["tactic"] = new JsonArray() },
            ["findings"] = findings, ["undated"] = undated, ["clusters"] = new JsonArray(), ["moments"] = moments,
        }.ToJsonString();
    }

    private static readonly Dictionary<string, string> SampleTechnique = new()
    {
        ["F-1"] = "T1566.001", ["F-2"] = "T1059.001", ["F-3"] = "T1543.003", ["F-4"] = "T1003.001", ["F-5"] = "T1021.002", ["F-6"] = "T1070.001", ["F-7"] = "T1053.005",
    };

    internal static string SampleList(bool allApproved = false) =>
        new JsonObject
        {
            ["findings"] = new JsonArray(Sample.Select(f => (JsonNode)new JsonObject
            {
                ["id"] = f.Id, ["finding_id"] = f.Id, ["title"] = f.Title, ["severity"] = f.Severity, ["mitre_attack"] = new JsonArray(SampleTechnique[f.Id]),
                ["artifact_type"] = "evtx", ["description"] = "d", ["approval"] = allApproved ? "APPROVED" : f.Approval, ["include_in_report"] = true, ["event_time"] = f.Start,
            }).ToArray()),
            ["unregistered"] = new JsonArray(),
        }.ToJsonString();

    // single-system case: one evidence system (host folder PC1) whose event logs name the machine two ways, plus findings with no host
    private static readonly (string Id, string Title, string Plain, string Severity, string Tactic, string? Host, string Artifact, string? CasePath, string? Start)[] OneSystem =
    {
        ("S-1", "Phishing attachment opened", "Phishing attachment opened", "Medium", "Initial Access", "DESKTOP-DEMO01", "evtx", "PC1/Persistence/events-a.csv", "2026-01-02T08:14:00Z"),
        ("S-2", "PowerShell encoded command", "Encoded PowerShell command executed", "High", "Execution", "DESKTOP-DEMO01", "evtx", "PC1/Persistence/events-a.csv", "2026-01-02T08:16:30Z"),
        ("S-3", "Service installed", "Service installed (Event 7045)", "High", "Persistence", "WIN-DEMO02", "evtx", "PC1/Persistence/events-b.csv", "2026-01-02T09:02:00Z"),
        ("S-4", "Run key added", "A program was set to start at sign-in", "Medium", "Persistence", null, "registry", null, null),
        ("S-5", "Prefetch of a password tool", "Credential dumping tool executed (mimikatz.exe)", "Critical", "Credential Access", null, "prefetch", null, null),
    };

    internal static string OneSystemTimeline()
    {
        var findings = new JsonArray();
        var undated = new JsonArray();
        foreach (var f in OneSystem)
        {
            var o = new JsonObject
            {
                ["id"] = f.Id, ["finding_id"] = f.Id, ["title"] = f.Title, ["plain_title"] = f.Plain, ["severity"] = f.Severity, ["tactic"] = f.Tactic,
                ["artifact_type"] = f.Artifact,
            };
            if (f.Host != null) o["host"] = f.Host;
            if (f.CasePath != null) o["case_path"] = f.CasePath;
            if (f.Start != null) { o["start"] = f.Start; findings.Add(o); }
            else undated.Add(o);
        }
        var moments = new JsonArray();
        int rank = 1;
        foreach (var f in OneSystem.Where(s => s.Start != null || s.Id == "S-5"))
            moments.Add(new JsonObject
            {
                ["id"] = "M-" + f.Id[2..], ["rank"] = rank++, ["time"] = f.Start, ["primary_finding_id"] = f.Id, ["finding_ids"] = new JsonArray(f.Id),
                ["headline"] = f.Severity + ": " + f.Title, ["plain_title"] = f.Plain, ["reason"] = f.Severity + " severity",
                ["why"] = "Spectre picked it because it is " + f.Severity.ToLowerInvariant() + " severity.",
            });
        // the engine counts the two record names as two computers
        return new JsonObject
        {
            ["case"] = "case1", ["time_basis"] = "UTC", ["lanes"] = new JsonObject { ["tactic"] = new JsonArray() },
            ["findings"] = findings, ["undated"] = undated, ["clusters"] = new JsonArray(), ["moments"] = moments,
            ["summary"] = new JsonObject { ["sentence"] = "Spectre found 4 key events on 2 Jan 2026 (UTC) across 2 hosts.", ["hosts"] = 2, ["moments"] = 4,
                ["first_seen"] = "2026-01-02T08:14:00Z", ["last_seen"] = "2026-01-02T09:02:00Z" },
        }.ToJsonString();
    }

    internal static string OneSystemList() =>
        new JsonObject
        {
            ["findings"] = new JsonArray(OneSystem.Select(f => (JsonNode)new JsonObject
            {
                ["id"] = f.Id, ["finding_id"] = f.Id, ["title"] = f.Title, ["severity"] = f.Severity, ["mitre_attack"] = new JsonArray("T1000"),
                ["artifact_type"] = f.Artifact, ["description"] = "d", ["include_in_report"] = true,
            }).ToArray()),
            ["unregistered"] = new JsonArray(),
        }.ToJsonString();

    // ------------------------------------------------------------ render

    /// <summary>The repository root (the folder holding the .sln), found from the test binaries.</summary>
    internal static string SnapshotDir()
    {
        var d = new DirectoryInfo(AppContext.BaseDirectory);
        while (d != null && !d.GetFiles("*.sln").Any()) d = d.Parent;
        return Path.Combine(d?.FullName ?? AppContext.BaseDirectory, "artifacts", "snapshots");
    }

    /// <summary>Lays the page out at <paramref name="width"/>, grows it so the whole story fits (no scrolling), and writes a PNG.</summary>
    internal static string Render(FrameworkElement page, ScrollViewer story, int width, string path, int? fixedHeight = null)
    {
        var host = new Border { Child = page, Padding = new Thickness(16) };
        host.SetResourceReference(Border.BackgroundProperty, "Brush.Window");
        host.SetResourceReference(TextElement.ForegroundProperty, "Brush.Text");
        void Layout(double h) { host.Measure(new Size(width, h)); host.Arrange(new Rect(0, 0, width, h)); host.UpdateLayout(); }
        Layout(1200);
        Layout(1200);   // the page picks its one or two columns from its width, so it needs a second pass
        var top = story.TranslatePoint(new Point(0, 0), host).Y;
        var height = fixedHeight ?? Math.Ceiling(top + story.ExtentHeight + 24);
        Layout(height);
        var bmp = new RenderTargetBitmap(width, (int)height, 96, 96, PixelFormats.Pbgra32);
        bmp.Render(host);
        var enc = new PngBitmapEncoder();
        enc.Frames.Add(BitmapFrame.Create(bmp));
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        using (var fs = File.Create(path)) enc.Save(fs);
        host.Child = null;
        return path;
    }

    [Fact]
    public void Story_timeline_panel_confirmed_badge_and_confirmed_only_render()
    {
        var dir = FindingsViewModelTests.TempDir();
        Directory.CreateDirectory(dir);
        SpectreCaseLink.Save(dir, new SpectreCaseLink("case1", 5, DateTime.UtcNow));
        var outDir = SnapshotDir();
        var written = new List<string>();
        Exception? failure = null;
        var t = new Thread(() =>
        {
            try
            {
                if (Application.Current == null) NewAppWithoutStartup();
                var ctx = new QueueContext(); SynchronizationContext.SetSynchronizationContext(ctx);
                Motion.ForceOff = true;
                var engine = new FindingsViewModelTests.FakeEngine { TimelineReply = SampleTimeline(), ListReply = SampleList(allApproved: true) };
                var vm = new FindingsViewModel(() => dir, new SpectreFindingsClient(engine, () => dir, null), _ => Task.FromResult(true));
                var open = vm.OpenCaseAsync(dir);
                Pump(ctx, () => open.IsCompleted && !vm.IsLoadingTimeline);
                foreach (var theme in new[] { AppTheme.Light, AppTheme.Dark })
                {
                    ThemeManager.Apply(theme);
                    var name = theme.ToString().ToLowerInvariant();
                    var view = new FindingsView { DataContext = vm };
                    Pump(ctx, () => false, 100);
                    vm.Walkthrough.Open();
                    vm.Walkthrough.GoToMoment("M-4").Should().BeTrue();
                    Pump(ctx, () => false, 100);
                    vm.Walkthrough.Card!.IsConfirmed.Should().BeTrue();
                    written.Add(Render(view, view.OverviewScroll, 1400, Path.Combine(outDir, $"findings-story-panel-{name}-1400.png"), 900));
                    vm.ConfirmedOnly = true;
                    Pump(ctx, () => false, 100);
                    written.Add(Render(view, view.OverviewScroll, 1400, Path.Combine(outDir, $"findings-story-confirmedonly-timeline-{name}-1400.png"), 900));
                    vm.SelectedStoryView = StoryView.AttackFlow;
                    Pump(ctx, () => false, 100);
                    written.Add(Render(view, view.OverviewScroll, 1400, Path.Combine(outDir, $"findings-story-confirmedonly-attackflow-{name}-1400.png"), 900));
                    vm.SelectedStoryView = StoryView.Timeline;
                    vm.ConfirmedOnly = false;
                    vm.Walkthrough.Close();
                    view.DataContext = null;
                }
            }
            catch (Exception ex) { failure = ex; }
            finally
            {
                Motion.ForceOff = false;
                try { ThemeManager.Apply(AppTheme.Dark); } catch (Exception) { }
            }
        });
        t.SetApartmentState(ApartmentState.STA);
        t.Start();
        t.Join();
        try { Directory.Delete(dir, true); } catch (IOException) { }
        if (failure != null) throw new Exception("Story panel snapshot failed: " + failure, failure);
        written.Should().HaveCount(6);
        foreach (var p in written) new FileInfo(p).Length.Should().BeGreaterThan(10_000);
    }

    [Fact]
    public void Story_renders_in_light_and_dark_at_two_widths()
    {
        var dir = FindingsViewModelTests.TempDir();
        Directory.CreateDirectory(dir);
        SpectreCaseLink.Save(dir, new SpectreCaseLink("case1", 5, DateTime.UtcNow));
        var outDir = SnapshotDir();
        var written = new List<string>();
        string? extra = null;
        var slides = new List<string>();
        var flows = new List<string>();
        Exception? failure = null;
        var t = new Thread(() =>
        {
            try
            {
                if (Application.Current == null) NewAppWithoutStartup();
                var ctx = new QueueContext(); SynchronizationContext.SetSynchronizationContext(ctx);
                Motion.ForceOff = true;
                var engine = new FindingsViewModelTests.FakeEngine { TimelineReply = SampleTimeline(), ListReply = SampleList() };
                var vm = new FindingsViewModel(() => dir, new SpectreFindingsClient(engine, () => dir, null), _ => Task.FromResult(true));
                var open = vm.OpenCaseAsync(dir);
                Pump(ctx, () => open.IsCompleted);
                open.IsCompleted.Should().BeTrue();
                Pump(ctx, () => !vm.IsLoadingTimeline);
                vm.Moments.Should().HaveCount(7);
                vm.Moments.Select(m => m.Id).Should().Equal("M-1", "M-2", "M-3", "M-4", "M-5", "M-6", "M-7"); // time order; the last is undated
                vm.Moments.Select(m => m.Number).Should().Equal(1, 2, 3, 4, 5, 6, 7);
                foreach (var theme in new[] { AppTheme.Light, AppTheme.Dark })
                {
                    ThemeManager.Apply(theme);
                    foreach (var width in new[] { 1400, 900 })
                    {
                        var view = new FindingsView { DataContext = vm };
                        Pump(ctx, () => false, 100);
                        written.Add(Render(view, view.OverviewScroll, width, Path.Combine(outDir, $"findings-story-{theme.ToString().ToLowerInvariant()}-{width}.png")));
                        view.DataContext = null;
                    }
                }

                // every finding decided: the review ring is full and the hero says the report is ready
                ThemeManager.Apply(AppTheme.Light);
                File.Delete(EzForensics.Core.Analysis.CaseAnalysisStore.PathFor(dir));   // a fresh case analysis: the engine's decisions are imported once
                var engine2 = new FindingsViewModelTests.FakeEngine { TimelineReply = SampleTimeline(), ListReply = SampleList(allApproved: true) };
                var vm2 = new FindingsViewModel(() => dir, new SpectreFindingsClient(engine2, () => dir, null), _ => Task.FromResult(true));
                var open2 = vm2.OpenCaseAsync(dir);
                Pump(ctx, () => open2.IsCompleted && !vm2.IsLoadingTimeline);
                vm2.AllReviewed.Should().BeTrue();
                var view2 = new FindingsView { DataContext = vm2 };
                Pump(ctx, () => false, 100);
                extra = Render(view2, view2.OverviewScroll, 1400, Path.Combine(outDir, "findings-story-light-1400-reviewed.png"));
                view2.DataContext = null;

                // timeline accents: a hovered marker's mini card on the Timeline tab
                ThemeManager.Apply(AppTheme.Dark);
                var view3 = new FindingsView { DataContext = vm };
                Pump(ctx, () => false, 100);
                vm.Walkthrough.Open();
                Pump(ctx, () => false, 100);
                var accents = Path.Combine(outDir, "findings-timeline-accents-dark-1400.png");
                Render(view3, view3.OverviewScroll, 1400, accents, 900);
                view3.SlideLine.HoverMarker("M-4").Should().BeTrue();
                Render(view3, view3.OverviewScroll, 1400, accents, 900);
                var cardHover = Path.Combine(outDir, "findings-timeline-cardhover-dark-1400.png");
                Render(view3, view3.OverviewScroll, 1400, cardHover, 900);   // lays the cards out
                view3.SlideLine.HoverCard("M-4").Should().BeTrue();
                Render(view3, view3.OverviewScroll, 1400, cardHover, 900);
                vm.Walkthrough.Close();
                view3.DataContext = null;

                // the walk-through slides: overview, and the Critical moment (M-4)
                foreach (var (theme, width, slide) in new[]
                {
                    (AppTheme.Light, 1400, "overview"), (AppTheme.Dark, 1400, "overview"),
                    (AppTheme.Light, 1400, "moment"), (AppTheme.Dark, 1400, "moment"), (AppTheme.Dark, 900, "moment"),
                })
                {
                    ThemeManager.Apply(theme);
                    var sv = new FindingsView { DataContext = vm };
                    Pump(ctx, () => false, 100);
                    vm.Walkthrough.Open();
                    if (slide == "moment") vm.Walkthrough.GoToMoment("M-4").Should().BeTrue();
                    Pump(ctx, () => false, 100);
                    slides.Add(Render(sv, sv.OverviewScroll, width, Path.Combine(outDir, $"findings-slide-{slide}-{theme.ToString().ToLowerInvariant()}-{width}.png"), 900));
                    vm.Walkthrough.Close();
                    sv.DataContext = null;
                }

                // Attack flow: the toggle, the nodes (dated key events in time order), the host filter, and a snapshot in both themes
                vm.SelectedStoryView.Should().Be(StoryView.Timeline);
                vm.AttackFlowNodes.Select(n => n.Id).Should().Equal(vm.Moments.Where(m => m.Moment.Time != null).OrderBy(m => m.Number).Select(m => m.Id));
                vm.AttackFlowNodes.Should().NotBeEmpty();
                foreach (var theme in new[] { AppTheme.Light, AppTheme.Dark })
                {
                    ThemeManager.Apply(theme);
                    var fv = new FindingsView { DataContext = vm };
                    Pump(ctx, () => false, 100);
                    vm.Walkthrough.Open();
                    vm.SelectedStoryView = StoryView.AttackFlow;
                    vm.ShowTimelineView.Should().BeFalse();
                    Pump(ctx, () => false, 100);
                    flows.Add(Render(fv, fv.OverviewScroll, 1400, Path.Combine(outDir, $"findings-attackflow-{theme.ToString().ToLowerInvariant()}-1400.png"), 900));
                    var hoverPath = Path.Combine(outDir, $"findings-attackflow-hover-{theme.ToString().ToLowerInvariant()}-1400.png");
                    fv.AttackFlow.HoverNode("M-4").Should().BeTrue();
                    flows.Add(Render(fv, fv.OverviewScroll, 1400, hoverPath, 900));
                    vm.SelectedStoryView = StoryView.Timeline;
                    vm.Walkthrough.Close();
                    fv.DataContext = null;
                }
                vm.ShowFlowHost.Should().BeTrue();
                vm.HostFilter = vm.HostChoices.Skip(1).First();
                vm.ShowFlowHost.Should().BeFalse();
                vm.AttackFlowNodes.Count.Should().BeLessThan(vm.Moments.Count);
                vm.HostFilter = null;
            }
            catch (Exception ex) { failure = ex; }
            finally
            {
                Motion.ForceOff = false;
                try { ThemeManager.Apply(AppTheme.Dark); } catch (Exception) { }
            }
        });
        t.SetApartmentState(ApartmentState.STA);
        t.Start();
        t.Join();
        try { Directory.Delete(dir, true); } catch (IOException) { }
        if (failure != null) throw new Exception("Findings snapshot failed: " + failure, failure);
        written.Should().HaveCount(4);
        slides.Should().HaveCount(5);
        flows.Should().HaveCount(4);
        foreach (var p in written.Append(extra!).Concat(slides).Concat(flows)) new FileInfo(p).Length.Should().BeGreaterThan(10_000);
    }

    [Fact]
    public void Tabs_render_Overview_Timeline_and_Details_in_light_and_dark_at_two_widths()
    {
        var dir = FindingsViewModelTests.TempDir();
        Directory.CreateDirectory(dir);
        SpectreCaseLink.Save(dir, new SpectreCaseLink("case1", 5, DateTime.UtcNow));
        var outDir = SnapshotDir();
        var written = new List<string>();
        Exception? failure = null;
        var t = new Thread(() =>
        {
            try
            {
                if (Application.Current == null) NewAppWithoutStartup();
                var ctx = new QueueContext(); SynchronizationContext.SetSynchronizationContext(ctx);
                Motion.ForceOff = true;
                var engine = new FindingsViewModelTests.FakeEngine { TimelineReply = SampleTimeline(), ListReply = SampleList() };
                var vm = new FindingsViewModel(() => dir, new SpectreFindingsClient(engine, () => dir, null), _ => Task.FromResult(true));
                var open = vm.OpenCaseAsync(dir);
                Pump(ctx, () => open.IsCompleted && !vm.IsLoadingTimeline);
                vm.Moments.Should().HaveCount(7);
                vm.ReviewBreakdown.Sum(l => l.Count).Should().Be(7);
                foreach (var theme in new[] { AppTheme.Light, AppTheme.Dark })
                {
                    ThemeManager.Apply(theme);
                    foreach (var width in new[] { 1400, 900 })
                    {
                        var name = theme.ToString().ToLowerInvariant();
                        var view = new FindingsView { DataContext = vm };
                        Pump(ctx, () => false, 100);
                        vm.SelectedTab = FindingsTab.Overview;
                        written.Add(Render(view, view.OverviewScroll, width, Path.Combine(outDir, $"findings-tab-overview-{name}-{width}.png")));
                        vm.IsWideLayout.Should().Be(width >= 1000, "the tiles sit side by side only when wide");

                        vm.SelectedTab = FindingsTab.Story;
                        vm.Walkthrough.GoToMoment("M-4").Should().BeTrue();
                        Pump(ctx, () => false, 100);
                        written.Add(Render(view, view.OverviewScroll, width, Path.Combine(outDir, $"findings-tab-timeline-{name}-{width}.png"), 900));

                        vm.SelectedTab = FindingsTab.Details;
                        vm.SelectFindingCore("F-4").Should().BeTrue();
                        vm.DraftTitle = "Credential dumping tool run (mimikatz.exe)";
                        Pump(ctx, () => false, 100);
                        written.Add(Render(view, view.OverviewScroll, width, Path.Combine(outDir, $"findings-tab-details-{name}-{width}.png"), 900));
                        vm.DiscardEditsCommand.Execute(null);
                        vm.Selected = null;
                        vm.SelectedTab = FindingsTab.Overview;
                        view.DataContext = null;
                    }
                }
            }
            catch (Exception ex) { failure = ex; }
            finally
            {
                Motion.ForceOff = false;
                try { ThemeManager.Apply(AppTheme.Dark); } catch (Exception) { }
            }
        });
        t.SetApartmentState(ApartmentState.STA);
        t.Start();
        t.Join();
        try { Directory.Delete(dir, true); } catch (IOException) { }
        if (failure != null) throw new Exception("Findings tab snapshot failed: " + failure, failure);
        written.Should().HaveCount(12);
        foreach (var p in written) new FileInfo(p).Length.Should().BeGreaterThan(10_000);
    }

    // far apart: key moments minutes, a day, weeks and months apart, with other findings spanning years (some before and after the moments)
    private static readonly (string Id, string Plain, string Severity, string Tactic, string Start, string? End, bool Moment)[] FarApart =
    {
        ("A-1", "Phishing attachment opened", "Medium", "Initial Access", "2026-01-02T08:14:00Z", null, true),
        ("A-7", "A tool was downloaded from the internet", "Medium", "Command and Control", "2026-01-02T08:14:30Z", null, true),   // 30 s after A-1: shares its point
        ("A-2", "Encoded PowerShell command executed", "High", "Execution", "2026-01-02T08:16:00Z", null, true),
        ("A-3", "Service installed (Event 7045)", "High", "Persistence", "2026-01-02T09:02:00Z", null, true),
        ("A-4", "Credential dumping tool executed (mimikatz.exe)", "Critical", "Credential Access", "2026-01-03T22:41:00Z", null, true),
        ("A-5", "Remote execution on DC-01", "High", "Lateral Movement", "2026-01-20T11:00:00Z", null, true),
        ("A-6", "Files were copied out", "High", "Exfiltration", "2026-03-03T17:20:00Z", null, true),
        ("B-1", "Old file timestamps", "Info", "Discovery", "2011-05-01T00:00:00Z", "2026-03-01T00:00:00Z", false),
        ("B-2", "An old program ran", "Low", "Execution", "2014-07-09T10:00:00Z", null, false),
        ("B-3", "A user signed in", "Info", "Initial Access", "2026-01-02T08:40:00Z", null, false),
        ("B-4", "A user signed in", "Info", "Initial Access", "2026-01-11T09:00:00Z", null, false),
        ("B-5", "A browser download", "Low", "Execution", "2026-02-14T15:00:00Z", null, false),
        ("B-6", "A later sign-in", "Info", "Initial Access", "2027-06-01T09:00:00Z", null, false),
    };

    internal static string FarApartTimeline()
    {
        var findings = new JsonArray();
        var moments = new JsonArray();
        int rank = 1;
        foreach (var f in FarApart)
        {
            var o = new JsonObject
            {
                ["id"] = f.Id, ["finding_id"] = f.Id, ["title"] = f.Plain, ["plain_title"] = f.Plain, ["severity"] = f.Severity, ["tactic"] = f.Tactic,
                ["host"] = "WS-01", ["artifact_type"] = "evtx", ["start"] = f.Start,
            };
            if (f.Id switch { "A-7" => "T1105", "A-2" => "T1059.001", "A-3" => "T1543.003", _ => null } is { } tq) o["technique"] = tq;   // A-7: download icon and scene
            if (f.End != null) o["end"] = f.End;
            findings.Add(o);
            if (f.Moment)
                moments.Add(new JsonObject
                {
                    ["id"] = "M-" + f.Id[2..], ["rank"] = rank++, ["time"] = f.Start, ["primary_finding_id"] = f.Id, ["finding_ids"] = new JsonArray(f.Id),
                    ["headline"] = f.Severity + ": " + f.Plain, ["plain_title"] = f.Plain, ["reason"] = f.Severity + " severity",
                    ["why"] = "Spectre picked it because it is " + f.Severity.ToLowerInvariant() + " severity.",
                });
        }
        return new JsonObject
        {
            ["case"] = "case1", ["time_basis"] = "UTC", ["lanes"] = new JsonObject { ["tactic"] = new JsonArray() },
            ["findings"] = findings, ["undated"] = new JsonArray(), ["clusters"] = new JsonArray(), ["moments"] = moments,
        }.ToJsonString();
    }

    internal static string FarApartList() =>
        new JsonObject
        {
            ["findings"] = new JsonArray(FarApart.Select(f => (JsonNode)new JsonObject
            {
                ["id"] = f.Id, ["finding_id"] = f.Id, ["title"] = f.Plain, ["severity"] = f.Severity, ["mitre_attack"] = new JsonArray("T1000"),
                ["artifact_type"] = "evtx", ["description"] = "d", ["include_in_report"] = true,
            }).ToArray()),
            ["unregistered"] = new JsonArray(),
        }.ToJsonString();

    [Fact]
    public void Far_apart_moments_spread_out_on_the_story_scale()
    {
        var dir = FindingsViewModelTests.TempDir();
        Directory.CreateDirectory(dir);
        SpectreCaseLink.Save(dir, new SpectreCaseLink("case1", 5, DateTime.UtcNow));
        var outDir = SnapshotDir();
        var written = new List<string>();
        Exception? failure = null;
        var t = new Thread(() =>
        {
            try
            {
                if (Application.Current == null) NewAppWithoutStartup();
                var ctx = new QueueContext(); SynchronizationContext.SetSynchronizationContext(ctx);
                Motion.ForceOff = true;
                var engine = new FindingsViewModelTests.FakeEngine { TimelineReply = FarApartTimeline(), ListReply = FarApartList() };
                var vm = new FindingsViewModel(() => dir, new SpectreFindingsClient(engine, () => dir, null), _ => Task.FromResult(true));
                var open = vm.OpenCaseAsync(dir);
                Pump(ctx, () => open.IsCompleted && !vm.IsLoadingTimeline);
                vm.Moments.Should().HaveCount(7);
                foreach (var theme in new[] { AppTheme.Light, AppTheme.Dark })
                {
                    ThemeManager.Apply(theme);
                    foreach (var width in new[] { 1400, 900 })
                    {
                        var view = new FindingsView { DataContext = vm };
                        Pump(ctx, () => false, 100);
                        var name = $"findings-farapart-story-{theme.ToString().ToLowerInvariant()}-{width}.png";
                        vm.Walkthrough.Open();
                        Pump(ctx, () => false, 100);
                        written.Add(Render(view, view.OverviewScroll, width, Path.Combine(outDir, name), 900));
                        var lay = view.SlideLine.LayoutFor(view.SlideLine.ActualWidth - 28)!;
                        lay.IsStoryScale.Should().BeTrue();
                        lay.EarlierCount.Should().Be(2);
                        lay.LaterCount.Should().Be(1);
                        lay.ClusterOf("M-1")!.MomentIds.Should().Equal("M-1", "M-7");
                        var xs = lay.Clusters.Select(c => c.AnchorX).ToList();
                        for (int i = 1; i < xs.Count; i++) (xs[i] - xs[i - 1]).Should().BeGreaterThanOrEqualTo(10, name);
                        lay.Callouts.Single(c => c.MomentId == "M-3").AnchorX.Should().BeLessThan(lay.Callouts.Single(c => c.MomentId == "M-4").AnchorX - 20, name);
                        vm.Walkthrough.Close();
                        view.DataContext = null;
                    }
                }

                // hover the first point (several moments) and zoom in 6x
                ThemeManager.Apply(AppTheme.Dark);
                var zv = new FindingsView { DataContext = vm };
                Pump(ctx, () => false, 100);
                vm.Walkthrough.Open();
                Pump(ctx, () => false, 100);
                var hover = Path.Combine(outDir, "findings-farapart-hover-dark-1400.png");
                Render(zv, zv.OverviewScroll, 1400, hover, 900);
                zv.SlideLine.HoverMarker("M-1").Should().BeTrue();
                written.Add(Render(zv, zv.OverviewScroll, 1400, hover, 900));
                zv.SlideLine.SetZoom(6, 40);
                zv.SlideLine.Zoom.Should().Be(6);
                written.Add(Render(zv, zv.OverviewScroll, 1400, Path.Combine(outDir, "findings-farapart-zoom-dark-1400.png"), 900));
                zv.SlideLine.Fit();
                zv.SlideLine.Zoom.Should().Be(1);
                vm.Walkthrough.Close();
                zv.DataContext = null;

                // walk-through slide on the Critical moment (M-4)
                foreach (var (theme, width) in new[] { (AppTheme.Light, 1400), (AppTheme.Dark, 1400), (AppTheme.Light, 900), (AppTheme.Dark, 900) })
                {
                    ThemeManager.Apply(theme);
                    var sv = new FindingsView { DataContext = vm };
                    Pump(ctx, () => false, 100);
                    vm.Walkthrough.Open();
                    vm.Walkthrough.GoToMoment("M-4").Should().BeTrue();
                    Pump(ctx, () => false, 100);
                    written.Add(Render(sv, sv.OverviewScroll, width, Path.Combine(outDir, $"findings-farapart-slide-{theme.ToString().ToLowerInvariant()}-{width}.png"), 900));
                    vm.Walkthrough.Close();
                    sv.DataContext = null;
                }
            }
            catch (Exception ex) { failure = ex; }
            finally
            {
                Motion.ForceOff = false;
                try { ThemeManager.Apply(AppTheme.Dark); } catch (Exception) { }
            }
        });
        t.SetApartmentState(ApartmentState.STA);
        t.Start();
        t.Join();
        try { Directory.Delete(dir, true); } catch (IOException) { }
        if (failure != null) throw new Exception("Far-apart snapshot failed: " + failure, failure);
        written.Should().HaveCount(10);
        foreach (var p in written) new FileInfo(p).Length.Should().BeGreaterThan(10_000);
    }

    /// <summary>A small evidence CSV (two computers' worth of event rows) for the evidence panel snapshot.</summary>
    private static void WriteEvidenceSample(string dir)
    {
        var folder = Path.Combine(dir, "WS-02", "EventLogs");
        Directory.CreateDirectory(folder);
        var sb = new System.Text.StringBuilder("TimeCreated,EventId,Computer,UserName,ExecutableInfo,PayloadData1\n");
        string[] exes = { "C:\\Windows\\System32\\svchost.exe -k netsvcs", "C:\\Users\\alice\\AppData\\Local\\Temp\\m64.exe sekurlsa::logonpasswords", "C:\\Windows\\System32\\lsass.exe",
                          "C:\\Windows\\System32\\cmd.exe /c whoami", "C:\\Windows\\explorer.exe", "C:\\Users\\alice\\AppData\\Local\\Temp\\m64.exe privilege::debug" };
        for (int i = 0; i < 18; i++)
            sb.Append($"2026-01-03 22:{30 + i:00}:00,{(i % 3 == 0 ? 4688 : 4624)},WS-02,{(i % 4 == 0 ? "SYSTEM" : "alice")},{exes[i % exes.Length]},Target: lsass.exe\n");
        File.WriteAllText(Path.Combine(folder, "Security.csv"), sb.ToString());
        File.WriteAllText(Path.Combine(folder, "Sysmon.csv"), "TimeCreated,EventId,Image\n2026-01-03 22:41:00,10,m64.exe\n2026-01-03 22:42:00,1,cmd.exe\n");
    }

    /// <summary>The sample list with evidence rows on the Critical finding (F-4) in two files.</summary>
    private static string SampleListWithEvidence()
    {
        var o = JsonNode.Parse(SampleList())!.AsObject();
        var f4 = o["findings"]!.AsArray().OfType<JsonObject>().Single(f => (string?)f["id"] == "F-4");
        f4["evidence"] = new JsonArray(
            new JsonObject { ["case_path"] = "WS-02/EventLogs/Security.csv", ["row"] = 1 }, new JsonObject { ["case_path"] = "WS-02/EventLogs/Security.csv", ["row"] = 5 },
            new JsonObject { ["case_path"] = "WS-02/EventLogs/Security.csv", ["row"] = 7 }, new JsonObject { ["case_path"] = "WS-02/EventLogs/Security.csv", ["row"] = 11 },
            new JsonObject { ["case_path"] = "WS-02/EventLogs/Sysmon.csv", ["row"] = 0 });
        f4["evidence_total"] = 5;
        return o.ToJsonString();
    }

    [Fact]
    public void Evidence_panel_renders_over_the_story_with_Spectre_and_analyst_marks()
    {
        var dir = FindingsViewModelTests.TempDir();
        Directory.CreateDirectory(dir);
        WriteEvidenceSample(dir);
        SpectreCaseLink.Save(dir, new SpectreCaseLink("case1", 5, DateTime.UtcNow));
        var written = new List<string>();
        Exception? failure = null;
        var t = new Thread(() =>
        {
            try
            {
                if (Application.Current == null) NewAppWithoutStartup();
                var ctx = new QueueContext(); SynchronizationContext.SetSynchronizationContext(ctx);
                Motion.ForceOff = true;
                var engine = new EvidencePanelViewModelTests.FlagEngine();   // the row flags below are row edits in the case analysis
                engine.Inner.TimelineReply = SampleTimeline(); engine.Inner.ListReply = SampleListWithEvidence();
                var vm = new FindingsViewModel(() => dir, new SpectreFindingsClient(engine, () => dir, null), _ => Task.FromResult(true));
                var open = vm.OpenCaseAsync(dir);
                Pump(ctx, () => open.IsCompleted && !vm.IsLoadingTimeline);
                vm.Mutate(an =>
                {
                    var i = an.Items.Single(x => x.Source.SpectreFindingId == "F-4");
                    var sec = "WS-02/EventLogs/Security.csv";
                    EzForensics.Core.Analysis.AnalysisEdits.SetRowNote(i, new EzForensics.Core.Analysis.AnalysisRow(sec, 1), true, "Matches the memory image");
                    EzForensics.Core.Analysis.AnalysisEdits.RemoveRow(i, new EzForensics.Core.Analysis.AnalysisRow(sec, 7));
                    EzForensics.Core.Analysis.AnalysisEdits.AddRow(i, new EzForensics.Core.Analysis.AnalysisRow(sec, 3));
                    EzForensics.Core.Analysis.AnalysisEdits.SetRowNote(i, new EzForensics.Core.Analysis.AnalysisRow(sec, 11), null, "Same parent as the service install");
                    return true;
                }).Should().BeTrue();
                foreach (var (theme, width) in new[] { (AppTheme.Light, 1400), (AppTheme.Dark, 1400), (AppTheme.Dark, 900) })
                {
                    ThemeManager.Apply(theme);
                    var view = new FindingsView { DataContext = vm };
                    Pump(ctx, () => false, 100);
                    var show = vm.ShowMomentEvidenceCommand.ExecuteAsync(vm.StoryMoments.Single(m => m.Id == "M-4"));
                    Pump(ctx, () => show.IsCompleted && vm.EvidencePanel.Rows != null && !vm.EvidencePanel.IsLoading && vm.EvidencePanel.Files.All(f => f.IsResolved));
                    vm.EvidencePanel.IsOpen.Should().BeTrue();
                    vm.EvidencePanel.Rows!.Count.Should().Be(5);   // Spectre's 4 rows and the added one
                    Pump(ctx, () => false, 200);
                    written.Add(Render(view, view.OverviewScroll, width, Path.Combine(SnapshotDir(), $"findings-evidence-panel-{theme.ToString().ToLowerInvariant()}-{width}.png"), 900));
                    vm.EvidencePanel.Close();
                    view.DataContext = null;
                }
            }
            catch (Exception ex) { failure = ex; }
            finally
            {
                Motion.ForceOff = false;
                try { ThemeManager.Apply(AppTheme.Dark); } catch (Exception) { }
            }
        });
        t.SetApartmentState(ApartmentState.STA);
        t.Start();
        t.Join();
        try { Directory.Delete(dir, true); } catch (IOException) { }
        if (failure != null) throw new Exception("Evidence panel snapshot failed: " + failure, failure);
        written.Should().HaveCount(3);
        foreach (var p in written) new FileInfo(p).Length.Should().BeGreaterThan(10_000);
    }

    [Fact]
    public void Single_system_case_shows_one_computer_with_the_names_seen()
    {
        var dir = FindingsViewModelTests.TempDir();
        Directory.CreateDirectory(Path.Combine(dir, "PC1", "Persistence"));   // layout v2: one host folder, so one evidence system
        SpectreCaseLink.Save(dir, new SpectreCaseLink("case1", 5, DateTime.UtcNow));
        var path = Path.Combine(SnapshotDir(), "findings-story-light-1400-one-system.png");
        Exception? failure = null;
        var t = new Thread(() =>
        {
            try
            {
                if (Application.Current == null) NewAppWithoutStartup();
                var ctx = new QueueContext(); SynchronizationContext.SetSynchronizationContext(ctx);
                Motion.ForceOff = true;
                ThemeManager.Apply(AppTheme.Light);
                var engine = new FindingsViewModelTests.FakeEngine { TimelineReply = OneSystemTimeline(), ListReply = OneSystemList() };
                var vm = new FindingsViewModel(() => dir, new SpectreFindingsClient(engine, () => dir, null), _ => Task.FromResult(true));
                var open = vm.OpenCaseAsync(dir);
                Pump(ctx, () => open.IsCompleted && !vm.IsLoadingTimeline);
                vm.HostTiles.Should().ContainSingle();
                vm.HostTiles[0].Host.Should().Be("PC1");
                vm.HostTiles[0].Summary.Findings.Should().Be(5);
                vm.HostTiles[0].AliasLine.Should().Be("Names seen in the records: DESKTOP-DEMO01, WIN-DEMO02");
                vm.ShowHostLabel.Should().BeTrue();
                vm.ShowHostPicker.Should().BeFalse();
                vm.SingleHostName.Should().Be("PC1");
                vm.HeadlineText.Should().NotContain("computers");
                var view = new FindingsView { DataContext = vm };
                Pump(ctx, () => false, 100);
                Render(view, view.OverviewScroll, 1400, path);
                view.DataContext = null;
            }
            catch (Exception ex) { failure = ex; }
            finally
            {
                Motion.ForceOff = false;
                try { ThemeManager.Apply(AppTheme.Dark); } catch (Exception) { }
            }
        });
        t.SetApartmentState(ApartmentState.STA);
        t.Start();
        t.Join();
        try { Directory.Delete(dir, true); } catch (IOException) { }
        if (failure != null) throw new Exception("Findings snapshot failed: " + failure, failure);
        new FileInfo(path).Length.Should().BeGreaterThan(10_000);
    }

    [Fact]
    public void Scene_gallery_renders_every_stage_in_light_and_dark()
    {
        var written = new List<string>();
        Exception? failure = null;
        var severities = new[] { "Critical", "High", "Medium", "Low", "Info" };
        var t = new Thread(() =>
        {
            try
            {
                if (Application.Current == null) NewAppWithoutStartup();
                Motion.ForceOff = true;
                foreach (var theme in new[] { AppTheme.Light, AppTheme.Dark })
                {
                    ThemeManager.Apply(theme);
                    var grid = new WrapPanel { Width = 4 * 244 };
                    for (int i = 0; i < StoryGraphics.StageOrder.Count; i++)
                    {
                        var stage = StoryGraphics.StageOrder[i];
                        var sev = severities[i % severities.Length];
                        var cell = new StackPanel { Margin = new Thickness(12) };
                        cell.Children.Add(new StageScene { Stage = stage, Severity = sev });
                        cell.Children.Add(new TextBlock { Text = $"{stage} · {sev}", Margin = new Thickness(2, 6, 0, 0), FontSize = 12 });
                        grid.Children.Add(cell);
                    }
                    var host = new Border { Child = grid, Padding = new Thickness(8) };
                    host.SetResourceReference(Border.BackgroundProperty, "Brush.Window");
                    host.SetResourceReference(TextElement.ForegroundProperty, "Brush.Text");
                    host.Measure(new Size(4 * 244 + 16, double.PositiveInfinity));
                    host.Arrange(new Rect(host.DesiredSize));
                    host.UpdateLayout();
                    var bmp = new RenderTargetBitmap((int)Math.Ceiling(host.ActualWidth), (int)Math.Ceiling(host.ActualHeight), 96, 96, PixelFormats.Pbgra32);
                    bmp.Render(host);
                    var enc = new PngBitmapEncoder();
                    enc.Frames.Add(BitmapFrame.Create(bmp));
                    var path = Path.Combine(SnapshotDir(), $"findings-scenes-{theme.ToString().ToLowerInvariant()}.png");
                    Directory.CreateDirectory(Path.GetDirectoryName(path)!);
                    using (var fs = File.Create(path)) enc.Save(fs);
                    written.Add(path);
                }
            }
            catch (Exception ex) { failure = ex; }
            finally
            {
                Motion.ForceOff = false;
                try { ThemeManager.Apply(AppTheme.Dark); } catch (Exception) { }
            }
        });
        t.SetApartmentState(ApartmentState.STA);
        t.Start();
        t.Join();
        if (failure != null) throw new Exception("Scene gallery failed: " + failure, failure);
        written.Should().HaveCount(2);
        foreach (var p in written) new FileInfo(p).Length.Should().BeGreaterThan(10_000);
    }

    [Fact]
    public void Analysis_states_render_empty_state_duplicate_compare_chips_and_flagged_in_light_and_dark_at_two_widths()
    {
        var dir = FindingsViewModelTests.TempDir();
        Directory.CreateDirectory(Path.Combine(dir, "PC1", "EventLogs"));
        const string csv = "PC1/EventLogs/Security.csv";
        File.WriteAllText(Path.Combine(dir, "PC1", "EventLogs", "Security.csv"),
            "Time,EventId,Message\n2026-01-01 10:00:00,4624,logon alice\n2026-01-01 10:01:00,4688,mimikatz.exe started\n2026-01-01 10:02:00,4624,logon bob\n2026-01-01 10:03:00,4672,special privileges\n");
        var outDir = SnapshotDir();
        var written = new List<string>();
        Exception? failure = null;
        var t = new Thread(() =>
        {
            try
            {
                if (Application.Current == null) NewAppWithoutStartup();
                var ctx = new QueueContext(); SynchronizationContext.SetSynchronizationContext(ctx);
                Motion.ForceOff = true;
                var engine = new FindingsViewModelTests.FakeEngine
                {
                    ListReply = "{\"findings\":[{\"id\":\"F-1\",\"finding_id\":\"F-1\",\"title\":\"Mimikatz\",\"severity\":\"High\",\"mitre_attack\":[\"T1003\"],\"evidence\":[{\"case_path\":\"" + csv + "\",\"row\":1},{\"case_path\":\"" + csv + "\",\"row\":3}]},"
                              + "{\"id\":\"F-2\",\"finding_id\":\"F-2\",\"title\":\"Logon\",\"severity\":\"Low\",\"evidence\":[{\"case_path\":\"" + csv + "\",\"row\":1}]},"
                              + "{\"id\":\"F-3\",\"finding_id\":\"F-3\",\"title\":\"Odd service\",\"severity\":\"Medium\",\"evidence\":[{\"case_path\":\"" + csv + "\",\"row\":2}]}],\"unregistered\":[]}",
                    TimelineReply = "{\"case\":\"case1\",\"time_basis\":\"UTC\",\"findings\":[{\"id\":\"F-1\",\"finding_id\":\"F-1\",\"title\":\"Mimikatz\",\"severity\":\"High\",\"start\":\"2026-01-01T10:01:00Z\"},"
                                  + "{\"id\":\"F-2\",\"finding_id\":\"F-2\",\"title\":\"Logon\",\"severity\":\"Low\",\"start\":\"2026-01-01T10:01:00Z\"},{\"id\":\"F-3\",\"finding_id\":\"F-3\",\"title\":\"Odd service\",\"severity\":\"Medium\",\"start\":\"2026-01-01T10:02:00Z\"}],\"undated\":[],\"clusters\":[],"
                                  + "\"moments\":[{\"id\":\"M-1\",\"rank\":1,\"time\":\"2026-01-01T10:01:00Z\",\"primary_finding_id\":\"F-1\",\"finding_ids\":[\"F-1\"],\"headline\":\"High: Mimikatz\"}]}",
                };
                // 1. the empty state: nothing in the case analysis yet, an analyst key event waiting in Analyze
                var store = EzForensics.Core.Analysis.CaseAnalysisStore.Open(dir);
                store.Data.AddFinding("Credential dumping", "High", new[] { "T1003" }, "my notes", new[] { 1L, 3L }.Select(r => new EzForensics.Core.Analysis.AnalysisRow(csv, r)));
                store.Save();
                var empty = new FindingsViewModel(() => dir, new SpectreFindingsClient(engine, () => dir, null), _ => Task.FromResult(true));
                var open0 = empty.OpenCaseAsync(dir);
                Pump(ctx, () => open0.IsCompleted);
                empty.ShowEmptyState.Should().BeTrue();
                empty.CanUseMyAnalysis.Should().BeTrue();
                foreach (var theme in new[] { AppTheme.Light, AppTheme.Dark })
                    foreach (var width in new[] { 1400, 900 })
                    {
                        ThemeManager.Apply(theme);
                        var view = new FindingsView { DataContext = empty };
                        Pump(ctx, () => false, 100);
                        written.Add(Render(view, view.OverviewScroll, width, Path.Combine(outDir, $"findings-empty-{theme.ToString().ToLowerInvariant()}-{width}.png"), 700));
                        view.DataContext = null;
                    }

                // 2. Spectre imported, then Add my analysis: a duplicate pair, chips and a Flagged section
                SpectreCaseLink.Save(dir, new SpectreCaseLink("case1", 5, DateTime.UtcNow));
                var vm = new FindingsViewModel(() => dir, new SpectreFindingsClient(engine, () => dir, null), _ => Task.FromResult(true));
                var open = vm.OpenCaseAsync(dir);
                Pump(ctx, () => open.IsCompleted);
                var add = vm.AddMyAnalysisCommand.ExecuteAsync(null);
                Pump(ctx, () => add.IsCompleted);
                vm.HasDuplicatePairs.Should().BeTrue();
                vm.HasKeyEvents.Should().BeTrue();
                foreach (var theme in new[] { AppTheme.Light, AppTheme.Dark })
                    foreach (var width in new[] { 1400, 900 })
                    {
                        ThemeManager.Apply(theme);
                        var name = theme.ToString().ToLowerInvariant();
                        var view = new FindingsView { DataContext = vm };
                        vm.CloseCompareCommand.Execute(null);
                        vm.SelectedTab = FindingsTab.Overview;
                        Pump(ctx, () => false, 100);
                        written.Add(Render(view, view.OverviewScroll, width, Path.Combine(outDir, $"findings-flagged-{name}-{width}.png"), 1100));   // key events with the origin and duplicate badges, then Flagged
                        vm.SelectedTab = FindingsTab.Details;
                        vm.Selected = vm.Rows.First(r => !r.IsKeyEvent);
                        Pump(ctx, () => false, 100);
                        written.Add(Render(view, view.OverviewScroll, width, Path.Combine(outDir, $"findings-chips-{name}-{width}.png"), 800));        // the list rows with their chips
                        vm.SelectedTab = FindingsTab.Overview;
                        vm.OpenCompareCommand.Execute(vm.Rows.First(r => r.OriginText == "You"));
                        Pump(ctx, () => false, 100);
                        written.Add(Render(view, view.OverviewScroll, width, Path.Combine(outDir, $"findings-compare-{name}-{width}.png"), 700));      // Keep A / Keep B / Merge / Keep both
                        vm.CloseCompareCommand.Execute(null);
                        view.DataContext = null;
                    }
            }
            catch (Exception ex) { failure = ex; }
            finally
            {
                Motion.ForceOff = false;
                try { ThemeManager.Apply(AppTheme.Dark); } catch (Exception) { }
            }
        });
        t.SetApartmentState(ApartmentState.STA);
        t.Start();
        t.Join();
        try { Directory.Delete(dir, true); } catch (IOException) { }
        if (failure != null) throw new Exception("Analysis states snapshot failed: " + failure, failure);
        written.Should().HaveCount(16);
        foreach (var p in written) new FileInfo(p).Length.Should().BeGreaterThan(8_000);
    }

    [Fact]
    public void OriginText_Both_AfterMerge_and_Review_lists_render_all_severities_in_light_and_dark_at_two_widths()
    {
        var dir = FindingsViewModelTests.TempDir();
        Directory.CreateDirectory(Path.Combine(dir, "PC1", "EventLogs"));
        const string csv = "PC1/EventLogs/Security.csv";
        File.WriteAllText(Path.Combine(dir, "PC1", "EventLogs", "Security.csv"),
            "Time,EventId,Message\n2026-01-01 10:00:00,4624,logon alice\n2026-01-01 10:01:00,4688,mimikatz.exe started\n2026-01-01 10:02:00,4624,logon bob\n2026-01-01 10:03:00,4672,special privileges\n2026-01-01 10:04:00,7045,service installed\n2026-01-01 10:05:00,1102,log cleared\n");
        var outDir = SnapshotDir();
        var written = new List<string>();
        Exception? failure = null;
        var t = new Thread(() =>
        {
            try
            {
                if (Application.Current == null) NewAppWithoutStartup();
                var ctx = new QueueContext(); SynchronizationContext.SetSynchronizationContext(ctx);
                Motion.ForceOff = true;
                string Ev(int row) => "\"evidence\":[{\"case_path\":\"" + csv + "\",\"row\":" + row + "}]";
                var engine = new FindingsViewModelTests.FakeEngine
                {
                    ListReply = "{\"findings\":[{\"id\":\"F-1\",\"finding_id\":\"F-1\",\"title\":\"Mimikatz\",\"severity\":\"High\",\"mitre_attack\":[\"T1003\"],\"evidence\":[{\"case_path\":\"" + csv + "\",\"row\":1},{\"case_path\":\"" + csv + "\",\"row\":3}]},"
                              + "{\"id\":\"F-2\",\"finding_id\":\"F-2\",\"title\":\"Logon\",\"severity\":\"Info\"," + Ev(0) + "},"
                              + "{\"id\":\"F-3\",\"finding_id\":\"F-3\",\"title\":\"Odd service\",\"severity\":\"Medium\"," + Ev(4) + "},"
                              + "{\"id\":\"F-4\",\"finding_id\":\"F-4\",\"title\":\"Privilege use\",\"severity\":\"Low\"," + Ev(3) + "},"
                              + "{\"id\":\"F-5\",\"finding_id\":\"F-5\",\"title\":\"Audit log cleared\",\"severity\":\"Critical\"," + Ev(5) + "}],\"unregistered\":[]}",
                    TimelineReply = "{\"case\":\"case1\",\"time_basis\":\"UTC\",\"findings\":[],\"undated\":[],\"clusters\":[],\"moments\":[]}",
                };
                var store = EzForensics.Core.Analysis.CaseAnalysisStore.Open(dir);
                store.Data.AddFinding("Credential dumping", "High", new[] { "T1003" }, "my notes", new[] { 1L, 3L }.Select(r => new EzForensics.Core.Analysis.AnalysisRow(csv, r)));
                store.Save();
                SpectreCaseLink.Save(dir, new SpectreCaseLink("case1", 5, DateTime.UtcNow));
                var vm = new FindingsViewModel(() => dir, new SpectreFindingsClient(engine, () => dir, null), _ => Task.FromResult(true));
                var open = vm.OpenCaseAsync(dir);
                Pump(ctx, () => open.IsCompleted);
                var add = vm.AddMyAnalysisCommand.ExecuteAsync(null);
                Pump(ctx, () => add.IsCompleted);
                vm.HasDuplicatePairs.Should().BeTrue();
                vm.OpenCompareCommand.Execute(vm.Rows.First(r => r.OriginText == "You"));
                vm.Compare.Should().NotBeNull();
                vm.Compare!.MergeCommand.Execute(null);
                vm.CloseCompareCommand.Execute(null);
                vm.Rows.Select(r => r.OriginText).Should().Contain("Both", "a merge of a You item and a Spectre item reads Both");
                FindingsViewModel.OriginFilters.Should().Contain("Both");
                vm.OriginFilter = "Both";
                vm.Rows.Should().NotBeEmpty().And.OnlyContain(r => r.OriginText == "Both");
                vm.OriginFilter = FindingsViewModel.AnyOrigin;
                foreach (var r in vm.Rows.Where(r => r.Item != null && !r.IsKeyEvent).Take(3).ToList()) vm.AddToTimelineCommand.Execute(r);
                vm.KeyEventRows.Should().NotBeEmpty();
                foreach (var theme in new[] { AppTheme.Light, AppTheme.Dark })
                    foreach (var width in new[] { 1400, 900 })
                    {
                        ThemeManager.Apply(theme);
                        var name = theme.ToString().ToLowerInvariant();
                        var view = new FindingsView { DataContext = vm };
                        vm.SelectedTab = FindingsTab.Details;
                        vm.DetailListTab = FindingsViewModel.ListTabFindings;
                        Pump(ctx, () => false, 100);
                        written.Add(Render(view, view.OverviewScroll, width, Path.Combine(outDir, $"review-findings-list-{name}-{width}.png"), 900));
                        vm.DetailListTab = FindingsViewModel.ListTabKeyEvents;
                        Pump(ctx, () => false, 100);
                        written.Add(Render(view, view.OverviewScroll, width, Path.Combine(outDir, $"review-keyevents-list-{name}-{width}.png"), 900));
                        vm.DetailListTab = FindingsViewModel.ListTabFindings;
                        vm.SelectedTab = FindingsTab.Overview;
                        view.DataContext = null;
                    }
            }
            catch (Exception ex) { failure = ex; }
            finally
            {
                Motion.ForceOff = false;
                try { ThemeManager.Apply(AppTheme.Dark); } catch (Exception) { }
            }
        });
        t.SetApartmentState(ApartmentState.STA);
        t.Start();
        t.Join();
        try { Directory.Delete(dir, true); } catch (IOException) { }
        if (failure != null) throw new Exception("Review lists snapshot failed: " + failure, failure);
        written.Should().HaveCount(8);
        foreach (var p in written) new FileInfo(p).Length.Should().BeGreaterThan(8_000);
    }
}
