using System.Text.Json.Nodes;
using EzForensics.Core.Assistant;
using FluentAssertions;

namespace EzForensics.Core.Tests;

public class McpClientTests
{
    private static readonly TimeSpan T = TimeSpan.FromSeconds(20);

    private static StdioProcessHost Fake(params string[] extra) =>
        StdioProcessHost.Start(FakeToolPaths.Exe, new[] { "--fake-mcp" }.Concat(extra).ToArray(), null, null, null);

    [Fact(Timeout = 60000)]
    public async Task Initialize_list_and_call()
    {
        await using var c = await McpClient.ConnectAsync(Fake(), T, default);
        var tools = await c.ListToolsAsync(default); // the fake prints a non-JSON line first: ignored
        tools.Select(t => t.Name).Should().Equal("echo", "mutate");
        tools[0].ReadOnly.Should().BeTrue();
        tools[1].ReadOnly.Should().BeFalse();
        var r = await c.CallToolAsync("echo", new JsonObject { ["x"] = "héllo ✓" }, T, default);
        r.Should().Be(new McpToolResult(false, "héllo ✓"));
    }

    [Fact(Timeout = 60000)]
    public async Task Client_returns_full_text_and_only_the_model_path_caps_it()
    {
        await using var c = await McpClient.ConnectAsync(Fake(), T, default);
        var big = new string('a', 50_000);
        var r = await c.CallToolAsync("echo", new JsonObject { ["x"] = big }, T, default);
        r.IsError.Should().BeFalse();
        r.Text.Should().Be(big);

        var tools = McpAssistantTools.Create(c, await c.ListToolsAsync(default));
        var o = await tools[0].RunAsync(new ToolCall("1", "echo", new JsonObject { ["x"] = new string('a', AssistantCaseTools.MaxOutputChars + 1234) }), default);
        o.Text.Should().StartWith(new string('a', 100)).And.Contain("1,234 more characters not shown");
        o.Text.Length.Should().BeLessThan(AssistantCaseTools.MaxOutputChars + 200);
    }

    [Fact(Timeout = 60000)]
    public async Task Non_text_content_is_dropped_with_a_note()
    {
        await using var c = await McpClient.ConnectAsync(Fake(), T, default);
        var r = await c.CallToolAsync("echo", new JsonObject { ["x"] = "image:hello" }, T, default);
        r.Text.Should().Be("hello\n(1 non-text content item(s) omitted)");
    }

    [Fact(Timeout = 60000)]
    public async Task Concurrent_calls_get_their_own_answers()
    {
        await using var c = await McpClient.ConnectAsync(Fake(), T, default);
        var calls = Enumerable.Range(0, 20).Select(i => c.CallToolAsync("echo", new JsonObject { ["x"] = "v" + i }, T, default)).ToArray();
        var results = await Task.WhenAll(calls);
        results.Select(r => r.Text).Should().Equal(Enumerable.Range(0, 20).Select(i => "v" + i));
    }

    [Fact(Timeout = 60000)]
    public async Task Jsonrpc_error_becomes_error_result()
    {
        await using var c = await McpClient.ConnectAsync(Fake(), T, default);
        (await c.CallToolAsync("fail", new JsonObject(), T, default)).Should().Be(new McpToolResult(true, "bad params"));
    }

    [Fact(Timeout = 60000)]
    public async Task Call_timeout_is_reported_not_hung()
    {
        await using var c = await McpClient.ConnectAsync(Fake("--hang"), T, default);
        var act = () => c.CallToolAsync("echo", new JsonObject { ["x"] = "a" }, TimeSpan.FromSeconds(1), default);
        await act.Should().ThrowAsync<TimeoutException>();
    }

    [Fact(Timeout = 60000)]
    public async Task Server_exit_fails_calls_with_IOException()
    {
        await using var c = await McpClient.ConnectAsync(Fake("--crash-after-init"), T, default);
        var act = () => c.ListToolsAsync(default);
        (await act.Should().ThrowAsync<IOException>()).WithMessage("*Spectre stopped*");
    }

    [Fact(Timeout = 60000)]
    public async Task Disposing_while_a_call_is_pending_fails_the_call()
    {
        var c = await McpClient.ConnectAsync(Fake("--hang"), T, default);
        var call = c.CallToolAsync("echo", new JsonObject { ["x"] = "a" }, T, default);
        await Task.Delay(300);
        var dispose = c.DisposeAsync().AsTask();
        var act = () => call;
        await act.Should().ThrowAsync<IOException>();
        await dispose;
    }

    [Fact(Timeout = 60000)]
    public async Task Dispose_kills_process_and_runs_cancel_command()
    {
        var marker = Path.Combine(Path.GetTempPath(), $"mcp-cancel-{Guid.NewGuid():N}");
        try
        {
            var host = StdioProcessHost.Start(FakeToolPaths.Exe, new[] { "--fake-mcp", "--hang" }, null,
                new[] { FakeToolPaths.Exe, "--fake-touch", marker }, null);
            await host.DisposeAsync();
            host.HasExited.Should().BeTrue();
            File.Exists(marker).Should().BeTrue();
        }
        finally { try { File.Delete(marker); } catch (IOException) { } }
    }

    [Fact(Timeout = 60000)]
    public async Task Adapter_maps_kinds_and_passes_text_through()
    {
        await using var c = await McpClient.ConnectAsync(Fake(), T, default);
        var tools = McpAssistantTools.Create(c, await c.ListToolsAsync(default));
        tools.Select(t => t.Spec.Kind).Should().Equal(ToolKind.Read, ToolKind.Write);
        var o = await tools[0].RunAsync(new ToolCall("1", "echo", new JsonObject { ["x"] = "a\nb" }), default);
        o.Should().Be(new ToolOutcome("a\nb", false));
    }

    [Fact]
    public void Spectre_command_uses_exec_and_absolute_path()
    {
        var (exe, args, cancel) = SpectreMcp.Command(@"C:\Windows\System32\wsl.exe");
        exe.Should().Be(@"C:\Windows\System32\wsl.exe");
        args.Should().ContainInOrder("-d", "EzForensics", "-u", "root", "-e", "/opt/ezforensics/spectre/venv/bin/spectre-mcp",
            "--cases-dir", "/var/lib/ezforensics/spectre/cases", "--no-journal-anchor");
        args.Should().NotContain("--");
        cancel.Should().ContainInOrder(@"C:\Windows\System32\wsl.exe", "-d", "EzForensics", "-u", "root", "-e", "pkill", "-f", "[/]opt/ezforensics/spectre/venv/bin/spectre-mcp");
    }

    [Fact]
    public void Dev_command_is_passed_through()
    {
        var (exe, args, cancel) = SpectreMcp.DevCommand("py", new[] { "-m", "spectre.mcp" });
        exe.Should().Be("py");
        args.Should().Equal("-m", "spectre.mcp");
        cancel.Should().BeEmpty();
    }
}
