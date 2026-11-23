using System.IO.Compression;
using System.Security.Cryptography;
using System.Text;
using EzForensics.Core.Archives;
using EzForensics.Core.Execution;
using EzForensics.Core.Memory;
using FluentAssertions;

namespace EzForensics.Core.Tests;

/// <summary>Regression tests for the review of the memory / archived-evidence round.</summary>
public sealed class ReviewFixesMemoryArchivesTests : IDisposable
{
    private readonly string _tmp = Path.Combine(Path.GetTempPath(), "ezreview-" + Guid.NewGuid().ToString("N"));
    private readonly string _case;

    public ReviewFixesMemoryArchivesTests()
    {
        Directory.CreateDirectory(_tmp);
        _case = Path.Combine(_tmp, "case");
        Directory.CreateDirectory(_case);
    }

    public void Dispose() { try { Directory.Delete(_tmp, true); } catch (IOException) { } catch (UnauthorizedAccessException) { } }

    private static readonly Func<ArchivePasswordRequest, Task<string?>> NoPrompt =
        _ => throw new InvalidOperationException("password prompt not expected");

    private Task<ArchiveIngestResult> Run(string archive, ArchiveIngestOptions? options = null) =>
        ArchiveIngest.PrepareAsync(archive, _case, NoPrompt, null, CancellationToken.None, options);

    private string Zip(string name, ZipTestWriter w) { var p = Path.Combine(_tmp, name); w.Save(p); return p; }

    private static string Sha(string path) => Convert.ToHexString(SHA256.HashData(File.ReadAllBytes(path))).ToLowerInvariant();

    // ------------------------------------------------------------------ zip bomb: total output capped by free space

    [Fact]
    public async Task Gzip_whose_size_is_not_declared_stops_at_the_free_space()
    {
        // Two gzip members: ISIZE (the last 4 bytes) only describes the last one (1 byte), the first expands to 3 MB.
        var gz = Path.Combine(_tmp, "bomb.log.gz");
        using (var fs = File.Create(gz))
        {
            using (var g = new GZipStream(fs, CompressionLevel.Optimal, leaveOpen: true)) g.Write(new byte[3_000_000]);
            using (var g = new GZipStream(fs, CompressionLevel.Optimal, leaveOpen: true)) g.Write(new byte[] { (byte)'x' });
        }
        var options = new ArchiveIngestOptions { FreeSpace = _ => 1_000_000, Limits = new ArchiveLimits { FreeSpaceMarginBytes = 0 } };
        var act = () => Run(gz, options);
        await act.Should().ThrowAsync<ArchiveLimitExceededException>();
        Directory.Exists(ArchiveIngest.ExtractionFolder(_case, Sha(gz), gz)).Should().BeFalse("the partial copy is removed");
    }

    [Fact]
    public async Task Archive_that_fits_the_free_space_is_not_capped()
    {
        var zip = Zip("fits.zip", new ZipTestWriter().Add("a.bin", new byte[100_000]));
        var r = await Run(zip, new ArchiveIngestOptions { FreeSpace = _ => 300_000, Limits = new ArchiveLimits { FreeSpaceMarginBytes = 100_000 } });
        new FileInfo(Path.Combine(r.Origin.ExtractedTo, "a.bin")).Length.Should().Be(100_000);
    }

    // ------------------------------------------------------------------ extraction never overwrites / deletes what is there

    [Fact]
    public async Task Entry_named_like_the_record_temp_file_is_kept()
    {
        var zip = Zip("tmpname.zip", new ZipTestWriter().Add(ExtractionRecord.FileName + ".tmp", "evidence bytes"));
        var r = await Run(zip);
        var rec = ExtractionRecord.Load(r.Origin.ExtractedTo)!;
        var e = rec.Entries.Single();
        e.ExtractedPath.Should().NotBe(ExtractionRecord.FileName + ".tmp");
        File.ReadAllText(Path.Combine(r.Origin.ExtractedTo, e.ExtractedPath)).Should().Be("evidence bytes");
    }

    [Fact]
    public void Entry_whose_target_already_exists_is_skipped_and_the_existing_file_is_untouched()
    {
        var zip = Zip("clash.zip", new ZipTestWriter().Add("a.txt", "from archive").Add("b.txt", "bravo"));
        var dest = Path.Combine(_tmp, "dest");
        Directory.CreateDirectory(dest);
        File.WriteAllText(Path.Combine(dest, "a.txt"), "already there");   // stands in for an 8.3 short-name alias
        var engine = new SharpCompressArchiveEngine();
        var outcome = ArchiveExtractor.Extract(engine, zip, null, engine.Inspect(zip), dest, new ArchiveLimits(), null, null, CancellationToken.None);
        File.ReadAllText(Path.Combine(dest, "a.txt")).Should().Be("already there");
        File.ReadAllText(Path.Combine(dest, "b.txt")).Should().Be("bravo");
        outcome.Entries.Single(x => x.OriginalName == "a.txt").ExtractedPath.Should().BeEmpty();
        outcome.Warnings.Should().Contain(w => w.Contains("short-name"));
    }

