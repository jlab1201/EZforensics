using EzForensics.Core.Execution;
using EzForensics.Core.Platforms;
using FluentAssertions;

namespace EzForensics.Core.Tests;

/// <summary>Staging of layout v2 jobs: stale files, concurrent runs, partial (cancelled / timed-out) outputs, leftovers.</summary>
public sealed class CaseLayoutStagingTests
{
    private static JobQueue Queue(int parallel = 2) => new(new FakeLocator(FakeToolPaths.Exe), parallel, TimeSpan.FromMinutes(1));

    private static JobSpec PrefetchSpec(string caseDir, string staging, IEnumerable<string>? extra = null, TimeSpan? timeout = null) =>
        new("PECmd", "EzForensics.FakeTool.exe", new[] { "--csv", staging, "--csvf", "Prefetch.csv" }.Concat(extra ?? Array.Empty<string>()).ToList(),
            Path.Combine(caseDir, "HOST", "ProgramExecution"), "Prefetch", "ProgramExecution", Timeout: timeout,
            Output: new OutputPlan(staging, "Prefetch", "prefetch", null, "Prefetch.csv"));

    private static ManifestJob ManifestJobOf(string caseDir, Job job) =>
        RunManifest.Load(Directory.GetFiles(caseDir, "EzForensics_manifest_*.json").Single()).Jobs.Single(j => j.JobId == job.Id);

    [Fact]
    public void Partial_name_passes_the_output_name_validator()
    {
        OutputPublisher.PartialName("MFT.csv").Should().Be("MFT_PARTIAL.csv");
        OutputPublisher.PartialName("ActivitiesCache_bob.csv").Should().Be("ActivitiesCache_bob_PARTIAL.csv");
        OutputPublisher.PartialName("Zeek_Conn_cap.log").Should().Be("Zeek_Conn_cap_PARTIAL.log");
        ArtifactOutputLayout.IsValidOutputName("MFT_PARTIAL.csv").Should().BeTrue();
        ArtifactOutputLayout.IsValidOutputName(OutputPublisher.PartialName("ActivitiesCache_bob.csv")).Should().BeTrue();
    }

    [Fact]
    public void Publish_partial_appends_the_qualifier()
    {
        using var t = new TempDir();
        var staging = Path.Combine(t.Path, ".ezstaging", "job");
        var final = Path.Combine(t.Path, "HOST", "FileSystem");
        var before = OutputSnapshot.Capture(staging);
        Directory.CreateDirectory(staging);
        File.WriteAllText(Path.Combine(staging, "MFT.csv"), "a");
        var published = OutputPublisher.Publish("MFTECmd", new OutputPlan(staging, "MFT", "mft", null, "MFT.csv"), final, before, partial: true);
        published.Should().ContainSingle().Which.FinalName.Should().Be("MFT_PARTIAL.csv");
    }

    [Fact]
    public async Task Stale_staging_files_of_an_earlier_run_are_not_this_jobs_outputs()
    {
        using var t = new TempDir();
        var caseDir = Path.Combine(t.Path, "case");
        var staging = Path.Combine(caseDir, OutputPlan.StagingFolderName, "HOST_prefetch_ABCDEF12");
        Directory.CreateDirectory(staging);
        var stale = Path.Combine(staging, "Stale_from_last_run.csv");
        File.WriteAllText(stale, "Name\nold\n");
        File.SetLastWriteTimeUtc(stale, DateTime.UtcNow);   // even a recent stale file

        var q = Queue();
        var job = q.Enqueue(PrefetchSpec(caseDir, staging), caseDir);
        await q.WhenAllAsync();

        job.State.Should().Be(JobState.Succeeded, job.StatusReason);
        job.StagingLeftovers.Should().BeEmpty();
        File.Exists(stale).Should().BeTrue("stale files are left alone, never published as this job's output");
        var mj = ManifestJobOf(caseDir, job);
        mj.OutputFiles.Select(f => Path.GetFileName(f.Path)).Should().Equal("Prefetch.csv");
        mj.LeftInStaging.Should().BeNull();
        mj.PartialOutputs.Should().BeNull();
    }

