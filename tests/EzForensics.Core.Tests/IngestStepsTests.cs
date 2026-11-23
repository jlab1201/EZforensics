using EzForensics.Core.Execution;
using EzForensics.Core.SystemInfo;
using EzForensics.Core.Triage;
using FluentAssertions;
using Xunit;

namespace EzForensics.Core.Tests;

/// <summary>Ingest planned jobs: Computer details is the first row, the plaso super timeline the last; the Result column text.</summary>
public class IngestStepsTests
{
    private static PlannedJob Tool(string rule, string label) =>
        new(new JobSpec("PECmd", "PECmd.exe", Array.Empty<string>(), @"C:\case\H\Prefetch", label, "Prefetch"), true,
            new DetectedArtifact(rule, label, "PECmd", "Prefetch", @"E:\Windows\Prefetch", false), false);

    [Fact]
    public void Compose_puts_computer_details_first_and_the_supertimeline_last()
    {
        var plan = IngestSteps.Compose(new[] { Tool("prefetch", "Prefetch"), Tool("amcache", "Amcache") }, @"C:\case", new[] { @"E:\" }, plasoAvailable: true);
        plan.Select(p => p.Spec.Label).Should().Equal(IngestSteps.ComputerDetailsLabel, "Prefetch", "Amcache", IngestSteps.SupertimelineLabelFor("_Case"));
        plan[0].SelectedByDefault.Should().BeTrue();
        plan[0].Spec.ToolId.Should().Be(ComputerDetailsStore.ToolId);
        plan[^1].SelectedByDefault.Should().BeFalse("the super timeline is not part of the fast-forensics defaults");
        plan[^1].Spec.OutputDir.Should().Be(@"C:\case\_Case\Timeline", "the prefetch hit has no system: its timeline is unattributed");
        IngestSteps.Sources(plan[^1]).Should().Equal(@"E:\");
        plan.Count(IngestSteps.IsStep).Should().Be(2);
    }

    [Fact]
    public void Defaults_are_only_the_sans_fast_forensics_disk_set()
    {
        var fast = new[] { "mft", "usnjrnl", "prefetch", "amcache", "appcompat", "evtx", "registry", "lnk", "jumplists", "shellbags" };
        var off = new[] { "srum", "sqlite-hunt", "recyclebin", "activitiescache", "sum", "recentfilecache", PersistenceTriage.RuleId, "mkape-custom" };
        // memory, network and RITA rows are planned but unticked by Defaults (round 7)
        var detected = new[] { MemoryTriage.RuleId, NetworkTriage.RuleId, EzForensics.Core.Rita.RitaJobs.RuleId };
        var plan = IngestSteps.Compose(fast.Concat(off).Concat(detected).Select(r => Tool(r, r)).ToList(), @"C:\case", new[] { @"E:\" }, plasoAvailable: true);

        plan.Where(p => p.SelectedByDefault).Select(p => p.Artifact.RuleId)
            .Should().BeEquivalentTo(new[] { IngestSteps.ComputerDetailsRuleId, IngestSteps.MacbTimelineRuleId }.Concat(fast));
        plan.Where(p => detected.Contains(p.Artifact.RuleId)).Should().HaveCount(3).And.OnlyContain(p => !p.SelectedByDefault);
        foreach (var rule in detected) IngestDefaults.IsDefault(Tool(rule, rule)).Should().BeFalse(rule + " is not a default");
        IngestDefaults.FastForensicsRuleIds.Should().BeEquivalentTo(new[] { IngestSteps.ComputerDetailsRuleId, IngestSteps.MacbTimelineRuleId, IngestSteps.ProcessTreeRuleId }.Concat(fast));
        // every listed rule id exists as a planned job (rules.json or an Ingest step)
        var known = TriageRules.LoadEmbedded().Select(r => r.Id).Append(IngestSteps.ComputerDetailsRuleId).Append(IngestSteps.MacbTimelineRuleId).Append(IngestSteps.ProcessTreeRuleId);
        IngestDefaults.FastForensicsRuleIds.Should().BeSubsetOf(known);
    }

    [Fact]
    public void Defaults_keep_a_job_off_when_its_own_default_is_off()
    {
        var hit = Tool("evtx", "Event logs") with { SelectedByDefault = false };
        IngestDefaults.IsDefault(hit).Should().BeFalse();
        IngestDefaults.IsDefault(Tool(MemoryTriage.RuleId, "Memory") with { SelectedByDefault = false }).Should().BeFalse();
    }

    [Fact]
    public void Compose_leaves_out_the_supertimeline_without_plaso_and_every_step_without_jobs_or_source()
    {
        IngestSteps.Compose(new[] { Tool("prefetch", "Prefetch") }, @"C:\case", new[] { @"E:\" }, plasoAvailable: false)
            .Select(p => p.Spec.Label).Should().Equal(IngestSteps.ComputerDetailsLabel, "Prefetch");
        IngestSteps.Compose(Array.Empty<PlannedJob>(), @"C:\case", new[] { @"E:\" }, true).Should().BeEmpty();
        IngestSteps.Compose(new[] { Tool("prefetch", "Prefetch") }, @"C:\case", Array.Empty<string>(), true).Should().ContainSingle();
    }

    [Fact]
    public void Compose_replaces_existing_steps_and_shows_several_sources_on_one_line()
    {
        var first = IngestSteps.Compose(new[] { Tool("prefetch", "Prefetch") }, @"C:\case", new[] { @"C:\x\a", @"C:\x\b" }, true);
        first[0].Artifact.MatchedPath.Should().Be(@"C:\x\a; C:\x\b");
        IngestSteps.Compose(first, @"C:\case", new[] { @"C:\x\a" }, true).Should().HaveCount(3);
    }

    [Fact]
    public void Order_moves_rows_appended_after_the_supertimeline_before_it()
    {
        var steps = IngestSteps.Compose(new[] { Tool("prefetch", "Prefetch") }, @"C:\case", new[] { @"E:\" }, true);
        var rows = steps.Append(Tool("memory", "Memory")).Prepend(Tool("evtx", "Event logs")).ToList();
        IngestSteps.Order(rows, p => p).Select(p => p.Spec.Label)
            .Should().Equal(IngestSteps.ComputerDetailsLabel, "Event logs", "Prefetch", "Memory", IngestSteps.SupertimelineLabelFor("_Case"));
    }

    private static DiscoveredSystem Sys(string root, string host) => new() { RootPath = root, HostName = host, HostNameSource = HostDiscovery.FromSystemHive };

    private static PlannedJob HostRow(string rule, DiscoveredSystem s) =>
        new(new JobSpec("PECmd", "PECmd.exe", Array.Empty<string>(), $@"C:\case\{s.HostName}\Prefetch", rule, "Prefetch")
            { Provenance = new JobProvenance { RuleId = rule, Host = s.HostName, SystemRoot = s.RootPath } }, true,
            new DetectedArtifact(rule, rule, "PECmd", "Prefetch", Path.Combine(s.RootPath, "Windows", "Prefetch"), false, SystemRoot: s.RootPath, System: s), false);

    [Fact]
    public void Compose_plans_one_supertimeline_per_host_into_its_timeline_folder()
    {
        var ws = Sys(@"E:\src\WS01\C", "WS01");
        var dc = Sys(@"E:\src\DC01\C", "DC01");
        var mem = Sys(@"E:\src\MEMHOST", "MEMHOST");
        var jobs = new[]
        {
            HostRow("prefetch", ws), HostRow("amcache", ws), HostRow("evtx", dc),
            HostRow(MemoryTriage.RuleId, mem), HostRow(NetworkTriage.RuleId, mem), HostRow(EzForensics.Core.Rita.RitaJobs.RuleId, mem),
        };
        var plan = IngestSteps.Compose(jobs, @"C:\case", new[] { @"E:\src" }, plasoAvailable: true);

        var timelines = plan.Where(IngestSteps.IsSupertimeline).ToList();
        timelines.Select(p => p.Spec.Label).Should().Equal("Supertimeline (plaso): WS01", "Supertimeline (plaso): DC01");
        plan.Skip(plan.Count - 2).Should().OnlyContain(p => IngestSteps.IsSupertimeline(p), "the timelines are the last rows");
        timelines.Select(p => p.Spec.OutputDir).Should().Equal(@"C:\case\WS01\Timeline", @"C:\case\DC01\Timeline");
        timelines.Select(p => IngestSteps.Sources(p).Single()).Should().Equal(ws.RootPath, dc.RootPath);
        timelines.Select(IngestSteps.SupertimelineHost).Should().Equal("WS01", "DC01");
        timelines.Select(p => p.Spec.Provenance!.Host).Should().Equal("WS01", "DC01");
        timelines.Should().OnlyContain(p => !p.SelectedByDefault);
        timelines[0].Artifact.System.Should().BeSameAs(ws);
        plan.Select(PlannedJobKey).Should().OnlyHaveUniqueItems("every timeline row has its own key");
    }

    private static string PlannedJobKey(PlannedJob p) => $"{p.Artifact.RuleId}|{p.Artifact.MatchedPath}|{p.Spec.Label}";

    [Fact]
    public void Evidence_of_no_system_gets_one_case_wide_timeline_and_memory_or_pcap_only_sources_get_none()
    {
        var ws = Sys(@"E:\src\WS01\C", "WS01");
        // a loose folder with hits but no system, next to a host
        var plan = IngestSteps.Compose(new[] { HostRow("prefetch", ws), Tool("lnk", "Lnk") }, @"C:\case", new[] { @"E:\src", @"F:\loose" }, true);
        var timelines = plan.Where(IngestSteps.IsSupertimeline).ToList();
        timelines.Should().HaveCount(2);
        timelines[1].Spec.Label.Should().Be("Supertimeline (plaso): " + IngestSteps.UnattributedHostLabel);
        timelines[1].Spec.OutputDir.Should().Be(@"C:\case\_Case\Timeline");
        IngestSteps.Sources(timelines[1]).Should().Equal(@"F:\loose");
        IngestSteps.SupertimelineHost(timelines[1]).Should().Be(EzForensics.Core.Case.CaseIndex.CaseWideFolder);

        // only memory and capture rows: no timeline at all
        var mem = Sys(@"E:\mem", "MEMHOST");
        IngestSteps.Compose(new[] { HostRow(MemoryTriage.RuleId, mem), HostRow(NetworkTriage.RuleId, mem) }, @"C:\case", new[] { @"E:\mem" }, true)
            .Should().NotContain(p => IngestSteps.IsSupertimeline(p));
    }

    [Fact]
    public void A_host_timeline_follows_its_system_tick()
    {
        var ws = Sys(@"E:\src\WS01\C", "WS01");
        var dc = Sys(@"E:\src\DC01\C", "DC01");
        var plan = IngestSteps.Compose(new[] { HostRow("prefetch", ws), HostRow("evtx", dc) }, @"C:\case", new[] { @"E:\src" }, true);
        var choices = new List<SystemChoice> { new(ws, true, "WS01"), new(dc, false, "DC01") };
        var reasons = IngestExclusion.Reasons(plan, new IngestCardSelection(choices, Array.Empty<string>(), Array.Empty<string>()));
        reasons[plan.FindIndex(p => p.Spec.Label.EndsWith("WS01") && IngestSteps.IsSupertimeline(p))].Should().BeNull();
        reasons[plan.FindIndex(p => p.Spec.Label.EndsWith("DC01") && IngestSteps.IsSupertimeline(p))].Should().Be(IngestExclusion.DiskReason + ": DC01");
    }

    [Fact]
    public void Plaso_output_goes_to_the_output_folder_when_one_is_set()
    {
        var o = new EzForensics.Core.Plaso.PlasoRunOptions { Source = @"E:\src\WS01\C", CaseDir = @"C:\case", Stamp = "S" };
        o.TimelineDir.Should().Be(@"C:\case\Timeline");
        o.OutputDir = @"C:\case\WS01\Timeline";
        o.OutputCsv.Should().Be(@"C:\case\WS01\Timeline\SuperTimeline_plaso_S.csv");
        o.StorageFile.Should().Be(@"C:\case\WS01\Timeline\plaso\S.plaso");
    }

    [Theory]
    [InlineData(JobState.Queued, null, "Queued")]
    [InlineData(JobState.Running, null, "Running")]
    [InlineData(JobState.Succeeded, 0, "Done")]
    [InlineData(JobState.Failed, 2, "Failed (exit 2)")]
    [InlineData(JobState.Failed, null, "Failed")]
    [InlineData(JobState.Failed, 0, "Failed")]
    [InlineData(JobState.TimedOut, null, "Timed out")]
    [InlineData(JobState.Cancelled, null, "Cancelled")]
    [InlineData(JobState.Skipped, null, "Cancelled")]
    public void Outcome_text(JobState state, int? exit, string expected) => JobResultText.Text(state, exit).Should().Be(expected);

    [Fact]
    public void Representative_job_of_a_row_prefers_running_then_queued_then_not_succeeded()
    {
        JobResultText.RepresentativeIndex(Array.Empty<JobState>()).Should().Be(-1);
        JobResultText.RepresentativeIndex(new[] { JobState.Succeeded, JobState.Queued, JobState.Running }).Should().Be(2);
        JobResultText.RepresentativeIndex(new[] { JobState.Succeeded, JobState.Queued, JobState.Failed }).Should().Be(1);
        JobResultText.RepresentativeIndex(new[] { JobState.Succeeded, JobState.Failed, JobState.Succeeded }).Should().Be(1);
        JobResultText.RepresentativeIndex(new[] { JobState.Succeeded, JobState.Succeeded }).Should().Be(1);
    }
}
