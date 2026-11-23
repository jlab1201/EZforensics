using EzForensics.Core.Explore;
using EzForensics.Core.Explore.Search;
using FluentAssertions;

namespace EzForensics.Core.Tests;

public sealed class NameFilterTests : IDisposable
{
    private readonly TempDir _tmp = new();
    public void Dispose() => _tmp.Dispose();

    [Theory]
    [InlineData("Report.docx", "port", true)]
    [InlineData("Report.docx", "PORT", true)]
    [InlineData("Report.docx", "zzz", false)]
    [InlineData("a.exe", "*.exe", true)]
    [InlineData("a.exe.txt", "*.exe", false)]
    [InlineData("a.dll", "*.exe;*.dll", true)]
    [InlineData("a.sys", "*.exe;*.dll", false)]
    [InlineData("a.exe", " *.exe ; ;", true)]
    [InlineData("ab.txt", "a?.txt", true)]
    [InlineData("abc.txt", "a?.txt", false)]
    [InlineData("A.EXE", "*.exe", true)]
    [InlineData("anything", "", true)]
    public void Matches(string name, string pattern, bool expected) => NameFilter.Matches(name, pattern).Should().Be(expected);

    [Fact]
    public async Task Searcher_uses_filter_semantics_for_names()
    {
        Directory.CreateDirectory(Path.Combine(_tmp.Path, "sub"));
        File.WriteAllText(Path.Combine(_tmp.Path, "a.exe"), "x");
        File.WriteAllText(Path.Combine(_tmp.Path, "sub", "b.dll"), "x");
        File.WriteAllText(Path.Combine(_tmp.Path, "sub", "c.txt"), "x");
        var o = new SearchOptions { Roots = new[] { _tmp.Path }, NameText = "*.exe;*.dll", NameAsFilter = true, Target = SearchTarget.Both };
        var names = new List<string>();
        await foreach (var r in new FileSearcher().SearchAsync(o)) names.Add(r.Name);
        names.Should().BeEquivalentTo("a.exe", "b.dll");
    }
}
