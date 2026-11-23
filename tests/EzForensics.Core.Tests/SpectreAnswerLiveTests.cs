using EzForensics.Core.Assistant;
using EzForensics.Core.Results;
using FluentAssertions;
using Xunit.Abstractions;

namespace EzForensics.Core.Tests;

/// <summary>
/// Opt-in (<c>EZFORENSICS_OLLAMA_MODEL</c>, e.g. qwen3:8b; Ollama running): the answer style and flagging asks from the examiner's
/// feedback. Processes.csv is open; the PowerShell rows are in WindowsEventLogs.csv. Each check must pass in at least 2 of 3 runs.
/// </summary>
public sealed class SpectreAnswerLiveTests : IDisposable
{
    private readonly ITestOutputHelper _out;
    private readonly string _case = Path.Combine(Path.GetTempPath(), "ezanswerlive-" + Guid.NewGuid().ToString("N"));
    private string Open => Path.Combine(_case, "Processes.csv");
    private string Logs => Path.Combine(_case, "WindowsEventLogs.csv");

    public SpectreAnswerLiveTests(ITestOutputHelper output)
    {
        _out = output;
        Directory.CreateDirectory(_case);
        File.WriteAllText(Open, "PID,PPID,ImageFileName,CreateTime\n4,0,System,2026-10-01 07:59\n612,4,smss.exe,2026-10-01 07:59\n" +
            "1880,1212,explorer.exe,2026-10-01 08:00\n2216,1880,cmd.exe,2026-10-01 08:02\n2290,2216,powershell.exe,2026-10-01 08:02\n");
        File.WriteAllText(Logs, "TimeCreated,EventId,UserName,ExecutableInfo\n" +
            "2026-10-01 08:00:01,4624,CORP\\alice,\n" +
            "2026-10-01 08:01:10,4688,CORP\\alice,C:\\Windows\\explorer.exe\n" +
            "2026-10-01 08:02:40,4688,CORP\\alice,\"powershell.exe -Exec ByPasS \"\". C:\\TMP\\WMIBackdoor.ps1;New-WMIBackdoorTrigger\"\"\"\n" +
            "2026-10-01 08:03:00,4688,CORP\\alice,C:\\Windows\\System32\\notepad.exe\n" +
            "2026-10-01 08:04:12,4688,CORP\\alice,\"powershell.exe -Exec ByPasS \"\". C:\\TMP\\schtasks-backdoor.ps1;Invoke-Tasksbackdoor -ip 8.8.8.8\"\"\"\n" +
            "2026-10-01 08:05:30,4688,SYSTEM,powershell.exe -ExecutionPolicy Restricted -Command Get-Date\n");
    }

    public void Dispose() { try { Directory.Delete(_case, true); } catch (IOException) { } }

    private sealed class Host : IAssistantHost
    {
        public List<AssistantProposal> Proposals { get; } = new();
        public void ShowProposal(AssistantProposal p) => Proposals.Add(p);
        public Task<bool> ConfirmWriteAsync(ToolCall call) => Task.FromResult(false);
        public void Status(string text) { }
    }

    private sealed class Chat : IDisposable
    {
        private readonly AssistantSession _session;
        private readonly AssistantCaseTools _caseTools;
        private readonly HttpClient _http = new() { Timeout = TimeSpan.FromMinutes(5) };
        public Host Host { get; } = new();

        public Chat(string modelId, string caseDir, string open)
        {
            var model = new OpenAiCompatibleModelClient(_http, new Uri(OllamaDefaults.BaseUrl), modelId, null, AssistantPrompt.System, AssistantModelFactory.ChatMaxTokens, AssistantModelFactory.OllamaContextTokens);
            _caseTools = new AssistantCaseTools(() => caseDir, Host, canOpenFile: true);
            var headers = new[] { "PID", "PPID", "ImageFileName", "CreateTime" };
            var rows = File.ReadAllLines(open).Skip(1).Select((l, i) => new GridSampleRow(i, l.Split(','))).ToList();
            var ctx = new GridContext(open, headers, rows.Count, rows.Count, "", rows);
            var flags = FlagCatalog.Defaults().Select(f => (f.Id, f.Label)).ToList();
            var tools = AssistantGridTools.Create(() => ctx, () => flags.Select(f => f.Id).ToList(), Host, () => flags, _caseTools).ToList();
            tools.AddRange(_caseTools.Tools);
            tools.Add(SpectreSkills.CreateHelpTool());
            _session = new AssistantSession(model, SpectreSkills.ForPage(SpectrePage.Analyze, tools), Host, maxToolRounds: 8);
            _state = "Page: Analyze\nCase: live (" + caseDir + ")\nOpen CSV: Processes.csv\n" + CaseInventory.AppStateText(caseDir);
            _view = ctx.Describe(flags: flags);
        }

