using EzForensics.Core.Execution;
using EzForensics.Core.Triage;
using FluentAssertions;

namespace EzForensics.Core.Tests;

/// <summary>Ingest Disk card: the "Type:" and "Artifacts:" lines of each system, and the full list in the detail window.</summary>
public class DiskCardDetailsTests
{
    private static PlannedJob Row(string rule, string label, string root) =>
        new(new JobSpec("X", "X.exe", Array.Empty<string>(), @"C:\case\H\X", label, "X"), true,
            new DetectedArtifact(rule, label, "X", "X", Path.Combine(root, "x"), false, SystemRoot: root), false);

    [Fact]
    public void Groups_are_the_rows_of_that_system_deduplicated_in_plan_order()
    {
        var plan = new List<PlannedJob>
        {
            IngestSteps.ComputerDetails(@"C:\case", new[] { @"E:\" }),
            Row("mft", "$MFT", @"E:\A\C"),
            Row("registry", "Registry hive", @"E:\A\C"),
            Row("registry", "Registry hive", @"E:\A\C"),       // one per hive: shown once
            Row("prefetch", "Prefetch", @"E:\B\C"),           // other system
            Row("evtx", "Event logs", @"E:\A\C\"),            // trailing separator
            Row("custom-rule", "My custom artifact", @"E:\A\C"),
            Row(PersistenceTriage.RuleId, "Persistence", @"E:\A\C"),
            Row(NetworkTriage.RuleId, "Packet capture", @"E:\A\C"),   // a capture under the root is not a disk artifact
            Row(MemoryTriage.RuleId, "Memory image", @"E:\A\C"),
        };

        ArtifactDetails.ArtifactGroups(plan, @"E:\A\C").Should().Equal("$MFT", "Registry", "Event logs", "My custom artifact", "Persistence");
        ArtifactDetails.ArtifactGroups(plan, @"e:\b\c").Should().Equal("Prefetch");
        ArtifactDetails.ArtifactGroups(plan, @"E:\Z").Should().BeEmpty();
    }

    [Fact]
    public void Short_list_shows_at_most_six_groups_then_a_count()
    {
        var groups = new[] { "$MFT", "$UsnJrnl", "Registry", "Event logs", "Prefetch", "Amcache", "LNK", "Jump lists" };
        ArtifactDetails.ShortList(groups).Should().Be("$MFT, $UsnJrnl, Registry, Event logs, Prefetch, Amcache +2 more");
        ArtifactDetails.ShortList(groups.Take(6).ToList()).Should().Be("$MFT, $UsnJrnl, Registry, Event logs, Prefetch, Amcache");
        ArtifactDetails.ShortList(Array.Empty<string>()).Should().Be("none");
        ArtifactDetails.ShortList(groups, 2).Should().Be("$MFT, $UsnJrnl +6 more");
    }

    [Fact]
    public void Evidence_type_names_the_source()
    {
        DiscoveredSystem S(AcquisitionKind k) => new() { RootPath = @"E:\", HostName = "H", HostNameSource = HostDiscovery.FromDefault, AcquisitionKind = k };
        ArtifactDetails.EvidenceType(S(AcquisitionKind.CopiedCollection)).Should().Be("Folder");
        ArtifactDetails.EvidenceType(S(AcquisitionKind.MountedImageOrRawCopy)).Should().Be("Mounted drive");
        ArtifactDetails.EvidenceType(S(AcquisitionKind.LiveVolume)).Should().Be("Live drive");
        ArtifactDetails.EvidenceType(S(AcquisitionKind.ExtractedArchive)).Should().Be("Archive");
    }

    [Fact]
    public void Detail_window_box_has_the_type_and_the_full_artifact_list()
    {
        var groups = new[] { "$MFT", "$UsnJrnl", "Registry", "Event logs", "Prefetch", "Amcache", "LNK", "Jump lists" };
        var box = ArtifactDetails.Disk("WKS-01", "Windows", null, @"E:\C", new[] { "alice" }, null, "Folder", groups);
        box.Rows.Select(r => r.Key).Should().Equal("Host", "OS", "System root", "Type", "Artifacts", "Users found");
        box.Rows.First(r => r.Key == "Artifacts").Value.Should().Be(string.Join(", ", groups), "the window shows every group");
        ArtifactDetails.Disk("H", "Windows", null, @"E:\C", Array.Empty<string>(), artifacts: Array.Empty<string>())
            .Rows.First(r => r.Key == "Artifacts").Value.Should().Be("none");
    }
}
