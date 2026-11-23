using EzForensics.Core.Timeline;
using FluentAssertions;

namespace EzForensics.Core.Tests;

public class TimelineTimestampTests
{
    [Theory]
    [InlineData("2019-04-27 19:27:55.2740604", 2019, 4, 27, 19, 27, 55, 2740604)]
    [InlineData("2016-01-11 22:08:20", 2016, 1, 11, 22, 8, 20, 0)]
    [InlineData("2020-05-03 12:00:00.5", 2020, 5, 3, 12, 0, 0, 5000000)]
    [InlineData("2013-10-10T03:10:15.6164265Z", 2013, 10, 10, 3, 10, 15, 6164265)]
    [InlineData("  2013-10-10 03:10:15+00:00 ", 2013, 10, 10, 3, 10, 15, 0)]
    [InlineData("2013-10-10 03:10:15.123456789", 2013, 10, 10, 3, 10, 15, 1234567)]
    public void Parses_ez_formats_as_utc(string s, int y, int mo, int d, int h, int mi, int sec, long fracTicks)
    {
        TimelineTimestamp.TryParse(s, out var dt).Should().Be(TimestampParseResult.Ok);
        dt.Kind.Should().Be(DateTimeKind.Utc);
        dt.Should().Be(new DateTime(y, mo, d, h, mi, sec, DateTimeKind.Utc).AddTicks(fracTicks));
    }

    [Theory]
    [InlineData("1601-01-01 00:00:00")]
    [InlineData("1601-01-01 00:00:00.0000000")]
    [InlineData("0001-01-01 00:00:00")]
    public void Null_dates_are_reported_as_null(string s) =>
        TimelineTimestamp.TryParse(s, out _).Should().Be(TimestampParseResult.NullDate);

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("   ")]
    public void Empty_values(string? s) => TimelineTimestamp.TryParse(s, out _).Should().Be(TimestampParseResult.Empty);

    [Theory]
    [InlineData("not-a-date")]
    [InlineData("2020-13-01 00:00:00")]
    [InlineData("2020-02-30 00:00:00")]
    [InlineData("12345")]
    [InlineData("True")]
    public void Garbage_is_invalid(string s) => TimelineTimestamp.TryParse(s, out _).Should().Be(TimestampParseResult.Invalid);

    [Fact]
    public void Strict_mode_rejects_non_iso_but_lenient_accepts_invariant_formats()
    {
        TimelineTimestamp.TryParse("05/01/2020 10:00:00", out _, strict: true).Should().Be(TimestampParseResult.Invalid);
        // 05/01 could be May 1 or 5 Jan: never guessed. 25/01 is unambiguous.
        TimelineTimestamp.TryParse("05/01/2020 10:00:00", out _).Should().Be(TimestampParseResult.Invalid);
        TimelineTimestamp.TryParse("25/01/2020 10:00:00", out var dt).Should().Be(TimestampParseResult.Ok);
        dt.Should().Be(new DateTime(2020, 1, 25, 10, 0, 0, DateTimeKind.Utc));
        TimelineTimestamp.TryParse("2020-05-01", out var d2).Should().Be(TimestampParseResult.Ok);
        d2.Should().Be(new DateTime(2020, 5, 1, 0, 0, 0, DateTimeKind.Utc));
    }

    [Fact]
    public void Formats_with_seven_fraction_digits()
    {
        TimelineTimestamp.Format(new DateTime(2020, 5, 1, 10, 0, 0, DateTimeKind.Utc).AddTicks(1234567))
            .Should().Be("2020-05-01 10:00:00.1234567");
        TimelineTimestamp.Format(new DateTime(2020, 5, 1, 10, 0, 0, DateTimeKind.Utc)).Should().Be("2020-05-01 10:00:00.0000000");
    }
}
