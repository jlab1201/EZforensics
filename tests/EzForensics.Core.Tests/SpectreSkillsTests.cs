using System.Text.Json.Nodes;
using EzForensics.Core.Assistant;
using FluentAssertions;

namespace EzForensics.Core.Tests;

public class SpectreSkillsTests
{
    [Theory]
    [InlineData("how do I decode this base64?", SpectrePage.Explore, "explore-cyberchef")]
    [InlineData("how do I save this filter?", SpectrePage.Analyze, "analyze-saved-filters")]
    [InlineData("how can I hide some columns?", SpectrePage.Analyze, "analyze-views")]
    [InlineData("where do I see the process tree?", SpectrePage.Analyze, "process-tree")]
    [InlineData("How do I ingest a KAPE folder?", SpectrePage.Analyze, "ingest")]
    [InlineData("how to retry a failed job", SpectrePage.Ingest, "jobs")]
    [InlineData("how do I change the model?", SpectrePage.Analyze, "options-ai")]
    public void How_to_questions_attach_the_matching_skill(string question, SpectrePage page, string expected) =>
        SpectreSkills.MatchHowTo(question, page).Select(s => s.Id).Should().Contain(expected).And.HaveCountLessThanOrEqualTo(2);

    [Theory]
    [InlineData("anything suspicious?")]
    [InlineData("filter to event 4688")]
    [InlineData("which file shows executed programs?")]
    [InlineData("how do I feel about this?")]
    public void Other_questions_attach_nothing(string question) =>
        SpectreSkills.MatchHowTo(question, SpectrePage.Analyze).Should().BeEmpty();

    [Fact]
    public void Leaked_skill_ids_become_feature_names() =>
        SpectreSkills.ReplaceIds("Use `analyze-go-to-row` or analyze-sort next.").Should().Be("Use Go to a row or Sort the table next.");

    [Fact]
    public void Action_results_ask_for_a_one_line_reply()
    {
        AssistantGridTools.AppliedText.Should().Contain("ONE short sentence");
        AssistantGridTools.ProposedText.Should().Contain("NOT applied").And.Contain("ONE short sentence");
    }

    [Fact]
    public void Help_block_carries_the_real_help()
    {
        var block = SpectreSkills.HelpBlock(new[] { SpectreSkills.Find("explore-cyberchef")! });
        block.Should().StartWith("<skill_help name=\"CyberChef (decode data)\"").And.Contain("Send to CyberChef");
    }

    private sealed class NullHost : IAssistantHost
    {
        public void ShowProposal(AssistantProposal proposal) { }
        public Task<bool> ConfirmWriteAsync(ToolCall call) => Task.FromResult(false);
        public void Status(string text) { }
    }

    /// <summary>Every tool the app can build: grid tools, case tools (with open-file) and skill_help.</summary>
    private static List<IAssistantTool> AppTools()
    {
        var host = new NullHost();
        var tools = new List<IAssistantTool>();
        tools.AddRange(AssistantGridTools.Create(() => new GridContext(null, Array.Empty<string>(), 0, 0, "", Array.Empty<GridSampleRow>()), () => Array.Empty<string>(), host));
        using var caseTools = new AssistantCaseTools(() => null, host, canOpenFile: true);
        tools.AddRange(caseTools.Tools);
        tools.Add(SpectreSkills.CreateHelpTool());
        return tools;
    }

    [Fact]
    public void Ids_are_unique_and_text_is_filled_in()
    {
        SpectreSkills.All.Select(s => s.Id).Should().OnlyHaveUniqueItems();
        SpectreSkills.All.Select(s => s.Name).Should().OnlyHaveUniqueItems();
        foreach (var s in SpectreSkills.All)
        {
            s.Summary.Should().NotBeNullOrWhiteSpace().And.NotContain("\n", because: s.Id);
            s.Help.Should().NotBeNullOrWhiteSpace();
            s.Help.Split('\n').Length.Should().BeLessThanOrEqualTo(15, s.Id);
        }
    }

    [Fact]
    public void Every_tool_name_is_a_real_tool()
    {
        var names = AppTools().Select(t => t.Spec.Name).ToHashSet();
        // page actions are bound by the app's page view models; their specs live in SpectrePageTools
        foreach (var spec in new[] { SpectrePageTools.GoToPageSpec, SpectrePageTools.JobsRetrySpec, SpectrePageTools.JobsCancelSpec, SpectrePageTools.ToolsInstallSpec, SpectrePageTools.OptionsSetSpec, SpectrePageTools.IngestStartSpec,
            SpectrePageTools.IngestSelectSourceSpec, SpectrePageTools.ExploreSearchSpec, SpectrePageTools.ExploreOpenSpec,
            SpectrePageTools.FindingsSelectedSpec, SpectrePageTools.FindingsListViewSpec, SpectrePageTools.FindingsSelectSpec, SpectrePageTools.FindingsProposeEditSpec,
            SpectrePageTools.FindingsTimelineViewSpec, SpectrePageTools.FindingsSelectMomentSpec, SpectrePageTools.FindingsShowEvidenceSpec, SpectrePageTools.KeyEventsListSpec, SpectrePageTools.KeyEventsGetSpec })
            names.Add(spec.Name);
        foreach (var s in SpectreSkills.All)
            foreach (var t in s.ToolNames) names.Should().Contain(t, because: s.Id);
    }

