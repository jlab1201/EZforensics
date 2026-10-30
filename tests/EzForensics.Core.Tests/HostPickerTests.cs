using EzForensics.Core.Network;
using EzForensics.Core.SystemInfo;
using EzForensics.Core.Triage;
using FluentAssertions;

namespace EzForensics.Core.Tests;

/// <summary>Triage host picker (SystemSelection) and the per-host computer details store.</summary>
public sealed class HostPickerTests : IDisposable
{
    private readonly string _dir = Path.Combine(Path.GetTempPath(), "ezforensics_picker_" + Guid.NewGuid().ToString("N")[..8]);

    public HostPickerTests() => Directory.CreateDirectory(_dir);
    public void Dispose() { try { Directory.Delete(_dir, true); } catch { } }

    private (TriageScanResult Scan, string R1, string R2) TwoHosts()
    {
        var src = Path.Combine(_dir, "src");
        var r1 = CaseLayoutTriageTests.MakeRoot(Path.Combine(src, "HOST1", "C"));
        var r2 = CaseLayoutTriageTests.MakeRoot(Path.Combine(src, "HOST2", "C"), "bob");
        return (ArtifactScanner.ScanSource(src), r1, r2);
    }

    private static List<SystemChoice> Choices(TriageScanResult scan) => scan.Systems.Select(s => new SystemChoice(s, true, s.HostName)).ToList();

    [Fact]
    public void Relabelled_host_flows_into_planned_output_dirs_for_artifacts_persistence_and_network()
    {
        var (scan, r1, _) = TwoHosts();
        var choices = Choices(scan);
        choices[0] = choices[0] with { HostName = "  DC-01 " };
        SystemSelection.Validate(choices).Should().OnlyContain(e => e == null);

        var caseDir = Path.Combine(_dir, "case");
        var found = SystemSelection.Filter(scan.Artifacts, choices);
        found.Where(a => a.SystemRoot == r1).Should().OnlyContain(a => a.Host == "DC-01");
        var plan = TriagePlanner.PlanJobs(found, TriageRules.LoadEmbedded(), caseDir);
        plan.Where(p => p.Artifact.RuleId == "prefetch").Select(p => p.Spec.OutputDir)
            .Should().Equal(Path.Combine(caseDir, "DC-01", "ProgramExecution"), Path.Combine(caseDir, "HOST2", "ProgramExecution"));
        plan.Where(p => p.Artifact.RuleId == "prefetch").Select(p => p.Spec.Provenance!.Host).Should().Equal("DC-01", "HOST2");

        var persistence = PersistenceTriage.Plan(SystemSelection.Active(choices), caseDir, @"C:\app\EzForensics.exe");
        persistence.Where(p => p.Artifact.RuleId == PersistenceTriage.RuleId).Select(p => p.Spec.OutputDir)
            .Should().Equal(Path.Combine(caseDir, "DC-01", "Persistence"), Path.Combine(caseDir, "HOST2", "Persistence"));

        var pcap = Path.Combine(r1, "capture.pcap");
        var net = NetworkTriage.Plan(new[] { new PcapFile(pcap, 10, PcapFormat.Pcap) }, caseDir, null, scan.Source, SystemSelection.Labelled(choices));
        net.Single().Spec.OutputDir.Should().Be(Path.Combine(caseDir, "DC-01", "Network"));
        net.Single().Spec.Provenance!.Host.Should().Be("DC-01");
    }

    [Fact]
    public void Unchecked_systems_are_excluded_from_artifacts_persistence_and_captures()
    {
        var (scan, r1, r2) = TwoHosts();
        var choices = Choices(scan);
        choices[1] = choices[1] with { Included = false };

        var found = SystemSelection.Filter(scan.Artifacts, choices);
        found.Should().NotBeEmpty().And.OnlyContain(a => a.SystemRoot == r1);
        scan.Artifacts.Count.Should().BeGreaterThan(found.Count);

        var caseDir = Path.Combine(_dir, "case");
        TriagePlanner.PlanJobs(found, TriageRules.LoadEmbedded(), caseDir).Should().OnlyContain(p => p.Spec.OutputDir.StartsWith(Path.Combine(caseDir, "HOST1")));
        var active = SystemSelection.Active(choices);
        active.Select(s => s.RootPath).Should().Equal(r1);
        PersistenceTriage.Plan(active, caseDir, @"C:\app\x.exe").Should().OnlyContain(p => p.Spec.OutputDir.StartsWith(Path.Combine(caseDir, "HOST1")));

        SystemSelection.IsRootIncluded(choices, r2).Should().BeFalse();
        SystemSelection.IsRootIncluded(choices, r1).Should().BeTrue();
        var pcaps = new[] { new PcapFile(Path.Combine(r1, "a.pcap"), 1, PcapFormat.Pcap), new PcapFile(Path.Combine(r2, "b.pcap"), 1, PcapFormat.Pcap),
            new PcapFile(Path.Combine(_dir, "src", "loose.pcap"), 1, PcapFormat.Pcap) };
        SystemSelection.FilterPcaps(pcaps, choices).Select(p => p.Name).Should().Equal("a.pcap", "loose.pcap");
    }

