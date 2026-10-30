using System.Text;
using EzForensics.Core.Execution;
using EzForensics.Core.Memory;
using FluentAssertions;

namespace EzForensics.Core.Tests;

/// <summary>Volatility jobs end to end with EzForensics.FakeTool as vol.exe (FakeVolatilityTool).</summary>
public class MemoryJobsTests
{
    private sealed class Fixture : IDisposable
    {
        public readonly TempDir Temp = new();
        public string Case => Path.Combine(Temp.Path, "case");
        public string OutputDir => Path.Combine(Case, "HOST1", "Memory");
        public string Staging => Path.Combine(Case, OutputPlan.StagingFolderName, "HOST1_memory_memory");
        public MemoryImage Image { get; }
        public VolatilityInstall Install { get; }
        public MemorySymbolGate Gate { get; } = new();

        public Fixture()
        {
            Directory.CreateDirectory(Case);
            var img = Path.Combine(Temp.Path, "memory.dmp");
            File.WriteAllBytes(img, Encoding.ASCII.GetBytes("PAGEDU64" + new string('\0', 4088)));
            Image = new MemoryImage(img, new FileInfo(img).Length, MemoryFormat.WindowsCrashDump, MemoryOs.Windows, DumpKind.Full, 1000, null);
            Install = new VolatilityInstall(FakeToolPaths.Exe, "python.exe", "2.28.2", Temp.Path, Path.Combine(Temp.Path, "symbols"), Path.Combine(Temp.Path, "cache"));
        }

        public JobSpec Spec(string pluginId, IReadOnlyDictionary<string, string>? env = null, string? qualifier = null)
        {
            var spec = MemoryJobs.CreateSpec(Install, Image, MemoryPlugins.Find(pluginId)!, "HOST1", OutputDir, Staging, Gate, offline: false, qualifier: qualifier);
            if (env == null) return spec;
            var merged = new Dictionary<string, string>(spec.Environment!);
            foreach (var (k, v) in env) merged[k] = v;
            return spec with { Environment = merged };
        }

        public void Dispose() => Temp.Dispose();
    }

    private static JobQueue Queue(int parallel = 4) => new(new FakeLocator(null), parallel, TimeSpan.FromMinutes(2));

    [Fact]
    public void Spec_captures_stdout_in_staging_and_records_the_engine_facts()
    {
        using var f = new Fixture();
        var spec = f.Spec("pslist", qualifier: "img2");
        spec.ToolId.Should().Be("Volatility3");
        spec.Category.Should().Be("Memory");
        spec.ExePath.Should().Be(FakeToolPaths.Exe);
        spec.Output!.StagingDir.Should().Be(f.Staging + "_PsList");
        spec.StdoutPath.Should().Be(Path.Combine(f.Staging + "_PsList", "PsList.csv"));
        spec.Output.ArtifactName.Should().Be("Memory");
        spec.Output.ArtifactId.Should().Be("memory-pslist");
        spec.Output.Qualifier.Should().Be("img2");
        spec.Environment!["PYTHONNOUSERSITE"].Should().Be("1");
        spec.ManifestDetails!["volatility.version"].Should().Be("2.28.2");
        spec.ManifestDetails["image.format"].Should().Be("WindowsCrashDump");
        spec.ManifestDetails["dump.kind"].Should().Be("Full");
        spec.ManifestDetails["symbols.mode"].Should().StartWith("online");
        spec.ManifestDetails["dump.bugcheck"].Should().Be("0x00000000");
        spec.ManifestDetails["memory.live"].Should().Be("true", "bug check code 0 = header made by an acquisition tool on a running system");
        spec.Provenance!.ArtifactId.Should().Be("memory-pslist");
        spec.Provenance.Host.Should().Be("HOST1");
        spec.PreProcess.Should().NotBeNull();
        spec.PostProcess.Should().NotBeNull();
    }

