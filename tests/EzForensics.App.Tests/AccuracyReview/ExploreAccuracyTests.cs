using System.Collections.Concurrent;
using System.Diagnostics;
using System.IO;
using System.Reflection;
using EzForensics.App.ViewModels.Explore;
using FluentAssertions;

namespace EzForensics.App.Tests.AccuracyReview;

public sealed class ExploreAccuracyTests
{
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
            try { var ctx = new QueueContext(); SynchronizationContext.SetSynchronizationContext(ctx); body(ctx); }
            catch (Exception ex) { failure = ex; }
        });
        t.SetApartmentState(ApartmentState.STA);
        t.Start();
        t.Join();
        if (failure != null) throw new Exception("STA test failed", failure);
    }

    private static void Pump(QueueContext ctx, Func<bool> until, int timeoutMs = 120000)
    {
        var sw = Stopwatch.StartNew();
        while (sw.ElapsedMilliseconds < timeoutMs) { ctx.Drain(); if (until()) return; Thread.Sleep(10); }
        ctx.Drain();
    }

    // Item 6: exactly CrawlMaxResults matches is not "capped" (nothing was cut off)
    [Fact]
    public void Item6_Exactly_10000_matches_is_not_reported_as_capped()
    {
        var dir = Path.Combine(Path.GetTempPath(), "ezf-acc6-" + Guid.NewGuid().ToString("N")[..8]);
        Directory.CreateDirectory(dir);
        try
        {
            for (int i = 0; i < ExploreViewModel.CrawlMaxResults; i++) File.WriteAllBytes(Path.Combine(dir, $"m{i}.hit"), Array.Empty<byte>());
            RunSta(ctx =>
            {
                var vm = new ExploreViewModel();
                vm.LoadCase(dir);
                Pump(ctx, () => vm.Rows.Count > 0);
                vm.NameFilter = "*.hit";
                vm.ApplyNameFilter();
                Pump(ctx, () => vm.WaitForCrawlAsync().IsCompleted);
                vm.Rows.Count.Should().Be(ExploreViewModel.CrawlMaxResults);
                vm.ListStatus.Should().NotContain("limit", "all 10,000 matches exist; nothing was cut off");
            });
        }
        finally { try { Directory.Delete(dir, true); } catch (IOException) { } }
    }

    // Item 7a: strings display capped at 20k without saying so
    [Fact]
    public void Item7_Strings_display_cap_is_disclosed_in_the_status()
    {
        RunSta(_ =>
        {
            var vm = new ExploreViewModel();
            var all = Enumerable.Range(0, 30_000).Select(i => new ExtractedString(i, false, "string" + i)).ToList();
            typeof(ExploreViewModel).GetField("_allStrings", BindingFlags.NonPublic | BindingFlags.Instance)!.SetValue(vm, all);
            typeof(ExploreViewModel).GetMethod("ApplyStringsFilter", BindingFlags.NonPublic | BindingFlags.Instance)!.Invoke(vm, new object?[] { "" });
            vm.FilteredStrings.Count.Should().Be(20_000);
            // all 30,000 strings match the (empty) filter; only 20,000 are shown, and the status must say it is a cut
            vm.StringsStatus.Should().MatchRegex("(?i)(cap|limit|first|showing|truncat)");
        });
    }
}
