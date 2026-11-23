using System.Text.Json.Nodes;

namespace EzForensics.Core.Assistant;

/// <summary>Adapts MCP tools to <see cref="IAssistantTool"/>.</summary>
public static class McpAssistantTools
{
    public static readonly TimeSpan DefaultTimeout = TimeSpan.FromMinutes(5);

    /// <summary>Tools that legitimately run for long (spectre_run walks the analysis phases); every other tool gets <see cref="DefaultTimeout"/>.</summary>
    public static readonly IReadOnlyDictionary<string, TimeSpan> ToolTimeouts = new Dictionary<string, TimeSpan>
    {
        ["spectre_run"] = TimeSpan.FromHours(2),
        ["spectre_open_ezf_case"] = TimeSpan.FromMinutes(30),
        ["spectre_report_generate"] = TimeSpan.FromMinutes(15),
    };

    public static TimeSpan TimeoutFor(string toolName) => ToolTimeouts.TryGetValue(toolName, out var t) ? t : DefaultTimeout;

    /// <param name="timeout">One timeout for every tool (tests); null = <see cref="TimeoutFor"/> per tool.</param>
    public static IReadOnlyList<IAssistantTool> Create(McpClient client, IReadOnlyList<McpToolInfo> tools, TimeSpan? timeout = null) =>
        tools.Select(t => (IAssistantTool)new McpTool(client, t, timeout ?? TimeoutFor(t.Name))).ToList();

    /// <summary>Calls one MCP tool; lets tools share a connection that is replaced after the server died.</summary>
    public delegate Task<McpToolResult> ToolCaller(string name, JsonObject args, TimeSpan timeout, CancellationToken ct);

    public static IReadOnlyList<IAssistantTool> Create(ToolCaller caller, IReadOnlyList<McpToolInfo> tools) =>
        tools.Select(t => (IAssistantTool)new McpTool(caller, t, TimeoutFor(t.Name))).ToList();

    private sealed class McpTool : IAssistantTool
    {
        private readonly ToolCaller _call;
        private readonly TimeSpan _timeout;
        public ToolSpec Spec { get; }

        public McpTool(McpClient client, McpToolInfo info, TimeSpan timeout) : this(client.CallToolAsync, info, timeout) { }

        public McpTool(ToolCaller call, McpToolInfo info, TimeSpan timeout)
        {
            _call = call; _timeout = timeout;
            Spec = new ToolSpec(info.Name, info.Description, info.InputSchema, info.ReadOnly ? ToolKind.Read : ToolKind.Write);
        }

        public async Task<ToolOutcome> RunAsync(ToolCall call, CancellationToken ct)
        {
            var r = await _call(Spec.Name, call.Input, _timeout, ct).ConfigureAwait(false);
            return new ToolOutcome(AssistantCaseTools.CapText(r.Text), r.IsError);
        }
    }
}
