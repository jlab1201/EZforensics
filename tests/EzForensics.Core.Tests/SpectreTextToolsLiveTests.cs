using EzForensics.Core.Assistant;
using FluentAssertions;

namespace EzForensics.Core.Tests;

/// <summary>
/// Opt-in: needs Ollama and <c>EZFORENSICS_OLLAMA_TEXT_MODEL</c>, a model without tool support (e.g. FenkoHQ/Foundation-Sec-8B).
/// Checks that a filter request becomes a filter proposal through the text tool layer.
/// </summary>
public class SpectreTextToolsLiveTests
{
    private sealed class Host : IAssistantHost
    {
        public readonly List<AssistantProposal> Proposals = new();
        public void ShowProposal(AssistantProposal proposal) => Proposals.Add(proposal);
        public Task<bool> ConfirmWriteAsync(ToolCall call) => Task.FromResult(false);
        public void Status(string text) { }
    }

    [Fact(Timeout = 1800000)]
    public async Task A_model_without_tools_filters_through_text_tool_calls()
    {
        if (Environment.GetEnvironmentVariable("EZFORENSICS_OLLAMA_TEXT_MODEL") is not { Length: > 0 } modelId) return;
        using var http = new HttpClient { Timeout = TimeSpan.FromMinutes(20) };
        var model = new OpenAiCompatibleModelClient(http, new Uri(OllamaDefaults.BaseUrl), modelId, null, AssistantPrompt.System,
            AssistantModelFactory.ChatMaxTokens, AssistantModelFactory.OllamaContextTokens);
        var host = new Host();
        var csv = Path.Combine(Path.GetTempPath(), "ezlive-text.csv");
        var rows = new[] { new GridSampleRow(1, new[] { "powershell.exe", "powershell -enc AAA" }), new GridSampleRow(2, new[] { "svchost.exe", "svchost -k netsvcs" }) };
        var ctx = new GridContext(csv, new[] { "Name", "CommandLine" }, 2, 2, "", rows);
        var tools = AssistantGridTools.Create(() => ctx, () => Array.Empty<string>(), host).ToList();
        var log = new List<string>();
        var said = await new AssistantSession(model, tools, host, maxToolRounds: 4, log: log.Add)
            .AskAsync("<app_state>\nPage: Analyze\n" + ctx.Describe() + "\n</app_state>\nfilter powershell-related commands", default, "filter powershell-related commands");
        Console.WriteLine("SAID> " + string.Join(" | ", said));
        foreach (var l in log) Console.WriteLine("LOG> " + l);
        foreach (var p in host.Proposals) Console.WriteLine("PROPOSAL> " + p);
        host.Proposals.OfType<FilterProposal>().Should().NotBeEmpty();
    }