        private readonly string _state, _view;
        private readonly AssistantContext _context = new();

        /// <summary>The same context blocks the app sends (unchanged blocks are not repeated).</summary>
        public async Task<string> AskAsync(string text)
        {
            var said = await _session.AskAsync(_context.Build(SpectrePage.Analyze, _state, _view, text), default, question: text);
            return string.Join("\n", said);
        }

        public void Dispose() { _caseTools.Dispose(); _http.Dispose(); }
    }

    private static int Sentences(string s) => s.Split(new[] { ". ", "? ", "! ", "\n" }, StringSplitOptions.RemoveEmptyEntries).Length;

    private async Task<int> Runs(string name, Func<Task<bool>> run)
    {
        int good = 0;
        for (int i = 1; i <= 3; i++) if (await run()) good++;
        _out.WriteLine($"{name}: {good}/3");
        return good;
    }

    [Fact(Timeout = 3600000)]
    public async Task Answers_are_short_and_flag_asks_become_flag_proposals_in_the_right_file()
    {
        if (Environment.GetEnvironmentVariable("EZFORENSICS_OLLAMA_MODEL") is not { Length: > 0 } modelId) return;

        var seeing = await Runs("seeing", async () =>
        {
            using var chat = new Chat(modelId, _case, Open);
            var said = await chat.AskAsync("are you seeing what I'm seeing now?");
            var ok = Sentences(said) <= 3 && said.Length < 300 && !said.Contains("\n-") && !said.Contains("\n1.");
            _out.WriteLine($"  [{(ok ? "OK" : "FAIL")}] {said.ReplaceLineEndings(" | ")}");
            return ok;
        });

        bool FlaggedBackdoors(Host h) => h.Proposals.OfType<FlagProposal>().Any(p =>
            ProposalPathIs(p.CsvPath, Logs) && p.Rows.Contains(2) && p.Rows.Contains(4) && !p.Rows.Contains(1));

        var flagAll = await Runs("flag all", async () =>
        {
            using var chat = new Chat(modelId, _case, Open);
            var said = await chat.AskAsync("flag all suspicious powershell commands");
            var ok = FlaggedBackdoors(chat.Host) && said.Length < 400;
            _out.WriteLine($"  [{(ok ? "OK" : "FAIL")}] proposals: {Describe(chat.Host)} | said: {said.ReplaceLineEndings(" | ")}");
            return ok;
        });

        var followUp = await Runs("flag them", async () =>
        {
            using var chat = new Chat(modelId, _case, Open);
            var first = await chat.AskAsync("which powershell commands look suspicious?");
            var said = await chat.AskAsync("flag them");
            var ok = FlaggedBackdoors(chat.Host);
            _out.WriteLine($"  [{(ok ? "OK" : "FAIL")}] first: {first.ReplaceLineEndings(" | ")} || proposals: {Describe(chat.Host)} | said: {said.ReplaceLineEndings(" | ")}");
            return ok;
        });

        seeing.Should().BeGreaterThanOrEqualTo(2);
        flagAll.Should().BeGreaterThanOrEqualTo(2);
        followUp.Should().BeGreaterThanOrEqualTo(2);
    }

    private static bool ProposalPathIs(string? a, string b) => a != null && string.Equals(Path.GetFullPath(a), Path.GetFullPath(b), StringComparison.OrdinalIgnoreCase);

    private static string Describe(Host h) => h.Proposals.Count == 0 ? "(none)" : string.Join("; ", h.Proposals.Select(p => p switch
    {
        FlagProposal f => $"flag {Path.GetFileName(f.CsvPath)} rows[{string.Join(",", f.Rows)}] {f.FlagId}",
        _ => p.GetType().Name,
    }));
}
