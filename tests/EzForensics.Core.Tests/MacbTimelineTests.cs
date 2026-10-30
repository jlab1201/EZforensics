using EzForensics.Core.Execution;
using EzForensics.Core.Results;
using EzForensics.Core.Timeline;
using EzForensics.Core.Triage;
using FluentAssertions;
using Xunit;

namespace EzForensics.Core.Tests;

/// <summary>Filesystem MACB timeline: bodyfile conversion, the Ingest rows and the Analyze tree order.</summary>
public class MacbTimelineTests : IDisposable
{
    private readonly string _dir = Directory.CreateTempSubdirectory("ezmacb").FullName;
    public void Dispose() { try { Directory.Delete(_dir, true); } catch (IOException) { } }

    private string Body(params string[] lines)
    {
        var p = Path.Combine(_dir, Guid.NewGuid().ToString("N") + ".body");
        File.WriteAllLines(p, lines);
        return p;
    }

    private string[] Convert(int chunk, params string[] bodies)
    {
        var csv = Path.Combine(_dir, "out.csv");
        var n = MactimeConverter.Convert(bodies, csv, chunk);
        var lines = File.ReadAllLines(csv);
        lines[0].Should().Be(MactimeConverter.Header);
        n.Should().Be(lines.Length - 1);
        return lines.Skip(1).ToArray();
    }

    // 1700000000 = 2023-11-14 22:13:20 UTC
    [Fact]
    public void Merges_flags_of_equal_timestamps_in_macb_order_and_skips_zero_times()
    {
        var rows = Convert(1000, Body("0|C:\\a.txt|100-128-1|r/rrwxrwxrwx|0|0|42|0|1700000000|1700000100|1700000000"));
        rows.Should().Equal(
            "2023-11-14 22:13:20.0000000,m..b,42,100-128-1,C:\\a.txt,SI",
            "2023-11-14 22:15:00.0000000,..c.,42,100-128-1,C:\\a.txt,SI");
    }

    [Fact]
    public void Fractional_seconds_and_all_four_flags()
    {
        var rows = Convert(1000, Body("0|C:\\b|5-128-1|r/r|0|0|1|1700000000.1234567|1700000000.1234567|1700000000.1234567|1700000000.1234567"));
        rows.Should().Equal("2023-11-14 22:13:20.1234567,macb,1,5-128-1,C:\\b,SI");
    }

    [Fact]
    public void File_name_attribute_lines_are_FN_and_the_marker_is_dropped_from_the_name()
    {
        var rows = Convert(1000, Body(
            "0|C:\\x\\f.txt ($FILE_NAME)|7-48-2|r/r|0|0|0|1700000000|1700000000|1700000000|1700000000",
            "0|C:\\x\\f.txt|7-128-1|r/r|0|0|0|1700000001|0|0|0"));
        rows.Should().Equal(
            "2023-11-14 22:13:20.0000000,macb,0,7-48-2,C:\\x\\f.txt,FN",
            "2023-11-14 22:13:21.0000000,.a..,0,7-128-1,C:\\x\\f.txt,SI");
    }

    [Fact]
    public void Quotes_names_with_commas_and_pipes_and_ignores_blank_comment_and_short_lines()
    {
        var rows = Convert(1000, Body("# comment", "", "bad|line", "0|C:\\a,b|c.txt|1-128-1|r/r|0|0|3|0|1700000000|0|0"));
        rows.Should().Equal("2023-11-14 22:13:20.0000000,m...,3,1-128-1,\"C:\\a,b|c.txt\",SI");
    }

    [Fact]
    public void Sorts_ascending_across_lines_and_bodyfiles()
    {
        var a = Body("0|late|1-128-1|r|0|0|1|0|1700000500|0|0", "0|early|2-128-1|r|0|0|1|0|1700000000|0|0");
        var b = Body("0|middle|3-128-1|r|0|0|1|0|1700000250|0|0");
        Convert(1000, a, b).Select(r => r.Split(',')[4]).Should().Equal("early", "middle", "late");
    }

