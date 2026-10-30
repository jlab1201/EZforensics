using System.Text.Json;
using EzForensics.Core.Execution;
using EzForensics.Core.Network;
using EzForensics.Core.Triage;
using EzForensics.Core.Zeek;
using FluentAssertions;

namespace EzForensics.Core.Tests;

public class ZeekNetworkTests
{
    private static readonly byte[] PcapLe = { 0xd4, 0xc3, 0xb2, 0xa1, 2, 0, 4, 0 };
    private static readonly byte[] PcapBe = { 0xa1, 0xb2, 0xc3, 0xd4, 0, 2, 0, 4 };
    private static readonly byte[] PcapNsLe = { 0x4d, 0x3c, 0xb2, 0xa1, 2, 0, 4, 0 };
    private static readonly byte[] PcapNsBe = { 0xa1, 0xb2, 0x3c, 0x4d, 0, 2, 0, 4 };
    private static readonly byte[] PcapNg = { 0x0a, 0x0d, 0x0d, 0x0a, 0x1c, 0, 0, 0, 0x4d, 0x3c, 0x2b, 0x1a };

    private static void WriteCapture(string path, byte[] magic, int size = 64)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        var b = new byte[size];
        magic.CopyTo(b, 0);
        File.WriteAllBytes(path, b);
    }

    // ------------------------------------------------------------------ magic numbers

    [Fact]
    public void Magic_numbers_of_every_variant()
    {
        PcapDetector.FromMagic(PcapLe).Should().Be(PcapFormat.Pcap);
        PcapDetector.FromMagic(PcapBe).Should().Be(PcapFormat.Pcap);
        PcapDetector.FromMagic(PcapNsLe).Should().Be(PcapFormat.PcapNanosecond);
        PcapDetector.FromMagic(PcapNsBe).Should().Be(PcapFormat.PcapNanosecond);
        PcapDetector.FromMagic(PcapNg).Should().Be(PcapFormat.PcapNg);
        // big-endian pcapng byte-order magic
        PcapDetector.FromMagic(new byte[] { 0x0a, 0x0d, 0x0d, 0x0a, 0, 0, 0, 0x1c, 0x1a, 0x2b, 0x3c, 0x4d }).Should().Be(PcapFormat.PcapNg);
        // SHB type without the byte-order magic is not pcapng
        PcapDetector.FromMagic(new byte[] { 0x0a, 0x0d, 0x0d, 0x0a, 0, 0, 0, 0, 1, 2, 3, 4 }).Should().BeNull();
        PcapDetector.FromMagic("MDMP"u8.ToArray()).Should().BeNull();
        PcapDetector.FromMagic(new byte[] { 0xd4, 0xc3 }).Should().BeNull();
    }

    [Theory]
    [InlineData("a.pcap", true)]
    [InlineData("a.PCAPNG", true)]
    [InlineData("a.cap", true)]
    [InlineData("memory.dmp", true)]
    [InlineData("trace.pcap0", true)]
    [InlineData("trace.pcap12", true)]
    [InlineData("trace.pcap.1", true)]
    [InlineData("trace.pcapng.3", true)]
    [InlineData("tcpdump_out", true)]
    [InlineData("rotated.7", true)]
    [InlineData("report.txt", false)]
    [InlineData("image.E01", false)]
    [InlineData("NTUSER.DAT", false)]
    public void Candidate_names(string name, bool expected) => PcapDetector.IsCandidateName(name).Should().Be(expected);

    [Fact]
    public void Probe_requires_the_magic_and_a_full_header()
    {
        using var t = new TempDir();
        var good = Path.Combine(t.Path, "x.dmp");
        WriteCapture(good, PcapLe);
        PcapDetector.Probe(good).Should().Be(PcapFormat.Pcap);
        var tiny = Path.Combine(t.Path, "tiny.pcap");
        File.WriteAllBytes(tiny, PcapLe); // 8 bytes: shorter than a pcap global header
        PcapDetector.Probe(tiny).Should().BeNull();
        var text = Path.Combine(t.Path, "notes.pcap");
        File.WriteAllText(text, "this is not a packet capture at all, just text");
        PcapDetector.Probe(text).Should().BeNull();
        PcapDetector.Probe(Path.Combine(t.Path, "missing.pcap")).Should().BeNull();
    }

    // ------------------------------------------------------------------ scanning

    /// <summary>A mixed evidence folder: 4 real captures among decoys, plus one in the skipped Windows tree.</summary>
    private static string BuildMixed(TempDir t)
    {
        var root = Path.Combine(t.Path, "evidence");
        WriteCapture(Path.Combine(root, "Users", "alice", "Desktop", "capture.cap"), PcapLe);
        WriteCapture(Path.Combine(root, "Users", "alice", "Downloads", "trace.pcap1"), PcapNsBe);
        WriteCapture(Path.Combine(root, "net", "netlog.dmp"), PcapBe);
        WriteCapture(Path.Combine(root, "net", "tcpdump_out"), PcapNg);
        WriteCapture(Path.Combine(root, "net", "memory.dmp"), "MDMP"u8.ToArray());
        File.WriteAllText(Path.Combine(root, "net", "notes.pcap"), "not a capture, just text with a .pcap name");
        File.WriteAllText(Path.Combine(root, "net", "report.txt"), "notes");
        WriteCapture(Path.Combine(root, "Windows", "System32", "ignored.pcap"), PcapLe);
        return root;
    }

    [Fact]
    public void Scan_finds_captures_by_magic_in_a_mixed_folder_and_skips_windows()
    {
        using var t = new TempDir();
        var root = BuildMixed(t);
        var r = PcapScanner.Scan(root);
        r.Files.Select(f => Path.GetRelativePath(root, f.Path)).Should().BeEquivalentTo(
            Path.Combine("Users", "alice", "Desktop", "capture.cap"),
            Path.Combine("Users", "alice", "Downloads", "trace.pcap1"),
            Path.Combine("net", "netlog.dmp"),
            Path.Combine("net", "tcpdump_out"));
        r.Files.Single(f => f.Name == "tcpdump_out").Format.Should().Be(PcapFormat.PcapNg);
        r.Files.Single(f => f.Name == "trace.pcap1").Format.Should().Be(PcapFormat.PcapNanosecond);
        r.Truncated.Should().BeFalse();
        r.FilesProbed.Should().BeLessThan(10, "report.txt is never opened");
    }

    [Fact]
    public void Scan_of_a_single_file_source()
    {
        using var t = new TempDir();
        var f = Path.Combine(t.Path, "one.pcapng");
        WriteCapture(f, PcapNg);
        var r = PcapScanner.Scan(f);
        r.Files.Should().ContainSingle().Which.Path.Should().Be(f);
        var txt = Path.Combine(t.Path, "x.txt");
        File.WriteAllText(txt, "hello hello hello hello hello");
        PcapScanner.Scan(txt).Files.Should().BeEmpty();
        PcapScanner.Scan(Path.Combine(t.Path, "missing")).Files.Should().BeEmpty();
    }

    [Fact]
    public void Scan_respects_depth_and_result_limits()
    {
        using var t = new TempDir();
        var deep = t.Path;
        for (int i = 0; i < 5; i++) deep = Path.Combine(deep, "d" + i);
        WriteCapture(Path.Combine(deep, "deep.pcap"), PcapLe);
        WriteCapture(Path.Combine(t.Path, "top.pcap"), PcapLe);
        WriteCapture(Path.Combine(t.Path, "top2.pcap"), PcapLe);
        PcapScanner.Scan(t.Path, new PcapScanOptions { MaxDepth = 2 }).Files.Should().HaveCount(2);
        PcapScanner.Scan(t.Path).Files.Should().HaveCount(3);
        var limited = PcapScanner.Scan(t.Path, new PcapScanOptions { MaxResults = 1 });
        limited.Files.Should().HaveCount(1);
        limited.Truncated.Should().BeTrue();
    }

    // ------------------------------------------------------------------ triage plan

    private static ZeekInstall FakeNative() =>
        new(ZeekEngine.Native, ZeekOrigin.Configured, FakeToolPaths.Exe, FakeToolPaths.Exe, "9.0.0", FakeToolPaths.Dir, true);

    [Fact]
    public void Planner_adds_one_ticked_zeek_job_per_capture_under_Network()
    {
        using var t = new TempDir();
        var root = BuildMixed(t);
        var pcaps = PcapScanner.Scan(root).Files;
        var caseDir = Path.Combine(t.Path, "case");
        var plan = NetworkTriage.Plan(pcaps, caseDir, FakeNative(), root);
        plan.Should().HaveCount(4);
        plan.Should().OnlyContain(p => p.SelectedByDefault && !p.RequiresAdmin && p.Spec.ToolId == "Zeek" && p.Spec.Category == "Network");
        // Case layout v2: <case>\<Host>\Network\Zeek_<Log>_<capture>.csv; no discovered system and no host evidence → the case-wide _Case folder.
        plan.Should().OnlyContain(p => p.Spec.OutputDir == Path.Combine(caseDir, "_Case", "Network"));
        plan.Should().OnlyContain(p => p.Spec.Output!.StagingDir.StartsWith(Path.Combine(caseDir, ".ezstaging") + "\\") && p.Spec.Output.ArtifactName == "Zeek");
        plan.Should().OnlyContain(p => p.Spec.ExePath == FakeToolPaths.Exe && p.Spec.PostProcess != null);
        plan.Select(p => p.Spec.Output!.Qualifier).Should().BeEquivalentTo("capture", "trace", "netlog", "tcpdumpout");
        plan.First(p => p.Artifact.MatchedPath.EndsWith("netlog.dmp")).Spec.Label.Should().Contain(Path.Combine("net", "netlog.dmp"));
    }

    [Fact]
    public void Planner_without_zeek_lists_placeholders_that_resolve_later()
    {
        var pcaps = new[] { new PcapFile(@"D:\a\x.pcap", 100, PcapFormat.Pcap), new PcapFile(@"D:\b\x.pcap", 2_000_000, PcapFormat.PcapNg) };
        var plan = NetworkTriage.Plan(pcaps, @"C:\case", null);
        plan.Should().OnlyContain(p => NetworkTriage.IsPlaceholder(p.Spec));
        plan.Select(p => p.Spec.OutputDir).Should().Equal(@"C:\case\_Case\Network", @"C:\case\_Case\Network");
        plan.Select(p => p.Spec.Output!.Qualifier).Should().Equal("x", "x_2");
        plan[0].Spec.Output!.StagingDir.Should().NotBe(plan[1].Spec.Output!.StagingDir);
        var real = NetworkTriage.Resolve(plan[1], FakeNative());
        real.ExePath.Should().Be(FakeToolPaths.Exe);
        real.OutputDir.Should().Be(@"C:\case\_Case\Network");
        real.Output.Should().Be(plan[1].Spec.Output);
        real.Provenance!.RuleId.Should().Be(NetworkTriage.RuleId);
        real.Args.Should().Contain(@"D:\b\x.pcap");
        real.ManifestDetails!["zeek.version"].Should().Be("9.0.0");
    }

    [Fact]
    public void Folder_names_are_sanitised_and_timeouts_grow_with_size()
    {
        ZeekJobs.FolderNameFor(@"D:\x\cap:1?.pcap").Should().Be("cap_1_.pcap");
        ZeekJobs.FolderNameFor(@"D:\x\NUL").Should().Be("_NUL");
        ZeekJobs.FolderNameFor(@"D:\x\con.pcap").Should().Be("_con.pcap");
        ZeekJobs.TimeoutFor(0).Should().Be(TimeSpan.FromMinutes(30));
        ZeekJobs.TimeoutFor(4L << 30).Should().Be(TimeSpan.FromMinutes(110));
    }

    // ------------------------------------------------------------------ real job (fake zeek) + manifest + timeline

    [Fact]
    public async Task Zeek_job_converts_logs_and_records_engine_and_input_hash_in_the_manifest()
    {
        using var t = new TempDir();
        var pcap = Path.Combine(t.Path, "in", "http.pcap");
        WriteCapture(pcap, PcapLe, 128);
        var caseDir = Path.Combine(t.Path, "case");
        var q = new JobQueue(new FakeLocator(null), 2, TimeSpan.FromMinutes(1));
        var jobs = ZeekJobs.Enqueue(q, FakeNative(), new[] { pcap }, caseDir);
        await q.WhenAllAsync();

        var job = jobs.Single();
        job.State.Should().Be(JobState.Succeeded, job.StatusReason);
        var outDir = Path.Combine(caseDir, "Network", "Zeek", "http.pcap");
        File.Exists(Path.Combine(outDir, "conn.log")).Should().BeTrue("zeek writes into its working directory");
        File.Exists(Path.Combine(outDir, "conn.csv")).Should().BeTrue();
        File.Exists(Path.Combine(outDir, "dns.csv")).Should().BeTrue();
        job.Details["input.sha256"].Should().Be(Convert.ToHexString(System.Security.Cryptography.SHA256.HashData(File.ReadAllBytes(pcap))));
        job.Details["zeek.logs"].Should().Be("conn,dns");

        var manifest = Directory.GetFiles(caseDir, "EzForensics_manifest_*.json").Single();
        using var doc = JsonDocument.Parse(File.ReadAllText(manifest));
        var mj = doc.RootElement.GetProperty("jobs")[0];
        mj.GetProperty("toolId").GetString().Should().Be("Zeek");
        mj.GetProperty("argv").EnumerateArray().Select(a => a.GetString()).Should().Equal("-C", "-r", pcap, "local", "LogAscii::use_json=F");
        var details = mj.GetProperty("details");
        details.GetProperty("zeek.version").GetString().Should().Be("9.0.0");
        details.GetProperty("zeek.engine").GetString().Should().Be("native");
        details.GetProperty("input.sha256").GetString().Should().HaveLength(64);
        mj.GetProperty("outputFiles").EnumerateArray().Select(f => Path.GetFileName(f.GetProperty("path").GetString()!))
            .Should().Contain(new[] { "conn.csv", "conn.log", "dns.csv", "dns.log" });
        mj.GetProperty("outputFiles").EnumerateArray().Should().OnlyContain(f => f.GetProperty("sha256").GetString()!.Length == 64);
    }

    [Fact]
    public async Task Standalone_zeek_run_in_a_layout_v2_case_writes_to_Case_Network_with_convention_names()
    {
        using var t = new TempDir();
        var pcap = Path.Combine(t.Path, "in", "http.pcap");
        WriteCapture(pcap, PcapLe, 128);
        var caseDir = Path.Combine(t.Path, "case");
        EzForensics.Core.Case.CaseLayout.EnsureMarker(caseDir);
        ZeekJobs.OutputRoot(caseDir).Should().Be(Path.Combine(caseDir, "_Case", "Network"));
        var q = new JobQueue(new FakeLocator(null), 1, TimeSpan.FromMinutes(1));
        var jobs = ZeekJobs.Enqueue(q, FakeNative(), new[] { pcap }, caseDir);
        await q.WhenAllAsync();

        jobs.Single().State.Should().Be(JobState.Succeeded, jobs.Single().StatusReason);
        Directory.GetFiles(Path.Combine(caseDir, "_Case", "Network")).Select(Path.GetFileName)
            .Should().BeEquivalentTo("Zeek_Conn_http.csv", "Zeek_Conn_http.log", "Zeek_Dns_http.csv", "Zeek_Dns_http.log");
        Directory.Exists(Path.Combine(caseDir, "Network")).Should().BeFalse("no legacy folder in a v2 case");
    }

    [Fact]
    public void Zeek_output_root_stays_legacy_without_a_case_marker()
    {
        using var t = new TempDir();
        ZeekJobs.OutputRoot(t.Path).Should().Be(Path.Combine(t.Path, "Network", "Zeek"));
    }

    [Fact]
    public async Task Triage_zeek_job_publishes_layout_v2_names_with_host()
    {
        using var t = new TempDir();
        var pcap = Path.Combine(t.Path, "in", "http.pcap");
        WriteCapture(pcap, PcapLe, 128);
        var caseDir = Path.Combine(t.Path, "case");
        var host = new DiscoveredSystem { RootPath = Path.Combine(t.Path, "in"), HostName = "SENSOR1", HostNameSource = HostDiscovery.FromFolderName };
        var plan = NetworkTriage.Plan(PcapScanner.Scan(pcap).Files, caseDir, FakeNative(), systems: new[] { host });
        var q = new JobQueue(new FakeLocator(null), 1, TimeSpan.FromMinutes(1));
        var job = q.Enqueue(plan.Single().Spec, caseDir);
        await q.WhenAllAsync();

        job.State.Should().Be(JobState.Succeeded, job.StatusReason);
        var net = Path.Combine(caseDir, "SENSOR1", "Network");
        Directory.GetFiles(net).Select(Path.GetFileName).Should().BeEquivalentTo("Zeek_Conn_http.csv", "Zeek_Conn_http.log", "Zeek_Dns_http.csv", "Zeek_Dns_http.log");
        Directory.Exists(Path.Combine(caseDir, OutputPlan.StagingFolderName)).Should().BeFalse();
        job.PublishedOutputs.Select(p => p.ArtifactId).Distinct().Should().BeEquivalentTo("zeek-conn", "zeek-dns");
    }

    [Fact]
    public async Task Cancelling_a_zeek_job_skips_conversion()
    {
        using var t = new TempDir();
        var pcap = Path.Combine(t.Path, "x.pcap");
        WriteCapture(pcap, PcapLe);
        var q = new JobQueue(new FakeLocator(null), 1, TimeSpan.FromMinutes(1));
        var spec = ZeekJobs.CreateSpec(FakeNative(), pcap, Path.Combine(t.Path, "out"));
        spec = spec with { Environment = new Dictionary<string, string> { ["FAKE_ZEEK_HANG"] = "1" } };
        var job = q.Enqueue(spec, t.Path);
        var deadline = DateTime.UtcNow.AddSeconds(20);
        while (job.State != JobState.Running && DateTime.UtcNow < deadline) await Task.Delay(50);
        await Task.Delay(300);
        job.Cancel();
        await q.WhenAllAsync();
        job.State.Should().Be(JobState.Cancelled);
        Directory.GetFiles(Path.Combine(t.Path, "out"), "*.csv").Should().BeEmpty();
    }

    [Fact]
    public void Output_naming_leaves_zeek_names_alone()
    {
        foreach (var n in new[] { "conn.csv", "dns.csv", "smb_files.csv", "x509.csv", "conn-summary.csv" })
        {
            OutputNaming.HasTimestampPrefix(n).Should().BeFalse();
            OutputNaming.CanonicalName(n).Should().Be(n);
        }
        OutputNaming.CanonicalName("conn (2).csv").Should().Be("conn.csv");
    }
}
