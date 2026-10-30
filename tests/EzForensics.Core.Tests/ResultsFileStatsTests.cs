using System.Globalization;
using System.Text;
using EzForensics.Core.Results;
using FluentAssertions;

namespace EzForensics.Core.Tests;

public class ResultsFileStatsTests : IDisposable
{
    private readonly string _dir;
    private readonly string _fallback;

    public ResultsFileStatsTests()
    {
        _dir = Path.Combine(Path.GetTempPath(), "ezforensics_stats_" + Guid.NewGuid().ToString("N"));
        _fallback = Path.Combine(_dir, "_fallback");
        Directory.CreateDirectory(_dir);
    }

    public void Dispose()
    {
        try { Directory.Delete(_dir, true); } catch { }
    }

    private string Write(string name, string text, bool bom = false)
    {
        var p = Path.Combine(_dir, name);
        File.WriteAllText(p, text, new UTF8Encoding(bom));
        File.SetLastWriteTimeUtc(p, new DateTime(2025, 1, 1, 0, 0, 0, DateTimeKind.Utc));
        return p;
    }

    private static DateTime U(string s) => DateTime.SpecifyKind(DateTime.Parse(s, CultureInfo.InvariantCulture), DateTimeKind.Utc);

    private FileStatsCache NewCache() => new(_fallback, TimeSpan.Zero);

    [Fact]
    public void Min_max_ignore_sentinels_empty_and_unparseable_cells()
    {
        var p = Write("EvtxECmd_Output.csv",
            "RecordNumber,TimeCreated,Payload\r\n" +
            "1,2024-03-05 10:00:00.1234567,a\r\n" +
            "2,,b\r\n" +
            "3,1601-01-01 00:00:00,c\r\n" +
            "4,0001-01-01 00:00:00,d\r\n" +
            "5,not a date,e\r\n" +
            "6,2023-12-31 23:59:59,f\r\n" +
            "7,2024-07-01 08:30:00,g\r\n", bom: true);
        var s = FileStatsCalculator.Compute(p);
        s.IsCsv.Should().BeTrue();
        s.TimestampColumn.Should().Be("TimeCreated");
        s.Events.Should().Be(7);
        s.Earliest.Should().Be(U("2023-12-31 23:59:59"));
        s.Latest.Should().Be(U("2024-07-01 08:30:00"));
        s.Size.Should().Be(new FileInfo(p).Length);
    }

    [Fact]
    public void Implausible_timestamps_are_counted_but_excluded_from_min_max()
    {
        var future = DateTime.UtcNow.AddYears(80).ToString("yyyy-MM-dd HH:mm:ss", CultureInfo.InvariantCulture);
        var p = Write("m.csv",
            "Id,Timestamp\n" +
            "1,1970-04-19 01:02:03\n" +
            "2,2024-05-06 07:08:09\n" +
            "3," + future + "\n" +
            "4,2025-01-02 03:04:05\n");
        var s = FileStatsCalculator.Compute(p);
        s.Events.Should().Be(4);
        s.Earliest.Should().Be(U("2024-05-06 07:08:09"));
        s.Latest.Should().Be(U("2025-01-02 03:04:05"));
        s.ImplausibleCount.Should().Be(2);
        FileStatsAggregator.Combine(new[] { s.Summary, s.Summary }).Implausible.Should().Be(4);
        TimestampPlausibility.Describe(2).Should().Contain("2 implausible timestamps ignored");
    }

    [Fact]
    public void Prefetch_stats_use_LastRun_not_SourceCreated()
    {
        var p = Write("20260101_PECmd_Output.csv",
            "ExecutableName,RunCount,SourceCreated,SourceModified,LastRun\n" +
            "A.EXE,1,2026-10-01 00:00:00,2026-10-01 00:00:00,2024-01-01 00:00:00\n");
        var s = FileStatsCalculator.Compute(p);
        s.TimestampColumn.Should().Be("LastRun");
        s.Earliest.Should().Be(U("2024-01-01"));
    }

