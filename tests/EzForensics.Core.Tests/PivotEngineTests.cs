using System.Text;
using EzForensics.Core.Results;
using FluentAssertions;
using Xunit;

namespace EzForensics.Core.Tests;

public class PivotEngineTests : IDisposable
{
    private readonly string _csv;

    public PivotEngineTests()
    {
        _csv = Path.Combine(Path.GetTempPath(), "ezpivot_" + Guid.NewGuid().ToString("N") + ".csv");
        File.WriteAllText(_csv,
            "Host,User,Size\r\n" +
            "a,bob,10\r\n" +     // 0
            "B,amy,9\r\n" +      // 1
            "A,bob,100\r\n" +    // 2
            ",amy,2\r\n" +       // 3
            "b,amy,\r\n" +       // 4
            "a,cat,1\r\n",       // 5
            new UTF8Encoding(false));
    }

    public void Dispose() { try { File.Delete(_csv); } catch { } }

    private static PivotGroupSpec G(string c, PivotSort s = PivotSort.ValueAsc) => new() { Column = c, Sort = s };

    private async Task<IReadOnlyList<PivotNode>> Run(int[]? rows, params PivotGroupSpec[] g)
    {
        using var idx = await CsvIndex.OpenAsync(_csv, null, default);
        await idx.IndexingCompleted;
        rows ??= Enumerable.Range(0, (int)idx.RowCount).ToArray();
        return await PivotEngine.GroupAsync(idx, rows, g, null, default);
    }

    [Fact]
    public async Task Single_group_counts_case_insensitive_blank_last_and_order_kept()
    {
        var r = await Run(new[] { 5, 4, 3, 2, 1, 0 }, G("Host"));
        r.Select(n => n.Value).Should().Equal("a", "b", ""); // first spelling seen (row 4) is kept
        r.Select(n => n.Count).Should().Equal(3, 2, 1);
        r[0].Rows.Should().Equal(5, 2, 0); // incoming order
        r[2].Rows.Should().Equal(3);
        r[0].Key.Should().Be("Host=a");
    }

    [Fact]
    public async Task Nested_grouping()
    {
        var r = await Run(null, G("Host"), G("User", PivotSort.CountDesc));
        var a = r[0];
        a.Children.Select(c => c.Value).Should().Equal("bob", "cat");
        a.Children[0].Count.Should().Be(2);
        a.Children[0].Rows.Should().Equal(0, 2);
        a.Children[0].Depth.Should().Be(1);
        a.Children[0].Key.Should().Be("Host=a/User=bob");
        a.Rows.Should().BeEmpty();
    }

    [Fact]
    public async Task Numeric_sort_vs_text_sort_and_count_sort()
    {
        var num = await Run(null, G("Size"));
        num.Select(n => n.Value).Should().Equal("1", "2", "9", "10", "100", "");
        var numDesc = await Run(null, G("Size", PivotSort.ValueDesc));
        numDesc.Select(n => n.Value).Should().Equal("100", "10", "9", "2", "1", "");
        var txt = await Run(null, G("User"));
        txt.Select(n => n.Value).Should().Equal("amy", "bob", "cat");
        var asc = await Run(null, G("User", PivotSort.CountAsc));
        asc.Select(n => n.Value).Should().Equal("cat", "bob", "amy");
        var desc = await Run(null, G("User", PivotSort.CountDesc));
        desc.Select(n => n.Value).Should().Equal("amy", "bob", "cat");
    }

    [Fact]
    public async Task Flatten_respects_collapsed_keys()
    {
        var r = await Run(null, G("Host"), G("User"));
        var full = PivotEngine.Flatten(r, new HashSet<string>());
        // hosts: a(bob,cat) B(amy) ""(amy) -> 3 hosts + 4 user groups + 6 rows
        full.Count(l => l.IsGroup).Should().Be(3 + 4);
        full.Count(l => !l.IsGroup).Should().Be(6);
        full[0].Node!.Value.Should().Be("a");
        full[0].Depth.Should().Be(0);
        full.First(l => !l.IsGroup).Depth.Should().Be(2);

        var c1 = PivotEngine.Flatten(r, new HashSet<string> { "Host=a" });
        c1.Count(l => l.IsGroup).Should().Be(3 + 2); // a's two user groups hidden
        c1.Count(l => !l.IsGroup).Should().Be(3);
        var c2 = PivotEngine.Flatten(r, new HashSet<string> { "Host=a/User=bob" });
        c2.Count(l => !l.IsGroup).Should().Be(4);
    }

