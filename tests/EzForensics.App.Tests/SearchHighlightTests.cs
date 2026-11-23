using EzForensics.App.ViewModels;
using Xunit;

namespace EzForensics.App.Tests;

public class SearchHighlightTests
{
    [Fact]
    public void Terms_skip_NOT_and_keep_phrases()
    {
        var t = SearchHighlight.Terms("psexec AND \"net use\" OR wmic NOT admin");
        Assert.Equal(new[] { "psexec", "net use", "wmic" }, t);
    }

    [Fact]
    public void Terms_empty_or_invalid_is_empty()
    {
        Assert.Empty(SearchHighlight.Terms(""));
        Assert.Empty(SearchHighlight.Terms("\"open"));
    }

    [Fact]
    public void Segments_are_case_insensitive_and_merge_overlaps()
    {
        var s = SearchHighlight.Segments("xx PsExec yy", new[] { "psexec", "sex" });
        Assert.Equal(new[] { ("xx ", false), ("PsExec", true), (" yy", false) }, s.Select(x => (x.Text, x.IsMatch)));
    }

    [Fact]
    public void Segments_repeat_hits_and_no_match()
    {
        var s = SearchHighlight.Segments("ab-ab", new[] { "ab" });
        Assert.Equal(2, s.Count(x => x.IsMatch));
        var n = SearchHighlight.Segments("hello", new[] { "zzz" });
        Assert.Single(n);
        Assert.False(SearchHighlight.HasMatch(n));
    }
}
