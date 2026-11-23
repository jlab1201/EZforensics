using System.Diagnostics;
using System.IO;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using EzForensics.App.ViewModels.Explore;
using EzForensics.App.Views.Explore;
using FluentAssertions;

namespace EzForensics.App.Tests;

/// <summary>The Explore page parses, binds and lays out over a real folder (list, lazy facts, every viewer tab) without binding errors.</summary>
[Collection("WpfApp")]
public sealed class ExploreViewSmokeTests
{
    private sealed class Collector : TraceListener
    {
        public readonly List<string> Lines = new();
        public override void Write(string? message) { }
        public override void WriteLine(string? message) { if (message != null) lock (Lines) Lines.Add(message); }
    }

    // A plain queue instead of the WPF dispatcher: pumping the dispatcher makes WPF shut the Application down when this thread ends, which breaks the other smoke tests.
    private sealed class QueueContext : SynchronizationContext
    {
        private readonly System.Collections.Concurrent.ConcurrentQueue<(SendOrPostCallback, object?)> _q = new();
        public override void Post(SendOrPostCallback d, object? state) => _q.Enqueue((d, state));
        public override void Send(SendOrPostCallback d, object? state) => d(state);
        public void Drain() { while (_q.TryDequeue(out var x)) x.Item1(x.Item2); }
    }

    private static readonly ThreadLocal<QueueContext> Ctx = new();

    private static void Pump(Func<bool> until, int timeoutMs = 15000)
    {
        var sw = Stopwatch.StartNew();
        while (sw.ElapsedMilliseconds < timeoutMs)
        {
            Ctx.Value!.Drain();
            if (until()) return;
            Thread.Sleep(10);
        }
        Ctx.Value!.Drain();
    }



    [Fact]
    public void Explore_view_binds_and_every_tab_fills_for_a_pe_file()
    {
        var dir = Path.Combine(Path.GetTempPath(), "ezf-explore-smoke-" + Guid.NewGuid().ToString("N")[..8]);
        Directory.CreateDirectory(dir);
        File.WriteAllText(Path.Combine(dir, "note.txt"), "hello from the smoke test\nsecond line with a url http://example.test/x\n");
        var pe = Path.Combine(dir, "sample.dll");
        File.Copy(typeof(ExploreViewModel).Assembly.Location, pe);

        Exception? failure = null;
        var listener = new Collector();
        var t = new Thread(() =>
        {
            try
            {
                if (Application.Current == null) new App().InitializeComponent();
                Ctx.Value = new QueueContext(); SynchronizationContext.SetSynchronizationContext(Ctx.Value);
                foreach (var d in Application.Current.Resources.MergedDictionaries) foreach (var k in d.Keys.Cast<object>().ToList()) _ = d[k];   // realize deferred resources (test host has no startup pass)
                PresentationTraceSources.DataBindingSource.Listeners.Add(listener);
                PresentationTraceSources.DataBindingSource.Switch.Level = SourceLevels.Error;
                var vm = new ExploreViewModel();
                var view = new ExploreView { DataContext = vm };
                void Layout() { view.Measure(new Size(1500, 900)); view.Arrange(new Rect(0, 0, 1500, 900)); view.UpdateLayout(); }
                vm.LoadCase(dir);
                Layout();
                Pump(() => vm.Rows.Any(r => r.Name == "sample.dll"));
                Layout();
                vm.Rows.Select(r => r.Name).Should().Contain(new[] { "note.txt", "sample.dll" }, $"path={vm.CurrentPath} err={vm.ListError} rows={string.Join(",", vm.Rows.Select(r => r.Name + ":" + r.Entry.Attributes))} dir={string.Join(",", Directory.GetFiles(dir))}");
                vm.Breadcrumb.Should().NotBeEmpty();

                var row = vm.Rows.First(r => r.Name == "sample.dll");
                vm.SelectedRow = row;
                Pump(() => vm.HasFile && !vm.ViewerBusy && vm.IsPeTab);
                Layout();
                vm.HasFile.Should().BeTrue();
                vm.Sha256.Should().HaveLength(64);
                vm.IsPeTab.Should().BeTrue();
                vm.PeSections.Should().NotBeEmpty();
                vm.PeImports.Should().NotBeEmpty();
                vm.HexPath.Should().Be(pe);

                foreach (var tab in new[] { ExploreViewModel.TabHex, ExploreViewModel.TabStrings, ExploreViewModel.TabMetadata, ExploreViewModel.TabPe, ExploreViewModel.TabOverview })
                {
                    vm.SelectedTab = tab;
                    Pump(() => false, 400);
                    Layout();
                }
                vm.SelectedTab = ExploreViewModel.TabStrings;
                Pump(() => vm.FilteredStrings.Count > 0);
                vm.FilteredStrings.Should().NotBeEmpty();

                var text = vm.Rows.First(r => r.Name == "note.txt");
                vm.SelectedRow = text;
                vm.SelectedTab = ExploreViewModel.TabOverview;
                Pump(() => vm.HasPreview);
                Layout();
                vm.PreviewText.Should().Contain("smoke test");
                vm.IsPeTab.Should().BeFalse();
                vm.SelectedRow = null;
                Layout();
            }
            catch (Exception ex) { failure = ex; }
        });
        t.SetApartmentState(ApartmentState.STA);
        t.Start();
        t.Join();
        try { Directory.Delete(dir, true); } catch (IOException) { }
        if (failure != null) throw new Exception("Explore smoke failed: " + failure, failure);
        listener.Lines.Should().BeEmpty("binding errors: " + string.Join("\n", listener.Lines));
    }

