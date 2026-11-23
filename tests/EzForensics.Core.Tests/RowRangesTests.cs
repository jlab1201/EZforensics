using EzForensics.Core.Analysis;
using FluentAssertions;

namespace EzForensics.Core.Tests;

public class RowRangesTests
{
    [Theory]
    [InlineData("12-15", new long[] { 12, 13, 14, 15 })]
    [InlineData("1, 15, 16, 21-24", new long[] { 1, 15, 16, 21, 22, 23, 24 })]
    [InlineData("21:23;5 5", new long[] { 5, 21, 22, 23 })]
    [InlineData("", new long[0])]
    public void Parses_spreadsheet_style_lists(string text, long[] expected) =>
        RowRanges.Parse(text, out _).Should().Equal(expected);

    [Theory]
    [InlineData("abc")]
    [InlineData("0")]
    [InlineData("15-12")]
    [InlineData("3-x")]
    public void Refuses_what_is_not_a_row_list(string text)
    {
        RowRanges.Parse(text, out var error).Should().BeNull();
        error.Should().NotBeNullOrEmpty();
    }

    [Fact]
    public void Refuses_rows_past_the_end_of_the_file()
    {
        RowRanges.Parse("9-11", out var error, max: 10).Should().BeNull();
        error.Should().Contain("10");
        RowRanges.Parse("9-10", out _, max: 10).Should().Equal(9, 10);
    }

    [Fact]
    public void Formats_runs_as_ranges() =>
        RowRanges.Format(new long[] { 24, 1, 15, 16, 21, 22, 23, 1 }).Should().Be("1, 15-16, 21-24");

    [Fact]
    public void Set_finding_file_rows_adds_and_removes_only_in_that_file()
    {
        var a = new CaseAnalysis();
        var f = a.AddFinding("x", "High", null, null, new[] { new AnalysisRow("a.csv", 1, "fpA"), new AnalysisRow("a.csv", 2, "fpA"), new AnalysisRow("b.csv", 7, "fpB") });
        var added = a.SetFindingFileRows(f.Id, "A.csv", new long[] { 2, 3, 4 });
        added.Should().Equal(3, 4);
        f.Rows.Where(r => r.CsvRelPath.Equals("a.csv", StringComparison.OrdinalIgnoreCase)).Select(r => r.Row).Should().BeEquivalentTo(new long[] { 2, 3, 4 });
        f.Rows.Where(r => r.Row is 3 or 4).Should().OnlyContain(r => r.Fingerprint == "fpA");
        f.Rows.Should().Contain(r => r.CsvRelPath == "b.csv" && r.Row == 7);
    }
}
