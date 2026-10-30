using EzForensics.Core.Results;
using FluentAssertions;

namespace EzForensics.Core.Tests;

public class CsvKeywordSearchTests : IDisposable
{
    private readonly string _dir = Path.Combine(Path.GetTempPath(), "ezforensics_kw_" + Guid.NewGuid().ToString("N"));

    public CsvKeywordSearchTests() => Directory.CreateDirectory(_dir);
    public void Dispose() { try { Directory.Delete(_dir, true); } catch { } }

    private string Write(string name, string content)
    {
        var p = Path.Combine(_dir, name);
        File.WriteAllText(p, content);
        return p;
    }

    [Fact]
    public async Task Finds_CaseInsensitive_WithCountAndFirstRow()
    {
        var a = Write("a.csv", "A,B\r\nx,y\r\nPowerShell.exe,1\r\nz,powershell\r\n");
        var b = Write("b.csv", "A,B\r\nx,y\r\n");
        var hits = await CsvKeywordSearch.SearchToListAsync(new[] { a, b }, "POWERSHELL");
        hits.Should().ContainSingle();
        hits[0].Path.Should().Be(a);
        hits[0].HitCount.Should().Be(2);
        hits[0].FirstHitRow.Should().Be(3);
        hits[0].FileNameMatch.Should().BeFalse();
    }

    [Fact]
    public async Task Matches_FileName()
    {
        var a = Write("Prefetch_Output.csv", "A\r\nnothing\r\n");
        var hits = await CsvKeywordSearch.SearchToListAsync(new[] { a }, "prefetch");
        hits.Should().ContainSingle();
        hits[0].FileNameMatch.Should().BeTrue();
        hits[0].HitCount.Should().Be(0);
    }

    [Fact]
    public async Task EmptyKeyword_YieldsNothing()
    {
        var a = Write("a.csv", "A\r\nx\r\n");
        (await CsvKeywordSearch.SearchToListAsync(new[] { a }, "")).Should().BeEmpty();
    }

    [Fact]
    public async Task Caps_Count_AndReportsProgress()
    {
        var a = Write("big.csv", "A\r\n" + string.Concat(Enumerable.Repeat("hit\r\n", CsvKeywordSearch.MaxCount + 500)));
        var reports = new List<KeywordSearchProgress>();
        var hits = await CsvKeywordSearch.SearchToListAsync(new[] { a }, "hit", new Progress<KeywordSearchProgress>(reports.Add));
        hits[0].HitCount.Should().Be(CsvKeywordSearch.MaxCount);
        hits[0].CountCapped.Should().BeTrue();
        await Task.Delay(100);
        reports.Should().NotBeEmpty();
    }

    [Fact]
    public async Task ManyFiles_InParallel()
    {
        var paths = Enumerable.Range(0, 20).Select(i => Write($"f{i}.csv", i % 2 == 0 ? "A\r\nneedle\r\n" : "A\r\nhay\r\n")).ToList();
        var hits = await CsvKeywordSearch.SearchToListAsync(paths, "needle");
        hits.Should().HaveCount(10);
    }

    [Fact]
    public async Task Cancellation_Throws()
    {
        var a = Write("a.csv", "A\r\n" + string.Concat(Enumerable.Repeat("filler,line\r\n", 200000)));
        using var cts = new CancellationTokenSource();
        cts.Cancel();
        var act = async () => await CsvKeywordSearch.SearchToListAsync(new[] { a }, "filler", null, cts.Token);
        await act.Should().ThrowAsync<OperationCanceledException>();
    }

    [Fact]
    public async Task MissingFile_IsSkipped()
    {
        var hits = await CsvKeywordSearch.SearchToListAsync(new[] { Path.Combine(_dir, "nope.csv") }, "x");
        hits.Should().BeEmpty();
    }
}
