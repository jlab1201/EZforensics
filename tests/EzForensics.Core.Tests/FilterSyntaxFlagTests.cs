using EzForensics.Core.Results;
using FluentAssertions;

namespace EzForensics.Core.Tests;

public class FilterSyntaxFlagTests
{
    [Theory]
    [InlineData("[Flag]")]
    [InlineData("Not [Flag]")]
    [InlineData("[Flag] In ('benign', 'malicious')")]
    [InlineData("Contains(*, 'x') And [Flag] In ('benign')")]
    [InlineData("[Source] = 'MFT' And Not [Flag]")]
    public void Flag_conditions_round_trip(string text)
    {
        FilterSyntax.TryParse(text, out var node, out var error).Should().BeTrue(error);
        node!.ToDisplayString().Should().Be(text);
    }

    [Fact]
    public void Flag_parses_to_flag_nodes()
    {
        FilterSyntax.TryParse("[flag] = 'red'", out var n, out _).Should().BeTrue();
        n.Should().BeOfType<FilterFlag>().Which.Ids.Should().BeEquivalentTo("red");
        FilterSyntax.TryParse("Not [Flag]", out n, out _).Should().BeTrue();
        n.Should().BeOfType<FilterFlag>().Which.Flagged.Should().BeFalse();
    }

    [Fact]
    public void Flag_nodes_survive_the_saved_filter_dto()
    {
        var n = new FilterFlag(true, new[] { "a", "b" });
        AnalyzeFilterNode.From(n).ToNode()!.ToDisplayString().Should().Be(n.ToDisplayString());
        AnalyzeFilterNode.From(new FilterFlag(false)).ToNode()!.ToDisplayString().Should().Be("Not [Flag]");
    }
}
