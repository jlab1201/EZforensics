using EzForensics.Core.Case;
using EzForensics.Core.Execution;
using EzForensics.Core.Rita;
using EzForensics.Core.Zeek;
using FluentAssertions;
using Xunit;

namespace EzForensics.Core.Tests;

/// <summary>RITA jobs end to end with the fake zeek + fake rita (RitaEngine.Direct) through the real JobQueue.</summary>
public class RitaJobsTests
{
    private static readonly byte[] PcapLe = { 0xd4, 0xc3, 0xb2, 0xa1, 2, 0, 4, 0 };

    private static string Capture(string dir, string name)
    {
        Directory.CreateDirectory(dir);
        var p = Path.Combine(dir, name);
        var b = new byte[64];
        PcapLe.CopyTo(b, 0);
        File.WriteAllBytes(p, b);
        return p;
    }

    private static ZeekInstall FakeZeek() =>
        new(ZeekEngine.Native, ZeekOrigin.Configured, FakeToolPaths.Exe, FakeToolPaths.Exe, "9.0.0", FakeToolPaths.Dir, true);

    private static RitaInstall FakeRita() => new(RitaEngine.Direct, FakeToolPaths.Exe, "5.1.2", "24.1.6.52");

    private static JobQueue Queue(int parallel = 3) => new(new FakeLocator(null), parallel, TimeSpan.FromMinutes(2));

    private static async Task<Job> Finish(JobQueue q, Job job)
    {
        await q.WhenAllAsync();
        await job.Recorded;
        return job;
    }

    private static List<string[]> Rows(string csv) => RitaResultConverter.ReadRecords(File.ReadAllText(csv)).Where(r => r.Length > 1).ToList();

