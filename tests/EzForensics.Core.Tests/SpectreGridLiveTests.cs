using System.Text.Json.Nodes;
using EzForensics.Core.Assistant;
using EzForensics.Core.Results;
using FluentAssertions;
using Xunit.Abstractions;

namespace EzForensics.Core.Tests;

/// <summary>
/// Opt-in: needs Ollama running and <c>EZFORENSICS_OLLAMA_MODEL</c> (e.g. qwen3:8b). Without it every test returns at once.
/// Real client + session + grid tools over a fake grid built from a small EvtxECmd-like CSV, with the current view text built the
/// way the app builds it. Each ask must end in the right tool call and a changed grid (auto-mode) in at least 2 of 3 runs.
/// </summary>
public sealed class SpectreGridLiveTests : IDisposable
{
    private readonly ITestOutputHelper _out;
    private readonly string _case = Path.Combine(Path.GetTempPath(), "ezgridlive-" + Guid.NewGuid().ToString("N"));
    public SpectreGridLiveTests(ITestOutputHelper output) => _out = output;
    public void Dispose() { try { Directory.Delete(_case, true); } catch (IOException) { } }

    private static readonly string[] Headers = { "TimeCreated", "EventId", "Computer", "PayloadData1", "ExecutableInfo" };

    private static readonly string[][] Data =
    {
        new[] { "2026-10-01 08:00:01", "4624", "WS01", "Target: CORP\\alice", "" },
        new[] { "2026-10-01 08:02:13", "4688", "WS01", "Target: CORP\\alice", "C:\\Windows\\System32\\cmd.exe /c whoami" },
        new[] { "2026-10-01 08:02:40", "4688", "WS01", "Target: CORP\\alice", "C:\\Windows\\System32\\WindowsPowerShell\\v1.0\\powershell.exe -enc SQBFAFgA" },
        new[] { "2026-10-01 08:03:05", "4688", "WS01", "Target: CORP\\alice", "C:\\Users\\alice\\AppData\\Local\\Temp\\svch0st.exe" },
        new[] { "2026-10-01 08:05:22", "4672", "WS01", "Target: CORP\\alice", "" },
        new[] { "2026-10-01 08:10:47", "4625", "WS01", "Target: CORP\\bob", "" },
        new[] { "2026-10-01 08:11:02", "4688", "WS01", "Target: CORP\\bob", "C:\\Windows\\explorer.exe" },
        new[] { "2026-10-01 08:15:30", "7045", "WS01", "Service: updsvc", "C:\\ProgramData\\upd.exe" },
    };

    /// <summary>The Analyze grid, faked: the same context text, filter, flags and sort the real one exposes.</summary>
    internal sealed class FakeGrid
    {
        public const string Csv = @"C:\Cases\live\EventLogs\20261001_EvtxECmd_Output.csv";
        public FilterGroup? Filter;
        public readonly Dictionary<long, string> Flags = new();
        public (string? Column, bool Descending) Sort;

        public GridContext Context() => new(Csv, Headers, Data.Length, Data.Length,
            Filter?.ToDisplayString() ?? "",
            Data.Select((r, i) => new GridSampleRow(i, r)).Take(20).ToList(), Array.Empty<GridSampleRow>());

        public static IReadOnlyList<FlagDefinition> FlagDefs => FlagCatalog.Defaults();
    }

    /// <summary>Applies proposals to the fake grid at once (auto-mode on), like the chat panel does.</summary>
    private sealed class AutoHost(FakeGrid grid) : IAssistantHost
    {
        public List<AssistantProposal> Proposals { get; } = new();
        public void ShowProposal(AssistantProposal p) => Proposals.Add(p);
        public Task<string> HandleProposalAsync(AssistantProposal p)
        {
            Proposals.Add(p);
            switch (p)
            {
                case FilterProposal f: grid.Filter = f.Filter; break;
                case FlagProposal g: foreach (var r in g.Rows) grid.Flags[r] = g.FlagId; break;
                case SortProposal s: grid.Sort = (s.Column, s.Descending); break;
                default: return Task.FromResult(AssistantGridTools.ProposedText);
            }
            return Task.FromResult(AssistantGridTools.AppliedText);
        }
        public Task<bool> ConfirmWriteAsync(ToolCall call) => Task.FromResult(false);
        public void Status(string text) { }
    }

