using System.Text;
using EzForensics.Core.Results;
using FluentAssertions;

namespace EzForensics.Core.Tests;

/// <summary>Reserved column "*" ("Any field"): positive operators match when any cell matches, negative ones when no cell matches.</summary>
public class AnyFieldFilterTests : IDisposable
{
    private readonly string _csv;

    public AnyFieldFilterTests()
    {
        _csv = Path.Combine(Path.GetTempPath(), "ezforensics_any_" + Guid.NewGuid().ToString("N") + ".csv");
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

    private async Task<int[]> Run(FilterCondition c)
    {
        using var idx = await CsvIndex.OpenAsync(_csv, null, default);
        await idx.IndexingCompleted;
        return await FilterEngine.FilterAsync(idx, new FilterSpec { Expression = c }, null, default);
    }

    [Theory]
    [InlineData(ColumnFilterMode.Contains, "power", new[] { 0, 4 })]
    [InlineData(ColumnFilterMode.NotContains, "power", new[] { 1, 2, 3 })]
    [InlineData(ColumnFilterMode.Equals, "LOGON", new[] { 1 })]
    [InlineData(ColumnFilterMode.NotEquals, "logon", new[] { 0, 2, 3, 4 })]
    [InlineData(ColumnFilterMode.BeginsWith, "mft", new[] { 0, 2 })]
    [InlineData(ColumnFilterMode.EndsWith, ".exe", new[] { 0 })]
    [InlineData(ColumnFilterMode.Like, "log*", new[] { 1 })]
    [InlineData(ColumnFilterMode.Regex, @"^\d{4}$", new[] { 2 })]
    [InlineData(ColumnFilterMode.Greater, "1000", new[] { 2 })]
    [InlineData(ColumnFilterMode.Less, "10", new[] { 1 })]
    public async Task Any_field_operators(ColumnFilterMode op, string value, int[] expected) =>
        (await Run(new FilterCondition(FilterNode.AnyColumn, op, value))).Should().Equal(expected);

    [Fact]
    public async Task Between_matches_when_any_cell_is_in_range() =>
        (await Run(new FilterCondition("*", ColumnFilterMode.Between, "20", "50"))).Should().Equal(3, 4);

    [Fact]
    public async Task IsNotEmpty_matches_any_non_empty_cell_and_IsEmpty_means_all_cells_empty()
    {
        (await Run(new FilterCondition("*", ColumnFilterMode.IsNotEmpty))).Should().Equal(0, 1, 2, 3, 4);
        (await Run(new FilterCondition("*", ColumnFilterMode.IsEmpty))).Should().BeEmpty();
    }

    [Fact]
    public void Display_string_shows_a_star()
    {
        new FilterCondition("*", ColumnFilterMode.Contains, "x").ToDisplayString().Should().Be("Contains(*, 'x')");
        new FilterCondition("*", ColumnFilterMode.NotContains, "x").ToDisplayString().Should().Be("Not Contains(*, 'x')");
    }

    [Fact]
    public void Columns_skips_any_field()
    {
        var tree = FilterGroup.And(new FilterCondition("*", ColumnFilterMode.Contains, "x"), new FilterCondition("Message", ColumnFilterMode.Equals, "y"));
        SavedFilterStore.Columns(tree).Should().Equal("Message");
    }
}