    [Fact]
    public void Advanced_search_window_runs_a_search_shows_hits_and_reveals_the_result_in_Explore()
    {
        var dir = Path.Combine(Path.GetTempPath(), "ezf-explore-search-smoke-" + Guid.NewGuid().ToString("N")[..8]);
        Directory.CreateDirectory(Path.Combine(dir, "sub"));
        File.WriteAllText(Path.Combine(dir, "sub", "found.txt"), "first line\nthe needle is here\nlast line\n");
        File.WriteAllText(Path.Combine(dir, "other.txt"), "nothing to see\n");

        Exception? failure = null;
        var listener = new Collector();
        var t = new Thread(() =>
        {
            try
            {
                if (Application.Current == null) new App().InitializeComponent();
                Ctx.Value = new QueueContext(); SynchronizationContext.SetSynchronizationContext(Ctx.Value);
                foreach (var d in Application.Current.Resources.MergedDictionaries) foreach (var k in d.Keys.Cast<object>().ToList()) _ = d[k];
                PresentationTraceSources.DataBindingSource.Listeners.Add(listener);
                PresentationTraceSources.DataBindingSource.Switch.Level = SourceLevels.Error;
                var vm = new ExploreViewModel();
                var view = new ExploreView { DataContext = vm };
                void Layout() { view.Measure(new Size(1500, 900)); view.Arrange(new Rect(0, 0, 1500, 900)); view.UpdateLayout(); }
                vm.LoadCase(dir);
                Layout();
                Pump(() => vm.Rows.Any(r => r.Name == "other.txt"));

                var win = new ExploreSearchWindow(vm.Search);
                var root = (FrameworkElement)win.Content;
                void WinLayout() { root.Measure(new Size(1100, 740)); root.Arrange(new Rect(0, 0, 1100, 740)); root.UpdateLayout(); }
                vm.Search.OnEnter(vm.CurrentPath);
                WinLayout();
                vm.Search.LookIn.Should().Be(vm.CurrentPath, "Look in starts at the open folder");
                vm.Search.Criteria.ContentText = "needle";
                var run = vm.Search.RunSearchAsync();
                Pump(() => run.IsCompleted);
                run.IsCompletedSuccessfully.Should().BeTrue();
                WinLayout();
                vm.Search.Errors.Should().BeNull();
                vm.Search.Results.Select(r => r.Name).Should().Equal("found.txt");
                vm.Search.Status.Should().Contain("1 found");
                vm.Search.FoundCount.Should().Be(1);
                vm.Search.StatusLeft.Should().Contain("finished");
                vm.Search.RecentFolders.Should().Contain(vm.CurrentPath!);
                vm.Search.IsRunning.Should().BeFalse();

                vm.Search.SelectedResult = vm.Search.Results[0];
                WinLayout();
                vm.Search.Hits.Should().HaveCount(1);
                vm.Search.Hits[0].LineNumber.Should().Be(2);
                vm.Search.Hits[0].Segments.Should().Contain(s => s.IsMatch && s.Text == "needle");

                // bad criteria give an inline error and no search
                vm.Search.Criteria.ContentMode = EzForensics.Core.Explore.Search.ContentMatchMode.Regex;
                vm.Search.Criteria.ContentText = "(";
                var bad = vm.Search.RunSearchAsync();
                Pump(() => bad.IsCompleted);
                vm.Search.Errors.Should().NotBeNullOrEmpty();
                vm.Search.ErrorsOneLine.Should().NotContain("\n");
                WinLayout();
                vm.Search.Criteria.ContentMode = EzForensics.Core.Explore.Search.ContentMatchMode.Literal;
                vm.Search.Criteria.ContentText = "needle";

                // the main page is not touched by selecting a result; revealing lists the folder and selects the file
                vm.CurrentPath.Should().Be(dir);
                var reveal = vm.RevealAsync(vm.Search.Results[0].FullPath, intoFolder: false);
                Pump(() => reveal.IsCompleted && vm.SelectedRow != null);
                Layout();
                vm.CurrentPath.Should().Be(Path.Combine(dir, "sub"));
                vm.SelectedRow!.Name.Should().Be("found.txt");
            }
            catch (Exception ex) { failure = ex; }
        });
        t.SetApartmentState(ApartmentState.STA);
        t.Start();
        t.Join();
        try { Directory.Delete(dir, true); } catch (IOException) { }
        if (failure != null) throw new Exception("Explore search smoke failed: " + failure, failure);
        listener.Lines.Should().BeEmpty("binding errors: " + string.Join("\n", listener.Lines));
    }
}