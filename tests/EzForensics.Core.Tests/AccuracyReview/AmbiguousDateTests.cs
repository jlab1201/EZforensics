using EzForensics.Core.Results;
using EzForensics.Core.Timeline;
using FluentAssertions;

namespace EzForensics.Core.Tests.AccuracyReview;

/// <summary>Policy: ambiguous slash dates (dd/MM vs MM/dd) are UNPARSED, never guessed, except in Plaso l2tcsv files (MM/dd/yyyy by spec).</summary>
public class AmbiguousDateTests
{
    [Theory]
    [InlineData("01/02/2020")]
    [InlineData("01/02/2020 10:00:00")]
    [InlineData("03/04/2020 10:00:00.1234567")]
    public void Filter_date_parse_rejects_ambiguous_slash_dates(string s)
        => CsvValueParser.TryParseDate(s, out _).Should().BeFalse();

    [Theory]
    [InlineData("01/02/2020")]
    [InlineData("01/02/2020 10:00:00")]
    [InlineData("03/04/2020 10:00:00.1234567")]
    public void TimelineTimestamp_non_strict_rejects_ambiguous_slash_dates(string s)   // also what FileStats calls
        => TimelineTimestamp.TryParse(s, out _).Should().NotBe(TimestampParseResult.Ok);

    [Theory]
    [InlineData("2020-02-13", 2020, 2, 13)]
    [InlineData("2020/02/13", 2020, 2, 13)]
    [InlineData("2020-02-13 10:00", 2020, 2, 13)]
    [InlineData("13/02/2020", 2020, 2, 13)]            // day above 12: day-first
    [InlineData("02/13/2020", 2020, 2, 13)]            // day above 12: month-first
    [InlineData("13/02/2020 10:00:00", 2020, 2, 13)]
    [InlineData("05/05/2020", 2020, 5, 5)]             // same either way
    public void Unambiguous_dates_still_parse(string s, int y, int m, int d)
    {
        CsvValueParser.TryParseDate(s, out var dt).Should().BeTrue();
        dt.Date.Should().Be(new DateTime(y, m, d));
        TimelineTimestamp.TryParse(s, out var utc).Should().Be(TimestampParseResult.Ok);
        utc.Date.Should().Be(new DateTime(y, m, d));
    }

    [Fact]
    public void Month_first_flag_reads_ambiguous_slash_dates_month_first()
    {
        CsvValueParser.TryParseDate("01/02/2020", out var dt, monthFirstSlash: true).Should().BeTrue();
        dt.Month.Should().Be(1);
        dt.Day.Should().Be(2);
        TimelineTimestamp.TryParse("03/04/2020 10:00:00", out var utc, monthFirstSlash: true).Should().Be(TimestampParseResult.Ok);
        (utc.Month, utc.Day).Should().Be((3, 4));
    }

    [Fact]
    public void L2tcsv_header_is_detected_and_dynamic_is_not()
    {
        SlashDates.IsL2tcsvHeader("date,time,timezone,MACB,source,sourcetype,type,user,host,short,desc".Split(',')).Should().BeTrue();
        SlashDates.IsL2tcsvHeader("datetime,timestamp_desc,macb,source".Split(',')).Should().BeFalse();
    }

    [Fact]
    public async Task L2tcsv_file_reads_its_date_column_month_first_but_other_csv_stays_text()
    {
        using var tmp = new TempDir();
        var l2t = Path.Combine(tmp.Path, "l2t.csv");
        File.WriteAllText(l2t, "date,time,timezone,MACB,source\n03/04/2020,10:00:00,UTC,M...,FILE\n01/02/2020,10:00:00,UTC,M...,FILE\n");
        var other = Path.Combine(tmp.Path, "o.csv");
        File.WriteAllText(other, "date,source\n03/04/2020,FILE\n01/02/2020,FILE\n");

        using var a = await CsvIndex.OpenAsync(l2t, null, CancellationToken.None);
        await a.IndexingCompleted;
        a.MonthFirstSlashDates.Should().BeTrue();
        DistinctValueCounter.LooksLikeDateColumn(a, "date").Should().BeTrue();

        using var b = await CsvIndex.OpenAsync(other, null, CancellationToken.None);
        await b.IndexingCompleted;
        b.MonthFirstSlashDates.Should().BeFalse();
        DistinctValueCounter.LooksLikeDateColumn(b, "date").Should().BeFalse();
    }
}
