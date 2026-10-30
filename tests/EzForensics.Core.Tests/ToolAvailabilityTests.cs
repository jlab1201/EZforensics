using EzForensics.Core.Execution;
using EzForensics.Core.Memory;
using EzForensics.Core.Network;
using EzForensics.Core.Tools;
using EzForensics.Core.Triage;
using FluentAssertions;
using Xunit;

namespace EzForensics.Core.Tests;

/// <summary>Ingest: greyed-out planned rows and Detected artifacts cards, and the detail window rows.</summary>
public class ToolAvailabilityTests
{
    private sealed class FakeLocator : IToolLocator
    {
        public HashSet<string> Present { get; } = new(StringComparer.OrdinalIgnoreCase);
        public void Rescan(string toolsRoot) { }
        public InstalledTool? Resolve(string exeName, BuildFlavor? preferred = null) => null;
        public IReadOnlyList<InstalledTool> All => Array.Empty<InstalledTool>();
    }

    private static PlannedJob Row(string rule, string exe = "PECmd.exe") =>
        new(new JobSpec("PECmd", exe, Array.Empty<string>(), @"C:\case\H\X", rule, "X"), true,
            new DetectedArtifact(rule, rule, "PECmd", "X", @"E:\x", false), false);

    [Theory]
    [InlineData(NetworkTriage.RuleId, true, false, false, false)]
    [InlineData(MemoryTriage.RuleId, false, false, true, false)]
    [InlineData(IngestSteps.SupertimelineRuleId, false, false, false, true)]
    public void Special_rows_are_unavailable_when_their_tool_is_missing(string rule, bool zeek, bool rita, bool vol, bool plaso)
    {
        var p = Row(rule);
        var none = new ToolStatus();
        ToolAvailability.RowReason(p, none, null).Should().BeNull();
        ToolAvailability.RowReason(p, new ToolStatus(zeek, rita, vol, plaso), null).Should().NotBeNullOrEmpty();
    }

    [Fact]
    public void Rita_row_needs_zeek_and_rita_and_computer_details_needs_nothing()
    {
        var rita = Row(Rita.RitaJobs.RuleId);
        ToolAvailability.RowReason(rita, new ToolStatus(ZeekMissing: true), null).Should().Contain("Zeek");
        ToolAvailability.RowReason(rita, new ToolStatus(RitaMissing: true), null).Should().Contain("RITA");
        var everything = new ToolStatus(true, true, true, true);
        ToolAvailability.RowReason(Row(IngestSteps.ComputerDetailsRuleId), everything, null).Should().BeNull();
        ToolAvailability.RowReason(Row(PersistenceTriage.RuleId), everything, null).Should().BeNull();
    }

    [Fact]
    public void Ez_row_is_unavailable_when_its_tool_is_not_installed()
    {
        var p = Row("prefetch", "PECmd.exe");
        ToolAvailability.RowReason(p, new ToolStatus(), new FakeLocator()).Should().Contain("PECmd.exe");
        ToolAvailability.IsEzRow(p).Should().BeTrue();
        ToolAvailability.IsEzRow(Row(MemoryTriage.RuleId)).Should().BeFalse();
        ToolAvailability.IsEzRow(Row(IngestSteps.ComputerDetailsRuleId)).Should().BeFalse();
    }

    [Fact]
    public void Cards_grey_out_by_their_tool_and_rita_alone_is_only_a_note()
    {
        ToolAvailability.Card(DetectedCardKind.Memory, new ToolStatus(VolatilityMissing: true), true, false)
            .Should().Match<CardState>(c => c.Disabled && c.SetUpText == "Set up Volatility" && c.Target == SetUpTarget.Volatility);
        ToolAvailability.Card(DetectedCardKind.Network, new ToolStatus(ZeekMissing: true, RitaMissing: true), true, false)
            .Should().Match<CardState>(c => c.Disabled && c.Target == SetUpTarget.Zeek);
        var rita = ToolAvailability.Card(DetectedCardKind.Network, new ToolStatus(RitaMissing: true), true, false);
        rita.Disabled.Should().BeFalse();
        rita.Note.Should().NotBeNull();
        rita.Target.Should().Be(SetUpTarget.Rita);
        ToolAvailability.Card(DetectedCardKind.Disk, new ToolStatus(), true, true).Should().Match<CardState>(c => c.Disabled && c.Target == SetUpTarget.Wizard);
        ToolAvailability.Card(DetectedCardKind.Disk, new ToolStatus(), true, false).Should().Be(CardState.Ready);
        ToolAvailability.Card(DetectedCardKind.Memory, new ToolStatus(VolatilityMissing: true), false, false).Should().Be(CardState.Ready);
    }

    [Fact]
    public void Detail_boxes_list_only_the_necessary_keys()
    {
        var disk = ArtifactDetails.Disk("WKS-01", "Windows", "Windows 10 Pro 22H2 (build 19045.3803)", @"E:\C", new[] { "alice", "bob" });
        disk.Rows.Select(r => r.Key).Should().Equal("Host", "OS", "Build", "System root", "Users found");
        disk.Rows.First(r => r.Key == "Build").Value.Should().Be("19045.3803");
        disk.Rows.First(r => r.Key == "Users found").Value.Should().Be("alice, bob");
        ArtifactDetails.Disk("H", "Windows", null, @"E:\C", Array.Empty<string>()).Rows.Select(r => r.Key).Should().NotContain("Build");

        var mem = ArtifactDetails.Memory(new MemoryImage(@"E:\m.raw", 2048, MemoryFormat.Raw, MemoryOs.Unknown, null, null, null), null);
        mem.Rows.Select(r => r.Key).Should().Equal("File", "Size");
        ArtifactDetails.Network(new PcapFile(@"E:\a.pcapng", 1024, PcapFormat.PcapNg)).Rows.Select(r => r.Key).Should().Equal("File", "Size", "Format");
    }
}
