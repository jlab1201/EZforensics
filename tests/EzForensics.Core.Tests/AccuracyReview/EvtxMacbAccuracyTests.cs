using EzForensics.Core.Timeline;
using FluentAssertions;

namespace EzForensics.Core.Tests.AccuracyReview;

public class EvtxMacbAccuracyTests
{
    private static string[] ConvertBody(string body)
    {
        var dir = Directory.CreateTempSubdirectory().FullName;
        try
        {
            var b = Path.Combine(dir, "b.body");
            File.WriteAllText(b, body);
            var csv = Path.Combine(dir, "o.csv");
            MactimeConverter.Convert(new[] { b }, csv);
            return File.ReadAllLines(csv).Skip(1).ToArray();
        }
        finally { Directory.Delete(dir, true); }
    }

    [Fact]
    public void Pre_1970_timestamp_is_kept()
    {
        // 1969-12-31 00:00:00 UTC is a valid NTFS time
        ConvertBody("0|C:/old.txt|5-128-1|r/rrwxrwxrwx|0|0|10|-86400|-86400|-86400|-86400\n").Should().HaveCount(1);
    }

}