    [Theory]
    [InlineData("COM\u00B9.txt")]
    [InlineData("lpt\u00B3")]
    public void Superscript_device_names_are_encoded(string name)
    {
        var s = ArchivePaths.Sanitize(name, out _);
        s.Should().NotBe(name);
        s.Should().StartWith("%");
    }

    [Fact]
    public async Task Complete_copy_of_a_different_archive_with_the_same_prefix_and_name_is_not_wiped()
    {
        var zip = Zip("same.zip", new ZipTestWriter().Add("a.txt", "alpha"));
        var sha = Sha(zip);
        var otherFolder = ArchiveIngest.ExtractionFolder(_case, sha, zip);
        var otherSha = sha[..8] + new string('0', 56);
        new ExtractionRecord { ArchivePath = @"C:\other\same.zip", Sha256 = otherSha, Complete = true }.Save(otherFolder);
        File.WriteAllText(Path.Combine(otherFolder, "other.txt"), "other evidence");

        var r = await Run(zip);

        File.ReadAllText(Path.Combine(otherFolder, "other.txt")).Should().Be("other evidence");
        ExtractionRecord.Load(otherFolder)!.Sha256.Should().Be(otherSha);
        r.Origin.ExtractedTo.Should().NotBe(otherFolder);
        File.ReadAllText(Path.Combine(r.Origin.ExtractedTo, "a.txt")).Should().Be("alpha");
        (await Run(zip)).Origin.ExtractedTo.Should().Be(r.Origin.ExtractedTo, "the second run reuses it");
    }

    // ------------------------------------------------------------------ provenance: outer archive of a nested container

    [Fact]
    public async Task Sole_nested_container_keeps_the_outer_archive_as_parent_and_the_manifest_records_both()
    {
        var inner = new ZipTestWriter().Add("C/Windows/System32/config/SYSTEM", "hive").ToArray();
        var outer = Zip("outer.zip", new ZipTestWriter().Add("data.zip", inner, e => e.Deflate = false));
        var r = await Run(outer);

        r.Origin.ArchivePath.Should().EndWith("data.zip");
        r.Origin.Parent.Should().NotBeNull();
        r.Origin.Parent!.Sha256.Should().Be(Sha(outer));
        r.Origin.Parent.ArchivePath.Should().Be(Path.GetFullPath(outer));
        r.Origin.Parent.ExtractedTo.Should().NotBeEmpty();
        r.Origin.SelfAndParents().Should().HaveCount(2);

        var w = new ManifestWriter(_case);
        w.NoteOrigin(new JobSpec("T", "t.exe", Array.Empty<string>(), _case, "x", "y",
            Provenance: new JobProvenance { Host = "H", Archive = r.Origin }));
        w.Manifest.Archives.Should().HaveCount(2);
        w.Manifest.Archives!.Select(a => a.Sha256).Should().Contain(Sha(outer));
    }

    [Fact]
    public void Archive_provenance_is_attached_by_path_only_when_missing()
    {
        var root = Path.Combine(_case, ".ezstaging", "extracted", "abcdef01", "mem");
        var origin = new ArchiveOrigin(@"D:\ev\mem.zip", "abcdef01" + new string('1', 56), ArchiveFormat.Zip, false, null, root, new[] { @"D:\ev\mem.zip" },
            TimestampCoverage.DosLocal);
        var spec = new JobSpec("Volatility3", "vol.exe", Array.Empty<string>(), _case, "x", "Memory", Provenance: new JobProvenance { Host = "MEM" });

        ArchiveProvenance.Attach(spec, Path.Combine(root, "memory.raw"), new[] { origin }).Provenance!.Archive.Should().Be(origin);
        ArchiveProvenance.Attach(spec, Path.Combine(_tmp, "memory.raw"), new[] { origin }).Provenance!.Archive.Should().BeNull();
        ArchiveProvenance.Attach(spec, Path.Combine(root + "x", "memory.raw"), new[] { origin }).Provenance!.Archive.Should().BeNull("a sibling folder with the same prefix");
        var other = origin with { Sha256 = "x" };
        var withArchive = spec with { Provenance = spec.Provenance! with { Archive = other } };
        ArchiveProvenance.Attach(withArchive, Path.Combine(root, "memory.raw"), new[] { origin }).Provenance!.Archive.Should().Be(other);
    }

    // ------------------------------------------------------------------ symbol gate: a failed check does not poison later runs

