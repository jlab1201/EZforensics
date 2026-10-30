using EzForensics.Core.Results;
using FluentAssertions;

namespace EzForensics.Core.Tests;

public class FilterTokenizerTests
{
    private static List<(FilterTokenKind k, string t)> Meaningful(string s) =>
        FilterTokenizer.Tokenize(s).Where(x => x.Kind != FilterTokenKind.Whitespace).Select(x => (x.Kind, x.Text)).ToList();

    [Theory]
    [InlineData("Contains([Message], 'x') And [Source] = 'MFT'")]
    [InlineData("Not [Flag]")]
    [InlineData("[Flag] In ('Red', 'Blue')")]
    [InlineData("[A] Between('1', '2.5') Or [B] >= '3' And [C] <= 'x' And [D] <> 'it''s'")]
    [InlineData("[Date] InDays(#2024-01-01#..#2024-01-03#, #2024-02-01#, … (+3))")]
    [InlineData("Not (Regex([A], '^a.*') Or [B] Like 'a*b?')  \t\r\n")]
    [InlineData("Contains(*, 'x') And [A] Between(#2024-01-01 10:00:00#, #2024-01-02 10:00:00#)")]
    [InlineData("[unterminated 'str #dat ( ??? ☃ ")]
    [InlineData("")]
    public void RoundTrip_IsLossless(string input)
    {
        var toks = FilterTokenizer.Tokenize(input);
        string.Concat(toks.Select(t => t.Text)).Should().Be(input);
        int pos = 0;
        foreach (var t in toks) { t.Start.Should().Be(pos); t.Length.Should().Be(t.Text.Length); pos += t.Length; }
    }

    [Fact]
    public void Null_ReturnsEmpty() => FilterTokenizer.Tokenize(null).Should().BeEmpty();

    [Fact]
    public void Contains_And_Equals()
    {
        Meaningful("Contains([Message], 'x') And [Source] = 'MFT'").Should().Equal(new[]
        {
            (FilterTokenKind.Function, "Contains"), (FilterTokenKind.Paren, "("), (FilterTokenKind.Column, "[Message]"),
            (FilterTokenKind.Comma, ","), (FilterTokenKind.String, "'x'"), (FilterTokenKind.Paren, ")"),
            (FilterTokenKind.Logical, "And"), (FilterTokenKind.Column, "[Source]"), (FilterTokenKind.Operator, "="),
            (FilterTokenKind.String, "'MFT'"),
        });
    }

    [Fact]
    public void Not_And_In()
    {
        Meaningful("Not [Flag]").Select(x => x.k).Should().Equal(FilterTokenKind.Not, FilterTokenKind.Column);
        Meaningful("[Flag] In ('Red','Blue')").Select(x => x.k).Should().Equal(
            FilterTokenKind.Column, FilterTokenKind.Operator, FilterTokenKind.Paren, FilterTokenKind.String,
            FilterTokenKind.Comma, FilterTokenKind.String, FilterTokenKind.Paren);
    }

    [Fact]
    public void Operators_Like_Between_Comparisons()
    {
        Meaningful("[A] Like 'a*'")[1].Should().Be((FilterTokenKind.Operator, "Like"));
        Meaningful("[A] Between('1', '2')")[1].Should().Be((FilterTokenKind.Operator, "Between"));
        foreach (var op in new[] { "<>", ">=", "<=", ">", "<", "=", "?" })
            Meaningful($"[A] {op} 'x'")[1].Should().Be((FilterTokenKind.Operator, op));
    }

    [Fact]
    public void Dates_Numbers_Ranges()
    {
        var m = Meaningful("[D] InDays(#2024-01-01#..#2024-01-03#, … (+3))");
        m.Should().Contain((FilterTokenKind.Function, "InDays"));
        m.Should().Contain((FilterTokenKind.Date, "#2024-01-01#"));
        m.Should().Contain((FilterTokenKind.Operator, ".."));
        m.Should().Contain((FilterTokenKind.Number, "+3"));
        Meaningful("[D] >= #2024-01-01 10:00:00#").Last().Should().Be((FilterTokenKind.Date, "#2024-01-01 10:00:00#"));
    }

    [Fact]
    public void Strings_WithEscapedQuotes_AndBracketsInside()
    {
        var m = Meaningful("[A] = 'it''s [x] and Or'");
        m.Should().HaveCount(3);
        m[2].Should().Be((FilterTokenKind.String, "'it''s [x] and Or'"));
    }

    [Fact]
    public void Malformed_ProducesUnknownOrRunsToEnd()
    {
        Meaningful("[abc")[0].Should().Be((FilterTokenKind.Column, "[abc"));
        Meaningful("'abc")[0].Should().Be((FilterTokenKind.String, "'abc"));
        Meaningful("foo")[0].Should().Be((FilterTokenKind.Unknown, "foo"));
        Meaningful("☃")[0].k.Should().Be(FilterTokenKind.Unknown);
    }

    [Fact]
    public void RealRenderedExpression_RoundTrips()
    {
        var g = FilterGroup.And(
            new FilterCondition("Message", ColumnFilterMode.Contains, "it's"),
            FilterGroup.Not(new FilterCondition("A", ColumnFilterMode.Like, "a*")),
            new FilterValueSet("Flag", new[] { "Red", "Blue" }, true),
            new FilterCondition("T", ColumnFilterMode.Between, "1", "2"));
        var text = g.ToDisplayString();
        string.Concat(FilterTokenizer.Tokenize(text).Select(t => t.Text)).Should().Be(text);
        FilterTokenizer.Tokenize(text).Should().NotContain(t => t.Kind == FilterTokenKind.Unknown);
    }

    [Fact]
    public void EveryOperatorRender_HasNoUnknownTokens_AndExpectedKinds()
    {
        foreach (var mode in Enum.GetValues<ColumnFilterMode>())
        {
            var text = new FilterCondition("Col A", mode, "v", "w").ToDisplayString();
            var toks = FilterTokenizer.Tokenize(text);
            string.Concat(toks.Select(t => t.Text)).Should().Be(text);
            toks.Should().NotContain(t => t.Kind == FilterTokenKind.Unknown, because: text);
            toks.Should().Contain(t => t.Kind == FilterTokenKind.Column && t.Text == "[Col A]", because: text);
        }
    }

    [Fact]
    public void FilterSpec_FullRender_HasNoUnknownTokens()
    {
        var spec = new FilterSpec
        {
            GlobalText = "ps",
            DateColumn = "T", DateFrom = new DateTime(2024, 1, 1), DateTo = new DateTime(2024, 1, 2),
            Flag = FlagFilterMode.Flagged,
            Expression = FilterGroup.Or(
                new FilterDateSet("T", Enumerable.Range(1, 12).Select(d => new DateOnly(2024, 1, 1).AddDays(d * 2)), true),
                new FilterValueSet("S", Enumerable.Range(0, 12).Select(i => "v" + i), true)),
        };
        var text = spec.ToDisplayString();
        FilterTokenizer.Tokenize(text).Where(t => t.Kind == FilterTokenKind.Unknown).Select(t => t.Text.Trim()).Should().OnlyContain(x => x == "…");
    }
}