    [Fact]
    public async Task Host_and_case_wide_jobs_wait_for_zeek_and_publish_rita_csvs()
    {
        using var t = new TempDir();
        var caseDir = Path.Combine(t.Path, "Case1");
        Directory.CreateDirectory(caseDir);
        CaseLayout.EnsureMarker(caseDir);
        var pcaps = new[] { Capture(Path.Combine(t.Path, "src"), "a.pcap"), Capture(Path.Combine(t.Path, "src"), "b.pcap") };
        var q = Queue();
        var zeek = ZeekJobs.Enqueue(q, FakeZeek(), pcaps, caseDir);
        var host = q.Enqueue(RitaJobs.CreateSpec(FakeRita(), new RitaJobRequest { CaseDir = caseDir, Host = "Unknown", ZeekJobs = zeek }), caseDir);
        var wide = q.Enqueue(RitaJobs.CreateSpec(FakeRita(), new RitaJobRequest { CaseDir = caseDir, ZeekJobs = zeek }), caseDir);
        await Finish(q, host);
        await wide.Recorded;

        zeek.Should().OnlyContain(z => z.State == JobState.Succeeded);
        host.State.Should().Be(JobState.Succeeded, host.StatusReason);
        wide.State.Should().Be(JobState.Succeeded, wide.StatusReason);
        host.StartUtc.Should().BeOnOrAfter(zeek.Max(z => z.EndUtc!.Value), "the RITA process starts after Zeek");

        var hostDir = Path.Combine(caseDir, "Unknown", "Network");
        var wideDir = Path.Combine(caseDir, "_Case", "Network");
        foreach (var dir in new[] { hostDir, wideDir })
            foreach (var f in RitaResultConverter.OutputFiles) File.Exists(Path.Combine(dir, f)).Should().BeTrue(f);
        host.PublishedOutputs.Select(p => (p.FinalName, p.ArtifactId)).Should().BeEquivalentTo(new[]
        {
            ("Rita_Threats.csv", "rita-threats"), ("Rita_Beacons.csv", "rita-beacons"),
            ("Rita_LongConnections.csv", "rita-long-connections"), ("Rita_DnsTunneling.csv", "rita-dns-tunneling"),
        });
        Directory.Exists(Path.Combine(caseDir, OutputPlan.StagingFolderName)).Should().BeFalse("work files and staging folders are cleaned up");
        Directory.GetFiles(caseDir, "*.tmp", SearchOption.AllDirectories).Should().BeEmpty();

        var rows = Rows(Path.Combine(hostDir, "Rita_Threats.csv"));
        var h = rows[0].ToList();
        var beacon = rows.Single(r => r[h.IndexOf("Src")] == "192.168.1.103" && r[h.IndexOf("Dst")] == "192.150.187.43");
        beacon[h.IndexOf("FirstSeenUtc")].Should().Be("2009-11-18 09:14:13.0000000");
        beacon[h.IndexOf("DateTimeUtc")].Should().Be(beacon[h.IndexOf("FirstSeenUtc")]);
        beacon[h.IndexOf("Captures")].Should().Be("a.pcap; b.pcap");
        rows.Should().Contain(r => r[h.IndexOf("Fqdn")] == "bro.org");
        rows.Should().NotContain(r => r[h.IndexOf("Dst")] == "192.168.1.1", "internal to internal is filtered");
        Rows(Path.Combine(hostDir, "Rita_DnsTunneling.csv")).Skip(1).Should().ContainSingle();

        var d = host.Details;
        d["rita.version"].Should().Be("5.1.2");
        d["clickhouse.version"].Should().Be("24.1.6.52");
        d["rita.db"].Should().Be(RitaCommandBuilder.DatabaseName(caseDir, "Unknown"));
        d["rita.captures"].Should().Be("a.pcap | b.pcap");
        d["rita.scope"].Should().Be("host");
        d["rita.dataSpanHours"].Should().Be("0");
        d["rita.findings"].Should().Be("threats=2,beacons=1,longConnections=0,dnsTunneling=1");
        wide.Details["rita.scope"].Should().Be("case");
        wide.Details["rita.db"].Should().EndWith(RitaCommandBuilder.DatabaseName(caseDir, null)[^6..]);

        // The case index sees them as RITA outputs of the right hosts.
        var manifest = q.GetManifestPath(caseDir)!;
        File.ReadAllText(manifest).Should().Contain("rita.dataSpanHours");
    }

    [Fact]
    public async Task A_failed_zeek_job_is_skipped_and_noted()
    {
        using var t = new TempDir();
        var caseDir = Path.Combine(t.Path, "legacy");   // no case.json: layout v1 output folders
        Directory.CreateDirectory(caseDir);
        var q = Queue();
        var good = q.Enqueue(ZeekJobs.CreateSpec(FakeZeek(), Capture(t.Path, "good.pcap"), Path.Combine(caseDir, "Network", "Zeek", "good")), caseDir);
        var bad = q.Enqueue(ZeekJobs.CreateSpec(FakeZeek(), Path.Combine(t.Path, "missing.pcap"), Path.Combine(caseDir, "Network", "Zeek", "missing")), caseDir);
        var rita = q.Enqueue(RitaJobs.CreateSpec(FakeRita(), new RitaJobRequest { CaseDir = caseDir, Host = "WS1", ZeekJobs = new[] { good, bad } }), caseDir);
        await Finish(q, rita);

        bad.State.Should().Be(JobState.Failed);
        rita.State.Should().Be(JobState.Succeeded, rita.StatusReason);
        rita.Details["rita.captures"].Should().Be("good.pcap");
        rita.Details["rita.capturesSkipped"].Should().Contain("missing.pcap");
        var outDir = Path.Combine(caseDir, "Network", "Rita", "WS1");
        File.Exists(Path.Combine(outDir, "Rita_Threats.csv")).Should().BeTrue();
        Directory.GetFiles(outDir, "*.tmp").Should().BeEmpty();
        File.Exists(Path.Combine(caseDir, "case.json")).Should().BeFalse("a layout v1 case stays v1");
    }