    [Fact]
    public void Old_schema_cache_entries_are_invalid()
    {
        var p = Write("v.csv", "Id,Timestamp\n1,2024-01-01 00:00:00\n");
        var s = FileStatsCalculator.Compute(p);
        s.SchemaVersion = 1;
        var fi = new FileInfo(p);
        s.Matches(fi.Length, fi.LastWriteTimeUtc).Should().BeFalse();
    }

    [Fact]
    public void Uses_the_same_column_as_the_grid()
    {
        var p = Write("x.csv", "Name,LastWriteTimestamp,Created\n" + "a,2020-01-01 00:00:00,1999-01-01 00:00:00\n");
        var s = FileStatsCalculator.Compute(p);
        s.TimestampColumn.Should().Be(TimestampColumnGuesser.Guess(new[] { "Name", "LastWriteTimestamp", "Created" }));
        s.TimestampColumn.Should().Be("LastWriteTimestamp");
        s.Earliest.Should().Be(U("2020-01-01"));
    }

    [Fact]
    public async Task Multi_line_quoted_rows_count_once_and_blank_lines_are_skipped()
    {
        var p = Write("q.csv",
            "Id,Message,Timestamp\n" +
            "1,\"line one\nline two, with comma\",2024-01-02 03:04:05\n" +
            "\n" +
            "2,\"he said \"\"hi\"\"\r\nbye\",2024-01-01 00:00:00\r\n" +
            "3,\"x\",\"2024-02-02 00:00:00\"");   // no trailing newline, quoted timestamp
        var s = FileStatsCalculator.Compute(p);
        s.Events.Should().Be(3);
        s.Earliest.Should().Be(U("2024-01-01"));
        s.Latest.Should().Be(U("2024-02-02"));

        using var idx = await CsvIndex.OpenAsync(p, null, CancellationToken.None);
        await idx.IndexingCompleted;
        idx.RowCount.Should().Be(s.Events!.Value);
    }

    [Fact]
    public void Dash_cases_no_time_column_header_only_and_non_csv()
    {
        var noTime = FileStatsCalculator.Compute(Write("plain.csv", "a,b\n1,2\n3,4\n"));
        noTime.Events.Should().Be(2);
        noTime.TimestampColumn.Should().BeNull();
        noTime.Earliest.Should().BeNull();
        FileStatsFormat.Timestamp(noTime.Earliest, null).Should().Be("-");

        var allBad = FileStatsCalculator.Compute(Write("bad.csv", "Date,b\nsoon,2\n,4\n"));
        allBad.TimestampColumn.Should().Be("Date");
        allBad.Earliest.Should().BeNull();
        allBad.Latest.Should().BeNull();

        var headerOnly = FileStatsCalculator.Compute(Write("h.csv", "Timestamp,b\r\n"));
        headerOnly.Events.Should().Be(0);
        headerOnly.Earliest.Should().BeNull();

        var empty = FileStatsCalculator.Compute(Write("e.csv", ""));
        empty.Events.Should().Be(0);

        var json = FileStatsCalculator.Compute(Write("m.manifest.json", "{\"timestamp\":\"2024-01-01\"}"));
        json.IsCsv.Should().BeFalse();
        json.Events.Should().BeNull();
        json.Earliest.Should().BeNull();
        FileStatsFormat.Events(json.Events).Should().Be("-");
        FileStatsFormat.Timestamp(json.Latest, "yyyy").Should().Be("-");
    }

    [Fact]
    public void Formatting_uses_utc_the_date_format_and_thousands_separators()
    {
        var t = U("2024-03-05 10:11:12.5");
        FileStatsFormat.Timestamp(t, "").Should().Be("2024-03-05 10:11:12");
        FileStatsFormat.Timestamp(t, "yyyy-MM-dd HH:mm:ss.fff").Should().Be("2024-03-05 10:11:12.500");
        FileStatsFormat.Events(1234567, CultureInfo.InvariantCulture).Should().Be("1,234,567");
        FileStatsFormat.Size(2048).Should().Be("2 KB");
    }

