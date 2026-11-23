using System.Security.Cryptography;
using System.Text.Json.Nodes;
using EzForensics.Core.Assistant;
using EzForensics.Core.Spectre;
using FluentAssertions;

namespace EzForensics.Core.Tests;

public class SpectreEngineMcpTests : IDisposable
{
    private readonly string _root = Path.Combine(Path.GetTempPath(), "ezf-spmcp-" + Guid.NewGuid().ToString("N"));

    public SpectreEngineMcpTests() => Directory.CreateDirectory(_root);
    public void Dispose() { try { Directory.Delete(_root, true); } catch (IOException) { } }

    [Fact]
    public void Long_running_engine_tools_get_longer_timeouts()
    {
        McpAssistantTools.TimeoutFor("spectre_run").Should().Be(TimeSpan.FromHours(2));
        McpAssistantTools.TimeoutFor("spectre_open_ezf_case").Should().Be(TimeSpan.FromMinutes(30));
        McpAssistantTools.TimeoutFor("spectre_findings_list").Should().Be(McpAssistantTools.DefaultTimeout);
    }

    [Fact]
    public void Install_source_prefers_bundled_wheel_then_env_then_walk_up()
    {
        var app = Path.Combine(_root, "app");
        var envSrc = Path.Combine(_root, "envsrc");
        Directory.CreateDirectory(app);
        Directory.CreateDirectory(envSrc);
        File.WriteAllText(Path.Combine(envSrc, "pyproject.toml"), "");
        var sibling = Path.Combine(_root, "Spectre");
        Directory.CreateDirectory(Path.Combine(sibling, "src", "spectre"));
        File.WriteAllText(Path.Combine(sibling, "pyproject.toml"), "");

        SpectreEngineSetup.FindInstallSource(app, app, envSrc).Should().Be(Path.GetFullPath(envSrc), "no wheel: the env var wins");
        SpectreEngineSetup.FindInstallSource(app, app, "").Should().Be(sibling, "no wheel, no env var: the sibling folder");

        Directory.CreateDirectory(Path.Combine(app, "spectre"));
        var wheel = Path.Combine(app, "spectre", "spectre_forensic-1.0-py3-none-any.whl");
        File.WriteAllText(wheel, "x");
        SpectreEngineSetup.FindInstallSource(app, app, envSrc).Should().Be(wheel, "a bundled wheel beats everything");
    }

    [Fact]
    public void Hand_off_lists_case_files_with_hashes_and_wsl_path()
    {
        var caseDir = Path.Combine(_root, "Case 1");
        Directory.CreateDirectory(Path.Combine(caseDir, "host", "sub"));
        Directory.CreateDirectory(Path.Combine(caseDir, ".ezassistant"));
        File.WriteAllText(Path.Combine(caseDir, "a.csv"), "one");
        File.WriteAllText(Path.Combine(caseDir, "host", "sub", "b.csv"), "two");
        File.WriteAllText(Path.Combine(caseDir, ".ezassistant", "hidden.db"), "no");

        var (args, error) = SpectreCaseHandoff.Build(caseDir);

        error.Should().BeNull();
        args!["ezf_case_path"]!.GetValue<string>().Should().StartWith("/mnt/").And.EndWith("/Case 1");
        args["case_name"]!.GetValue<string>().Should().Be("Case 1");
        var files = args["files"]!.AsArray().Select(f => (Path: f!["path"]!.GetValue<string>(), Sha: f["sha256"]!.GetValue<string>())).ToList();
        files.Select(f => f.Path).Should().BeEquivalentTo("a.csv", "host/sub/b.csv");
        files.Single(f => f.Path == "a.csv").Sha.Should().Be(Convert.ToHexString(SHA256.HashData("one"u8.ToArray())).ToLowerInvariant());
    }