    [Fact]
    public async Task Fails_clearly_when_no_zeek_job_succeeded()
    {
        using var t = new TempDir();
        var q = Queue();
        var bad = q.Enqueue(ZeekJobs.CreateSpec(FakeZeek(), Path.Combine(t.Path, "missing.pcap"), Path.Combine(t.Path, "z")), t.Path);
        var rita = q.Enqueue(RitaJobs.CreateSpec(FakeRita(), new RitaJobRequest { CaseDir = t.Path, Host = "H", ZeekJobs = new[] { bad } }), t.Path);
        await Finish(q, rita);
        rita.State.Should().Be(JobState.Failed);
        rita.StatusReason.Should().Contain("none of the 1 Zeek job(s)");
    }

    [Fact]
    public async Task Existing_logs_need_no_zeek_job_and_no_data_gives_header_only_files()
    {
        using var t = new TempDir();
        var caseDir = Path.Combine(t.Path, "c");
        Directory.CreateDirectory(caseDir);
        CaseLayout.EnsureMarker(caseDir);
        // layout v2 Zeek names, as triage publishes them
        var net = Path.Combine(caseDir, "HOST1", "Network");
        Directory.CreateDirectory(net);
        var q0 = Queue();
        var z = await Finish(q0, q0.Enqueue(ZeekJobs.CreateSpec(FakeZeek(), Capture(t.Path, "x.pcap"), Path.Combine(t.Path, "zout")), t.Path));
        File.Copy(Path.Combine(t.Path, "zout", "conn.log"), Path.Combine(net, "Zeek_Conn_x.log"));
        File.Copy(Path.Combine(t.Path, "zout", "dns.log"), Path.Combine(net, "Zeek_Dns_x.log"));

        var q = Queue();
        var spec = RitaJobs.CreateSpec(FakeRita(), new RitaJobRequest
        {
            CaseDir = caseDir, Host = "HOST1",
            Captures = new[] { new RitaCaptureLogs("x.pcap", Directory.GetFiles(net, "Zeek_*.log")) },
        }) with { Environment = new Dictionary<string, string> { ["FAKE_RITA_NODATA"] = "1" } };
        var job = await Finish(q, q.Enqueue(spec, caseDir));
        job.State.Should().Be(JobState.Succeeded, job.StatusReason);
        job.Details["rita.note"].Should().Contain("no data");
        foreach (var f in RitaResultConverter.OutputFiles)
            File.ReadAllLines(Path.Combine(net, f)).Should().ContainSingle();
    }

    [Fact]
    public async Task Waiting_job_shows_its_reason_and_cancels_cleanly()
    {
        using var t = new TempDir();
        var q = Queue();
        var zspec = ZeekJobs.CreateSpec(FakeZeek(), Capture(t.Path, "h.pcap"), Path.Combine(t.Path, "z"))
            with { Environment = new Dictionary<string, string> { ["FAKE_ZEEK_HANG"] = "1" } };
        var zeek = q.Enqueue(zspec, t.Path);
        var rita = q.Enqueue(RitaJobs.CreateSpec(FakeRita(), new RitaJobRequest { CaseDir = t.Path, Host = "H", ZeekJobs = new[] { zeek } }), t.Path);
        var deadline = DateTime.UtcNow.AddSeconds(20);
        while (rita.StatusReason?.StartsWith("Waiting for 1 of 1 Zeek job", StringComparison.Ordinal) != true && DateTime.UtcNow < deadline) await Task.Delay(50);
        rita.StatusReason.Should().StartWith("Waiting for 1 of 1 Zeek job");
        rita.State.Should().Be(JobState.Queued, "it waits outside a queue slot");
        rita.Cancel();
        zeek.Cancel();
        await q.WhenAllAsync();
        rita.State.Should().Be(JobState.Cancelled);
    }