    [Fact]
    public void Folder_aggregation_min_max_and_sums()
    {
        var a = new StatsSummary(U("2024-01-05"), U("2024-02-01"), 10, 100);
        var b = new StatsSummary(U("2023-06-01"), U("2024-01-10"), 5, 50);
        var noTs = new StatsSummary(null, null, 7, 30);
        var nonCsv = new StatsSummary(null, null, null, 20);

        var folder = FileStatsAggregator.Combine(new[] { a, b, noTs, nonCsv });
        folder.Earliest.Should().Be(U("2023-06-01"));
        folder.Latest.Should().Be(U("2024-02-01"));
        folder.Events.Should().Be(22);
        folder.Size.Should().Be(200);

        // nested folders give the same result as the flat list
        var nested = FileStatsAggregator.Combine(new[] { FileStatsAggregator.Combine(new[] { a, noTs }), FileStatsAggregator.Combine(new[] { b, nonCsv }) });
        nested.Should().Be(folder);

        var none = FileStatsAggregator.Combine(new[] { noTs, nonCsv });
        none.Earliest.Should().BeNull();
        none.Latest.Should().BeNull();
        FileStatsAggregator.Combine(new[] { nonCsv }).Events.Should().BeNull();
        FileStatsAggregator.Combine(Array.Empty<StatsSummary>()).Should().Be(new StatsSummary(null, null, null, 0));
    }

    [Fact]
    public void Cache_writes_a_sidecar_reads_it_back_and_invalidates_on_change()
    {
        var p = Write("c.csv", "Timestamp\n2024-01-01 00:00:00\n");
        var cache = NewCache();
        cache.GetOrCompute(p, CancellationToken.None, out var fromCache).Events.Should().Be(1);
        fromCache.Should().BeFalse();
        var sidecar = Path.Combine(_dir, "c.csv.ezstats.json");
        File.Exists(sidecar).Should().BeTrue();
        File.ReadAllText(sidecar).Should().Contain("\"schemaVersion\": 2").And.Contain("\"timestampColumn\": \"Timestamp\"");

        // a new cache instance (next launch) reads the sidecar instead of the CSV
        var again = NewCache().GetOrCompute(p, CancellationToken.None, out fromCache);
        fromCache.Should().BeTrue();
        again.Latest.Should().Be(U("2024-01-01"));

        // size change
        File.AppendAllText(p, "2024-05-05 00:00:00\n");
        File.SetLastWriteTimeUtc(p, new DateTime(2025, 1, 1, 0, 0, 0, DateTimeKind.Utc));
        NewCache().TryGet(p).Should().BeNull();
        var changed = cache.GetOrCompute(p, CancellationToken.None, out fromCache);
        fromCache.Should().BeFalse();
        changed.Events.Should().Be(2);
        changed.Latest.Should().Be(U("2024-05-05"));

        // last-write change only
        File.SetLastWriteTimeUtc(p, new DateTime(2025, 2, 1, 0, 0, 0, DateTimeKind.Utc));
        cache.TryGet(p).Should().BeNull();

        // older schema
        cache.GetOrCompute(p);
        File.WriteAllText(sidecar, File.ReadAllText(sidecar).Replace("\"schemaVersion\": 2", "\"schemaVersion\": 0"));
        NewCache().TryGet(p).Should().BeNull();
    }

    [Fact]
    public void Cache_falls_back_when_the_sidecar_cannot_be_written()
    {
        var p = Write("ro.csv", "Timestamp\n2024-01-01 00:00:00\n");
        Directory.CreateDirectory(Path.Combine(_dir, "ro.csv.ezstats.json.tmp")); // makes the sidecar write fail
        var written = NewCache().Store(p, FileStatsCalculator.Compute(p));
        written.Should().Be(FileStatsCache.FallbackPathFor(p, _fallback));
        File.Exists(written).Should().BeTrue();
        NewCache().TryGet(p)!.Events.Should().Be(1);
    }

    [Fact]
    public void Recently_written_files_are_cached_in_memory_only()
    {
        var p = Path.Combine(_dir, "growing.csv");
        File.WriteAllText(p, "a\n1\n");
        var cache = new FileStatsCache(_fallback, TimeSpan.FromMinutes(5));
        cache.Store(p, FileStatsCalculator.Compute(p)).Should().BeNull();
        File.Exists(p + FileStatsCache.SidecarSuffix).Should().BeFalse();
        cache.TryGet(p).Should().NotBeNull();
    }

