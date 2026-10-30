using System.Diagnostics;
using System.Text;
using EzForensics.Core.Results;
using FluentAssertions;

namespace EzForensics.Core.Tests;

public class ResultsCsvTests
{
    private static string Tricky => Path.Combine(TriageTestData.Root, "sample", "tricky.csv");

    [Fact]
    public async Task Tricky_csv_rows_parse()
    {
        using var idx = await CsvIndex.OpenAsync(Tricky, null, default);
        await idx.IndexingCompleted;
        idx.Headers.Should().Equal("Id", "Name", "Notes", "SourceCreated");
        idx.RowCount.Should().Be(4);
        idx.ReadRow(0).Should().Equal("1", "Smith, John", "line one\r\nline two", "2024-01-02 03:04:05.1234567");
        idx.ReadRow(1).Should().Equal("2", "Plain", "He said \"hi\"", "2024-02-03 00:00:00");
        idx.ReadRow(2).Should().Equal("3", "", "", "2023-12-31 23:59:59");
        idx.ReadRow(3)[1].Should().Be("Last");
        idx.ReadRow(3)[3].Should().Be("2025-01-01 00:00:00");
    }

    [Fact]
    public async Task Lf_only_and_no_bom()
    {
        var p = Path.GetTempFileName();
        try
        {
            File.WriteAllText(p, "a,b\n\"x\ny\",2\n3,\"q\"\"\"\n", new UTF8Encoding(false));
            using var idx = await CsvIndex.OpenAsync(p, null, default);
            await idx.IndexingCompleted;
            idx.RowCount.Should().Be(2);
            idx.ReadRow(0).Should().Equal("x\ny", "2");
            idx.ReadRow(1).Should().Equal("3", "q\"");
        }
        finally { File.Delete(p); }
    }

    [Fact]
    public async Task Large_file_indexes_quickly_and_reads_back()
    {
        var p = Path.GetTempFileName();
        try
        {
            using (var w = new StreamWriter(p, false, new UTF8Encoding(false)))
            {
                w.Write("Id,Name,Time,Num\r\n");
                for (int i = 0; i < 200_000; i++)
                    w.Write($"{i},\"name, {i}\",{new DateTime(2020, 1, 1).AddMinutes(i):yyyy-MM-dd HH:mm:ss},{(i * 7) % 1000}\r\n");
            }
            var sw = Stopwatch.StartNew();
            using var idx = await CsvIndex.OpenAsync(p, null, default);
            await idx.IndexingCompleted;
            sw.Stop();
            sw.Elapsed.Should().BeLessThan(TimeSpan.FromSeconds(5));
            idx.RowCount.Should().Be(200_000);
            idx.ReadRow(199_999)[0].Should().Be("199999");
            idx.ReadRow(12_345)[1].Should().Be("name, 12345");

            // filter
            var hits = await FilterEngine.FilterAsync(idx, new FilterSpec { GlobalText = "\"name, 77\"" }, null, default);
            hits.Length.Should().Be(Enumerable.Range(0, 200_000).Count(i => $"name, {i}".Contains("name, 77")));

            var spec = new FilterSpec
            {
                DateColumn = "Time",
                DateFrom = new DateTime(2020, 1, 1, 1, 0, 0),
                DateTo = new DateTime(2020, 1, 1, 1, 9, 0),
            };
            var dh = await FilterEngine.FilterAsync(idx, spec, null, default);
            dh.Should().Equal(Enumerable.Range(60, 10));

            var sorted = await FilterEngine.SortAsync(idx, dh, "Num", descending: true);
            sorted.Should().HaveCount(10);
            sorted[0].Should().Be(dh.OrderByDescending(i => (i * 7) % 1000).ThenBy(i => i).First());
        }
        finally { File.Delete(p); }
    }

    [Fact]
    public async Task Rows_readable_while_indexing_and_cancel_is_honoured()
    {
        var p = Path.GetTempFileName();
        try
        {
            File.WriteAllText(p, "a\n1\n2\n3\n");
            using var idx = await CsvIndex.OpenAsync(p, null, default);
            await idx.IndexingCompleted;
            idx.IsIndexingComplete.Should().BeTrue();
            var act = () => idx.ReadRow(3);
            act.Should().Throw<ArgumentOutOfRangeException>();
        }
        finally { File.Delete(p); }
    }

    [Fact]
    public async Task Column_filters_and_sort_and_export()
    {
        using var idx = await CsvIndex.OpenAsync(Tricky, null, default);
        await idx.IndexingCompleted;

        (await FilterEngine.FilterAsync(idx, new FilterSpec { GlobalText = "SMITH" }, null, default)).Should().Equal(0);
        (await FilterEngine.FilterAsync(idx, new FilterSpec { ColumnFilters = { new("Name", ColumnFilterMode.Equals, "plain") } }, null, default)).Should().Equal(1);
        (await FilterEngine.FilterAsync(idx, new FilterSpec { ColumnFilters = { new("Name", ColumnFilterMode.NotContains, "i") } }, null, default)).Should().Equal(2, 3);
        (await FilterEngine.FilterAsync(idx, new FilterSpec { ColumnFilters = { new("Name", ColumnFilterMode.Regex, "^(Plain|Last)$") } }, null, default)).Should().Equal(1, 3);
        (await FilterEngine.FilterAsync(idx, new FilterSpec(), null, default)).Should().Equal(0, 1, 2, 3);

        var byDate = await FilterEngine.SortAsync(idx, new[] { 0, 1, 2, 3 }, "SourceCreated", false);
        byDate.Should().Equal(2, 0, 1, 3);
        var byId = await FilterEngine.SortAsync(idx, new[] { 0, 1, 2, 3 }, "Id", true);
        byId.Should().Equal(3, 2, 1, 0);

        var outp = Path.Combine(Path.GetTempPath(), "ezforensics_export_" + Guid.NewGuid().ToString("N") + ".csv");
        try
        {
            await CsvExporter.ExportAsync(idx, new[] { 0, 3 }, outp);
            using var re = await CsvIndex.OpenAsync(outp, null, default);
            await re.IndexingCompleted;
            re.RowCount.Should().Be(2);
            re.ReadRow(0).Should().Equal(idx.ReadRow(0));
            re.ReadRow(1).Should().Equal(idx.ReadRow(3));
        }
        finally { File.Delete(outp); }
    }

    [Fact]
    public void Timestamp_guesser()
    {
        TimestampColumnGuesser.Guess(new[] { "Id", "SourceCreated", "LastRun" }).Should().Be("LastRun");
        TimestampColumnGuesser.Guess(new[] { "Id", "SourceCreated", "SourceModified" }).Should().Be("SourceCreated");
        TimestampColumnGuesser.Guess(new[] { "ExecutableName", "RunCount", "SourceCreated", "SourceModified", "LastRun" }, "20260101_PECmd_Output.csv").Should().Be("LastRun");
        TimestampColumnGuesser.Guess(new[] { "SourceFile", "SourceCreated", "SourceModified", "TargetCreated", "TargetModified", "LocalPath" }, "LECmd_Output.csv").Should().Be("TargetModified");
        TimestampColumnGuesser.Guess(new[] { "Id", "TimeCreated" }).Should().Be("TimeCreated");
        TimestampColumnGuesser.Guess(new[] { "Id", "WhenSeenTime" }).Should().Be("WhenSeenTime");
        TimestampColumnGuesser.Guess(new[] { "Id", "Name" }).Should().BeNull();
    }
}
