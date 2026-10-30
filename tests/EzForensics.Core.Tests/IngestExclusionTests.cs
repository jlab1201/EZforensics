using EzForensics.Core.Execution;
using EzForensics.Core.Network;
using EzForensics.Core.Triage;
using FluentAssertions;

namespace EzForensics.Core.Tests;

/// <summary>Ingest card ticks only exclude rows (the plan is built from every artifact): disk rows follow their system, memory / network their own card.</summary>
public sealed class IngestExclusionTests : IDisposable
{
    private readonly string _dir = Path.Combine(Path.GetTempPath(), "ezforensics_excl_" + Guid.NewGuid().ToString("N")[..8]);

    public IngestExclusionTests() => Directory.CreateDirectory(_dir);
    public void Dispose() { try { Directory.Delete(_dir, true); } catch { } }

    private static PlannedJob Memory(string image) =>
        new(new JobSpec(EzForensics.Core.Memory.MemoryJobs.ToolId, "vol", Array.Empty<string>(), @"C:\case\H\Memory", "windows.info", "Memory"), true,
            new DetectedArtifact(MemoryTriage.RuleId, "Memory image", "volatility3", "Memory", image, true), false);

    private (TriageScanResult Scan, string R1, string R2, string Case, List<SystemChoice> Choices) Setup()
    {
        var src = Path.Combine(_dir, "src");
        var r1 = CaseLayoutTriageTests.MakeRoot(Path.Combine(src, "HOST1", "C"));
        var r2 = CaseLayoutTriageTests.MakeRoot(Path.Combine(src, "HOST2", "C"), "bob");
        var scan = ArtifactScanner.ScanSource(src);
        var choices = scan.Systems.Select(s => new SystemChoice(s, true, s.HostName)).ToList();
        return (scan, r1, r2, Path.Combine(_dir, "case"), choices);
    }

    private static List<PlannedJob> FullPlan(TriageScanResult scan, List<SystemChoice> choices, string caseDir, string pcap, string image, string source)
    {
        var jobs = new List<PlannedJob>(TriagePlanner.PlanJobs(SystemSelection.Label(scan.Artifacts, choices), TriageRules.LoadEmbedded(), caseDir));
        var zeek = NetworkTriage.Plan(new[] { new PcapFile(pcap, 10, PcapFormat.Pcap) }, caseDir, null, source, SystemSelection.Labelled(choices));
        jobs.AddRange(zeek);
        jobs.AddRange(NetworkTriage.PlanRita(zeek, caseDir, null));
        jobs.Add(Memory(image));
        return IngestSteps.Compose(jobs, caseDir, new[] { source }, plasoAvailable: true);
    }

    [Fact]
    public void Unticking_every_disk_system_keeps_memory_and_network_rows_planned_and_included()
    {
        var (scan, r1, r2, caseDir, choices) = Setup();
        // the capture and the memory image sit under a disk root: they must not drop out with the disk
        var pcap = Path.Combine(r2, "capture.pcap");
        var image = Path.Combine(r1, "memory.raw");
        var plan = FullPlan(scan, choices, caseDir, pcap, image, scan.Source);
        var off = choices.Select(c => c with { Included = false }).ToList();

        var reasons = IngestExclusion.Reasons(plan, new IngestCardSelection(off, Array.Empty<string>(), Array.Empty<string>()));

        plan.Should().Contain(p => IngestSteps.IsComputerDetails(p)).And.Contain(p => IngestSteps.IsSupertimeline(p));
        for (int i = 0; i < plan.Count; i++)
        {
            var rule = plan[i].Artifact.RuleId;
            if (rule == MemoryTriage.RuleId || rule == NetworkTriage.RuleId || rule == EzForensics.Core.Rita.RitaJobs.RuleId)
                reasons[i].Should().BeNull(plan[i].Spec.Label + " follows only its own card");
            else
                reasons[i].Should().StartWith(IngestExclusion.DiskReason, plan[i].Spec.Label + " follows its system");
        }
    }

    [Fact]
    public void One_unticked_system_excludes_only_its_rows_with_the_system_name()
    {
        var (scan, r1, r2, caseDir, choices) = Setup();
        var plan = FullPlan(scan, choices, caseDir, Path.Combine(_dir, "loose.pcap"), Path.Combine(_dir, "mem.raw"), scan.Source);
        choices[1] = choices[1] with { Included = false };
        var reasons = IngestExclusion.Reasons(plan, new IngestCardSelection(choices, Array.Empty<string>(), Array.Empty<string>()));

        var disk = plan.Select((p, i) => (p, r: reasons[i])).Where(x => ToolAvailability.IsEzRow(x.p)).ToList();
        disk.Where(x => x.p.Artifact.SystemRoot == r2).Should().NotBeEmpty().And.OnlyContain(x => x.r == IngestExclusion.DiskReason + ": " + choices[1].HostName);
        disk.Where(x => x.p.Artifact.SystemRoot == r1).Should().NotBeEmpty().And.OnlyContain(x => x.r == null);
        reasons[plan.FindIndex(IngestSteps.IsComputerDetails)].Should().BeNull("one system is still ticked");
        reasons[plan.FindIndex(IngestSteps.IsSupertimeline)].Should().BeNull();
    }

    [Fact]
    public void Memory_and_network_cards_exclude_their_rows_and_rita_follows_its_captures()
    {
        var (scan, _, r2, caseDir, choices) = Setup();
        var pcap = Path.Combine(r2, "capture.pcap");
        var image = Path.Combine(_dir, "mem.raw");
        var plan = FullPlan(scan, choices, caseDir, pcap, image, scan.Source);

        var reasons = IngestExclusion.Reasons(plan, new IngestCardSelection(choices, new[] { image.ToUpperInvariant() }, new[] { pcap }));

        reasons[plan.FindIndex(p => p.Artifact.RuleId == MemoryTriage.RuleId)].Should().Be(IngestExclusion.MemoryReason);
        reasons[plan.FindIndex(p => p.Artifact.RuleId == NetworkTriage.RuleId)].Should().Be(IngestExclusion.NetworkReason);
        reasons[plan.FindIndex(NetworkTriage.IsRita)].Should().Be(IngestExclusion.NetworkReason, "its only capture is unticked");
        reasons.Where((_, i) => ToolAvailability.IsEzRow(plan[i])).Should().OnlyContain(r => r == null);
    }

    [Fact]
    public void Nothing_is_excluded_when_everything_is_ticked()
    {
        var (scan, _, _, caseDir, choices) = Setup();
        var plan = FullPlan(scan, choices, caseDir, Path.Combine(_dir, "a.pcap"), Path.Combine(_dir, "m.raw"), scan.Source);
        IngestExclusion.Reasons(plan, new IngestCardSelection(choices, Array.Empty<string>(), Array.Empty<string>())).Should().OnlyContain(r => r == null);
        IngestExclusion.Reasons(plan, IngestCardSelection.All).Should().OnlyContain(r => r == null);
    }
}
