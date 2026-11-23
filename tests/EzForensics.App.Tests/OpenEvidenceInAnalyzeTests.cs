using System.Collections.Concurrent;
using System.Diagnostics;
using System.IO;
using System.Text;
using EzForensics.App.Services;
using EzForensics.App.ViewModels;
using EzForensics.Core.Results;
using EzForensics.Core.Settings;
using EzForensics.Core.Spectre;
using FluentAssertions;

namespace EzForensics.App.Tests;

/// <summary>"Open in Analyze" from Findings filters to the evidence rows without touching the examiner's flags.</summary>
[Collection("WpfApp")]
public sealed class OpenEvidenceInAnalyzeTests : IDisposable
{
    private sealed class QueueContext : SynchronizationContext
    {
        private readonly ConcurrentQueue<(SendOrPostCallback, object?)> _q = new();
        public override void Post(SendOrPostCallback d, object? state) => _q.Enqueue((d, state));
        public override void Send(SendOrPostCallback d, object? state) => d(state);
        public void Drain() { while (_q.TryDequeue(out var x)) x.Item1(x.Item2); }
    }

    private readonly string _dir = Path.Combine(Path.GetTempPath(), "ezf-evrows-" + Guid.NewGuid().ToString("N")[..8]);
    private string Csv => Path.Combine(_dir, "H1", "Evtx", "Events.csv");
    private string Csv2 => Path.Combine(_dir, "H1", "Evtx", "Other.csv");

    public OpenEvidenceInAnalyzeTests()
    {
        Directory.CreateDirectory(Path.GetDirectoryName(Csv)!);
        File.WriteAllText(Csv, "RecordNumber,Message\r\n1,a\r\n2,b\r\n3,c\r\n4,d\r\n5,e\r\n", new UTF8Encoding(false));
        File.WriteAllText(Csv2, "RecordNumber,Message\r\n1,x\r\n2,y\r\n", new UTF8Encoding(false));
    }
    public void Dispose() { try { Directory.Delete(_dir, true); } catch (IOException) { } }

    private static void Pump(QueueContext ctx, Func<bool> until, int timeoutMs = 20000)
    {
        var sw = Stopwatch.StartNew();
        while (sw.ElapsedMilliseconds < timeoutMs) { ctx.Drain(); if (until()) return; Thread.Sleep(10); }
        ctx.Drain();
    }

    private void Run(Action<QueueContext, ResultsViewModel, AppServices> body)
    {
        Exception? failure = null;
        var t = new Thread(() =>
        {
            var prev = AppServices.Current;
            try
            {
                var ctx = new QueueContext();
                SynchronizationContext.SetSynchronizationContext(ctx);
                var s = new AppServices(new JsonSettingsStore(Path.Combine(_dir, "settings.json")), new AppSettings());
                AppServices.Current = s;
                body(ctx, new ResultsViewModel(), s);
            }
            catch (Exception ex) { failure = ex; }
            finally { AppServices.Current = prev; }
        });
        t.SetApartmentState(ApartmentState.STA);
        t.Start();
        t.Join();
        if (failure != null) throw new Exception("STA test failed", failure);
    }

    private static int Shown(ResultsViewModel vm) => vm.Rows == null ? -1 : vm.Rows.Count;

    [Fact]
    public void Open_evidence_filters_to_the_rows_and_leaves_flags_alone()
    {
        Run((ctx, vm, s) =>
        {
            var flagsBefore = s.Settings.Flags.Select(f => f.Id + f.Label + f.Color).ToList();
            var target = new EvidenceTarget(Csv, new long[] { 1, 3 }, Array.Empty<string>());
            var task = vm.ShowEvidenceAsync(_dir, new[] { target }, "Finding");
            Pump(ctx, () => task.IsCompleted && Shown(vm) == 2);
            task.IsCompleted.Should().BeTrue();
            Shown(vm).Should().Be(2);
            vm.HasEvidenceBanner.Should().BeTrue();
            s.Settings.Flags.Select(f => f.Id + f.Label + f.Color).Should().Equal(flagsBefore);
            vm.FlagFilterText.Should().Be("All rows");
            File.Exists(RowFlagStore.SidecarPathFor(Csv)).Should().BeFalse();

            vm.ShowAllEvidenceRowsCommand.Execute(null);
            Pump(ctx, () => Shown(vm) == 5);
            Shown(vm).Should().Be(5);
            File.Exists(RowFlagStore.SidecarPathFor(Csv)).Should().BeFalse();
        });
    }

    [Fact]
    public void Opening_another_file_clears_the_evidence_filter()
    {
        Run((ctx, vm, s) =>
        {
            var task = vm.ShowEvidenceAsync(_dir, new[] { new EvidenceTarget(Csv, new long[] { 0 }, Array.Empty<string>()) }, "Finding");
            Pump(ctx, () => task.IsCompleted && Shown(vm) == 1);
            Shown(vm).Should().Be(1);
            var other = vm.OpenCsvAsync(Csv2);
            Pump(ctx, () => other.IsCompleted && Shown(vm) == 2);
            Shown(vm).Should().Be(2);
            vm.HasAnyFilter.Should().BeFalse();
        });
    }

    [Fact]
    public void Switching_to_another_case_closes_the_file_and_clears_its_filters()
    {
        var newCase = Path.Combine(_dir, "NewCase");
        Directory.CreateDirectory(newCase);
        Run((ctx, vm, s) =>
        {
            var open = vm.OpenCsvAsync(Csv);
            Pump(ctx, () => open.IsCompleted && Shown(vm) == 5);
            vm.GlobalText = "a";
            vm.HasAnyFilter.Should().BeTrue();

            vm.LeaveFileOutside(newCase);
            vm.CurrentFile.Should().BeNull();
            vm.GlobalText.Should().BeEmpty();
            vm.HasAnyFilter.Should().BeFalse();

            // the open file is inside the case: nothing is closed
            var again = vm.OpenCsvAsync(Csv);
            Pump(ctx, () => again.IsCompleted && vm.Rows != null);
            vm.LeaveFileOutside(_dir);
            vm.CurrentFile.Should().Be(Csv);
        });
    }
}
