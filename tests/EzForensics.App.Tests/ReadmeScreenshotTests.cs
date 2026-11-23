using System.Diagnostics;
using System.IO;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Documents;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using EzForensics.App.Controls;
using EzForensics.App.Services;
using EzForensics.App.ViewModels;
using EzForensics.App.ViewModels.Findings;
using EzForensics.App.Views;
using EzForensics.App.Views.Findings;
using EzForensics.Core.Assistant;
using EzForensics.Core.Settings;
using EzForensics.Core.Spectre;
using FluentAssertions;

namespace EzForensics.App.Tests;

/// <summary>
/// Opt-in (EZFORENSICS_README_SHOTS=1): renders the README screenshots offscreen from the bundled demo case (1440x900, 96 dpi, dark; one light copy)
/// to <c>docs/images</c>. Never runs in CI. Uses a temp case root and temp settings, not the user's.
/// </summary>
[Collection("WpfApp")]
[Trait("Category", "Screenshots")]
public sealed class ReadmeScreenshotTests
{
    private const int W = 1440, H = 900;

    private sealed class QueueContext : SynchronizationContext
    {
        private readonly System.Collections.Concurrent.ConcurrentQueue<(SendOrPostCallback, object?)> _q = new();
        public override void Post(SendOrPostCallback d, object? state) => _q.Enqueue((d, state));
        public override void Send(SendOrPostCallback d, object? state) => d(state);
        public void Drain() { while (_q.TryDequeue(out var x)) x.Item1(x.Item2); }
    }

    private static void Pump(QueueContext ctx, Func<bool> until, int timeoutMs = 20000)
    {
        var sw = Stopwatch.StartNew();
        while (sw.ElapsedMilliseconds < timeoutMs) { ctx.Drain(); DoEvents(); if (until()) return; Thread.Sleep(10); }
        ctx.Drain(); DoEvents();
    }

    private static void Settle(QueueContext ctx, int ms = 300) => Pump(ctx, () => false, ms);

    private static void DoEvents()
    {
        var frame = new System.Windows.Threading.DispatcherFrame();
        System.Windows.Threading.Dispatcher.CurrentDispatcher.BeginInvoke(System.Windows.Threading.DispatcherPriority.Background, () => frame.Continue = false);
        System.Windows.Threading.Dispatcher.PushFrame(frame);
    }

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

    /// <summary>A machine with nothing on it: no file, no PATH hit, no process ever started (the setup wizard's system check runs against this).</summary>
    private sealed class NoMachineProbe : EzForensics.Core.Runtimes.IRuntimeProbe
    {
        public bool FileExists(string path) => false;
        public bool DirectoryExists(string path) => false;
        public string? FindOnPath(string fileName) => null;
        public string? Run(string exe, IReadOnlyList<string> args, TimeSpan timeout, IReadOnlyDictionary<string, string>? environment = null) => null;
    }

    private static IEnumerable<Exception> Chain(Exception? ex) { for (; ex != null; ex = ex.InnerException) yield return ex; }

    /// <summary>
    /// The content of a Window's XAML as a UserControl: the test host cannot resolve the app's pack://application:,,,/Assets/... URIs (the window's Icon
    /// throws), and the window itself must never be created. The root tag and the code-behind handlers are dropped, the logo points at the file.
    /// </summary>
    private static FrameworkElement LoadWindowContent(string xamlPath)
    {
        var x = File.ReadAllText(xamlPath).TrimStart('\uFEFF');
        var rootEnd = x.IndexOf('>', x.IndexOf("<Window", StringComparison.Ordinal));
        var root = x[..(rootEnd + 1)];
        var keep = System.Text.RegularExpressions.Regex.Matches(root, "\\s(xmlns(:\\w+)?|Background|Foreground|FontFamily|FontSize|TextOptions\\.TextFormattingMode|UseLayoutRounding)=\"[^\"]*\"")
            .Select(m => m.Value);
        var assets = Path.Combine(RepoRoot(), "src", "EzForensics.App", "Assets");
        x = "<UserControl" + string.Concat(keep) + ">" + x[(rootEnd + 1)..];
        x = x.Replace("<Window.Resources>", "<UserControl.Resources>").Replace("</Window.Resources>", "</UserControl.Resources>").Replace("</Window>", "</UserControl>");
        x = System.Text.RegularExpressions.Regex.Replace(x, "\\sTextChanged=\"[^\"]*\"", "");
        x = System.Text.RegularExpressions.Regex.Replace(x, "clr-namespace:([\\w.]+)\"", "clr-namespace:$1;assembly=" + typeof(App).Assembly.GetName().Name + "\"");
        x = x.Replace("pack://application:,,,/Assets/", new Uri(assets + Path.DirectorySeparatorChar).AbsoluteUri);
        return (FrameworkElement)System.Windows.Markup.XamlReader.Parse(x);
    }