    [Fact]
    public async Task Cancelling_a_running_analysis_removes_the_work_files()
    {
        using var t = new TempDir();
        var q = Queue();
        var zeek = await Finish(q, q.Enqueue(ZeekJobs.CreateSpec(FakeZeek(), Capture(t.Path, "c.pcap"), Path.Combine(t.Path, "z")), t.Path));
        var spec = RitaJobs.CreateSpec(FakeRita(), new RitaJobRequest { CaseDir = t.Path, Host = "H", ZeekJobs = new[] { zeek } })
            with { Environment = new Dictionary<string, string> { ["FAKE_RITA_HANG"] = "1" } };
        var job = q.Enqueue(spec, t.Path);
        var deadline = DateTime.UtcNow.AddSeconds(20);
        while (job.State != JobState.Running && DateTime.UtcNow < deadline) await Task.Delay(50);
        await Task.Delay(300);
        job.Cancel();
        await Finish(q, job);
        job.State.Should().Be(JobState.Cancelled);
        var outDir = RitaJobs.OutputDirFor(t.Path, "H");
        (Directory.Exists(outDir) ? Directory.GetFiles(outDir, "*", SearchOption.AllDirectories) : Array.Empty<string>()).Should().BeEmpty();
    }

    private static async Task Until(Func<bool> condition, int seconds = 20)
    {
        var deadline = DateTime.UtcNow.AddSeconds(seconds);
        while (!condition() && DateTime.UtcNow < deadline) await Task.Delay(50);
    }

    private static readonly IReadOnlyDictionary<string, string> Hang = new Dictionary<string, string> { ["FAKE_RITA_HANG"] = "1" };

    [Fact]
    public async Task Analyses_run_one_at_a_time_and_the_waiting_one_holds_no_slot_and_no_timeout()
    {
        using var t = new TempDir();
        var q = Queue(parallel: 2);
        var zeek = await Finish(q, q.Enqueue(ZeekJobs.CreateSpec(FakeZeek(), Capture(t.Path, "c.pcap"), Path.Combine(t.Path, "z")), t.Path));
        var first = q.Enqueue(RitaJobs.CreateSpec(FakeRita(), new RitaJobRequest { CaseDir = t.Path, Host = "A", ZeekJobs = new[] { zeek } })
            with { Environment = Hang }, t.Path);
        await Until(() => first.State == JobState.Running);
        first.State.Should().Be(JobState.Running);

        // Short process timeout: it must not run while the job only waits for the other analysis.
        var second = q.Enqueue(RitaJobs.CreateSpec(FakeRita(), new RitaJobRequest { CaseDir = t.Path, Host = "B", ZeekJobs = new[] { zeek } })
            with { Timeout = TimeSpan.FromSeconds(30) }, t.Path);
        await Until(() => second.StatusReason == RitaJobs.WaitingForAnalysisReason);
        second.StatusReason.Should().Be(RitaJobs.WaitingForAnalysisReason);
        second.State.Should().Be(JobState.Queued, "it waits outside a queue slot");
        second.StartUtc.Should().BeNull();

        // A third job can still use the second slot meanwhile.
        var other = q.Enqueue(ZeekJobs.CreateSpec(FakeZeek(), Capture(t.Path, "d.pcap"), Path.Combine(t.Path, "z2")), t.Path);
        await other.Recorded.WaitAsync(TimeSpan.FromSeconds(30));
        other.State.Should().Be(JobState.Succeeded);

        first.Cancel();
        await first.Recorded;
        await second.Recorded;
        first.State.Should().Be(JobState.Cancelled);
        second.State.Should().Be(JobState.Succeeded, second.StatusReason);
        second.StartUtc.Should().BeOnOrAfter(first.EndUtc!.Value);
        RitaJobs.AnalysesRunning.Should().Be(0);
    }