    [Fact]
    public void Hand_off_reports_a_missing_case_and_an_empty_case()
    {
        SpectreCaseHandoff.Build(null).Error.Should().NotBeNull();
        Directory.CreateDirectory(Path.Combine(_root, "empty"));
        SpectreCaseHandoff.Build(Path.Combine(_root, "empty")).Error.Should().Contain("no files");
    }

    [Fact]
    public async Task Hand_off_tool_calls_the_engine_tool_with_the_built_arguments()
    {
        var caseDir = Path.Combine(_root, "c");
        Directory.CreateDirectory(caseDir);
        File.WriteAllText(Path.Combine(caseDir, "x.csv"), "x");
        var engine = new Recorder();
        var tool = SpectreCaseHandoff.CreateTool(new IAssistantTool[] { engine }, () => caseDir)!;

        tool.Spec.Kind.Should().Be(ToolKind.Write);
        var r = await tool.RunAsync(new ToolCall("1", SpectreCaseHandoff.ToolName, new JsonObject()), default);

        r.IsError.Should().BeFalse();
        engine.Last!.Name.Should().Be("spectre_open_ezf_case");
        engine.Last.Input["files"]!.AsArray().Should().HaveCount(1);
        SpectreCaseHandoff.CreateTool(Array.Empty<IAssistantTool>(), () => caseDir).Should().BeNull();
    }

    [Fact]
    public void Step_lines_for_engine_tools_are_plain_words()
    {
        string D(string name, JsonObject? input, string text, bool err = false) =>
            SpectrePageTools.Describe(new ToolCall("1", name, input ?? new JsonObject()), new ToolOutcome(text, err));
        D("spectre_run", new JsonObject { ["through_phase"] = 3 }, "{\"ok\":true}").Should().Be("Ran the Spectre engine (phases 1-3)");
        D("spectre_findings_list", null, "[{},{}]").Should().Be("Listed 2 findings");
        D("spectre_findings_list", null, "not json").Should().Be("Listed the findings");
        D("spectre_report", null, "x", err: true).Should().Be("Built the Spectre report (failed)");
    }

    /// <summary>Every spectre_* tool name the app relies on must still exist in the Spectre repo's MCP server (skipped without the repo).</summary>
    [Fact]
    public void Tool_names_the_app_relies_on_exist_in_Spectre()
    {
        var repo = SpectreEngineSetup.FindSource();
        if (repo == null) return;
        var toolsPy = Path.Combine(repo, "src", "spectre", "mcp", "tools.py");
        if (!File.Exists(toolsPy)) return;
        var text = File.ReadAllText(toolsPy);
        var relied = McpAssistantTools.ToolTimeouts.Keys.Append(SpectreCaseHandoff.EngineTool).Distinct();
        foreach (var name in relied) text.Should().Contain($"\"{name}\"", $"the app relies on the Spectre tool {name}");
        // every name Describe knows about
        var src = File.ReadAllText(FindFile("SpectrePageTools.cs"));
        foreach (System.Text.RegularExpressions.Match m in System.Text.RegularExpressions.Regex.Matches(src, "case \"(spectre_[a-z_]+)\""))
            if (m.Groups[1].Value != "spectre_open_current_case")
                text.Should().Contain($"\"{m.Groups[1].Value}\"", $"Describe names the Spectre tool {m.Groups[1].Value}");
    }

    private static string FindFile(string name)
    {
        for (var d = new DirectoryInfo(AppContext.BaseDirectory); d != null; d = d.Parent)
        {
            var p = Path.Combine(d.FullName, "src", "EzForensics.Core", "Assistant", name);
            if (File.Exists(p)) return p;
        }
        throw new FileNotFoundException(name);
    }

    private sealed class Recorder : IAssistantTool
    {
        public ToolCall? Last;
        public ToolSpec Spec { get; } = new("spectre_open_ezf_case", "", new JsonObject(), ToolKind.Write);
        public Task<ToolOutcome> RunAsync(ToolCall call, CancellationToken ct) { Last = call; return Task.FromResult(new ToolOutcome("{}", false)); }
    }
}
