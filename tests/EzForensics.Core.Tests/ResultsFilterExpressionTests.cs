using System.Diagnostics;
using System.Text;
using EzForensics.Core.Results;
using FluentAssertions;

namespace EzForensics.Core.Tests;

public class ResultsFilterExpressionTests : IDisposable
{
    private readonly string _csv;

    public ResultsFilterExpressionTests()
    {
        _csv = Path.Combine(Path.GetTempPath(), "ezforensics_fx_" + Guid.NewGuid().ToString("N") + ".csv");
        File.WriteAllText(_csv,
            "Source,Message,Size,Time\r\n" +
            "MFT,Ran PowerShell.exe,100,2024-01-01 10:00:00\r\n" +     // 0
            "EVTX,logon,5,2024-01-02 11:00:00\r\n" +                    // 1
            "mft,cmd.exe /c whoami,2000,2024-01-03 00:00:00\r\n" +      // 2
            "Prefetch,,30,\r\n" +                                       // 3
            "EVTX,powershell -enc,40,not a date\r\n",                   // 4
            new UTF8Encoding(false));
    }

    public void Dispose() { try { File.Delete(_csv); } catch { } }

    private async Task<int[]> Run(FilterSpec spec)
    {
        using var idx = await CsvIndex.OpenAsync(_csv, null, default);
        await idx.IndexingCompleted;
        return await FilterEngine.FilterAsync(idx, spec, null, default);
    }

    private Task<int[]> Run(FilterNode node) => Run(new FilterSpec { Expression = node });

    [Theory]
    [InlineData(ColumnFilterMode.Contains, "power", new[] { 0, 4 })]
    [InlineData(ColumnFilterMode.NotContains, "power", new[] { 1, 2, 3 })]
    [InlineData(ColumnFilterMode.Equals, "LOGON", new[] { 1 })]
    [InlineData(ColumnFilterMode.NotEquals, "logon", new[] { 0, 2, 3, 4 })]
    [InlineData(ColumnFilterMode.BeginsWith, "RAN", new[] { 0 })]
    [InlineData(ColumnFilterMode.EndsWith, "WHOAMI", new[] { 2 })]
    [InlineData(ColumnFilterMode.Like, "*shell*", new[] { 0, 4 })]
    [InlineData(ColumnFilterMode.Like, "?ogo?", new[] { 1 })]
    [InlineData(ColumnFilterMode.IsEmpty, "", new[] { 3 })]
    [InlineData(ColumnFilterMode.IsNotEmpty, "", new[] { 0, 1, 2, 4 })]
    [InlineData(ColumnFilterMode.Regex, "^(logon|cmd)", new[] { 1, 2 })]
    public async Task Text_operators_are_case_insensitive(ColumnFilterMode op, string value, int[] expected)
    {
        (await Run(new FilterCondition("Message", op, value))).Should().Equal(expected);
        // the legacy ColumnFilters list goes through the same evaluator
        (await Run(new FilterSpec { ColumnFilters = { new ColumnFilter("Message", op, value) } })).Should().Equal(expected);
    }

    [Fact]
    public async Task Numeric_and_date_comparisons()
    {
        (await Run(new FilterCondition("Size", ColumnFilterMode.Greater, "35"))).Should().Equal(0, 2, 4);
        (await Run(new FilterCondition("Size", ColumnFilterMode.Less, "35"))).Should().Equal(1, 3);
        (await Run(new FilterCondition("Size", ColumnFilterMode.Between, "30", "100"))).Should().Equal(0, 3, 4);
        (await Run(new FilterCondition("Time", ColumnFilterMode.Greater, "2024-01-01 12:00"))).Should().Equal(1, 2);
        (await Run(new FilterCondition("Time", ColumnFilterMode.Between, "2024-01-01", "2024-01-02 23:59:59"))).Should().Equal(0, 1);
        (await Run(new FilterCondition("Time", ColumnFilterMode.Between, "2024-01-02", null))).Should().Equal(1, 2);
    }

