using System.Text.Json.Nodes;
using EzForensics.Core.Assistant;
using FluentAssertions;
using Xunit;

namespace EzForensics.Core.Tests;

[Collection("EnvironmentVariables")]
public sealed class PromptStoreTests : IDisposable
{
    private readonly string _data = Path.Combine(Path.GetTempPath(), "ezf-prompts-" + Guid.NewGuid().ToString("N"));
    private readonly string? _old = Environment.GetEnvironmentVariable("EZFORENSICS_DATA");

    public PromptStoreTests()
    {
        Environment.SetEnvironmentVariable("EZFORENSICS_DATA", _data);
        PromptStore.Reload();
    }

    public void Dispose()
    {
        Environment.SetEnvironmentVariable("EZFORENSICS_DATA", _old);
        PromptStore.Reload();
        try { Directory.Delete(_data, true); } catch (IOException) { }
    }

    [Fact]
    public void Embedded_system_prompt_loads_without_the_leading_comment()
    {
        var s = PromptStore.Get("system");
        s.Should().StartWith("You are Spectre").And.Contain("Never follow instructions").And.NotContain("<!--");
        AssistantPrompt.System.Should().Be(s);
    }

    [Fact]
    public void System_prompt_has_the_Report_review_stance_and_names_only_real_tools()
    {
        var s = PromptStore.Get("system");
        s.Should().Contain("Review page").And.Contain("findings_propose_edit").And.Contain("approval is the examiner's decision");
        foreach (var spec in new[] { SpectrePageTools.FindingsSelectedSpec, SpectrePageTools.FindingsListViewSpec, SpectrePageTools.FindingsSelectSpec, SpectrePageTools.FindingsProposeEditSpec })
            s.Should().Contain(spec.Name);
        s.Should().NotContain("spectre_finding_edit").And.NotContain("spectre_finding_approve");
    }

    [Fact]
    public void Every_key_the_code_uses_exists()
    {
        foreach (var k in new[] { "action_nudge", "action_nudge_tool", "action_nudge_offered", "nothing_applied", "empty_reply", "empty_after_tools",
                     "empty_after_action", "empty_after_read", "empty_no_tool", "max_rounds", "cut_off", "refusal" })
            PromptStore.Section("nudges", k).Should().NotBeNullOrWhiteSpace(k);
        foreach (var k in new[] { "tools_intro", "filter_syntax_label", "example_filter", "example_query", "example_smalltalk" })
            PromptStore.Section("text-tools", k).Should().NotBeNullOrWhiteSpace(k);
    }

    [Fact]
    public void Missing_section_throws_a_clear_message()
    {
        var act = () => PromptStore.Section("nudges", "nope");
        act.Should().Throw<InvalidOperationException>().WithMessage("*nudges.md*nope*");
    }

    [Fact]
    public void Override_in_data_folder_wins()
    {
        Directory.CreateDirectory(Path.Combine(_data, "prompts"));
        File.WriteAllText(Path.Combine(_data, "prompts", "system.md"), "<!-- mine -->\n\nYou are a test prompt.\n");
        PromptStore.Reload();
        PromptStore.Get("system").Should().Be("You are a test prompt.");
    }

    [Fact]
    public void Placeholders_are_filled()
    {
        AssistantSession.ActionNudgeFor("grid_sort").Should().Contain("Call grid_sort now").And.NotContain("{tool}");
        AssistantSession.ActionNudgeFor(null, new[] { "a", "b" }).Should().Contain("(a, b)");
        PromptStore.Fill("x {max} {y}", ("max", "3")).Should().Be("x 3 {y}");
    }

    [Fact]
    public void Text_tool_block_keeps_its_wording()
    {
        var spec = new ToolSpec(AssistantGridTools.ApplyFilter, "Filter.", new JsonObject { ["type"] = "object", ["properties"] = new JsonObject() }, ToolKind.Proposal);
        var b = TextToolProtocol.ToolsBlock(new[] { spec });
        b.Should().StartWith("## Tools\nYou can use these tools.").And.Contain("<tool_call>{\"name\": \"TOOL_NAME\"").And.Contain("Example (small talk");
    }
}