    /// <summary>The case tools on top (as on Analyze): the reply must stay short and the query must target the open file.</summary>
    [Fact(Timeout = 1800000)]
    public async Task A_model_without_tools_answers_about_the_open_file_in_reasonable_time()
    {
        if (Environment.GetEnvironmentVariable("EZFORENSICS_OLLAMA_TEXT_MODEL") is not { Length: > 0 } modelId) return;
        var dir = Path.Combine(Path.GetTempPath(), "ezlive-text-case-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(dir);
        try
        {
            var csv = Path.Combine(dir, "Processes.csv");
            File.WriteAllText(csv, "PID,Name,CommandLine\n100,powershell.exe,powershell -nop -w hidden -enc SQBFAFgA\n200,svchost.exe,svchost -k netsvcs\n300,powershell.exe,powershell Get-Date\n");
            File.WriteAllText(Path.Combine(dir, "Security.csv"), "EventId,Payload\n4688,powershell -enc AAA\n");
            using var http = new HttpClient { Timeout = TimeSpan.FromMinutes(20) };
            var model = new OpenAiCompatibleModelClient(http, new Uri(OllamaDefaults.BaseUrl), modelId, null, AssistantPrompt.System,
                AssistantModelFactory.ChatMaxTokens, AssistantModelFactory.OllamaContextTokens);
            var host = new Host();
            var rows = new[]
            {
                new GridSampleRow(1, new[] { "100", "powershell.exe", "powershell -nop -w hidden -enc SQBFAFgA" }),
                new GridSampleRow(2, new[] { "200", "svchost.exe", "svchost -k netsvcs" }),
                new GridSampleRow(3, new[] { "300", "powershell.exe", "powershell Get-Date" }),
            };
            var ctx = new GridContext(csv, new[] { "PID", "Name", "CommandLine" }, 3, 3, "", rows);
            using var caseTools = new AssistantCaseTools(() => dir, host, canOpenFile: true, openFile: () => csv);
            var tools = AssistantGridTools.Create(() => ctx, () => new[] { "suspicious" }, host, caseFiles: caseTools).Concat(caseTools.Tools).ToList();
            var log = new List<string>();
            var sw = System.Diagnostics.Stopwatch.StartNew();
            var said = await new AssistantSession(model, tools, host, maxToolRounds: 4, log: log.Add)
                .AskAsync("<app_state>\nPage: Analyze\nOpen CSV: Processes.csv\n</app_state>\n" + ctx.Describe() + "\nflag suspicious powershell commands",
                    default, "flag suspicious powershell commands");
            Console.WriteLine($"TIME> {sw.Elapsed}");
            Console.WriteLine("SAID> " + string.Join(" | ", said));
            foreach (var l in log) Console.WriteLine("LOG> " + l);
            foreach (var p in host.Proposals) Console.WriteLine("PROPOSAL> " + p);
            sw.Elapsed.Should().BeLessThan(TimeSpan.FromMinutes(8));
            host.Proposals.Should().NotBeEmpty();   // a base model may filter instead of flag; either way it must be the open file
            host.Proposals.OfType<FilterProposal>().Select(p => p.CsvPath).Concat(host.Proposals.OfType<FlagProposal>().Select(p => p.CsvPath))
                .Should().OnlyContain(p => p == csv);
        }
        finally { try { Directory.Delete(dir, true); } catch (IOException) { } }
    }

    /// <summary>The examiner's real chat that got stuck repeating one line (Nov 2): each reply must differ and the data question must query.</summary>
    [Fact(Timeout = 1800000)]
    public async Task A_model_without_tools_does_not_repeat_itself_over_a_chat()
    {
        if (Environment.GetEnvironmentVariable("EZFORENSICS_OLLAMA_TEXT_MODEL") is not { Length: > 0 } modelId) return;
        var dir = Path.Combine(Path.GetTempPath(), "ezlive-text-chat-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(dir);
        try
        {
            var csv = Path.Combine(dir, "Processes.csv");
            File.WriteAllText(csv, "PID,Name,CommandLine\n100,powershell.exe,powershell -nop -w hidden -enc SQBFAFgA\n200,svchost.exe,svchost -k netsvcs\n300,rundll32.exe,rundll32 C:/Users/Public/x.dll Start\n400,explorer.exe,C:/Windows/explorer.exe\n");
            using var http = new HttpClient { Timeout = TimeSpan.FromMinutes(20) };
            var model = new OpenAiCompatibleModelClient(http, new Uri(OllamaDefaults.BaseUrl), modelId, null, AssistantPrompt.System,
                AssistantModelFactory.ChatMaxTokens, AssistantModelFactory.OllamaContextTokens);
            var host = new Host();
            var rows = new[]
            {
                new GridSampleRow(1, new[] { "100", "powershell.exe", "powershell -nop -w hidden -enc SQBFAFgA" }),
                new GridSampleRow(2, new[] { "200", "svchost.exe", "svchost -k netsvcs" }),
                new GridSampleRow(3, new[] { "300", "rundll32.exe", "rundll32 C:/Users/Public/x.dll Start" }),
                new GridSampleRow(4, new[] { "400", "explorer.exe", "C:/Windows/explorer.exe" }),
            };
            var ctx = new GridContext(csv, new[] { "PID", "Name", "CommandLine" }, 4, 4, "", rows);
            using var caseTools = new AssistantCaseTools(() => dir, host, canOpenFile: true, openFile: () => csv);
            var tools = AssistantGridTools.Create(() => ctx, () => new[] { "suspicious" }, host, caseFiles: caseTools).Concat(caseTools.Tools).ToList();
            var log = new List<string>();
            var session = new AssistantSession(model, tools, host, maxToolRounds: 4, log: log.Add);
            var context = new AssistantContext();
            var replies = new List<string>();
            foreach (var q in new[] { "hello", "can you see what is currently displayed and selected?", "is there any other suspicious events here or only powershell?", "yes, any suspicious activity found?", "are you still there?" })
            {
                var said = await session.AskAsync(context.Build(SpectrePage.Analyze, "Page: Analyze\nOpen CSV: Processes.csv", ctx.Describe(), q), default, q);
                var text = string.Join(" | ", said.Where(s => s != OpenAiCompatibleModelClient.NoToolsNotice));
                Console.WriteLine($"Q> {q}\nA> {text}");
                replies.Add(text);
            }
            foreach (var l in log) Console.WriteLine("LOG> " + l);
            replies.Distinct(StringComparer.OrdinalIgnoreCase).Count().Should().Be(replies.Count);
            replies.Should().NotContain(r => r.Contains("<current_view>"));
            log.Should().Contain(l => l.Contains("csv_query") || l.Contains("csv_group_by") || l.Contains("csv_column_values"));
        }
        finally { try { Directory.Delete(dir, true); } catch (IOException) { } }
    }
}