    [Fact]
    public void Chunked_merge_matches_the_in_memory_result_and_leaves_no_temp_files()
    {
        var rnd = new Random(7);
        var lines = Enumerable.Range(0, 400).Select(i =>
            $"0|f{i}|{i}-128-1|r|0|0|{i}|{1600000000 + rnd.Next(0, 50)}|{1600000000 + rnd.Next(0, 50)}|{1600000000 + rnd.Next(0, 50)}|{1600000000 + rnd.Next(0, 50)}").ToArray();
        var a = Body(lines.Take(200).ToArray());
        var b = Body(lines.Skip(200).ToArray());
        var whole = Convert(int.MaxValue, a, b);
        var chunked = Convert(37, a, b);
        chunked.Should().Equal(whole);
        whole.Length.Should().BeGreaterThan(400);
        whole.Select(r => r.Split(',')[0]).Should().BeInAscendingOrder(StringComparer.Ordinal);
        Directory.GetFiles(_dir, "*.tmp").Should().BeEmpty();
    }

    [Fact]
    public void Cancelled_conversion_leaves_no_csv()
    {
        var csv = Path.Combine(_dir, "c.csv");
        using var cts = new CancellationTokenSource();
        cts.Cancel();
        var act = () => MactimeConverter.Convert(new[] { Body("0|a|1-128-1|r|0|0|1|0|1700000000|0|0") }, csv, 10, cts.Token);
        act.Should().Throw<OperationCanceledException>();
        File.Exists(csv).Should().BeFalse();
    }

