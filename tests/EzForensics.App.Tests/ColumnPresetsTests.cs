using EzForensics.App.Services;
using FluentAssertions;
using Xunit;

namespace EzForensics.App.Tests;

public class ColumnPresetsTests
{
    private static string[] H(params string[] h) => h;

    [Fact]
    public void Evtx_puts_time_first_and_hides_record_ids()
    {
        var h = H("RecordNumber", "EventRecordId", "TimeCreated", "EventId", "Level", "Provider", "Channel", "Computer", "MapDescription", "UserName", "PayloadData1");
        var p = ColumnPresets.Pick(h);
        p.Front.Select(i => h[i]).Should().StartWith(new[] { "TimeCreated", "EventId", "Channel", "Computer", "MapDescription", "UserName", "PayloadData1" });
        p.Hide.Select(i => h[i]).Should().BeEquivalentTo(new[] { "RecordNumber", "EventRecordId" });
        var order = ColumnPresets.DisplayOrder(p, h.Length);
        order[0].Should().Be(2);
        order.Should().HaveCount(h.Length).And.OnlyHaveUniqueItems();
    }

    [Fact]
    public void Mft_and_prefetch_have_presets()
    {
        var m = H("EntryNumber", "SequenceNumber", "ParentEntryNumber", "ParentSequenceNumber", "ParentPath", "FileName", "Created0x10");
        var pm = ColumnPresets.Pick(m);
        m[pm.Front[0]].Should().Be("Created0x10");
        pm.Hide.Select(i => m[i]).Should().BeEquivalentTo(new[] { "SequenceNumber", "ParentSequenceNumber" });

        var pf = H("SourceFilename", "SourceCreated", "ExecutableName", "RunCount", "LastRun");
        pf[ColumnPresets.Pick(pf).Front[0]].Should().Be("LastRun");
    }

    [Fact]
    public void Generic_moves_first_time_column_to_front_only_when_not_already_first()
    {
        ColumnPresets.Pick(H("Name", "Path", "LastWriteTimestamp", "Size")).Front.Should().Equal(2);
        ColumnPresets.Pick(H("Timestamp", "Name")).IsEmpty.Should().BeTrue();
        ColumnPresets.Pick(H("Name", "Size")).IsEmpty.Should().BeTrue();
    }
}