    private static string RepoRoot()
    {
        var d = new DirectoryInfo(AppContext.BaseDirectory);
        while (d != null && !d.GetFiles("*.sln").Any()) d = d.Parent;
        return d?.FullName ?? throw new InvalidOperationException("repository root not found");
    }

    private static string Shot(QueueContext ctx, FrameworkElement view, string file, int w = W, int h = H, Thickness? pad = null)
    {
        var host = new Border { Child = view, Width = w, Height = h, ClipToBounds = true, Padding = pad ?? new Thickness(16, 12, 16, 0) };
        host.SetResourceReference(Border.BackgroundProperty, "Brush.Window");
        host.SetResourceReference(TextElement.ForegroundProperty, "Brush.Text");
        void Layout() { host.Measure(new Size(w, h)); host.Arrange(new Rect(0, 0, w, h)); host.UpdateLayout(); }
        for (int i = 0; i < 3; i++) { Layout(); Settle(ctx, 150); }
        var bmp = new RenderTargetBitmap(w, h, 96, 96, PixelFormats.Pbgra32);
        bmp.Render(host);
        var enc = new PngBitmapEncoder();
        enc.Frames.Add(BitmapFrame.Create(bmp));
        var path = Path.Combine(RepoRoot(), "docs", "images", file);
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        using (var fs = File.Create(path)) enc.Save(fs);
        host.Child = null;
        return path;
    }

