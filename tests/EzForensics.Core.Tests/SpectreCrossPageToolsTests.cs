using System.Text.Json.Nodes;
using EzForensics.Core.Assistant;
using FluentAssertions;

namespace EzForensics.Core.Tests;

public class SpectreCrossPageToolsTests
{
    private sealed class Named : IAssistantTool
    {
        public Named(string name) { Spec = new ToolSpec(name, "", new JsonObject(), ToolKind.Proposal); }
        public ToolSpec Spec { get; }
        public Task<ToolOutcome> RunAsync(ToolCall call, CancellationToken ct) => Task.FromResult(new ToolOutcome("", false));
    }

    [Fact]
    public void Page_actions_are_offered_on_every_page_but_grid_tools_stay_on_Analyze()
    {
        var tools = new IAssistantTool[] { new Named(SpectrePageTools.IngestSelectSource), new Named(SpectrePageTools.ExploreSearch), new Named("grid_flag_rows") };
        SpectreSkills.ForPage(SpectrePage.Analyze, tools).Select(t => t.Spec.Name).Should().Contain(new[] { "ingest_select_source", "explore_search", "grid_flag_rows" });
        SpectreSkills.ForPage(SpectrePage.Tools, tools).Select(t => t.Spec.Name).Should().Equal("ingest_select_source", "explore_search");
    }

    [Theory]
    [InlineData("ingest D:\\Evidence with all jobs", true)]
    [InlineData("start the ingest", true)]
    [InlineData("run them", true)]
    [InlineData("scan D:\\Evidence", false)]
    [InlineData("just scan it, don't start anything", false)]
    [InlineData("ingest it but do not start the jobs", false)]
    [InlineData(null, false)]
    public void Start_is_auto_allowed_only_when_the_examiner_asked_to_start(string? question, bool expected) =>
        SpectrePageTools.AsksToStart(question).Should().Be(expected);

    [Theory]
    [InlineData("scan D:\\Evidence")]
    [InlineData("please ingest it")]
    [InlineData("now start the jobs")]
    public void Scan_and_start_requests_are_actions(string text) => SpectreSkills.IsActionRequest(text).Should().BeTrue();

    [Fact]
    public void Step_lines_say_what_was_done_with_only_the_short_target()
    {
        var call = new ToolCall("1", SpectrePageTools.ExploreSearch, new JsonObject { ["name"] = "*.ps1", ["text"] = "Invoke-WebRequest", ["look_in"] = "D:\\Case" });
        SpectrePageTools.Describe(call, new ToolOutcome("12 found", false)).Should().Be("Advanced search for *.ps1 containing Invoke-WebRequest in D:\\Case");

        var scan = new ToolCall("2", SpectrePageTools.IngestSelectSource, new JsonObject { ["path"] = "D:\\" + new string('x', 200) });
        var line = SpectrePageTools.Describe(scan, new ToolOutcome("no", true));
        line.Should().StartWith("Ingest: scan D:\\x").And.EndWith("... (failed)");
        line.Length.Should().BeLessThan(110);

        SpectrePageTools.Describe(new ToolCall("3", "case_search", new JsonObject { ["query"] = "mimikatz" }), new ToolOutcome("", false))
            .Should().Be("Searched the case for mimikatz");
    }
}
