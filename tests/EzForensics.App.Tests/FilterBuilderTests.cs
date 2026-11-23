using EzForensics.App.ViewModels;
using EzForensics.Core.Results;
using FluentAssertions;

namespace EzForensics.App.Tests;

public sealed class FilterBuilderTests
{
    private static readonly string[] Headers = { "Source", "Message", "EventId" };

    private static FilterBuilderViewModel Builder() => new(Headers, new FilterGroup(), flagChoices: new[] { ("red", "Malicious"), ("blue", "Info") });

    private static FilterNode Parse(string text)
    {
        FilterSyntax.TryParse(text, out var node, out var err).Should().BeTrue(err);
        return node!;
    }

    [Theory]
    [InlineData("[Flag]")]
    [InlineData("Not [Flag]")]
    [InlineData("[Flag] In ('red')")]
    [InlineData("[Flag] In ('blue', 'red')")]
    [InlineData("[Source] = 'MFT' And [Flag]")]
    [InlineData("[Source] = 'MFT' And Not [Flag]")]
    [InlineData("Contains([Message], 'x') And [Flag] In ('blue', 'red') And [EventId] = '4688'")]
    public void LoadNode_keeps_flag_conditions(string text)
    {
        var vm = Builder();
        vm.LoadNode(Parse(text));
        vm.Build().ToDisplayString().Should().Be(text);
        vm.SyntaxText.Should().Be(text);
    }

    [Fact]
    public void Flag_condition_shows_as_a_flag_row_with_flag_operators()
    {
        var vm = Builder();
        vm.LoadNode(Parse("[Flag] In ('red')"));
        var row = vm.RootGroup.Children.Should().ContainSingle().Subject.Should().BeOfType<BuilderConditionViewModel>().Subject;
        row.IsFlag.Should().BeTrue();
        row.Value.Should().Be("red");
        row.AvailableOps.Should().BeSameAs(FilterOps.Flag);
    }

    [Fact]
    public void Several_flag_rows_merge_into_one_flag_filter()
    {
        var vm = Builder();
        vm.LoadNode(Parse("[Flag] In ('blue', 'red')"));
        vm.RootGroup.Children.Should().HaveCount(2);
        vm.Build().ToDisplayString().Should().Be("[Flag] In ('blue', 'red')");
    }

    [Fact]
    public void Switching_to_the_flag_field_gives_it_valid_operators()
    {
        var vm = Builder();
        vm.LoadNode(Parse("Contains([Message], 'x')"));
        var row = (BuilderConditionViewModel)vm.RootGroup.Children[0];
        row.Column = "Flag";
        FilterOps.Flag.Should().Contain(row.Op);
        row.Column = "Message";
        FilterOps.All.Should().Contain(row.Op);
    }

    [Fact]
    public void Field_box_accepts_only_valid_fields()
    {
        var vm = Builder();
        vm.LoadNode(Parse("[Source] = 'a'"));
        var row = (BuilderConditionViewModel)vm.RootGroup.Children[0];
        row.CommitColumnText("message").Should().BeTrue();
        row.Column.Should().Be("Message");
        row.CommitColumnText("nope").Should().BeFalse();
        row.Column.Should().Be("Message");
        row.CommitColumnText("Any field").Should().BeTrue();
        row.Column.Should().Be("*");
        row.ColumnText.Should().Be("Any field");
        row.CommitColumnText("flag").Should().BeTrue();
        row.Column.Should().Be("Flag");
    }

    [Fact]
    public void FieldSearch_filters_contains_with_prefix_matches_first()
    {
        var all = new[] { "Any field", "Flag", "Source", "SourceFile", "Message", "ParentSource" };
        FieldSearch.Filter(all, "").Should().Equal(all);
        FieldSearch.Filter(all, "sou").Should().Equal("Source", "SourceFile", "ParentSource");
        FieldSearch.Filter(all, "ource").Should().Equal("Source", "SourceFile", "ParentSource");
        FieldSearch.Filter(all, "SOURCEF").Should().Equal("SourceFile");
        FieldSearch.Filter(all, "a").Should().Equal("Any field", "Flag", "Message", "ParentSource");
        FieldSearch.Filter(all, "zzz").Should().BeEmpty();
    }

    [Fact]
    public void Split_flag_out_of_the_expression_for_the_grid()
    {
        var (expr, flag) = ResultsViewModel.SplitFlag((FilterGroup)Parse("[Source] = 'a' And [Flag] In ('red') And [Flag] In ('blue')"));
        expr.ToDisplayString().Should().Be("[Source] = 'a'");
        flag!.Flagged.Should().BeTrue();
        flag.Ids.Should().BeEquivalentTo("red", "blue");
        ResultsViewModel.SplitFlag(FilterGroup.And(Parse("[Source] = 'a'"))).Flag.Should().BeNull();
        ResultsViewModel.SplitFlag(FilterGroup.And(new FilterFlag(false))).Flag!.Flagged.Should().BeFalse();
    }
}