    [Fact]
    public void Render_readme_screenshots_from_the_demo_case()
    {
        if (Environment.GetEnvironmentVariable("EZFORENSICS_README_SHOTS") != "1") return;

        var tmp = Path.Combine(Path.GetTempPath(), "ezf-readme-" + Guid.NewGuid().ToString("N")[..8]);
        Directory.CreateDirectory(tmp);
        // the case path shows on Analyze: keep it short and free of the user's profile folder when the drive root is writable
        var caseRoot = Path.Combine(tmp, "Cases");
        try { var c = Path.Combine(Path.GetPathRoot(tmp)!, "Cases-" + Guid.NewGuid().ToString("N")[..6]); Directory.CreateDirectory(c); caseRoot = c; } catch (Exception) { }
        // the Ingest source path is shown too: a short folder on the drive root, never the profile's temp folder
        var evidenceRoot = Path.Combine(tmp, "Evidence");
        try { var e = Path.Combine(Path.GetPathRoot(tmp)!, "Lab-" + Guid.NewGuid().ToString("N")[..6]); Directory.CreateDirectory(e); evidenceRoot = e; } catch (Exception) { }
        var written = new List<string>();
        Exception? failure = null;
        var failures = new List<string>();
        var t = new Thread(() =>
        {
            var prev = AppServices.Current;
            try
            {
                if (Application.Current == null) NewAppWithoutStartup();
                var ctx = new QueueContext(); SynchronizationContext.SetSynchronizationContext(ctx);
                Motion.ForceOff = true;
                var s = new AppServices(new JsonSettingsStore(Path.Combine(tmp, "settings.json")), new AppSettings { CaseRoot = caseRoot });
                AppServices.Current = s;

                var vm = new FindingsViewModel(() => s.LastCaseDir, new SpectreFindingsClient(new FindingsViewModelTests.FakeEngine(), () => s.LastCaseDir, null),
                    _ => Task.FromResult(true), queue: () => null);
                vm.LoadDemoCaseCommand.ExecuteAsync(null);
                Pump(ctx, () => vm.HasAnalysis && vm.EngineChecked && !vm.IsLoadingTimeline && vm.Moments.Count > 0);
                vm.HasAnalysis.Should().BeTrue();
                var caseDir = s.LastCaseDir!;

                // each shot is its own try: one failing page does not stop the others (collected, and the test fails at the end)
                void Try(string name, Action body)
                {
                    try { body(); }
                    catch (Exception ex) { failures.Add(name + ": " + string.Join(" <- ", System.Linq.Enumerable.Select(Chain(ex), e => e.GetType().Name + ": " + e.Message))); }
                }

                Try("review-overview", () =>
                {
                    // Review overview (dark + light)
                    foreach (var theme in new[] { AppTheme.Dark, AppTheme.Light })
                    {
                        ThemeManager.Apply(theme);
                        var view = new FindingsView { DataContext = vm };
                        Settle(ctx);
                        written.Add(Shot(ctx, view, theme == AppTheme.Dark ? "review-overview.png" : "review-overview-light.png"));
                        view.DataContext = null;
                    }
                });

                Try("review-story", () =>
                {
                    // Review story: the whole timeline (every key event, fitted to the width), the slide panel collapsed, then the attack flow
                    ThemeManager.Apply(AppTheme.Dark);
                    var sv = new FindingsView { DataContext = vm };
                    Settle(ctx);
                    vm.PresentCommand.Execute(null);
                    Settle(ctx, 500);
                    var line = (StoryTimeline)sv.FindName("SlideLine");
                    ((FrameworkElement)sv.FindName("SlideCard")).Visibility = Visibility.Collapsed;
                    line.Fit();
                    Settle(ctx, 700);
                    written.Add(Shot(ctx, sv, "review-story.png"));
                    ((FrameworkElement)sv.FindName("SlideCard")).Visibility = Visibility.Visible;
                    // top-of-README shot: timeline with the first slide ("The story so far") showing
                    Settle(ctx, 500);
                    written.Add(Shot(ctx, sv, "story-timeline.png", 1406, 1080));

                    vm.SelectedStoryView = StoryView.AttackFlow;
                    Settle(ctx, 1200);
                    written.Add(Shot(ctx, sv, "review-attack-flow.png"));
                    vm.SelectedStoryView = StoryView.Timeline;

                    // Review story: the Present walk-through at a key event
                    vm.Walkthrough.Next();
                    vm.Walkthrough.Next();
                    Settle(ctx, 500);
                    written.Add(Shot(ctx, sv, "review-present.png"));
                    vm.Walkthrough.Close();
                    sv.DataContext = null;
                });

                // Analyze (one results view shared by the process tree and the grid shot)
                var host = Path.Combine(caseDir, "DEMO-WS01");
                var rvm = new ResultsViewModel();
                var csv = Path.Combine(host, "EventLogs", "EventLogs.csv");
                var rv = new ResultsView { DataContext = rvm };
                Try("process-tree", () =>
                {
                    var open = rvm.OpenCaseFolderAsync(caseDir);
                    Pump(ctx, () => open.IsCompleted);
                    Settle(ctx);
                    var tree = rvm.OpenProcessTreeAsync(host);
                    Pump(ctx, () => tree.IsCompleted);
                    var ptvm = tree.Result;
                    ptvm.Should().NotBeNull();
                    // focus mode (case tree hidden): the shell raises it from the Loaded handler, which an unshown view never runs
                    typeof(ResultsView).GetMethod("ApplyFocus", System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.NonPublic)!.Invoke(rv, new object[] { true });
                    // the tree pane takes the full height (events grid collapsed); svchost selected, then the whole tree instead of the path view
                    ((RowDefinition)rv.FindName("TreeRow")).Height = new GridLength(1, GridUnitType.Star);
                    ((RowDefinition)rv.FindName("TreeSplitterRow")).Height = new GridLength(0);
                    ((RowDefinition)rv.FindName("EventsRow")).Height = new GridLength(0);
                    ptvm!.SelectPid(6104, null, null, null);
                    Settle(ctx, 500);
                    if (ptvm.ShowWholeTreeCommand.CanExecute(null)) ptvm.ShowWholeTreeCommand.Execute(null);
                    Settle(ctx, 500);
                    ptvm.FitCommand.Execute(null);
                    Settle(ctx, 500);
                    written.Add(Shot(ctx, rv, "process-tree.png"));
                    rvm.CloseProcessTree();
                    typeof(ResultsView).GetMethod("ApplyFocus", System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.NonPublic)!.Invoke(rv, new object[] { false });
                    Settle(ctx);
                });

                Try("analyze-grid", () =>
                {
                    // the grid is the main pane: the case tree column is narrowed for the shot (the product's default layout is unchanged)
                    typeof(ResultsView).GetField("_preferredLeft", System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.NonPublic)!.SetValue(rv, 380.0);
                    ((ColumnDefinition)rv.FindName("LeftColumn")).Width = new GridLength(380);
                    var o2 = rvm.OpenCsvAsync(csv);
                    Pump(ctx, () => o2.IsCompleted && rvm.Rows is { Count: > 0 });
                    Settle(ctx, 500);
                    // the time column is wide enough for the whole value
                    var grid = (DataGrid)((EzForensics.App.Views.Analyze.EventsPane)rv.FindName("Events")).FindName("Grid");
                    foreach (var col in grid.Columns.Where(c => c.SortMemberPath == "TimeCreated")) col.Width = new DataGridLength(220);
                    Settle(ctx, 500);
                    written.Add(Shot(ctx, rv, "analyze-grid.png"));
                });
                rv.DataContext = null;

                ThemeManager.Apply(AppTheme.Dark);

                // a temp tools folder with an empty stand-in .exe for every catalog tool (nothing is ever run); Ingest and Tools read it
                string FakeTools()
                {
                    var toolsRoot = Path.Combine(evidenceRoot, "Tools");
                    Directory.CreateDirectory(toolsRoot);
                    foreach (var exe in s.Catalog.Select(d => d.Exe).Where(e => !string.IsNullOrEmpty(e)).Distinct(StringComparer.OrdinalIgnoreCase))
                        File.WriteAllBytes(Path.Combine(toolsRoot, exe), Array.Empty<byte>());
                    s.Settings.ToolsRoot = toolsRoot;
                    s.Locator.Rescan(toolsRoot);
                    return toolsRoot;
                }

                // Ingest: a small fake evidence folder scanned, so the Jobs list shows the planned jobs (the demo case itself is outputs, not evidence)
                Try("ingest", () =>
                {
                    FakeTools();
                    var evidence = Path.Combine(evidenceRoot, "DEMO-WS01");
                    foreach (var d in new[] { @"Windows\System32\config", @"Windows\System32\winevt\Logs", @"Windows\Prefetch", @"Windows\AppCompat\Programs", @"Users\analyst\AppData\Roaming\Microsoft\Windows\Recent" })
                        Directory.CreateDirectory(Path.Combine(evidence, d));
                    foreach (var f in new[] { @"Windows\Prefetch\CMD.EXE-4A81B6E2.pf", @"Windows\Prefetch\POWERSHELL.EXE-920D2FD8.pf", @"Windows\System32\winevt\Logs\Security.evtx",
                        @"Windows\System32\winevt\Logs\System.evtx", @"Windows\System32\config\SYSTEM", @"Windows\System32\config\SOFTWARE", @"Windows\System32\config\SAM",
                        @"Windows\AppCompat\Programs\Amcache.hve", @"Users\analyst\NTUSER.DAT", @"Users\analyst\AppData\Roaming\Microsoft\Windows\Recent\report.lnk", "$MFT" })
                        File.WriteAllText(Path.Combine(evidence, f), "x");
                    var tvm = new TriageViewModel { Source = evidence, CaseName = "DEMO", CaseFolder = caseDir };
                    var scan = tvm.ScanCommand.ExecuteAsync(null);
                    Pump(ctx, () => scan.IsCompleted && tvm.Planned.Count > 0);
                    Settle(ctx, 500);
                    tvm.Planned.Should().NotBeEmpty("the scan planned jobs");
                    Settle(ctx, 1500);   // rows added after the scan (process tree data) are there before ticking
                    foreach (var pj in tvm.Planned) pj.IsSelected = true;   // the default skips the slow hunts (SQLite, persistence): tick every job
                    Settle(ctx, 300);
                    tvm.SelectionSummary.Should().StartWith($"{tvm.Planned.Count} of {tvm.Planned.Count}");
                    var tv = new TriageView { DataContext = tvm };
                    Settle(ctx);
                    written.Add(Shot(ctx, tv, "ingest.png"));
                    tv.DataContext = null;
                });

                // Review: Details tab, a finding selected, its evidence rows
                Try("review-details", () =>
                {
                    var dv = new FindingsView { DataContext = vm };
                    Settle(ctx);
                    vm.SelectedTab = FindingsTab.Details;
                    vm.Selected = vm.Rows[0];
                    vm.SelectedDetailTab = FindingsViewModel.TabEvidence;
                    Pump(ctx, () => !vm.IsLoadingEvidence && vm.DetailEvidence.IsOpen, 8000);
                    Settle(ctx, 600);
                    written.Add(Shot(ctx, dv, "review-details.png"));
                    vm.SelectedTab = FindingsTab.Overview;
                    dv.DataContext = null;
                });

                // Explore: the demo case, a file selected
                Try("explore", () =>
                {
                    var evm = new EzForensics.App.ViewModels.Explore.ExploreViewModel();
                    var ev = new EzForensics.App.Views.Explore.ExploreView { DataContext = evm };
                    evm.LoadCase(caseDir);
                    Pump(ctx, () => evm.Rows.Count > 0);
                    evm.NavigateTo(Path.Combine(host, "EventLogs"));
                    Pump(ctx, () => evm.Rows.Any(r => r.Name == "EventLogs.csv"));
                    evm.SelectedRow = evm.Rows.First(r => r.Name == "EventLogs.csv");
                    Pump(ctx, () => evm.HasFile && !evm.ViewerBusy);
                    evm.SelectedTab = EzForensics.App.ViewModels.Explore.ExploreViewModel.TabOverview;
                    Settle(ctx, 800);
                    written.Add(Shot(ctx, ev, "explore.png"));
                    ev.DataContext = null;
                });

                // Learn: To review (Inbox) with two sample notes in a temp knowledge store
                Try("learn", () =>
                {
                    var notes = Path.Combine(tmp, "notes");
                    Directory.CreateDirectory(notes);
                    var topics = new (string Title, string Text)[]
                    {
                        ("Prefetch", "Prefetch files record program execution evidence for the examiner. They show when a program first ran, when it last ran and how often."),
                        ("ShellBags", "ShellBags record folders the user browsed, even after the folder is deleted. Useful for finding removable media use and network share access."),
                        ("Amcache", "Amcache.hve lists programs that ran or were installed, with the SHA-1 of the file and its first run time. It also keeps entries for deleted programs."),
                        ("Event ID 4688", "Event ID 4688 in the Security log records a new process with its command line, when process creation auditing is on. Compare the parent process with what you expect."),
                        ("Scheduled tasks", "Scheduled tasks are a common persistence method. Check the Task Scheduler operational log and the task XML files for new tasks that run scripts or unknown binaries."),
                    };
                    var paths = new List<string>();
                    foreach (var (title, text) in topics)
                    {
                        var path = Path.Combine(notes, title + ".txt");
                        File.WriteAllText(path, text + " " + string.Concat(Enumerable.Repeat("Read it together with the other artifacts of the same host and place it on the timeline. ", 6)));
                        paths.Add(path);
                    }
                    var lvm = new EzForensics.App.ViewModels.Learn.LearnViewModel(new EzForensics.App.ViewModels.Learn.KnowledgeWorkspace(Path.Combine(tmp, "knowledge.db"), () => null));
                    var lv = new EzForensics.App.Views.Learn.LearnView { DataContext = lvm };
                    lvm.SelectedTab = EzForensics.App.ViewModels.Learn.LearnTab.Inbox;
                    var add = lvm.Inbox.AddPathsAsync(paths);
                    Pump(ctx, () => add.IsCompleted);
                    lvm.Inbox.Rows.Count.Should().Be(topics.Length);
                    lvm.Banner = null;          // the green "Added n for review." bar and the line under the buttons are noise in a screenshot
                    lvm.Inbox.Message = "";
                    Settle(ctx, 600);
                    written.Add(Shot(ctx, lv, "ai.png"));
                    lv.DataContext = null;
                });

                // Tools: a temp tools folder with stand-in .exe files for the whole catalog, so the table shows installed tools (nothing is run)
                Try("tools", () =>
                {
                    var toolsRoot = FakeTools();
                    var tlvm = new ToolsViewModel();
                    var tlv = new ToolsView { DataContext = tlvm };
                    var rescan = s.RescanAsync();   // fills the Tools table from the temp tools folder (OnNavigatedTo would also probe Python and ask GitHub)
                    Pump(ctx, () => rescan.IsCompleted);
                    // plaso / Volatility / Zeek / RITA / Spectre rows describe this machine (profile paths, WSL): only the catalog tools of the temp folder stay
                    foreach (var row in tlvm.Manager.ToolRows.Where(r => r.Kind != ToolRowKind.EzZip).ToList()) tlvm.Manager.ToolRows.Remove(row);
                    Settle(ctx, 600);
                    written.Add(Shot(ctx, tlv, "tools.png", W, 868));
                    tlv.DataContext = null;
                });

                // Setup wizard: the "Choose what to install" step; a fake probe, so the system check starts no process and makes no network call
                Try("setup-wizard", () =>
                {
                    var wvm = new SetupWizardViewModel(s, null, true, null, new NoMachineProbe());
                    Pump(ctx, () => !wvm.IsChecking);
                    wvm.Page = SetupPage.Bundles;
                    var content = LoadWindowContent(Path.Combine(RepoRoot(), "src", "EzForensics.App", "Views", "SetupWizardWindow.xaml"));
                    content.DataContext = wvm;
                    Settle(ctx, 600);
                    written.Add(Shot(ctx, content, "setup-wizard.png", W, H, new Thickness(0)));
                    content.DataContext = null;
                });

                // Spectre: the chat panel, empty state with the starter prompts (no engine, no model calls)
                Try("spectre", () =>
                {
                    var cvm = new AssistantChatViewModel(new AppSettings { AiModel = "qwen3:8b" }, new ApiKeyStore(Path.Combine(tmp, "keys")),
                        () => new System.Net.Http.HttpClient(), () => null, null, null, (_, _) => false, a => a(), (_, _) => Task.FromResult<AssistantExtraTools?>(null));
                    var panel = new EzForensics.App.Views.Assistant.AssistantChatPanel { DataContext = cvm };
                    Settle(ctx);
                    written.Add(Shot(ctx, panel, "spectre.png", 520, 330));
                    panel.DataContext = null;
                });
            }
            catch (Exception ex) { failure = ex; }
            finally
            {
                Motion.ForceOff = false;
                AppServices.Current = prev;
                try { ThemeManager.Apply(AppTheme.Dark); } catch (Exception) { }
            }
        });
        t.SetApartmentState(ApartmentState.STA);
        t.Start();
        t.Join();
        try { Directory.Delete(tmp, true); } catch (IOException) { }
        try { if (caseRoot != Path.Combine(tmp, "Cases")) Directory.Delete(caseRoot, true); } catch (Exception) { }
        try { if (evidenceRoot != Path.Combine(tmp, "Evidence")) Directory.Delete(evidenceRoot, true); } catch (Exception) { }
        if (failure != null) throw new Exception("README screenshots failed: " + failure, failure);
        failures.Should().BeEmpty("these README screenshots failed:\n" + string.Join("\n", failures));
        written.Should().OnlyContain(p => File.Exists(p));
    }
}