    [Fact]
    public async Task Info_then_plugins_publish_normalised_csvs_and_hash_the_image()
    {
        using var f = new Fixture();
        var q = Queue();
        var argsFile = Path.Combine(f.Temp.Path, "args.txt");
        var info = q.Enqueue(f.Spec("info", new Dictionary<string, string> { ["FAKE_VOL_ARGS"] = argsFile }), f.Case);
        var ps = q.Enqueue(f.Spec("pslist"), f.Case);
        var cmd = q.Enqueue(f.Spec("cmdline"), f.Case);
        await q.WhenAllAsync();

        info.State.Should().Be(JobState.Succeeded, info.StatusReason);
        ps.State.Should().Be(JobState.Succeeded, ps.StatusReason);
        cmd.State.Should().Be(JobState.Succeeded, cmd.StatusReason);
        f.Gate.IsOpen.Should().BeTrue();

        var infoCsv = Path.Combine(f.OutputDir, "Memory_Info.csv");
        File.ReadAllText(infoCsv).Should().StartWith("TreeDepth,Variable,Value\r\n").And.Contain("SystemTime,2024-03-01 10:20:30.1234560");
        info.PublishedOutputs.Should().ContainSingle().Which.ArtifactId.Should().Be("memory-info");
        info.Details.Should().ContainKey("input.sha256");
        info.Details["volatility.rows"].Should().Be("3");

        var psText = File.ReadAllText(Path.Combine(f.OutputDir, "Memory_PsList.csv"));
        psText.Should().NotContain("Volatility 3 Framework").And.NotContain("UTC");
        psText.Should().Contain("\"evil, inc.exe\",0xfa8000c9e041,3,-,1,False,2024-03-01 09:30:15.5000000,2024-03-01 09:31:00.0000000,Disabled");
        ps.PublishedOutputs.Single().ArtifactId.Should().Be("memory-pslist");
        ps.Details.Should().NotContainKey("input.sha256");

        File.ReadAllText(Path.Combine(f.OutputDir, "Memory_CmdLine.csv")).Should().Contain("C:\\Windows\\System32\\cmd.exe /c \"\"echo hi\"\"");

        // stdout went to the file, not the log; stderr (progress) did.
        var log = File.ReadAllText(ps.LogPath!);
        log.Should().NotContain("TreeDepth").And.Contain("Progress:");

        var argv = File.ReadAllLines(argsFile);
        argv.Should().ContainInOrder("-r", "csv", "-f", f.Image.Path, "--symbol-dirs", f.Install.SymbolsDir, "--cache-path", f.Install.CacheDir, "windows.info");
        argv.Should().Contain("PYTHONNOUSERSITE=1");
    }

    [Fact]
    public async Task Missing_symbols_fail_info_and_make_the_other_plugins_fail_fast()
    {
        using var f = new Fixture();
        var q = Queue();
        var noSym = new Dictionary<string, string> { ["FAKE_VOL_NOSYMBOLS"] = "1" };
        var info = q.Enqueue(f.Spec("info", noSym), f.Case);
        var ps = q.Enqueue(f.Spec("pslist"), f.Case);
        var net = q.Enqueue(f.Spec("netscan"), f.Case);
        await q.WhenAllAsync();

        info.State.Should().Be(JobState.Failed);
        info.StatusReason.Should().Contain(MemorySymbolGate.SymbolsMissingMessage).And.NotContain("symbol_table_name",
            "the info job shows the same plain message as the other plugin jobs, not Volatility's raw requirement error");
        f.Gate.Failed.Should().BeTrue();
        f.Gate.Message.Should().Be(MemorySymbolGate.SymbolsMissingMessage);
        File.ReadAllText(info.LogPath!).Should().Contain(MemorySymbolGate.SymbolsMissingMessage);
        foreach (var j in new[] { ps, net })
        {
            j.State.Should().Be(JobState.Failed);
            j.StatusReason.Should().Contain("could not find the symbols");
            j.ExitCode.Should().BeNull("the process never started");
        }
        Directory.Exists(f.OutputDir).Should().BeTrue();
        Directory.GetFiles(f.OutputDir).Should().BeEmpty("a failed info run publishes nothing");
    }

    [Theory]
    [InlineData(0x80u, "false")]    // NMI_HARDWARE_FAILURE: written by Windows with the system halted (samples\C\memory.dmp)
    [InlineData(0x161u, "true")]    // LIVE_SYSTEM_DUMP
    public void Crash_dump_bug_check_is_recorded_for_the_smear_advisory(uint bugCheck, string live)
    {
        using var f = new Fixture();
        var bytes = Encoding.ASCII.GetBytes("PAGEDU64" + new string('\0', 4088));
        BitConverter.GetBytes(bugCheck).CopyTo(bytes, 0x38);
        File.WriteAllBytes(f.Image.Path, bytes);
        var spec = f.Spec("info");
        spec.ManifestDetails!["dump.bugcheck"].Should().Be($"0x{bugCheck:X8}");
        spec.ManifestDetails["memory.live"].Should().Be(live);
    }