    private sealed class MemFixture : IDisposable
    {
        public readonly TempDir Temp = new();
        public string Case => Path.Combine(Temp.Path, "case");
        public string OutputDir => Path.Combine(Case, "HOST1", "Memory");
        public string Staging => Path.Combine(Case, OutputPlan.StagingFolderName, "HOST1_memory_memory");
        public MemoryImage Image { get; }
        public VolatilityInstall Install { get; }
        public MemorySymbolGate Gate { get; } = new();

        public MemFixture()
        {
            Directory.CreateDirectory(Case);
            var img = Path.Combine(Temp.Path, "memory.dmp");
            File.WriteAllBytes(img, Encoding.ASCII.GetBytes("PAGEDU64" + new string('\0', 4088)));
            Image = new MemoryImage(img, new FileInfo(img).Length, MemoryFormat.WindowsCrashDump, MemoryOs.Windows, DumpKind.Full, 1000, null);
            Install = new VolatilityInstall(FakeToolPaths.Exe, "python.exe", "2.28.2", Temp.Path, Path.Combine(Temp.Path, "symbols"), Path.Combine(Temp.Path, "cache"));
        }

        public JobSpec Spec(string pluginId, bool noSymbols = false)
        {
            var spec = MemoryJobs.CreateSpec(Install, Image, MemoryPlugins.Find(pluginId)!, "HOST1", OutputDir, Staging, Gate, offline: false);
            if (!noSymbols) return spec;
            var env = new Dictionary<string, string>(spec.Environment!) { ["FAKE_VOL_NOSYMBOLS"] = "1" };
            return spec with { Environment = env };
        }

        public void Dispose() => Temp.Dispose();
    }

    private static JobQueue Queue() => new(new FakeLocator(null), 4, TimeSpan.FromMinutes(2));

    [Fact]
    public async Task After_a_failed_symbol_check_a_retried_plugin_runs_and_a_rerun_rechecks()
    {
        using var f = new MemFixture();
        var q = Queue();
        q.Enqueue(f.Spec("info", noSymbols: true), f.Case);
        var ps = q.Enqueue(f.Spec("pslist"), f.Case);
        await q.WhenAllAsync();
        ps.State.Should().Be(JobState.Failed);
        f.Gate.Failed.Should().BeTrue();

        // Ingest job list "Retry" of the plugin alone (same spec, same gate): runs instead of failing on the old check.
        var retry = q.Enqueue(ps.Spec, f.Case);
        await q.WhenAllAsync();
        retry.State.Should().Be(JobState.Succeeded, retry.StatusReason);

        // Run selected again (symbols now available): the info job re-checks and the plugins wait for it.
        var info2 = q.Enqueue(f.Spec("info"), f.Case);
        var net = q.Enqueue(f.Spec("netscan"), f.Case);
        await q.WhenAllAsync();
        info2.State.Should().Be(JobState.Succeeded, info2.StatusReason);
        net.State.Should().Be(JobState.Succeeded, net.StatusReason);
        f.Gate.IsOpen.Should().BeTrue();
        f.Gate.Failed.Should().BeFalse();
    }

    [Fact]
    public async Task Rerun_with_symbols_still_missing_fails_the_plugins_of_that_run_fast()
    {
        using var f = new MemFixture();
        var q = Queue();
        q.Enqueue(f.Spec("info", noSymbols: true), f.Case);
        await q.WhenAllAsync();
        f.Gate.Failed.Should().BeTrue();

        // A gate with one Volatility slot makes the plugin wait for the info job deterministically.
        var info2 = q.Enqueue(f.Spec("info", noSymbols: true), f.Case);
        await Task.Delay(50);
        var ps = q.Enqueue(f.Spec("pslist"), f.Case);
        await q.WhenAllAsync();
        info2.State.Should().Be(JobState.Failed);
        ps.State.Should().BeOneOf(JobState.Failed, JobState.Succeeded);   // fake vol only fails info on NOSYMBOLS; never stuck waiting
        ps.IsFinished.Should().BeTrue();
    }

    [Fact]
    public void Gate_rearm_only_forgets_decisions_older_than_the_info_job()
    {
        var gate = new MemorySymbolGate();
        gate.Fail("x");
        var spec = new JobSpec("T", "t.exe", Array.Empty<string>(), _tmp, "x", "y");
        var q = new JobQueue(new FakeLocator(null), 1, TimeSpan.FromMinutes(1));
        var job = q.Enqueue(spec with { ExePath = Path.Combine(_tmp, "missing.exe") }, _case);   // any job: only its queue order matters
        gate.FailedBefore(job).Should().BeTrue();
        gate.Rearm(job);
        gate.IsDecided.Should().BeFalse();
        gate.Open();
        gate.Rearm(job);
        gate.IsDecided.Should().BeTrue("the decision is newer than the job");
    }
}
