using System.Text.Json.Nodes;
using EzForensics.Core.Assistant;
using FluentAssertions;

namespace EzForensics.Core.Tests;

public class TextToolProtocolTests
{
    private static readonly string[] Names = { "grid_apply_filter", "grid_sort" };

    [Fact]
    public void A_tagged_call_is_read_and_the_text_before_it_kept()
    {
        var (said, calls) = TextToolProtocol.Parse("Sure. <tool_call>{\"name\": \"grid_sort\", \"arguments\": {\"column\": \"Time\"}}</tool_call>", Names);
        said.Should().Be("Sure.");
        calls.Single().Name.Should().Be("grid_sort");
        calls.Single().Args["column"]!.GetValue<string>().Should().Be("Time");
    }

    [Fact]
    public void A_call_cut_off_by_the_stop_sequence_is_still_read()
    {
        // Ollama drops the stop text, so the closing tag never arrives
        var (_, calls) = TextToolProtocol.Parse(" <tool_call>{\"name\":\"grid_apply_filter\",\"parameters\":{\"column\":\"Name\",\"op\":\"contains\",\"value\":\"a}b\"}}", Names);
        calls.Single().Args["value"]!.GetValue<string>().Should().Be("a}b");
    }

    [Fact]
    public void A_bare_object_naming_an_offered_tool_counts_and_a_copied_schema_gives_no_arguments()
    {
        var copied = "{\"type\":\"function\",\"function\":{\"name\":\"grid_apply_filter\",\"parameters\":{\"type\":\"object\",\"properties\":{\"column\":{\"type\":\"string\"}}}}}";
        var (_, calls) = TextToolProtocol.Parse(copied, Names);
        calls.Single().Name.Should().Be("grid_apply_filter");
        calls.Single().Args.Should().BeEmpty();

        TextToolProtocol.Parse("{\"name\":\"other\",\"arguments\":{}}", Names).Calls.Should().BeEmpty();
        var plain = TextToolProtocol.Parse("Prefetch shows executed programs.", Names);
        plain.Said.Should().Be("Prefetch shows executed programs.");
        plain.Calls.Should().BeEmpty();
    }

    [Fact]
    public void Signatures_show_required_optional_and_enums()
    {
        var schema = JsonNode.Parse("""{"type":"object","properties":{"column":{"type":"string"},"op":{"type":"string","enum":["contains","equals"]},"rows":{"type":"array","items":{"type":"integer"}}},"required":["column"]}""")!.AsObject();
        TextToolProtocol.Signature(new ToolSpec("f", "d", schema, ToolKind.Proposal))
            .Should().Be("f(column: string, op?: \"contains\"|\"equals\", rows?: integer[])");
    }

    [Fact]
    public void The_real_tools_render_in_a_compact_block()
    {
        var specs = SpectreSkillsTestsTools();
        var block = TextToolProtocol.ToolsBlock(specs);
        block.Should().Contain("- grid_apply_filter(").And.Contain("Example:");
        block.Split('\n').Length.Should().BeLessThan(specs.Count * 4 + 20);
    }

    private static List<ToolSpec> SpectreSkillsTestsTools()
    {
        var host = new NullHost();
        var specs = AssistantGridTools.Create(() => new GridContext(null, Array.Empty<string>(), 0, 0, "", Array.Empty<GridSampleRow>()), () => Array.Empty<string>(), host)
            .Select(t => t.Spec).ToList();
        using var caseTools = new AssistantCaseTools(() => null, host, canOpenFile: true);
        specs.AddRange(caseTools.Tools.Select(t => t.Spec));
        return specs;
    }

    private sealed class NullHost : IAssistantHost
    {
        public void ShowProposal(AssistantProposal proposal) { }
        public Task<bool> ConfirmWriteAsync(ToolCall call) => Task.FromResult(false);
        public void Status(string text) { }
    }

    [Fact]
    public void Context_blocks_leave_the_dialogue_and_show_once_before_the_last_question_and_repeated_answers_are_dropped()
    {
        var msgs = new JsonArray
        {
            new JsonObject { ["role"] = "system", ["content"] = "SYS" },
            new JsonObject { ["role"] = "user", ["content"] = "<app_state>\nPage: Analyze\n</app_state>\n<current_view>\nFile: Processes.csv\n</current_view>\n\nhello" },
            new JsonObject { ["role"] = "assistant", ["content"] = "The view is Processes.csv." },
            new JsonObject { ["role"] = "user", ["content"] = "<current_view>Same as in my previous message.</current_view>\n\nanything odd?" },
            new JsonObject { ["role"] = "assistant", ["content"] = "The view is Processes.csv." },
            new JsonObject { ["role"] = "user", ["content"] = "<current_view>Same as in my previous message.</current_view>\n\nany suspicious activity?" },
        };
        var text = TextToolProtocol.Render(msgs, Array.Empty<ToolSpec>());
        var dialogue = text[text.IndexOf("The conversation:", StringComparison.Ordinal)..];
        dialogue.Should().Contain("Examiner: hello\n").And.Contain("Examiner: anything odd?\n").And.NotContain("Same as in my previous message");
        System.Text.RegularExpressions.Regex.Matches(dialogue, "File: Processes.csv").Count.Should().Be(1);
        dialogue.IndexOf("File: Processes.csv", StringComparison.Ordinal).Should().BeGreaterThan(dialogue.IndexOf("Examiner: anything odd?", StringComparison.Ordinal));
        System.Text.RegularExpressions.Regex.Matches(dialogue, "The view is Processes.csv.").Count.Should().Be(1);
        text.Should().EndWith("Examiner: any suspicious activity?\n\nSpectre:");
        TextToolProtocol.StripContextTags("<current_view>Yes.</current_view>").Should().Be("Yes.");
    }
}
