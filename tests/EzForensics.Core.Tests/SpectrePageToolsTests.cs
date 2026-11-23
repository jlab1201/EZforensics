using System.Text.Json.Nodes;
using EzForensics.Core.Assistant;
using FluentAssertions;

namespace EzForensics.Core.Tests;

public class SpectrePageToolsTests
{
    private sealed class Host : IAssistantHost
    {
        public List<AssistantProposal> Proposals { get; } = new();
        public void ShowProposal(AssistantProposal p) => Proposals.Add(p);
        public Task<bool> ConfirmWriteAsync(ToolCall call) => Task.FromResult(false);
        public void Status(string text) { }
    }

    [Theory]
    [InlineData("theme", "light", "Light")]
    [InlineData("tool_logging", "DEBUG", "Debug")]
    [InlineData("auto_mode", "off", "off")]
    public void Safe_settings_are_accepted_with_their_canonical_value(string setting, string value, string expected) =>
        SpectrePageTools.CheckSetting(setting, value).Should().Be((expected, (string?)null));

    [Theory]
    [InlineData("auto_mode", "on")]          // only the examiner turns auto-mode on
    [InlineData("ai_provider", "anthropic")] // provider, keys and consent are never Spectre's
    [InlineData("api_key", "sk-x")]
    [InlineData("theme", "pink")]
    [InlineData(null, "x")]
    public void Anything_outside_the_allow_list_is_refused(string? setting, string value)
    {
        var (v, err) = SpectrePageTools.CheckSetting(setting, value);
        v.Should().BeNull();
        err.Should().NotBeNullOrEmpty();
    }

    [Fact]
    public async Task A_page_tool_refuses_bad_arguments_and_proposes_otherwise()
    {
        var host = new Host();
        var tool = SpectrePageTools.Create(SpectrePageTools.OptionsSetSpec, host,
            (id, input) => new PageActionProposal(id, "Set theme", "", () => Task.FromResult<string?>(null)),
            input => SpectrePageTools.CheckSetting(input["setting"]?.ToString(), input["value"]?.ToString()).Error);
        tool.Spec.Kind.Should().Be(ToolKind.Proposal);

        var bad = await tool.RunAsync(new ToolCall("c1", SpectrePageTools.OptionsSet, new JsonObject { ["setting"] = "auto_mode", ["value"] = "on" }), default);
        bad.IsError.Should().BeTrue();
        host.Proposals.Should().BeEmpty();

        var ok = await tool.RunAsync(new ToolCall("c2", SpectrePageTools.OptionsSet, new JsonObject { ["setting"] = "theme", ["value"] = "Dark" }), default);
        ok.IsError.Should().BeFalse();
        host.Proposals.Should().ContainSingle().Which.Should().BeOfType<PageActionProposal>();
    }

    [Fact]
    public void Every_page_action_is_offered_on_every_page()
    {
        var host = new Host();
        var tools = new[] { SpectrePageTools.GoToPageSpec, SpectrePageTools.JobsRetrySpec, SpectrePageTools.JobsCancelSpec, SpectrePageTools.ToolsInstallSpec,
                SpectrePageTools.OptionsSetSpec, SpectrePageTools.IngestStartSpec }
            .Select(s => SpectrePageTools.Create(s, host, (_, _) => null)).ToList();
        IEnumerable<string> On(SpectrePage p) => SpectreSkills.ForPage(p, tools).Select(t => t.Spec.Name);

        // each opens its own page first in the app ("scan D:\x" from Analyze), so all are offered everywhere
        foreach (var p in new[] { SpectrePage.Tools, SpectrePage.Options, SpectrePage.Ingest, SpectrePage.Analyze, SpectrePage.Explore })
            On(p).Should().BeEquivalentTo(tools.Select(t => t.Spec.Name), because: p.ToString());
    }

    [Fact]
    public void The_index_says_what_Spectre_can_do_on_the_current_page()
    {
        var jobs = SpectreSkills.Index(SpectrePage.Ingest);
        jobs.Should().Contain("I can do this: jobs_retry, jobs_cancel");
        jobs.Should().Contain("I can do this: app_go_to_page");
        // other pages' actions are listed as features, not as things it can do here
        jobs.Should().NotContain("I can do this: tools_install");
    }
}