    [Fact]
    public async Task AllKeys_collapsed_shows_only_top_level_and_expanding_a_group_shows_collapsed_subgroups()
    {
        var r = await Run(null, G("Host"), G("User"));
        var keys = PivotEngine.AllKeys(r).ToList();
        keys.Should().HaveCount(3 + 4).And.OnlyHaveUniqueItems();
        var all = new HashSet<string>(keys);
        var top = PivotEngine.Flatten(r, all);
        top.Should().OnlyContain(l => l.IsGroup && l.Depth == 0).And.HaveCount(3);
        all.Remove("Host=a");
        var expanded = PivotEngine.Flatten(r, all);
        expanded.Should().HaveCount(3 + 2);
        expanded.Where(l => l.Depth == 1).Should().OnlyContain(l => l.IsGroup);
    }

    [Fact]
    public void Sort_flip_and_key_switch_keep_the_other_axis()
    {
        PivotSort.ValueAsc.Flip().Should().Be(PivotSort.ValueDesc);
        PivotSort.ValueDesc.Flip().Should().Be(PivotSort.ValueAsc);
        PivotSort.CountDesc.Flip().Should().Be(PivotSort.CountAsc);
        PivotSort.CountAsc.Flip().Should().Be(PivotSort.CountDesc);
        PivotSort.ValueAsc.WithKey(true).Should().Be(PivotSort.CountAsc);
        PivotSort.ValueDesc.WithKey(true).Should().Be(PivotSort.CountDesc);
        PivotSort.CountDesc.WithKey(false).Should().Be(PivotSort.ValueDesc);
        PivotSort.CountAsc.WithKey(false).Should().Be(PivotSort.ValueAsc);
        PivotSort.CountDesc.IsCount().Should().BeTrue();
        PivotSort.ValueDesc.IsCount().Should().BeFalse();
    }

    [Fact]
    public async Task Cancellation_and_unknown_column()
    {
        using var idx = await CsvIndex.OpenAsync(_csv, null, default);
        await idx.IndexingCompleted;
        var rows = Enumerable.Range(0, (int)idx.RowCount).ToArray();
        using var cts = new CancellationTokenSource();
        cts.Cancel();
        await FluentActions.Awaiting(() => PivotEngine.GroupAsync(idx, rows, new[] { G("Host") }, null, cts.Token))
            .Should().ThrowAsync<OperationCanceledException>();
        await FluentActions.Awaiting(() => PivotEngine.GroupAsync(idx, rows, new[] { G("Nope") }, null, default))
            .Should().ThrowAsync<ArgumentException>();
    }

    [Fact]
    public void Session_state_tolerates_null_lists()
    {
        var p = Path.Combine(Path.GetTempPath(), "ezpivot_s_" + Guid.NewGuid().ToString("N") + ".json");
        try
        {
            File.WriteAllText(p, "{\"version\":1,\"files\":[{\"path\":\"x.csv\",\"groupColumns\":null,\"collapsedGroups\":null},{\"path\":\"y.csv\",\"groupColumns\":[{\"column\":\"Host\",\"sort\":\"countDesc\"}],\"collapsedGroups\":[\"Host=a\"]}]}");
            var s = AnalyzeSessionStore.Load(p);
            s.Files[0].GroupColumns.Should().BeEmpty();
            s.Files[0].CollapsedGroups.Should().BeEmpty();
            s.Files[1].GroupColumns.Should().ContainSingle().Which.Sort.Should().Be(PivotSort.CountDesc);
            s.Files[1].CollapsedGroups.Should().Equal("Host=a");
        }
        finally { File.Delete(p); }
    }
}
