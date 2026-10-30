using EzForensics.Core.Execution;
using EzForensics.Core.Network;

using EzForensics.Core.Rita;
using EzForensics.Core.Triage;
using EzForensics.Core.Zeek;
using FluentAssertions;
using Xunit;

namespace EzForensics.Core.Tests;

/// <summary>Ingest planning of RITA rows after the Zeek rows (NetworkTriage.PlanRita / ZeekJobsFor / ResolveRita).</summary>
public class RitaTriageTests
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

    private static DiscoveredSystem Host(string root, string name) =>
        new() { RootPath = root, HostName = name, HostNameSource = HostDiscovery.FromFolderName };

    [Fact]
    public void One_row_per_host_folder_and_no_all_captures_row()
    {
        using var t = new TempDir();
        var a = Path.Combine(t.Path, "src", "HOSTA");
        var b = Path.Combine(t.Path, "src", "HOSTB");
        var pcaps = new[] { Capture(a, "x.pcap"), Capture(a, "y.pcap"), Capture(b, "z.pcap") };
        var caseDir = Path.Combine(t.Path, "case");
        var zeek = NetworkTriage.Plan(PcapScanner.Scan(Path.Combine(t.Path, "src")).Files, caseDir, FakeZeek(), systems: new[] { Host(a, "HOSTA"), Host(b, "HOSTB") });
        zeek.Should().HaveCount(3);

        var rita = NetworkTriage.PlanRita(zeek, caseDir, FakeRita());
        rita.Should().HaveCount(2);
        rita.Should().OnlyContain(r => NetworkTriage.IsRita(r) && r.SelectedByDefault && r.Spec.ToolId == RitaJobs.ToolId && r.Spec.ExePath == null);
        rita.Select(r => r.Spec.OutputDir).Should().Equal(Path.Combine(caseDir, "HOSTA", "Network"), Path.Combine(caseDir, "HOSTB", "Network"));
        rita.Select(r => r.Artifact.MatchCount).Should().Equal(2, 1);
        rita.Should().NotContain(r => NetworkTriage.IsCaseWideRita(r));
        rita.Select(r => r.Spec.Provenance!.Host).Should().Equal("HOSTA", "HOSTB");
    }

    [Fact]
    public void A_capture_with_no_identified_host_gets_the_case_wide_row_and_uninstalled_rows_are_unticked()
    {
        var zeek = NetworkTriage.Plan(new[] { new PcapFile(@"D:\in\a.pcap", 100, PcapFormat.Pcap) }, @"C:\case", null);
        var rita = NetworkTriage.PlanRita(zeek, @"C:\case", null);
        rita.Should().ContainSingle();
        rita[0].SelectedByDefault.Should().BeFalse();
        rita[0].Spec.OutputDir.Should().Be(@"C:\case\_Case\Network");
        NetworkTriage.IsCaseWideRita(rita[0]).Should().BeTrue();
        rita[0].Spec.Label.Should().Contain("case-wide (1 capture with no identified host");
        NetworkTriage.PlanRita(Array.Empty<PlannedJob>(), @"C:\case", FakeRita()).Should().BeEmpty();
    }

    [Fact]
    public void Two_unidentified_captures_share_one_case_wide_row()
    {
        var pcaps = new[] { new PcapFile(@"D:\in\a.pcap", 100, PcapFormat.Pcap), new PcapFile(@"D:\in\b.pcap", 100, PcapFormat.Pcap) };
        var rita = NetworkTriage.PlanRita(NetworkTriage.Plan(pcaps, @"C:\case", null), @"C:\case", FakeRita());
        rita.Should().ContainSingle();
        rita[0].Spec.OutputDir.Should().Be(@"C:\case\_Case\Network");
        rita[0].Artifact.MatchCount.Should().Be(2);
    }
    [Fact]
    public async Task Rita_rows_resolve_against_this_runs_zeek_jobs_and_publish_after_them()
    {
        using var t = new TempDir();
        var a = Path.Combine(t.Path, "src", "HOSTA");
        var b = Path.Combine(t.Path, "src", "HOSTB");
        Capture(a, "x.pcap");
        Capture(b, "z.pcap");
        Capture(Path.Combine(t.Path, "src"), "router.pcap");   // no identified host: _Case
        var caseDir = Path.Combine(t.Path, "case");
        var zeekPlan = NetworkTriage.Plan(PcapScanner.Scan(Path.Combine(t.Path, "src")).Files, caseDir, FakeZeek(), systems: new[] { Host(a, "HOSTA"), Host(b, "HOSTB") });
        var ritaPlan = NetworkTriage.PlanRita(zeekPlan, caseDir, FakeRita());
        ritaPlan.Should().HaveCount(3);
        var hostA = ritaPlan.Single(r => r.Spec.Provenance!.Host == "HOSTA");
        var caseRow = ritaPlan.Single(NetworkTriage.IsCaseWideRita);

        var q = new JobQueue(new FakeLocator(null), 3, TimeSpan.FromMinutes(2));
        var run = zeekPlan.Select(p => (Planned: p, Job: q.Enqueue(p.Spec, caseDir))).ToList();
        NetworkTriage.ZeekJobsFor(hostA, run).Should().ContainSingle().Which.Should().BeSameAs(run.Single(z => z.Planned.Spec.Provenance!.Host == "HOSTA").Job);
        NetworkTriage.ZeekJobsFor(caseRow, run).Should().ContainSingle().Which.Should().BeSameAs(run.Single(z => z.Planned.Spec.Provenance!.Host == RitaJobs.CaseWideHost).Job);

        var rita = ritaPlan.Select(r => q.Enqueue(NetworkTriage.ResolveRita(r, FakeRita(), caseDir, NetworkTriage.ZeekJobsFor(r, run), RitaOptions.Default), caseDir)).ToList();
        await q.WhenAllAsync();
        foreach (var j in rita) await j.Recorded;

        run.Should().OnlyContain(z => z.Job.State == JobState.Succeeded);
        rita.Should().OnlyContain(j => j.State == JobState.Succeeded);
        rita.Should().OnlyContain(j => j.StartUtc >= run.Min(z => z.Job.EndUtc!.Value));
        rita[0].Spec.Label.Should().Be(ritaPlan[0].Spec.Label);
        int ci = ritaPlan.ToList().IndexOf(caseRow), hi = ritaPlan.ToList().IndexOf(hostA);
        File.Exists(Path.Combine(caseDir, "HOSTA", "Network", RitaResultConverter.ThreatsFile)).Should().BeTrue();
        File.Exists(Path.Combine(caseDir, "HOSTB", "Network", RitaResultConverter.ThreatsFile)).Should().BeTrue();
        File.Exists(Path.Combine(caseDir, RitaJobs.CaseWideHost, "Network", RitaResultConverter.ThreatsFile)).Should().BeTrue();
        rita[ci].Details["rita.scope"].Should().Be("case");
        rita[hi].Details["rita.scope"].Should().Be("host");
    }
}
