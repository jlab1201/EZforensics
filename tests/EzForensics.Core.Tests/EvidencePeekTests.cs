using EzForensics.Core.Spectre;
using FluentAssertions;

namespace EzForensics.Core.Tests;

public class EvidencePeekTests
{
    [Fact]
    public void Build_keeps_key_columns_with_values_and_cuts_long_cells()
    {
        var headers = new[] { "Empty", "Payload", "EventId", "Other", "TimeCreated", "X1", "X2" };
        var rows = new[] { new[] { "", new string('a', 60), "4688", "o", "2026-01-01", "1", "2" } };
        var p = EvidencePeek.Build("Security.csv", headers, rows, total: 7, maxColumns: 4, maxCell: 20);
        p.Headers.Should().Equal("Payload", "EventId", "Other", "TimeCreated");   // file order; Empty skipped
        p.Rows[0][0].Should().HaveLength(20).And.EndWith("…");
        p.TotalRows.Should().Be(7);
    }

    [Fact]
    public async Task ReadAsync_reads_the_first_rows_behind_the_target()
    {
        var dir = Path.Combine(Path.GetTempPath(), "ezf-peek-" + Guid.NewGuid().ToString("N")[..8]);
        Directory.CreateDirectory(dir);
        try
        {
            var csv = Path.Combine(dir, "Security.csv");
            File.WriteAllText(csv, "TimeCreated,EventId,EventRecordId\n2026-01-01,4688,100\n2026-01-02,4624,200\n2026-01-03,4688,300\n2026-01-04,1,400\n");
            var p = await EvidencePeek.ReadAsync(new EvidenceTarget(csv, new long[] { 3 }, new[] { "100", "200", "300" }));
            p.FileName.Should().Be("Security.csv");
            p.TotalRows.Should().Be(4);
            p.Rows.Should().HaveCount(3);
            p.Rows[0][0].Should().Be("2026-01-01");
            var none = await EvidencePeek.ReadAsync(new EvidenceTarget(csv, Array.Empty<long>(), new[] { "999" }));
            none.IsEmpty.Should().BeTrue();
        }
        finally { try { Directory.Delete(dir, true); } catch (IOException) { } }
    }
}
