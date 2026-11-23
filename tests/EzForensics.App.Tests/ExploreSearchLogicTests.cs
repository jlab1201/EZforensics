using System.IO;
using EzForensics.App.ViewModels.Explore;
using EzForensics.Core.Explore.Search;
using FluentAssertions;

namespace EzForensics.App.Tests;

public class ExploreSearchLogicTests
{
    private static SearchCriteria Valid()
    {
        var c = new SearchCriteria();
        c.Roots.Add(Path.GetTempPath());
        return c;
    }

    [Fact]
    public void LookIn_splits_on_semicolons_trims_and_drops_blanks_and_repeats()
    {
        ExploreSearchLogic.ParseLookIn(@"C:\a; ""D:\b c"" ;;c:\A;  E:\x ").Should().Equal(@"C:\a", @"D:\b c", @"E:\x");
        ExploreSearchLogic.ParseLookIn(@"C:\single").Should().Equal(@"C:\single");
        ExploreSearchLogic.ParseLookIn("  ;  ").Should().BeEmpty();
        ExploreSearchLogic.ParseLookIn(null).Should().BeEmpty();
    }

    [Theory]
    [InlineData("", SizeUnit.MB, null)]
    [InlineData("  ", SizeUnit.KB, null)]
    [InlineData("10", SizeUnit.Bytes, 10L)]
    [InlineData("2", SizeUnit.KB, 2048L)]
    [InlineData("1.5", SizeUnit.MB, 1572864L)]
    [InlineData("3", SizeUnit.GB, 3221225472L)]
    public void Size_parses_with_unit(string text, SizeUnit unit, long? expected)
    {
        ExploreSearchLogic.TryParseSize(text, unit, out var bytes).Should().BeTrue();
        bytes.Should().Be(expected);
    }

    [Theory]
    [InlineData("abc")]
    [InlineData("-1")]
    [InlineData("1e30")]
    public void Size_rejects_bad_input(string text)
    {
        ExploreSearchLogic.TryParseSize(text, SizeUnit.MB, out var bytes).Should().BeFalse();
        bytes.Should().BeNull();
    }

    [Theory]
    [InlineData(null, "", SizeUnit.MB)]
    [InlineData(2048L, "2", SizeUnit.KB)]
    [InlineData(5L * 1024 * 1024, "5", SizeUnit.MB)]
    [InlineData(3L * 1024 * 1024 * 1024, "3", SizeUnit.GB)]
    [InlineData(1500L, "1500", SizeUnit.Bytes)]
    public void Size_formats_into_largest_exact_unit(long? bytes, string text, SizeUnit unit) =>
        ExploreSearchLogic.FormatSize(bytes).Should().Be((text, unit));

    [Fact]
    public void Build_maps_the_criteria()
    {
        var c = Valid();
        c.NameText = " *.log;*.txt "; c.NameMode = NameMatchMode.Wildcard; c.NameMatchCase = true; c.Target = SearchTarget.Both;
        c.ContentText = "password"; c.ContentMode = ContentMatchMode.Boolean; c.ContentMatchCase = true; c.ContentWholeWord = true;
        c.IncludeSubfolders = false; c.ExcludeFolders = "node_modules; ;C:\\Windows\\WinSxS ";
        c.MinSize = "1"; c.MinSizeUnit = SizeUnit.KB; c.MaxSize = "2"; c.MaxSizeUnit = SizeUnit.MB;
        c.Encoding = ContentEncoding.Utf16LE; c.SkipBinary = false; c.ContextLines = "4"; c.MaxResults = "50";
        c.Hidden = AttrFilter.Must; c.System = AttrFilter.MustNot; c.ReadOnly = AttrFilter.Any;

        var (o, errors) = ExploreSearchLogic.Build(c);
        errors.Should().BeEmpty();
        o.Should().NotBeNull();
        o!.NameText.Should().Be("*.log;*.txt");
        o.NameMode.Should().Be(NameMatchMode.Wildcard);
        o.NameMatchCase.Should().BeTrue();
        o.Target.Should().Be(SearchTarget.Both);
        o.ContentText.Should().Be("password");
        o.ContentMode.Should().Be(ContentMatchMode.Boolean);
        o.ContentWholeWord.Should().BeTrue();
        o.IncludeSubfolders.Should().BeFalse();
        o.ExcludeFolders.Should().Equal("node_modules", "C:\\Windows\\WinSxS");
        o.MinSize.Should().Be(1024);
        o.MaxSize.Should().Be(2L * 1024 * 1024);
        o.Encoding.Should().Be(ContentEncoding.Utf16LE);
        o.SkipBinary.Should().BeFalse();
        o.ContextLines.Should().Be(4);
        o.MaxResults.Should().Be(50);
        o.AttributesMustHave.Should().Be(FileAttributes.Hidden);
        o.AttributesMustNotHave.Should().Be(FileAttributes.System);
    }

    [Fact]
    public void Build_blank_text_means_no_criteria()
    {
        var (o, errors) = ExploreSearchLogic.Build(Valid());
        errors.Should().BeEmpty();
        o!.NameText.Should().BeNull();
        o.ContentText.Should().BeNull();
        o.HasNameCriteria.Should().BeFalse();
        o.HasContentCriteria.Should().BeFalse();
    }