    [Fact]
    public void Index_is_compact_and_lists_the_current_page_first()
    {
        var index = SpectreSkills.Index(SpectrePage.Explore);
        index.Split('\n').Length.Should().BeLessThanOrEqualTo(30);
        index.Split('\n')[1].Should().StartWith("- explore-");
        index.Should().Contain("analyze-saved-filters [Analyze]");
        SpectreSkills.Index(SpectrePage.Analyze).Split('\n')[1].Should().StartWith("- analyze-");
    }

    [Fact]
    public async Task Skill_help_accepts_id_or_name_and_lists_ids_otherwise()
    {
        var tool = SpectreSkills.CreateHelpTool();
        var byId = await tool.RunAsync(new ToolCall("c", "skill_help", new JsonObject { ["name"] = "ANALYZE-saved-filters" }), default);
        byId.IsError.Should().BeFalse();
        byId.Text.Should().Contain("Save filter as");
        var byName = await tool.RunAsync(new ToolCall("c", "skill_help", new JsonObject { ["name"] = "saved filters" }), default);
        byName.Text.Should().Contain("Save filter as");
        var bad = await tool.RunAsync(new ToolCall("c", "skill_help", new JsonObject { ["name"] = "nope" }), default);
        bad.IsError.Should().BeTrue();
        bad.Text.Should().Contain("analyze-filter").And.Contain("explore-cyberchef");
    }

    [Fact]
    public void Explore_gets_no_grid_actions_but_analyze_does()
    {
        var all = AppTools();
        var explore = SpectreSkills.ForPage(SpectrePage.Explore, all).Select(t => t.Spec.Name).ToList();
        explore.Should().NotContain(n => n.StartsWith("grid_")).And.NotContain("analyze_open_file");
        explore.Should().Contain(new[] { "case_list_files", "case_search", "csv_query", "csv_column_values", "csv_get_rows", "skill_help" });
        var analyze = SpectreSkills.ForPage(SpectrePage.Analyze, all).Select(t => t.Spec.Name).ToList();
        analyze.Should().Contain(new[] { "grid_apply_filter", "grid_flag_rows", "grid_sort", "grid_go_to_row", "grid_get_context", "analyze_open_file", "skill_help" });
    }

    [Fact]
    public void Findings_page_tools_stay_on_Findings_and_only_select_follows_to_other_pages()
    {
        var specs = new[] { SpectrePageTools.FindingsSelectedSpec, SpectrePageTools.FindingsListViewSpec, SpectrePageTools.FindingsSelectSpec, SpectrePageTools.FindingsProposeEditSpec };
        var tools = specs.Select(s => (IAssistantTool)new Fixed(s)).ToList();
        SpectreSkills.ForPage(SpectrePage.Findings, tools).Select(t => t.Spec.Name).Should().BeEquivalentTo(specs.Select(s => s.Name));
        SpectreSkills.ForPage(SpectrePage.Analyze, tools).Select(t => t.Spec.Name).Should().Equal("findings_select");
        SpectrePageTools.CrossPage.Should().Contain("findings_select").And.NotContain("findings_propose_edit");
        specs.Should().OnlyContain(s => s.Kind == ToolKind.Read);   // no card, no engine write: the examiner decides on the yellow bar
        // the model never gets the engine's write tools
        specs.Select(s => s.Name).Should().NotContain(n => n.StartsWith("spectre_finding_"));
    }

    [Fact]
    public void Findings_tools_are_kept_on_every_page()
    {
        var extra = new ToolSpec("spectre_list_findings", "x", new JsonObject(), ToolKind.Read);
        var tools = new List<IAssistantTool> { new Fixed(extra) };
        SpectreSkills.ForPage(SpectrePage.Explore, tools).Should().HaveCount(1);
    }

    private sealed class Fixed : IAssistantTool
    {
        public Fixed(ToolSpec s) => Spec = s;
        public ToolSpec Spec { get; }
        public Task<ToolOutcome> RunAsync(ToolCall call, CancellationToken ct) => Task.FromResult(new ToolOutcome("", false));
    }

    [Theory]
    [InlineData("flag them", true)]
    [InlineData("i said flag them.", true)]
    [InlineData("Please sort by TimeCreated newest first", true)]
    [InlineData("flag all suspicious powershell commands", true)]
    [InlineData("ok, filter to 4688", true)]
    [InlineData("are you seeing what I'm seeing?", false)]
    [InlineData("how do I flag a row?", false)]
    [InlineData("what is flagged?", false)]
    [InlineData("markers of persistence?", false)]
    [InlineData("alright, from here, filter powershell-related commands.", true)]
    [InlineData("great. now sort by time", true)]
    [InlineData("how do I flag rows, then sort them?", false)]
    [InlineData("what did it run, and when?", false)]
    public void Action_requests_are_recognised(string text, bool expected) => SpectreSkills.IsActionRequest(text).Should().Be(expected);

    [Fact]
    public void Prompt_puts_the_answer_style_first_and_last()
    {
        var p = AssistantPrompt.System;
        p.IndexOf("How to answer", StringComparison.Ordinal).Should().BeLessThan(p.IndexOf("Actions:", StringComparison.Ordinal));
        p.Should().Contain("are you seeing what I'm seeing?").And.Contain("Never summarize, review or list observations");
        p.TrimEnd().Should().EndWith("unless the examiner asks for more.");
    }
}