    [Fact]
    public async Task Groups_not_value_sets_and_date_sets()
    {
        var tree = FilterGroup.And(
            FilterGroup.Or(new FilterCondition("Source", ColumnFilterMode.Equals, "mft"), new FilterCondition("Source", ColumnFilterMode.Equals, "evtx")),
            FilterGroup.Not(new FilterCondition("Message", ColumnFilterMode.Contains, "logon")));
        (await Run(tree)).Should().Equal(0, 2, 4);

        (await Run(new FilterValueSet("Source", new[] { "MFT", "prefetch" }))).Should().Equal(0, 2, 3);
        (await Run(new FilterValueSet("Message", new[] { "logon" }, includeBlanks: true))).Should().Equal(1, 3);
        (await Run(FilterGroup.Not(new FilterValueSet("Source", new[] { "evtx" })))).Should().Equal(0, 2, 3);

        (await Run(new FilterDateSet("Time", new[] { new DateOnly(2024, 1, 2), new DateOnly(2024, 1, 3) }))).Should().Equal(1, 2);
        (await Run(new FilterDateSet("Time", new[] { new DateOnly(2024, 1, 1) }, includeBlanks: true))).Should().Equal(0, 3, 4);

        // empty groups and unknown columns place no constraint
        (await Run(new FilterGroup())).Should().Equal(0, 1, 2, 3, 4);
        (await Run(FilterGroup.Or(new FilterCondition("Nope", ColumnFilterMode.Equals, "x"), new FilterCondition("Source", ColumnFilterMode.Equals, "MFT")))).Should().Equal(0, 2);
    }

    [Fact]
    public async Task Expression_stacks_with_global_date_and_flags()
    {
        var spec = new FilterSpec
        {
            GlobalText = "e",
            Expression = new FilterCondition("Source", ColumnFilterMode.NotEquals, "prefetch"),
            ColumnFilters = { new ColumnFilter("Size", ColumnFilterMode.Greater, "10") },
        };
        (await Run(spec)).Should().Equal(0, 2, 4);

        spec.DateColumn = "Time";
        spec.DateFrom = new DateTime(2024, 1, 2);
        (await Run(spec)).Should().Equal(2);
        spec.DateColumn = null;

        var flags = new HashSet<long> { 2, 4, 3, 99 };
        spec.FlaggedRows = flags;
        spec.Flag = FlagFilterMode.Flagged;
        (await Run(spec)).Should().Equal(2, 4);
        spec.Flag = FlagFilterMode.Unflagged;
        (await Run(spec)).Should().Equal(0);

        (await Run(new FilterSpec { Flag = FlagFilterMode.Flagged, FlaggedRows = flags })).Should().Equal(2, 3, 4);
        new FilterSpec { Flag = FlagFilterMode.Flagged }.IsEmpty.Should().BeFalse();
    }

    [Fact]
    public void Display_string_is_timeline_explorer_style()
    {
        var spec = new FilterSpec
        {
            Expression = FilterGroup.And(
                new FilterCondition("Source", ColumnFilterMode.Equals, "MFT"),
                new FilterCondition("Message", ColumnFilterMode.Contains, "powershell")),
            Flag = FlagFilterMode.Unflagged,
        };
        spec.ToDisplayString().Should().Be("[Source] = 'MFT' And Contains([Message], 'powershell') And Not [Flag]");

        FilterGroup.Or(
                new FilterCondition("A", ColumnFilterMode.NotEquals, "it's"),
                FilterGroup.And(new FilterCondition("B", ColumnFilterMode.IsEmpty), new FilterCondition("C", ColumnFilterMode.Between, "1", "5")))
            .ToDisplayString().Should().Be("[A] <> 'it''s' Or (IsNullOrEmpty([B]) And [C] Between('1', '5'))");

        FilterGroup.Not(FilterGroup.Or(new FilterCondition("A", ColumnFilterMode.Like, "a*"), new FilterCondition("A", ColumnFilterMode.EndsWith, "z")))
            .ToDisplayString().Should().Be("Not ([A] Like 'a*' Or EndsWith([A], 'z'))");

        new FilterValueSet("S", new[] { "b", "a" }, includeBlanks: true).ToDisplayString().Should().Be("[S] In ('a', 'b') Or IsNullOrEmpty([S])");
        new FilterDateSet("T", new[] { new DateOnly(2024, 1, 1), new DateOnly(2024, 1, 2), new DateOnly(2024, 1, 5) }).ToDisplayString()
            .Should().Be("[T] InDays(#2024-01-01#..#2024-01-02#, #2024-01-05#)");

        new FilterSpec { GlobalText = "x", ColumnFilters = { new ColumnFilter("A", ColumnFilterMode.NotContains, "y") } }
            .ToDisplayString().Should().Be("Not Contains([A], 'y') And Contains(*, 'x')");
        new FilterSpec().ToDisplayString().Should().BeEmpty();
    }