    [Fact]
    public async Task Timed_out_job_publishes_PARTIAL_outputs_and_records_it()
    {
        using var t = new TempDir();
        var caseDir = Path.Combine(t.Path, "case");
        var staging = Path.Combine(caseDir, OutputPlan.StagingFolderName, "HOST_prefetch_11111111");
        var q = Queue();
        // The timeout runs from process start and the tool writes its CSV just after starting; on a loaded machine the start can
        // take seconds, so the timeout must be well above any start-up delay for the CSV to exist when the job is killed.
        var job = q.Enqueue(PrefetchSpec(caseDir, staging, new[] { "--hang" }, TimeSpan.FromSeconds(10)), caseDir);
        await q.WhenAllAsync();

        job.State.Should().Be(JobState.TimedOut);
        job.PartialOutputs.Should().BeTrue();
        Directory.GetFiles(Path.Combine(caseDir, "HOST", "ProgramExecution")).Select(Path.GetFileName).Should().Equal("Prefetch_PARTIAL.csv");
        job.StatusReason.Should().Contain("_PARTIAL");
        var mj = ManifestJobOf(caseDir, job);
        mj.State.Should().Be("TimedOut");
        mj.PartialOutputs.Should().BeTrue();
        mj.OutputFiles.Select(f => Path.GetFileName(f.Path)).Should().Equal("Prefetch_PARTIAL.csv");
    }

    [Fact]
    public async Task Output_that_cannot_be_published_is_recorded_as_left_in_staging()
    {
        using var t = new TempDir();
        var caseDir = Path.Combine(t.Path, "case");
        var staging = Path.Combine(caseDir, OutputPlan.StagingFolderName, "HOST_prefetch_22222222");
        FileStream? hold = null;
        var spec = PrefetchSpec(caseDir, staging) with
        {
            // keep the output open without delete sharing so the publish move fails; released before the manifest is written
            PostProcess = (_, _) => { hold = new FileStream(Path.Combine(staging, "Prefetch.csv"), FileMode.Open, FileAccess.Read, FileShare.Read); return Task.CompletedTask; },
            Finally = _ => hold?.Dispose(),
        };
        var q = Queue();
        var job = q.Enqueue(spec, caseDir);
        await q.WhenAllAsync();

        job.StagingLeftovers.Select(Path.GetFileName).Should().Equal("Prefetch.csv");
        job.StatusReason.Should().Contain("1 output file(s) left in staging: " + staging);
        job.Details.Should().ContainKey("output.leftInStaging");
        var mj = ManifestJobOf(caseDir, job);
        mj.LeftInStaging.Should().ContainSingle().Which.Should().EndWith("Prefetch.csv");
        mj.OutputFiles.Should().ContainSingle().Which.Path.Should().StartWith(staging);
    }

    [Fact]
    public async Task Two_jobs_never_share_one_staging_folder()
    {
        using var t = new TempDir();
        var caseDir = Path.Combine(t.Path, "case");
        var staging = Path.Combine(caseDir, OutputPlan.StagingFolderName, "HOST_prefetch_33333333");
        var q = Queue(parallel: 2);
        var a = q.Enqueue(PrefetchSpec(caseDir, staging, new[] { "--lines", "4", "--delay", "500" }), caseDir);
        var b = q.Enqueue(PrefetchSpec(caseDir, staging, new[] { "--lines", "4", "--delay", "500" }), caseDir);
        await q.WhenAllAsync();

        var states = new[] { a, b };
        states.Should().ContainSingle(j => j.State == JobState.Succeeded);
        states.Should().ContainSingle(j => j.State == JobState.Failed && j.StatusReason!.Contains("same staging folder"));
        // the folder is free again afterwards
        var c = q.Enqueue(PrefetchSpec(caseDir, staging), caseDir);
        await q.WhenAllAsync();
        c.State.Should().Be(JobState.Succeeded, c.StatusReason);
    }
}