    [Fact]
    public void Sidecars_are_not_listed_in_the_tree()
    {
        CaseTreeOrder.IsListedFile("EvtxECmd_Output.csv").Should().BeTrue();
        CaseTreeOrder.IsListedFile("X.CSV").Should().BeTrue();
        CaseTreeOrder.IsListedFile("EvtxECmd_Output.csv.ezstats.json").Should().BeFalse();
        CaseTreeOrder.IsListedFile("EvtxECmd_Output.ezflags.json").Should().BeFalse();
        CaseTreeOrder.IsListedFile("SuperTimeline_EZ.manifest.json").Should().BeFalse();
        CaseTreeOrder.IsListedFile("run.log").Should().BeFalse();

        var p = Write("listed.csv", "Timestamp\n2024-01-01 00:00:00\n");
        NewCache().GetOrCompute(p);
        Directory.EnumerateFiles(_dir).Select(Path.GetFileName).Where(n => CaseTreeOrder.IsListedFile(n!))
            .Should().Equal("listed.csv");
    }

    [Fact]
    public void Super_timelines_are_newest_first_by_last_write_time()
    {
        var t0 = new DateTime(2026, 10, 1, 0, 0, 0, DateTimeKind.Utc);
        var items = new (string Name, bool Dir, DateTime Time)[]
        {
            ("other.csv", false, t0.AddDays(9)),
            ("SuperTimeline_EZ.csv", false, t0),
            ("plaso", true, t0),
            ("SuperTimeline_EZ (2).csv", false, t0.AddHours(2)),
            ("SuperTimeline_plaso_20261001T000000Z.csv", false, t0.AddHours(1)),
            ("SuperTimeline_B.csv", false, t0),
        };
        CaseTreeOrder.Sort(items, i => i.Name, i => i.Dir, i => i.Time).Select(i => i.Name).Should().Equal(
            "SuperTimeline_EZ (2).csv", "SuperTimeline_plaso_20261001T000000Z.csv", "SuperTimeline_B.csv", "SuperTimeline_EZ.csv",
            "plaso", "other.csv");
    }

    [Fact]
    public void Header_sort_orders_siblings_keeps_timeline_first_and_puts_dashes_last()
    {
        var items = new (string Name, bool Dir, long? Events)[]
        {
            ("Timeline", true, 1), ("EventLogs", true, 500), ("FileSystem", true, null), ("Registry", true, 50),
        };
        var def = CaseTreeOrder.Sort(items, i => i.Name, i => i.Dir);
        string[] Names(IEnumerable<(string Name, bool Dir, long? Events)> x) => x.Select(i => i.Name).ToArray();

        Names(CaseTreeOrder.SortBy(def, i => i.Name, i => i.Dir, i => i.Events, false, true))
            .Should().Equal("Timeline", "Registry", "EventLogs", "FileSystem");
        Names(CaseTreeOrder.SortBy(def, i => i.Name, i => i.Dir, i => i.Events, true, true))
            .Should().Equal("Timeline", "EventLogs", "Registry", "FileSystem");
        Names(CaseTreeOrder.SortBy(def, i => i.Name, i => i.Dir, null, true, true))
            .Should().Equal("Timeline", "Registry", "FileSystem", "EventLogs");
        Names(CaseTreeOrder.SortBy(def, i => i.Name, i => i.Dir, null, false, true)).Should().Equal(Names(def));
        // below the top level nothing is pinned
        Names(CaseTreeOrder.SortBy(def, i => i.Name, i => i.Dir, i => i.Events, false, false))
            .Should().Equal("Timeline", "Registry", "EventLogs", "FileSystem");
        Names(CaseTreeOrder.SortBy(def, i => i.Name, i => i.Dir, i => i.Events, true, false))
            .Should().Equal("EventLogs", "Registry", "Timeline", "FileSystem");
    }
}