    [Fact]
    public async Task A_second_job_of_the_same_scope_never_deletes_the_running_jobs_work_folder()
    {
        using var t = new TempDir();
        var caseDir = Path.Combine(t.Path, "case");
        Directory.CreateDirectory(caseDir);
        CaseLayout.EnsureMarker(caseDir);
        var q = Queue();
        var zeek = await Finish(q, q.Enqueue(ZeekJobs.CreateSpec(FakeZeek(), Capture(t.Path, "c.pcap"), Path.Combine(t.Path, "z")), t.Path));
        var running = q.Enqueue(RitaJobs.CreateSpec(FakeRita(), new RitaJobRequest { CaseDir = caseDir, Host = "H", ZeekJobs = new[] { zeek } })
            with { Environment = Hang }, caseDir);
        await Until(() => running.State == JobState.Running);
        var inputs = Path.Combine(RitaJobs.WorkDirFor(caseDir, "H"), RitaCommandBuilder.InputsFile);
        File.Exists(inputs).Should().BeTrue();

        var second = q.Enqueue(RitaJobs.CreateSpec(FakeRita(), new RitaJobRequest { CaseDir = caseDir, Host = "H", ZeekJobs = new[] { zeek } }), caseDir);
        await second.Recorded.WaitAsync(TimeSpan.FromSeconds(30));
        second.State.Should().Be(JobState.Failed, "the staging folder is in use");
        File.Exists(inputs).Should().BeTrue("the running job's work folder is left alone");

        running.Cancel();
        await running.Recorded;
        File.Exists(inputs).Should().BeFalse("the job that owned it cleans it up");
    }

    [Fact]
    public async Task A_failing_import_fails_the_job()
    {
        using var t = new TempDir();
        var q = Queue();
        var zeek = await Finish(q, q.Enqueue(ZeekJobs.CreateSpec(FakeZeek(), Capture(t.Path, "c.pcap"), Path.Combine(t.Path, "z")), t.Path));
        var spec = RitaJobs.CreateSpec(FakeRita(), new RitaJobRequest { CaseDir = t.Path, Host = "H", ZeekJobs = new[] { zeek } })
            with { Environment = new Dictionary<string, string> { ["FAKE_RITA_EXIT"] = "71" } };
        var job = await Finish(q, q.Enqueue(spec, t.Path));
        job.State.Should().Be(JobState.Failed);
        Directory.GetFiles(t.Path, "*.tmp", SearchOption.AllDirectories).Should().BeEmpty();
    }

    [Fact]
    public void Spec_shape()
    {
        using var t = new TempDir();
        CaseLayout.EnsureMarker(t.Path);
        var spec = RitaJobs.CreateSpec(FakeRita(), new RitaJobRequest { CaseDir = t.Path, Host = "WS 01", InputBytesHint = 2L << 30 });
        spec.ToolId.Should().Be("Rita");
        spec.Category.Should().Be("Network");
        spec.PrepareOutsideSlot.Should().BeTrue();
        spec.PreProcess.Should().NotBeNull();
        spec.OutputDir.Should().Be(Path.Combine(Path.GetFullPath(t.Path), "WS 01", "Network"));
        spec.Output!.ArtifactName.Should().Be("Rita");
        spec.Output.StagingDir.Should().Contain(OutputPlan.StagingFolderName);
        spec.Timeout.Should().Be(TimeSpan.FromMinutes(20 + 60 + 15));
        spec.Provenance!.RuleId.Should().Be("rita");
        spec.Provenance.Host.Should().Be("WS 01");
        RitaJobs.TimeoutFor(1L << 30).Should().Be(TimeSpan.FromMinutes(50));
        RitaJobs.OutputDirFor(t.Path, null).Should().EndWith(Path.Combine("_Case", "Network"));
        FluentActions.Invoking(() => RitaJobs.CreateSpec(FakeRita() with { Engine = RitaEngine.Docker }, new RitaJobRequest { CaseDir = t.Path }))
            .Should().Throw<NotSupportedException>();
    }
}