    [Theory]
    [InlineData(@"E:\src\WS01\C", "C")]
    [InlineData(@"E:\src\WS01\d", "D")]
    [InlineData(@"F:\", "F")]
    [InlineData(@"E:\src\WS01", "C")]
    [InlineData(null, "C")]
    public void Drive_letter_of_the_system_root(string? root, string expected) => MacbTimelineRunner.DriveFor(root).Should().Be(expected);

    [Fact]
    public void Body_file_job_args()
    {
        var spec = MacbTimelineRunner.MftBodySpec(@"E:\src\C\$MFT", @"C:\case\H\Timeline", "S", 0, 2, "C", null);
        spec.ExeName.Should().Be("MFTECmd.exe");
        spec.Args.Should().Equal("-f", @"E:\src\C\$MFT", "--body", @"C:\case\H\Timeline\macb", "--bodyf", "S_1.body", "--bdl", "C");
        spec.Label.Should().Contain("(1/2)");
        MacbTimelineRunner.OutputCsvFor(@"C:\case\H\Timeline", "S").Should().Be(@"C:\case\H\Timeline\Timeline_MACB_S.csv");
    }

    // ---- Ingest rows

    private static PlannedJob Tool(string rule, string label, string matched, string host) =>
        new(new JobSpec("MFTECmd", "MFTECmd.exe", Array.Empty<string>(), @"C:\case\" + host + @"\FileSystem", label, "FileSystem")
            { Provenance = new JobProvenance { RuleId = rule, Host = host } }, true,
            new DetectedArtifact(rule, label, "MFTECmd", "FileSystem", matched, false), false);

    [Fact]
    public void Compose_adds_a_ticked_macb_row_per_host_with_an_mft_row_between_the_tools_and_the_supertimelines()
    {
        var plan = IngestSteps.Compose(new[]
        {
            Tool("mft", "$MFT", @"E:\a\C\$MFT", "WS01"), Tool("mft", "$MFT", @"E:\b\C\$MFT", "DC01"), Tool("prefetch", "Prefetch", @"E:\a\C\Windows\Prefetch", "WS01"),
        }, @"C:\case", new[] { @"E:\" }, plasoAvailable: true);
        var macb = plan.Where(IngestSteps.IsMacbTimeline).ToList();
        macb.Select(p => p.Spec.Label).Should().BeEquivalentTo(IngestSteps.MacbTimelineLabelFor("WS01"), IngestSteps.MacbTimelineLabelFor("DC01"));
        macb.Should().OnlyContain(p => p.SelectedByDefault && IngestSteps.IsStep(p));
        macb.Single(p => p.Spec.Provenance!.Host == "WS01").Spec.Args.Should().Equal(@"E:\a\C\$MFT");
        macb.Single(p => p.Spec.Provenance!.Host == "WS01").Spec.OutputDir.Should().Be(@"C:\case\WS01\Timeline");
        var firstMacb = plan.FindIndex(IngestSteps.IsMacbTimeline);
        var firstSuper = plan.FindIndex(IngestSteps.IsSupertimeline);
        firstMacb.Should().BeGreaterThan(plan.FindLastIndex(p => !IngestSteps.IsStep(p)));
        firstMacb.Should().BeLessThan(firstSuper);
        IngestSteps.Order(plan.AsEnumerable().Reverse(), p => p).Select(p => p.Artifact.RuleId)
            .SkipWhile(r => r == IngestSteps.ComputerDetailsRuleId).SkipWhile(r => r is not (IngestSteps.MacbTimelineRuleId or IngestSteps.SupertimelineRuleId))
            .SkipWhile(r => r == IngestSteps.MacbTimelineRuleId).Should().OnlyContain(r => r == IngestSteps.SupertimelineRuleId);
    }

    [Fact]
    public void Compose_macb_rows_do_not_depend_on_plaso_and_need_an_mft_row()
    {
        IngestSteps.Compose(new[] { Tool("mft", "$MFT", @"E:\a\C\$MFT", "WS01") }, @"C:\case", new[] { @"E:\" }, plasoAvailable: false)
            .Count(IngestSteps.IsMacbTimeline).Should().Be(1);
        IngestSteps.Compose(new[] { Tool("prefetch", "Prefetch", @"E:\a", "WS01") }, @"C:\case", new[] { @"E:\" }, plasoAvailable: true)
            .Count(IngestSteps.IsMacbTimeline).Should().Be(0);
    }

    [Fact]
    public void Macb_rows_are_in_the_default_set_and_replaced_not_duplicated()
    {
        IngestDefaults.FastForensicsRuleIds.Should().Contain(IngestSteps.MacbTimelineRuleId);
        var first = IngestSteps.Compose(new[] { Tool("mft", "$MFT", @"E:\a\C\$MFT", "WS01") }, @"C:\case", new[] { @"E:\" }, true);
        IngestSteps.Compose(first, @"C:\case", new[] { @"E:\" }, true).Count(IngestSteps.IsMacbTimeline).Should().Be(1);
    }

    // ---- Analyze tree

    [Fact]
    public void Macb_csv_is_pinned_timeline_output_newest_first()
    {
        CaseTreeOrder.IsSuperTimeline("Timeline_MACB_20261024T010101Z.csv", false).Should().BeTrue();
        CaseTreeOrder.IsSuperTimeline("Timeline_MACB_x.txt", false).Should().BeFalse();
        CaseTreeOrder.IsListedFile("Timeline_MACB_x.csv").Should().BeTrue();
        var t0 = new DateTime(2026, 10, 1, 0, 0, 0, DateTimeKind.Utc);
        var items = new (string Name, bool Dir, DateTime Time)[]
        {
            ("other.csv", false, t0), ("Timeline_MACB_A.csv", false, t0.AddHours(1)), ("SuperTimeline_plaso_A.csv", false, t0.AddHours(2)),
            ("Timeline_MACB_B.csv", false, t0.AddHours(3)),
        };
        CaseTreeOrder.Sort(items, i => i.Name, i => i.Dir, i => i.Time).Select(i => i.Name).Should().Equal(
            "Timeline_MACB_B.csv", "SuperTimeline_plaso_A.csv", "Timeline_MACB_A.csv", "other.csv");
    }
}
