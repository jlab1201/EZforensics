using System.Collections.Concurrent;
using System.Diagnostics;
using System.IO;
using EzForensics.App.ViewModels.Explore;
using FluentAssertions;

namespace EzForensics.App.Tests;

public sealed class ExploreFilterCrawlTests
{
    // A plain queue instead of the WPF dispatcher (see ExploreViewSmokeTests): the view model's awaits resume when it is drained.
    private sealed class QueueContext : SynchronizationContext
    {
        private readonly ConcurrentQueue<(SendOrPostCallback, object?)> _q = new();
        public override void Post(SendOrPostCallback d, object? state) => _q.Enqueue((d, state));
        public override void Send(SendOrPostCallback d, object? state) => d(state);
        public void Drain() { while (_q.TryDequeue(out var x)) x.Item1(x.Item2); }
    }

    private static void RunSta(Action<QueueContext> body)
    {
        Exception? failure = null;
        var t = new Thread(() =>
        {
            try
            {
                var ctx = new QueueContext();
                SynchronizationContext.SetSynchronizationContext(ctx);
                body(ctx);
            }
            catch (Exception ex) { failure = ex; }
        });
        t.SetApartmentState(ApartmentState.STA);
        t.Start();
        t.Join();
        if (failure != null) throw new Exception("STA test failed", failure);
    }

    private static void Pump(QueueContext ctx, Func<bool> until, int timeoutMs = 15000)
    {
        var sw = Stopwatch.StartNew();
        while (sw.ElapsedMilliseconds < timeoutMs)
        {
            ctx.Drain();
            if (until()) return;
            Thread.Sleep(10);
        }
        ctx.Drain();
    }

    private static string MakeTree()
    {
        var dir = Path.Combine(Path.GetTempPath(), "ezf-crawl-" + Guid.NewGuid().ToString("N")[..8]);
        Directory.CreateDirectory(Path.Combine(dir, "sub", "deep"));
        File.WriteAllText(Path.Combine(dir, "top.txt"), "x");
        File.WriteAllText(Path.Combine(dir, "sub", "tool.exe"), "x");
        File.WriteAllText(Path.Combine(dir, "sub", "deep", "lib.dll"), "x");
        File.WriteAllText(Path.Combine(dir, "sub", "deep", "Tool2.EXE"), "x");
        return dir;
    }

    [Fact]
    public void Filter_streams_matches_from_subfolders_and_empty_filter_restores_the_listing()
    {
        var dir = MakeTree();
        try
        {
            RunSta(ctx =>
            {
                var vm = new ExploreViewModel();
                vm.LoadCase(dir);
                Pump(ctx, () => vm.Rows.Any(r => r.Name == "top.txt"));
                vm.Rows.Select(r => r.Name).Should().Contain(new[] { "top.txt", "sub" });

                vm.NameFilter = "*.exe";
                vm.ApplyNameFilter();
                Pump(ctx, () => vm.WaitForCrawlAsync().IsCompleted);
                vm.IsFiltering.Should().BeTrue();
                vm.Rows.Select(r => r.Name).Should().BeEquivalentTo("tool.exe", "Tool2.EXE");
                vm.Rows.Select(r => r.ParentPath).Should().Contain(Path.Combine(dir, "sub", "deep"));
                vm.ListStatus.Should().Be($"2 matches in {Path.GetFileName(dir)}");

                vm.NameFilter = "*.exe;*.dll";
                vm.ApplyNameFilter();
                Pump(ctx, () => vm.WaitForCrawlAsync().IsCompleted);
                vm.Rows.Select(r => r.Name).Should().BeEquivalentTo("tool.exe", "Tool2.EXE", "lib.dll");

                vm.ClearNameFilter();
                Pump(ctx, () => !vm.IsFiltering && vm.Rows.Any(r => r.Name == "top.txt"));
                vm.IsFiltering.Should().BeFalse();
                vm.Rows.Select(r => r.Name).Should().BeEquivalentTo("top.txt", "sub");
            });
        }
        finally { try { Directory.Delete(dir, true); } catch (IOException) { } }
    }

    [Fact]
    public void Changing_the_filter_cancels_the_previous_crawl()
    {
        var dir = MakeTree();
        try
        {
            RunSta(ctx =>
            {
                var vm = new ExploreViewModel();
                vm.LoadCase(dir);
                Pump(ctx, () => vm.Rows.Any(r => r.Name == "top.txt"));

                vm.NameFilter = "*.exe";
                vm.ApplyNameFilter();
                var first = vm.WaitForCrawlAsync();
                vm.NameFilter = "*.dll";
                vm.ApplyNameFilter();   // before the first crawl delivered anything
                Pump(ctx, () => first.IsCompleted && vm.WaitForCrawlAsync().IsCompleted);

                vm.Rows.Select(r => r.Name).Should().BeEquivalentTo("lib.dll");
                vm.ListStatus.Should().StartWith("1 match in ");
            });
        }
        finally { try { Directory.Delete(dir, true); } catch (IOException) { } }
    }

    private static void RunCapCase(int files, bool expectCapped)
    {
        var dir = Path.Combine(Path.GetTempPath(), "ezf-cap-" + Guid.NewGuid().ToString("N")[..8]);
        Directory.CreateDirectory(dir);
        try
        {
            for (int i = 0; i < files; i++) File.WriteAllBytes(Path.Combine(dir, $"m{i}.hit"), Array.Empty<byte>());
            RunSta(ctx =>
            {
                var vm = new ExploreViewModel();
                vm.LoadCase(dir);
                Pump(ctx, () => vm.Rows.Count > 0);
                vm.NameFilter = "*.hit";
                vm.ApplyNameFilter();
                Pump(ctx, () => vm.WaitForCrawlAsync().IsCompleted, 120000);
                vm.Rows.Count.Should().Be(ExploreViewModel.CrawlMaxResults);
                if (expectCapped) vm.ListStatus.Should().Contain("limit");
                else vm.ListStatus.Should().NotContain("limit");
            });
        }
        finally { try { Directory.Delete(dir, true); } catch (IOException) { } }
    }

    [Fact]
    public void Exactly_the_limit_of_matches_is_not_reported_as_capped() => RunCapCase(ExploreViewModel.CrawlMaxResults, expectCapped: false);

    [Fact]
    public void More_matches_than_the_limit_is_reported_as_capped() => RunCapCase(ExploreViewModel.CrawlMaxResults + 5, expectCapped: true);

    [Fact]
    public void Strings_status_separates_matching_count_display_limit_and_total()
    {
        RunSta(_ =>
        {
            var vm = new ExploreViewModel();
            var all = Enumerable.Range(0, 50_000).Select(i => new ExtractedString(i, false, (i % 5 < 3 ? "match" : "other") + i)).ToList();
            typeof(ExploreViewModel).GetField("_allStrings", System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Instance)!.SetValue(vm, all);
            var apply = typeof(ExploreViewModel).GetMethod("ApplyStringsFilter", System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Instance)!;
            vm.StringsFilter = "match";
            apply.Invoke(vm, new object?[] { "" });
            vm.FilteredStrings.Count.Should().Be(20_000);
            vm.StringsStatus.Should().Contain("20,000 shown of 30,000 matching (display limit)").And.Contain("50,000 total");
            vm.StringsFilter = "match1";
            apply.Invoke(vm, new object?[] { "" });
            vm.StringsStatus.Should().NotContain("display limit");
        });
    }
}
