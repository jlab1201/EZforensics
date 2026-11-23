using EzForensics.Core.Processes;
using EzForensics.Core.Time;
using Xunit;

namespace EzForensics.Core.Tests;

/// <summary>Characterizes the four former private ParseTime copies (ProcessActivity, ProcessEventLogs, ZeekSocketIndex lenient; ProcessCorrelator exact).</summary>
public class TimeParseTests
{
    private const string F = "yyyy-MM-dd HH:mm:ss.fffffff";   // ProcessCorrelator.TimeFormat
    [Theory]
    [InlineData("2024-03-05 10:11:12", 2024, 3, 5, 10, 11, 12)]
    [InlineData("2024-03-05T10:11:12Z", 2024, 3, 5, 10, 11, 12)]
    [InlineData("2024-03-05T12:11:12+02:00", 2024, 3, 5, 10, 11, 12)]   // offset converted to UTC
    [InlineData("2024-03-05T10:11:12.5", 2024, 3, 5, 10, 11, 12)]       // no offset = UTC, not local
    public void Lenient_reads_utc(string s, int y, int mo, int d, int h, int mi, int sec)
    {
        var t = TimeParse.Lenient(s);
        Assert.NotNull(t);
        Assert.Equal(DateTimeKind.Utc, t!.Value.Kind);
        Assert.Equal(new DateTime(y, mo, d, h, mi, sec), new DateTime(t.Value.Year, t.Value.Month, t.Value.Day, t.Value.Hour, t.Value.Minute, t.Value.Second));
    }

    [Theory]
    [InlineData("")]
    [InlineData("   ")]
    [InlineData("not a date")]
    [InlineData("N/A")]
    [InlineData("1700000000.123")]   // Zeek epoch float: never a date here (callers handle epochs)
    public void Lenient_gives_null(string s) => Assert.Null(TimeParse.Lenient(s));

    [Fact]
    public void Lenient_null_input_is_null() => Assert.Null(TimeParse.Lenient(null));

    [Fact]
    public void Exact_reads_only_the_app_format()
    {
        var t = TimeParse.Exact("2024-03-05 10:11:12.1234567", F);
        Assert.NotNull(t);
        Assert.Equal(DateTimeKind.Utc, t!.Value.Kind);
        Assert.Equal(1234567, t.Value.Ticks % 10_000_000);
        Assert.Null(TimeParse.Exact("2024-03-05 10:11:12", F));
        Assert.Null(TimeParse.Exact("2024-03-05T10:11:12Z", F));
        Assert.Null(TimeParse.Exact("", F));
    }
}