    [Fact]
    public void Host_labels_must_be_folder_safe_unique_and_not_reserved()
    {
        SystemSelection.ValidateHostLabel("WKS-01", new[] { "OTHER" }).Should().BeNull();
        SystemSelection.ValidateHostLabel("HOST (2)", Array.Empty<string>()).Should().BeNull();
        SystemSelection.ValidateHostLabel("", Array.Empty<string>()).Should().NotBeNull();
        SystemSelection.ValidateHostLabel("a\\b", Array.Empty<string>()).Should().Contain("folder name");
        SystemSelection.ValidateHostLabel("bad:name", Array.Empty<string>()).Should().NotBeNull();
        SystemSelection.ValidateHostLabel("trailing.", Array.Empty<string>()).Should().NotBeNull();
        SystemSelection.ValidateHostLabel("Timeline", Array.Empty<string>()).Should().Contain("reserved");
        SystemSelection.ValidateHostLabel("host1", new[] { "HOST1" }).Should().Contain("already");
        // another system's label "Timeline" goes to the folder Host_Timeline: the same folder as this label
        SystemSelection.ValidateHostLabel("Host_Timeline", new[] { "Timeline" }).Should().Contain("already");
        SystemSelection.ValidateHostLabel("host_registry", new[] { "Registry" }).Should().Contain("already");
        SystemSelection.ValidateHostLabel("Host_Other", new[] { "Timeline" }).Should().BeNull();

        var (scan, _, _) = TwoHosts();
        var choices = Choices(scan);
        choices[1] = choices[1] with { HostName = "host1" };
        SystemSelection.Validate(choices).Should().OnlyContain(e => e != null && e.Contains("already"));
        // An unusable label never reaches the planner: the effective system keeps its discovered name when the label is empty.
        (choices[0] with { HostName = " " }).Effective.HostName.Should().Be("HOST1");
    }

    // ------------------------------------------------------------------ computer details store

    private static ComputerDetailsReport Report(string title, string source, DateTime when, string? host = null) => new()
    {
        Source = source, GeneratedUtc = when,
        Systems = { new SystemDetails { Title = title, Summary = title, Host = host ?? "", Fields = { new DetailField("System", "Computer name", title, "SYSTEM") } } },
    };

    [Fact]
    public void Computer_details_per_host_files_are_merged_and_legacy_still_loads()
    {
        var caseDir = Path.Combine(_dir, "case");
        ComputerDetailsStore.Load(caseDir).Should().BeNull();

        var t1 = new DateTime(2026, 1, 1, 0, 0, 0, DateTimeKind.Utc);
        ComputerDetailsStore.Write(Report("HOST1", "src", t1, "HOST1"), ComputerDetailsStore.HostFolderFor(caseDir, "HOST1"));
        ComputerDetailsStore.Write(Report("HOST2", "src", t1.AddHours(1), "HOST2"), ComputerDetailsStore.HostFolderFor(caseDir, "HOST2"));
        File.Exists(Path.Combine(caseDir, "HOST1", "SystemInfo", "ComputerDetails.json")).Should().BeTrue();
        File.Exists(ComputerDetailsStore.HostCsvPathFor(caseDir, "HOST2")).Should().BeTrue();

        var merged = ComputerDetailsStore.Load(caseDir)!;
        merged.Systems.Select(s => (s.Title, s.Host)).Should().Equal(("HOST1", "HOST1"), ("HOST2", "HOST2"));
        merged.GeneratedUtc.Should().Be(t1.AddHours(1));
        merged.Source.Should().Be("src");
        ComputerDetailsStore.TryLoad(caseDir)!.Systems.Should().HaveCount(2);
        ComputerDetailsStore.CsvPathFor(caseDir).Should().Be(ComputerDetailsStore.HostCsvPathFor(caseDir, "HOST1"));

        // Legacy file next to the host folders: included, host stays empty.
        ComputerDetailsStore.Write(Report("OLD", "old", t1), ComputerDetailsStore.FolderFor(caseDir));
        ComputerDetailsStore.Load(caseDir)!.Systems.Select(s => s.Host).Should().Equal("", "HOST1", "HOST2");
        ComputerDetailsStore.CsvPathFor(caseDir).Should().Be(Path.Combine(caseDir, "SystemInfo", "ComputerDetails.csv"));
    }

    [Fact]
    public void Computer_details_legacy_only_case_loads_unchanged()
    {
        var caseDir = Path.Combine(_dir, "legacy");
        ComputerDetailsStore.Write(Report("OLD", "old", DateTime.UtcNow), ComputerDetailsStore.FolderFor(caseDir));
        var r = ComputerDetailsStore.Load(caseDir)!;
        r.Systems.Single().Host.Should().BeEmpty();
        r.Source.Should().Be("old");
    }

    [Fact]
    public void Computer_details_switch_records_the_host_and_reads_a_system_root()
    {
        var root = CaseLayoutTriageTests.MakeRoot(Path.Combine(_dir, "src", "WKS-9"));
        var outDir = ComputerDetailsStore.HostFolderFor(Path.Combine(_dir, "case"), "WKS-9");
        var w = new StringWriter();
        // No hives: exit code 2, but the JSON is still written for the host folder (the same as the legacy path).
        ComputerDetailsStore.RunCommandLine(new[] { "--source", root, "--out", outDir, "--host", "WKS-9" }, w).Should().BeOneOf(0, 2);
        File.Exists(Path.Combine(outDir, ComputerDetailsStore.JsonName)).Should().BeTrue();
    }
}
