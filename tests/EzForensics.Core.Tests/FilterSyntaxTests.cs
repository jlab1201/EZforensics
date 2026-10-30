using EzForensics.Core.Results;
using FluentAssertions;

namespace EzForensics.Core.Tests;

public class FilterSyntaxTests
{
    private static FilterNode Parse(string text)
    {
        FilterSyntax.TryParse(text, out var node, out var error).Should().BeTrue(error);
        node.Should().NotBeNull();
        return node!;
    }

    private static void RoundTrip(FilterNode n)
    {
        var text = n.ToDisplayString();
        var back = Parse(text);
        back.ToDisplayString().Should().Be(text);
    }

    public static IEnumerable<object[]> Modes() =>
        Enum.GetValues<ColumnFilterMode>().Select(m => new object[] { m });

    [Theory]
    [MemberData(nameof(Modes))]
    public void Every_mode_round_trips(ColumnFilterMode mode)
    {
        RoundTrip(new FilterCondition("Message", mode, "a b"));
        RoundTrip(new FilterCondition("*", mode, "x"));
        RoundTrip(FilterGroup.And(new FilterCondition("A", mode, "1"), new FilterCondition("B", mode, "2")));
    }

    [Fact]
    public void Between_variants_round_trip()
    {
        RoundTrip(new FilterCondition("Size", ColumnFilterMode.Between, "1", "9"));
        RoundTrip(new FilterCondition("Size", ColumnFilterMode.Between, "1"));
        RoundTrip(new FilterCondition("Size", ColumnFilterMode.Between, "", "9"));
        var c = (FilterCondition)Parse("[Size] Between('1', '9')");
        c.Op.Should().Be(ColumnFilterMode.Between);
        c.Value.Should().Be("1");
        c.Value2.Should().Be("9");
    }

    [Fact]
    public void Value_and_date_sets_round_trip()
    {
        RoundTrip(new FilterValueSet("Source", new[] { "MFT", "EVTX" }));
        RoundTrip(new FilterValueSet("Source", new[] { "MFT" }, true));
        RoundTrip(new FilterValueSet("Source", Array.Empty<string>(), true));
        RoundTrip(new FilterValueSet("Source", Array.Empty<string>()));
        RoundTrip(new FilterDateSet("Time", new[] { new DateOnly(2024, 1, 1), new DateOnly(2024, 1, 2), new DateOnly(2024, 1, 3), new DateOnly(2024, 2, 9) }));
        RoundTrip(new FilterDateSet("Time", new[] { new DateOnly(2024, 1, 1) }, true));
        RoundTrip(FilterGroup.And(new FilterValueSet("S", new[] { "a" }, true), new FilterCondition("M", ColumnFilterMode.Contains, "x")));
    }

    [Fact]
    public void Value_set_with_blanks_restores_include_blanks()
    {
        var vs = Parse("[S] In ('a', 'b') Or IsNullOrEmpty([S])").Should().BeOfType<FilterValueSet>().Subject;
        vs.IncludeBlanks.Should().BeTrue();
        vs.Values.Should().BeEquivalentTo(new[] { "a", "b" });
    }

    [Fact]
    public void Nesting_and_not_round_trip()
    {
        var n = FilterGroup.And(
            new FilterCondition("A", ColumnFilterMode.Equals, "1"),
            FilterGroup.Or(new FilterCondition("B", ColumnFilterMode.Like, "x*"),
                FilterGroup.Not(FilterGroup.And(new FilterCondition("C", ColumnFilterMode.Regex, "^a"), new FilterCondition("D", ColumnFilterMode.IsEmpty)))),
            FilterGroup.Not(new FilterCondition("E", ColumnFilterMode.Contains, "z")),
            FilterGroup.Not(FilterGroup.Not(new FilterCondition("F", ColumnFilterMode.BeginsWith, "q"))));
        RoundTrip(n);
        RoundTrip(FilterGroup.Or(n, new FilterCondition("G", ColumnFilterMode.Greater, "5")));
    }

    [Fact]
    public void Not_contains_and_not_empty_map_to_modes()
    {
        var g = (FilterGroup)Parse("Not Contains([A], 'x') And Not IsNullOrEmpty([B])");
        ((FilterCondition)g.Children[0]).Op.Should().Be(ColumnFilterMode.NotContains);
        ((FilterCondition)g.Children[1]).Op.Should().Be(ColumnFilterMode.IsNotEmpty);
    }

    [Fact]
    public void Quotes_backslashes_and_any_column()
    {
        var n = new FilterCondition("*", ColumnFilterMode.Contains, @"it's a \path\ ''x''");
        RoundTrip(n);
        var c = (FilterCondition)Parse(n.ToDisplayString());
        c.Column.Should().Be("*");
        c.Value.Should().Be(@"it's a \path\ ''x''");
        RoundTrip(new FilterCondition("Col Name (x)", ColumnFilterMode.Equals, "v"));
        RoundTrip(new FilterValueSet("S", new[] { "it's", @"a\b" }));
    }

    [Fact]
    public void Keywords_are_case_insensitive()
    {
        var g = (FilterGroup)Parse("contains([A], 'x') OR NOT startswith([B], 'y')");
        g.Kind.Should().Be(FilterGroupKind.Or);
        g.Children.Should().HaveCount(2);
    }

    [Fact]
    public void Empty_text_is_an_empty_filter()
    {
        var n = Parse("   ");
        n.IsEmpty.Should().BeTrue();
        n.Should().BeOfType<FilterGroup>();
    }

    [Theory]
    [InlineData("(Contains([A], 'x')")]
    [InlineData("Contains([A], 'x'))")]
    [InlineData("Foo([A], 'x')")]
    [InlineData("Contains([A])")]
    [InlineData("Contains([A],")]
    [InlineData("[A] =")]
    [InlineData("[A] = 'x' garbage")]
    [InlineData("[A] = 'x' And")]
    [InlineData("[A] = 'unterminated")]
    [InlineData("[A")]
    [InlineData("[A] In ('a', … (+3))")]
    [InlineData("[A] InDays(#2024-13-01#)")]
    [InlineData("[A] InDays(#2024-01-05#..#2024-01-01#)")]
    [InlineData("()")]
    [InlineData("[A] ~ 'x'")]
    public void Invalid_text_reports_an_error(string text)
    {
        FilterSyntax.TryParse(text, out var node, out var error).Should().BeFalse();
        node.Should().BeNull();
        error.Should().NotBeNullOrWhiteSpace();
    }

    [Fact]
    public void Error_messages_are_readable()
    {
        FilterSyntax.TryParse("Foo([A], 'x')", out _, out var e1);
        e1.Should().Contain("Unknown function 'Foo'");
        FilterSyntax.TryParse("Contains([A])", out _, out var e2);
        e2.Should().Contain("second argument");
        FilterSyntax.TryParse("[A] = 'x' zzz", out _, out var e3);
        e3.Should().Contain("zzz");
    }

    [Fact]
    public void Deep_nesting_is_rejected_instead_of_overflowing_the_stack()
    {
        FilterSyntax.TryParse(new string('(', 50_000) + "[A] = 'x'" + new string(')', 50_000), out _, out var e1).Should().BeFalse();
        e1.Should().Contain("nested too deeply");
        FilterSyntax.TryParse(string.Concat(Enumerable.Repeat("Not ", 50_000)) + "[A] = 'x'", out _, out var e2).Should().BeFalse();
        e2.Should().Contain("nested too deeply");
        FilterSyntax.TryParse("((([A] = 'x')))", out var ok, out _).Should().BeTrue();
        ok.Should().NotBeNull();
    }
}