    [Fact]
    public void Build_reports_unreadable_numbers_together()
    {
        var c = Valid();
        c.MinSize = "x"; c.MaxSize = "-3"; c.ContextLines = "many"; c.MaxResults = "0";
        var (o, errors) = ExploreSearchLogic.Build(c);
        o.Should().BeNull();
        errors.Should().HaveCount(4);
    }

    [Fact]
    public void Build_reports_core_validation_errors()
    {
        var c = Valid();
        c.NameText = "("; c.NameMode = NameMatchMode.Regex;
        var (o, errors) = ExploreSearchLogic.Build(c);
        o.Should().BeNull();
        errors.Should().NotBeEmpty();

        var noRoot = new SearchCriteria();
        ExploreSearchLogic.Build(noRoot).Errors.Should().NotBeEmpty("a search needs a folder to look in");
    }

    [Fact]
    public void Build_date_bounds_cover_whole_days()
    {
        var c = Valid();
        c.ModifiedAfter = new DateTime(2026, 5, 5); c.ModifiedBefore = new DateTime(2026, 5, 5);
        var (o, errors) = ExploreSearchLogic.Build(c);
        errors.Should().BeEmpty();
        o!.ModifiedAfter!.Value.Kind.Should().Be(DateTimeKind.Utc);
        o.ModifiedAfter.Value.Should().Be(new DateTime(2026, 5, 5, 0, 0, 0, DateTimeKind.Utc));          // UTC days, whatever the machine's zone
        o.ModifiedBefore!.Value.Should().Be(new DateTime(2026, 5, 6, 0, 0, 0, DateTimeKind.Utc).AddTicks(-1));
    }

    [Fact]
    public void Apply_round_trips_through_options()
    {
        var c = Valid();
        c.NameText = "a"; c.ContentText = "b"; c.MinSize = "4"; c.MinSizeUnit = SizeUnit.KB; c.ModifiedAfter = new DateTime(2026, 1, 2);
        c.Encrypted = AttrFilter.MustNot; c.Hidden = AttrFilter.Must; c.ExcludeFolders = "x;y"; c.ContextLines = "7";
        var o = ExploreSearchLogic.Build(c).Options!;

        var d = new SearchCriteria();
        ExploreSearchLogic.Apply(d, o);
        d.NameText.Should().Be("a");
        d.ContentText.Should().Be("b");
        d.MinSize.Should().Be("4");
        d.MinSizeUnit.Should().Be(SizeUnit.KB);
        d.ModifiedAfter.Should().Be(new DateTime(2026, 1, 2));
        d.Encrypted.Should().Be(AttrFilter.MustNot);
        d.Hidden.Should().Be(AttrFilter.Must);
        d.ExcludeFolders.Should().Be("x; y");
        d.ContextLines.Should().Be("7");
        d.Roots.Should().Equal(c.Roots);
    }

    [Fact]
    public void Segments_split_around_matches_and_rejoin()
    {
        var segs = ExploreSearchLogic.SplitSegments("foo bar foo", new[] { new MatchSpan(0, 3), new MatchSpan(8, 3) });
        segs.Should().Equal(new TextSegment("foo", true), new TextSegment(" bar ", false), new TextSegment("foo", true));
    }

    [Fact]
    public void Segments_merge_overlaps_clamp_and_drop_bad_spans()
    {
        var segs = ExploreSearchLogic.SplitSegments("abcdefgh",
            new[] { new MatchSpan(4, 10), new MatchSpan(2, 3), new MatchSpan(-2, 3), new MatchSpan(3, 0), new MatchSpan(99, 2) });
        segs.Should().Equal(new TextSegment("a", true), new TextSegment("b", false), new TextSegment("cdefgh", true));
        string.Concat(segs.Select(s => s.Text)).Should().Be("abcdefgh");
    }

    [Fact]
    public void Segments_without_spans_are_one_plain_piece()
    {
        ExploreSearchLogic.SplitSegments("hello", null).Should().Equal(new TextSegment("hello", false));
        ExploreSearchLogic.SplitSegments("", new[] { new MatchSpan(0, 1) }).Should().BeEmpty();
    }

    [Fact]
    public void Status_text_lists_counts_and_notes()
    {
        var p = new SearchProgress(10, 1234, 20, 56, 7, new Dictionary<SkipReason, long>(), null, TimeSpan.FromSeconds(3.5), true, false, false);
        ExploreSearchLogic.StatusText(p).Should().Be($"56 found · {1234:N0} files scanned · 7 skipped · 3.5 s");
        ExploreSearchLogic.StatusText(p with { LimitReached = true }).Should().EndWith(" · limit reached");
        ExploreSearchLogic.StatusText(p with { Cancelled = true }).Should().EndWith(" · cancelled");
    }

    [Theory]
    [InlineData(0.4, "0.4 s")]
    [InlineData(75, "1:15")]
    [InlineData(3725, "1:02:05")]
    public void Elapsed_is_short_and_readable(double seconds, string expected) =>
        ExploreSearchLogic.FormatElapsed(TimeSpan.FromSeconds(seconds)).Should().Be(expected);

    [Fact]
    public void Hits_text_marks_truncation()
    {
        var r = new SearchResult("C:\\a.txt", "a.txt", "C:\\", false, 1, default, default, default, 0) { HitCount = 1000, HitsTruncated = true };
        ExploreSearchLogic.HitsText(r).Should().Be($"{1000:N0}+");
        ExploreSearchLogic.HitsText(r with { HitCount = 0 }).Should().BeEmpty();
    }
}