    [Fact]
    public void Remove_column_prunes_groups()
    {
        var g = FilterGroup.And(
            new FilterCondition("A", ColumnFilterMode.Equals, "1"),
            FilterGroup.Or(new FilterCondition("a", ColumnFilterMode.Equals, "2"), new FilterValueSet("A", new[] { "x" })),
            new FilterCondition("B", ColumnFilterMode.Equals, "3"));
        g.References("A").Should().BeTrue();
        g.RemoveColumn("A").Should().BeTrue();
        g.ToDisplayString().Should().Be("[B] = '3'");
        g.References("A").Should().BeFalse();
    }

    [Fact]
    public async Task Distinct_values_and_day_buckets()
    {
        using var idx = await CsvIndex.OpenAsync(_csv, null, default);
        await idx.IndexingCompleted;

        var r = await DistinctValueCounter.CountAsync(idx, "Source");
        r.BlankCount.Should().Be(0);
        r.DistinctCount.Should().Be(3); // MFT/mft merge (case-insensitive)
        r.Values[0].Count.Should().Be(2);
        r.Values.Select(v => v.Value.ToUpperInvariant()).Should().BeEquivalentTo("MFT", "EVTX", "PREFETCH");
        r.Truncated.Should().BeFalse();

        var top1 = await DistinctValueCounter.CountAsync(idx, "Source", maxValues: 1);
        top1.Values.Should().HaveCount(1);
        top1.Truncated.Should().BeTrue();

        var capped = await DistinctValueCounter.CountAsync(idx, "Message", maxTracked: 2);
        capped.CountsApproximate.Should().BeTrue();
        capped.BlankCount.Should().Be(1);
        capped.NonBlankCount.Should().Be(4);

        var search = await DistinctValueCounter.CountAsync(idx, "Message", search: "POWER");
        search.Values.Select(v => v.Value).Should().BeEquivalentTo("Ran PowerShell.exe", "powershell -enc");

        var days = await DistinctValueCounter.CountDaysAsync(idx, "Time");
        days.Days.Select(d => d.Day).Should().Equal(new DateOnly(2024, 1, 1), new DateOnly(2024, 1, 2), new DateOnly(2024, 1, 3));
        days.BlankCount.Should().Be(2);

        DistinctValueCounter.LooksLikeDateColumn(idx, "Time", 3).Should().BeTrue();
        DistinctValueCounter.LooksLikeDateColumn(idx, "Size").Should().BeFalse();
    }

    [Fact]
    public async Task Count_is_cancellable()
    {
        using var idx = await CsvIndex.OpenAsync(_csv, null, default);
        await idx.IndexingCompleted;
        using var cts = new CancellationTokenSource();
        cts.Cancel();
        var act = () => DistinctValueCounter.CountAsync(idx, "Source", ct: cts.Token);
        await act.Should().ThrowAsync<OperationCanceledException>();
    }

    [Fact]
    public async Task Two_million_rows_filter_with_a_tree_in_time()
    {
        var p = Path.Combine(Path.GetTempPath(), "ezforensics_big_" + Guid.NewGuid().ToString("N") + ".csv");
        try
        {
            using (var w = new StreamWriter(p, false, new UTF8Encoding(false), 1 << 20))
            {
                w.Write("Id,Kind,Path,Size\r\n");
                for (int i = 0; i < 2_000_000; i++)
                    w.Write($"{i},{(i % 3 == 0 ? "File" : "Dir")},C:\\Users\\u{i % 50}\\file{i}.txt,{i % 1000}\r\n");
            }
            using var idx = await CsvIndex.OpenAsync(p, null, default);
            await idx.IndexingCompleted;
            var spec = new FilterSpec
            {
                GlobalText = "users",
                Expression = FilterGroup.And(
                    new FilterValueSet("Kind", new[] { "file" }),
                    FilterGroup.Or(new FilterCondition("Path", ColumnFilterMode.Like, "*\\u7\\*"), new FilterCondition("Size", ColumnFilterMode.Greater, "990"))),
            };
            var sw = Stopwatch.StartNew();
            var hits = await FilterEngine.FilterAsync(idx, spec, null, default);
            sw.Stop();
            sw.Elapsed.Should().BeLessThan(TimeSpan.FromSeconds(10));
            hits.Length.Should().Be(Enumerable.Range(0, 2_000_000).Count(i => i % 3 == 0 && (i % 50 == 7 || i % 1000 > 990)));
        }
        finally { File.Delete(p); }
    }
}

public class FilterRegexTimeoutTests
{
    [Fact]
    public void Like_pattern_regex_has_a_match_timeout()
    {
        var rx = FilterCondition.LikeToRegex("*a*a*a*a*a*a*a*a*a*a*b");
        rx.MatchTimeout.Should().NotBe(System.Text.RegularExpressions.Regex.InfiniteMatchTimeout);
    }
}
