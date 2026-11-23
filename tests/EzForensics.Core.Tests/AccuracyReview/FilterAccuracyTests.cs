using System.Text;
using EzForensics.Core.Results;
using FluentAssertions;

namespace EzForensics.Core.Tests.AccuracyReview;

public class FilterAccuracyTests
{
    internal static async Task<T> WithCsv<T>(string content, Func<CsvIndex, Task<T>> body)
    {
        var p = Path.Combine(Path.GetTempPath(), "acc_" + Guid.NewGuid().ToString("N") + ".csv");
        File.WriteAllText(p, content, new UTF8Encoding(false));
        try
        {
            using var idx = await CsvIndex.OpenAsync(p, null, default);
            await idx.IndexingCompleted;
            return await body(idx);
        }
        finally { try { File.Delete(p); } catch { } }
    }

    private const string Basic = "A,B\r\nx,1\r\ny,2\r\nz,3\r\n";

    private static Task<int[]> Run(string csv, FilterNode n) =>
        WithCsv(csv, i => FilterEngine.FilterAsync(i, new FilterSpec { Expression = n }, null, default));

    // Item 1: Not(A And Missing) must not behave like Not(A). Correct: reported (throw) or at least the full semantics (all rows).
    [Fact]
    public async Task Item1_Not_of_And_with_unknown_column_is_not_silently_Not_A()
    {
        var node = FilterGroup.Not(FilterGroup.And(
            new FilterCondition("A", ColumnFilterMode.Equals, "x"),
            new FilterCondition("Missing", ColumnFilterMode.Equals, "q")));
        int[]? r = null; Exception? ex = null;
        try { r = await Run(Basic, node); } catch (Exception e) { ex = e; }
        (ex != null || r!.SequenceEqual(new[] { 0, 1, 2 }))
            .Should().BeTrue("unknown column must not be silently dropped; got " + (r == null ? "exception" : string.Join(",", r)));
    }

}