    /// <summary>Keeps the last raw model reply, to show what came back when a run ended with neither text nor a tool call.</summary>
    private sealed class LastReply() : DelegatingHandler(new HttpClientHandler())
    {
        public string Body = "";
        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken ct)
        {
            var resp = await base.SendAsync(request, ct);
            Body = await resp.Content.ReadAsStringAsync(ct);
            resp.Content = new StringContent(Body, System.Text.Encoding.UTF8, "application/json");
            return resp;
        }
    }

    private sealed class Recording(IAssistantTool inner, List<string> log) : IAssistantTool
    {
        public ToolSpec Spec => inner.Spec;
        public async Task<ToolOutcome> RunAsync(ToolCall call, CancellationToken ct)
        {
            var o = await inner.RunAsync(call, ct);
            lock (log) log.Add($"{call.Name}({call.Input.ToJsonString()}) -> {(o.IsError ? "ERROR " : "")}{o.Text}");
            return o;
        }
    }

    private async Task<(FakeGrid Grid, AutoHost Host, List<string> Calls, string Said)> AskAsync(string modelId, string ask)
    {
        var last = new LastReply();
        using var http = new HttpClient(last) { Timeout = TimeSpan.FromMinutes(5) };
        var model = new OpenAiCompatibleModelClient(http, new Uri(OllamaDefaults.BaseUrl), modelId, null, AssistantPrompt.System, AssistantModelFactory.ChatMaxTokens);
        var grid = new FakeGrid();
        var host = new AutoHost(grid);
        var calls = new List<string>();
        var tools = AssistantGridTools.Create(grid.Context, () => FakeGrid.FlagDefs.Select(f => f.Id).ToList(), host,
                () => FakeGrid.FlagDefs.Select(f => (f.Id, f.Label)).ToList())
            .Select(t => (IAssistantTool)new Recording(t, calls)).ToList();
        using var caseTools = new AssistantCaseTools(() => _case, host, canOpenFile: true);   // the app offers both sets
        tools.AddRange(caseTools.Tools.Select(t => (IAssistantTool)new Recording(t, calls)));
        // the same shape the app sends: <app_state>, <current_view>, then the examiner's text
        var state = "Page: Analyze\nCase: live (" + _case + ")\nOpen CSV: EventLogs\\20261001_EvtxECmd_Output.csv\n" + CaseInventory.AppStateText(_case);
        var view = grid.Context().Describe(flags: FakeGrid.FlagDefs.Select(f => (f.Id, f.Label)).ToList());
        var said = await new AssistantSession(model, tools, host, maxToolRounds: 6)
            .AskAsync($"<app_state>\n{state}\n</app_state>\n<current_view>\n{view}\n</current_view>\n\n{ask}", default);
        var text = string.Join("\n", said);
        if (calls.Count == 0 && string.IsNullOrWhiteSpace(text)) text = "(empty; last raw reply: " + (last.Body.Length > 1500 ? last.Body[..1500] : last.Body) + ")";
        return (grid, host, calls, text);
    }

    private async Task<int> RunAsync(string modelId, string ask, Func<FakeGrid, AutoHost, bool> ok)
    {
        int good = 0;
        for (int run = 1; run <= 3; run++)
        {
            var (grid, host, calls, said) = await AskAsync(modelId, ask);
            var pass = ok(grid, host);
            if (pass) good++;
            _out.WriteLine($"[{ask}] run {run}: {(pass ? "OK" : "FAIL")}\n  tools: {(calls.Count == 0 ? "(none)" : string.Join("\n         ", calls))}\n  said: {said.ReplaceLineEndings(" ")}");
        }
        return good;
    }

    [Fact(Timeout = 1800000)]
    public async Task Local_model_turns_grid_asks_into_tool_calls()
    {
        if (Environment.GetEnvironmentVariable("EZFORENSICS_OLLAMA_MODEL") is not { Length: > 0 } modelId) return;
        CaseInventoryTests.BuildCase(_case);

        var filter = await RunAsync(modelId, "filter to event 4688", (g, h) =>
            h.Proposals.OfType<FilterProposal>().Any() && g.Filter != null && g.Filter.ToDisplayString().Contains("4688"));
        var flag = await RunAsync(modelId, "flag row 3 as suspicious", (g, h) =>
            h.Proposals.OfType<FlagProposal>().Any() && g.Flags.TryGetValue(2, out var f)   // row 3 as shown = source row 2
             && f == FlagCatalog.Suspicious);
        var sort = await RunAsync(modelId, "sort by TimeCreated newest first", (g, h) =>
            h.Proposals.OfType<SortProposal>().Any() && g.Sort == ("TimeCreated", true));

        _out.WriteLine($"filter {filter}/3, flag {flag}/3, sort {sort}/3");
        filter.Should().BeGreaterThanOrEqualTo(2);
        flag.Should().BeGreaterThanOrEqualTo(2);
        sort.Should().BeGreaterThanOrEqualTo(2);
    }
}