    [Fact]
    public void Non_crash_dump_images_record_no_bug_check()
    {
        using var f = new Fixture();
        var raw = f.Image with { Format = MemoryFormat.Raw, DumpKind = null };
        var spec = MemoryJobs.CreateSpec(f.Install, raw, MemoryPlugins.Find("pslist")!, "HOST1", f.OutputDir, f.Staging, f.Gate, offline: false);
        spec.ManifestDetails!.Should().NotContainKey("memory.live").And.NotContainKey("dump.bugcheck");
    }

    [Fact]
    public async Task Non_zero_exit_fails_the_job_but_still_opens_the_gate()
    {
        using var f = new Fixture();
        var q = Queue();
        var info = q.Enqueue(f.Spec("info", new Dictionary<string, string> { ["FAKE_VOL_EXIT"] = "3" }), f.Case);
        var ps = q.Enqueue(f.Spec("pslist"), f.Case);
        await q.WhenAllAsync();
        info.State.Should().Be(JobState.Failed);
        info.ExitCode.Should().Be(3);
        f.Gate.IsOpen.Should().BeTrue();
        ps.State.Should().Be(JobState.Succeeded, ps.StatusReason);
    }

    [Fact]
    public async Task Cancel_kills_vol_and_publishes_a_normalised_partial_csv()
    {
        using var f = new Fixture();
        f.Gate.Open();   // no info job in this test
        var q = Queue();
        var spec = f.Spec("pslist", new Dictionary<string, string> { ["FAKE_VOL_HANG"] = "1" });
        var job = q.Enqueue(spec, f.Case);
        var deadline = DateTime.UtcNow.AddSeconds(30);
        while (DateTime.UtcNow < deadline && !(File.Exists(spec.StdoutPath!) && new FileInfo(spec.StdoutPath!).Length > 200))
            await Task.Delay(50);
        job.Cancel();
        await q.WhenAllAsync();

        job.State.Should().Be(JobState.Cancelled);
        var partial = Path.Combine(f.OutputDir, "Memory_PsList_PARTIAL.csv");
        File.Exists(partial).Should().BeTrue();
        File.ReadAllText(partial).Should().StartWith("TreeDepth,PID").And.NotContain("Volatility 3 Framework");
        f.Gate.Running.Should().Be(0, "the slot is released when the job ends");
    }

    [Fact]
    public async Task At_most_two_vol_processes_run_per_image()
    {
        using var f = new Fixture();
        f.Gate.Open();
        var q = Queue(parallel: 4);
        var hang = new Dictionary<string, string> { ["FAKE_VOL_HANG"] = "1" };
        var jobs = new[] { "pslist", "pstree", "psscan" }.Select(id => q.Enqueue(f.Spec(id, hang), f.Case)).ToList();
        var deadline = DateTime.UtcNow.AddSeconds(30);
        while (DateTime.UtcNow < deadline && f.Gate.Running < 2) await Task.Delay(50);
        try
        {
            // Condition-based wait: the third job reaches the slot wait asynchronously.
            while (DateTime.UtcNow < deadline && !jobs.Any(j => j.State == JobState.Queued && j.StatusReason == MemorySymbolGate.WaitingForSlotReason)) await Task.Delay(50);
            f.Gate.Running.Should().Be(2);
            jobs.Count(j => j.State == JobState.Running).Should().Be(2);
            var waiting = jobs.Single(j => j.State == JobState.Queued);
            waiting.StatusReason.Should().Be(MemorySymbolGate.WaitingForSlotReason, "the third job waits in PreProcess (for a Volatility slot) without a queue slot or a start time");
            waiting.StartUtc.Should().BeNull();
        }
        finally
        {
            q.CancelAll();   // never leave hung FakeTool processes behind, even when an assertion fails
            await q.WhenAllAsync();
        }
        jobs.Should().OnlyContain(j => j.State == JobState.Cancelled);
        f.Gate.Running.Should().Be(0);
    }

    [Fact]
    public async Task ProcessRunner_writes_raw_stdout_to_the_file_and_only_stderr_to_the_callback()
    {
        using var t = new TempDir();
        var outFile = Path.Combine(t.Path, "out.txt");
        var seen = new List<(bool Err, string Line)>();
        var r = await ProcessRunner.RunAsync(FakeToolPaths.Exe, new[] { "--lines", "4", "--stderr" }, null, (e, l) => seen.Add((e, l)),
            TimeSpan.FromMinutes(1), CancellationToken.None, stdoutPath: outFile);
        r.ExitCode.Should().Be(0);
        var text = File.ReadAllText(outFile);
        text.Should().Contain("line 1").And.Contain("line 3").And.NotContain("err 2");
        seen.Should().OnlyContain(s => s.Err);
        seen.Select(s => s.Line).Should().Contain("err 2").And.Contain("err 4");
    }
}
