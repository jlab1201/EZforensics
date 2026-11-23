using System.IO;
using System.Text.Json.Nodes;
using EzForensics.App.ViewModels.Findings;
using EzForensics.Core.Assistant;
using EzForensics.Core.Analysis;
using EzForensics.Core.Spectre;
using FluentAssertions;

namespace EzForensics.App.Tests;

/// <summary>The Findings evidence panel over a fake engine: rows into the panel, labels saved and the calls made, the old-engine fallback, Previous / Next, new finding.</summary>
[Collection("WpfApp")]   // the chat tool opens the panel through the page tools, which Invoke on the smoke tests' dispatcher
public sealed class EvidencePanelViewModelTests : IDisposable
{
    /// <summary>The page's fake engine plus the evidence-flag tools (or none, like an older Spectre).</summary>
    internal sealed class FlagEngine : ISpectreToolCaller
    {
        public readonly FindingsViewModelTests.FakeEngine Inner = new();
        public readonly List<(string Tool, JsonObject Args)> Calls = new();
        public readonly List<JsonObject> Flags = new();
        public bool Unknown;
        public string? Refuse;

        public Task<McpToolResult> CallAsync(string tool, JsonObject args, CancellationToken ct)
        {
            Calls.Add((tool, (JsonObject)args.DeepClone()));
            switch (tool)
            {
                case SpectreFindingsClient.FlagTool or SpectreFindingsClient.FlagsTool or SpectreFindingsClient.CreateTool when Unknown:
                    return Task.FromResult(new McpToolResult(true, $"unknown tool '{tool}'"));
                case SpectreFindingsClient.FlagsTool:
                    return Ok(new JsonObject { ["flags"] = new JsonArray(Flags.Select(f => (JsonNode)f.DeepClone()).ToArray()) });
                case SpectreFindingsClient.FlagTool:
                    if (Refuse != null) return Ok(new JsonObject { ["ok"] = false, ["reason"] = Refuse });
                    var flag = new JsonObject
                    {
                        ["case_path"] = args["case_path"]!.ToString(), ["row"] = (long)args["row"]!, ["label"] = args["label"]!.ToString(),
                        ["note"] = args["note"]?.ToString(), ["analyst"] = args["analyst"]!.ToString(), ["at"] = "2026-11-07T10:00:00Z",
                    };
                    return Ok(new JsonObject { ["ok"] = true, ["finding_id"] = args["finding_id"]!.ToString(), ["flag"] = flag });
                case SpectreFindingsClient.CreateTool:
                    return Ok(new JsonObject { ["ok"] = true, ["finding_id"] = "A-1" });
                default:
                    return Inner.CallAsync(tool, args, ct);
            }
        }

        private static Task<McpToolResult> Ok(JsonObject o) => Task.FromResult(new McpToolResult(false, o.ToJsonString()));
        public IEnumerable<JsonObject> ArgsOf(string tool) => Calls.Where(c => c.Tool == tool).Select(c => c.Args);
    }

    private readonly string _dir = FindingsViewModelTests.TempDir();
    private readonly FlagEngine _engine = new();

    public EvidencePanelViewModelTests()
    {
        Directory.CreateDirectory(Path.Combine(_dir, "PC1", "EventLogs"));
        // Security.csv: 6 rows, Spectre names rows 1 and 3; System.csv: 4 rows, Spectre names row 2 (F-2)
        File.WriteAllText(Path.Combine(_dir, "PC1", "EventLogs", "Security.csv"),
            "Time,EventId,Message\n2026-01-01 10:00:00,4624,logon alice\n2026-01-01 10:01:00,4688,mimikatz.exe started\n2026-01-01 10:02:00,4624,logon bob\n" +
            "2026-01-01 10:03:00,4672,special privileges\n2026-01-01 10:04:00,4634,logoff alice\n2026-01-01 10:05:00,4688,cmd.exe started\n");
        File.WriteAllText(Path.Combine(_dir, "PC1", "EventLogs", "System.csv"),
            "Time,EventId,Message\n2026-01-01 09:00:00,7045,service a\n2026-01-01 09:01:00,7036,service b\n2026-01-01 09:02:00,7045,service mimi\n2026-01-01 09:03:00,7036,service c\n");
        _engine.Inner.ListReply = """
            {"findings":[
              {"id":"F-1","finding_id":"F-1","title":"Mimikatz","severity":"High","evidence_total":3,
               "evidence":[{"case_path":"PC1/EventLogs/Security.csv","row":1},{"case_path":"PC1/EventLogs/Security.csv","row":3},{"case_path":"PC1/EventLogs/System.csv","row":2}]},
              {"id":"F-2","finding_id":"F-2","title":"Service","severity":"Low","evidence":[{"case_path":"PC1/EventLogs/System.csv","row":2}]}],"unregistered":[]}
            """;
        _engine.Inner.TimelineReply = """
            {"case":"case1","time_basis":"UTC","findings":[{"id":"F-1","finding_id":"F-1","title":"Mimikatz","severity":"High","start":"2026-01-01T10:01:00Z"},
                                                           {"id":"F-2","finding_id":"F-2","title":"Service","severity":"Low","start":"2026-01-01T09:02:00Z"}],"undated":[],"clusters":[],
             "moments":[{"id":"M-1","rank":1,"time":"2026-01-01T10:01:00Z","primary_finding_id":"F-1","finding_ids":["F-1","F-2"],"headline":"High: Mimikatz","plain_title":"A password tool ran"}]}
            """;
    }

    public void Dispose() { try { Directory.Delete(_dir, true); } catch (IOException) { } }

    private async Task<FindingsViewModel> OpenAsync()
    {
        SpectreCaseLink.Save(_dir, new SpectreCaseLink("case1", 5, DateTime.UtcNow));
        var vm = new FindingsViewModel(() => _dir, new SpectreFindingsClient(_engine, () => _dir, null), _ => Task.FromResult(true));
        await vm.OpenCaseAsync(_dir);
        return vm;
    }

    private async Task<EvidencePanelViewModel> OpenPanelAsync(FindingsViewModel vm)
    {
        await vm.ShowMomentEvidenceCommand.ExecuteAsync(vm.StoryMoments.Single(m => m.Id == "M-1"));
        vm.EvidencePanel.IsOpen.Should().BeTrue();
        return vm.EvidencePanel;
    }

    private static async Task Select(EvidencePanelViewModel p, params long[] rows)
    {
        await p.GoToRowAsync(rows[0]);
        p.SelectedRows = rows;
    }

    [Fact]
    public async Task Opens_on_the_finding_rows_with_Spectre_and_analyst_marks()
    {
        var vm = await OpenAsync();
        vm.Mutate(a =>
        {
            var i = a.Items.Single(x => x.Source.SpectreFindingId == "F-1");
            AnalysisEdits.RemoveRow(i, new AnalysisRow("PC1/EventLogs/Security.csv", 3));
            AnalysisEdits.SetRowNote(i, new AnalysisRow("PC1/EventLogs/Security.csv", 5), null, "check this");
            return true;
        }).Should().BeTrue();
        var p = await OpenPanelAsync(vm);

        p.Title.Should().Be("A password tool ran");
        p.FindingId.Should().Be("F-1");
        p.Files.Select(f => f.Name).Should().Equal("Security.csv", "System.csv");
        p.HasSeveralFiles.Should().BeTrue();
        p.Files[0].CasePath.Should().Be("PC1/EventLogs/Security.csv");
        p.Files.Select(f => f.SpectreCount).Should().Equal(2, 1);
        p.Files[0].CountText.Should().Be("2 Spectre rows · 2 analyst labels");
        p.FileLine.Should().Be("Security.csv · PC1 · 2 Spectre rows of 6");
        p.Headers.Should().Equal("Time", "EventId", "Message");
        p.Rows!.Map.Should().Equal(1, 3, 5);              // the item's rows (a removed row stays, marked) and the row with a note
        p.CurrentRow.Should().Be(1);                       // the first evidence row is selected
        p.DetailCells.Should().Contain(new KeyValuePair<string, string>("Message", "mimikatz.exe started"));
        p.DetailMarkText.Should().Be("Row 2 · Spectre evidence");

        var spectre = p.MarkOf(p.Files[0], 1)!;
        spectre.IsSpectre.Should().BeTrue(); spectre.HasLabel.Should().BeFalse();
        var faded = p.MarkOf(p.Files[0], 3)!;
        faded.IsSpectre.Should().BeTrue(); faded.IsNotRelevant.Should().BeTrue();
        faded.Text.Should().Be("Spectre evidence\nNot relevant (analyst)");
        p.MarkOf(p.Files[0], 5)!.Text.Should().Be("Note: check this");
        p.MarkOf(p.Files[0], 0).Should().BeNull();
        _engine.Calls.Should().NotContain(c => c.Tool == "spectre_evidence_flags");   // the flags come from the case analysis, not the engine
        p.IsReadOnly.Should().BeFalse();

        // quick search inside the evidence rows; Show all rows
        p.SearchText = "mimikatz";
        await p.ApplyViewAsync();
        p.Rows.Map.Should().Equal(1);
        p.ShowAllRows = true;
        await p.ApplyViewAsync();
        p.Rows.Map.Should().Equal(1);
        p.SearchText = "";
        await p.ApplyViewAsync();
        p.Rows.Map.Should().BeNull();
        p.VisibleCount.Should().Be(6);
    }

    [Fact]
    public async Task Labels_save_to_the_case_analysis_with_history_and_not_to_the_engine()
    {
        var vm = await OpenAsync();
        var p = await OpenPanelAsync(vm);
        _engine.Calls.Clear();
        var item = () => vm.Analysis!.Items.Single(x => x.Source.SpectreFindingId == "F-1");

        await Select(p, 1, 3);
        await p.ConfirmCommand.ExecuteAsync(null);
        item().RowEdits.PerRow.Where(n => n.Confirmed == true).Select(n => n.Row).Should().BeEquivalentTo(new long[] { 1, 3 });
        item().History.Select(h => h.What).Should().Contain("row note PC1/EventLogs/Security.csv#1");
        p.MarkOf(p.Files[0], 1)!.Label.Should().Be("confirmed");
        p.Files[0].AnalystCount.Should().Be(2);
        _engine.Calls.Should().NotContain(c => c.Tool == "spectre_evidence_flag" || c.Tool == "spectre_finding_get");

        // a row Spectre missed: Add (A) puts it in the evidence rows; Del clears it again
        p.ShowAllRows = true;
        await p.ApplyViewAsync();
        await Select(p, 4);
        await p.AddCommand.ExecuteAsync(null);
        item().RowEdits.Added.Select(r => r.Row).Should().Equal(4);
        p.MarkOf(p.Files[0], 4)!.Label.Should().Be("added");
        p.MarkOf(p.Files[0], 4)!.IsSpectre.Should().BeFalse();
        p.EvidenceRowsOf(p.Files[0]).Should().Equal(1, 3, 4);
        await p.ClearCommand.ExecuteAsync(null);
        item().RowEdits.Added.Should().BeEmpty();
        p.MarkOf(p.Files[0], 4).Should().BeNull();
        p.EvidenceRowsOf(p.Files[0]).Should().Equal(1, 3);

        // a note opens a text box; the row keeps its label
        await Select(p, 3);
        p.NoteCommand.Execute(null);
        p.IsNoteOpen.Should().BeTrue();
        p.NoteText = "seen in the memory image too";
        await p.SaveNoteCommand.ExecuteAsync(null);
        p.IsNoteOpen.Should().BeFalse();
        item().RowEdits.PerRow.Single(n => n.Row == 3).Note.Should().Be("seen in the memory image too");
        p.MarkOf(p.Files[0], 3)!.HasNote.Should().BeTrue();
        p.MarkOf(p.Files[0], 3)!.Label.Should().Be("confirmed");   // the note does not replace the label

        // Not relevant removes the row from the item; it stays in the panel, marked, and can be restored
        await p.ShowFileAsync(p.Files[1], selectFirst: true);
        p.CurrentRow.Should().Be(2);
        await p.NotRelevantCommand.ExecuteAsync(null);
        item().RowEdits.Removed.Select(r => (r.CsvRelPath, r.Row)).Should().Equal(("PC1/EventLogs/System.csv", 2L));
        p.MarkOf(p.Files[1], 2)!.Label.Should().Be("not_relevant");
        vm.Analysis!.EffectiveRows(item()).Select(r => r.Row).Should().NotContain(2);
    }

    [Fact]
    public async Task A_row_removed_as_not_relevant_stays_removed_after_a_new_import_and_a_sync()
    {
        var vm = await OpenAsync();
        var p = await OpenPanelAsync(vm);
        await Select(p, 1);
        await p.NotRelevantCommand.ExecuteAsync(null);
        var removed = () => vm.Analysis!.Items.Single(x => x.Source.SpectreFindingId == "F-1").RowEdits.Removed.Select(r => r.Row).ToList();
        removed().Should().Equal(1);

        await vm.LoadFindingsAsync();                          // Spectre's results are imported again
        removed().Should().Equal(1);
        await vm.AddMyAnalysisCommand.ExecuteAsync(null);      // and the analyst's analysis is synced
        removed().Should().Equal(1);
        var again = await OpenAsync();                         // a fresh page on the same case sees the same
        again.Analysis!.Items.Single(x => x.Source.SpectreFindingId == "F-1").RowEdits.Removed.Select(r => r.Row).Should().Equal(1);
        again.Analysis.EffectiveRows(again.Analysis.Items.Single(x => x.Source.SpectreFindingId == "F-1")).Select(r => r.Row).Should().NotContain(1);
    }

    [Fact]
    public async Task Previous_and_next_step_through_the_evidence_rows_across_files()
    {
        var p = await OpenPanelAsync(await OpenAsync());
        p.CurrentFile!.Name.Should().Be("Security.csv");
        p.CurrentRow.Should().Be(1);
        await p.NextRowCommand.ExecuteAsync(null);
        p.CurrentRow.Should().Be(3);
        await p.NextRowCommand.ExecuteAsync(null);
        p.CurrentFile!.Name.Should().Be("System.csv");
        p.CurrentRow.Should().Be(2);
        p.Files[1].IsCurrent.Should().BeTrue();
        await p.NextRowCommand.ExecuteAsync(null);
        p.Message.Should().Be("This is the last evidence row.");
        p.CurrentRow.Should().Be(2);
        await p.PreviousRowCommand.ExecuteAsync(null);
        p.CurrentFile!.Name.Should().Be("Security.csv");
        p.CurrentRow.Should().Be(3);
        await p.PreviousRowCommand.ExecuteAsync(null);
        await p.PreviousRowCommand.ExecuteAsync(null);
        p.Message.Should().Be("This is the first evidence row.");
        p.CurrentRow.Should().Be(1);
    }


    [Fact]
    public async Task Esc_close_and_the_chat_tool_open_the_panel()
    {
        var vm = await OpenAsync();
        var r = await FindingsViewModelTests.Run(vm, "findings_show_evidence", new JsonObject { ["finding_id"] = "F-2" });
        r.IsError.Should().BeFalse();
        vm.EvidencePanel.IsOpen.Should().BeTrue();
        vm.EvidencePanel.FindingId.Should().Be("F-2");
        vm.EvidencePanel.Files.Select(f => f.Name).Should().Equal("System.csv");
        vm.EvidencePanel.CloseCommand.Execute(null);
        vm.EvidencePanel.IsOpen.Should().BeFalse();
        vm.EvidencePanel.Rows.Should().BeNull();
        (await FindingsViewModelTests.Run(vm, "findings_show_evidence", new JsonObject { ["finding_id"] = "F-404" })).IsError.Should().BeTrue();
    }

    [Fact]
    public void Analyst_marks_never_read_as_Spectre()
    {
        foreach (var label in EvidenceFlagLabels.AnalystLabels.Append(EvidenceFlagLabels.Note))
        {
            EvidenceFlagLabels.Text(label).Should().NotContain("Spectre");
            EvidenceFlagLabels.Text(label).Should().MatchRegex("analyst|Analyst");
        }
        EvidencePanelViewModel.AuditHint.Should().Be("Your flags are recorded with your name and the time (UTC) and appear in the report under the finding.");
        new EvidenceRowMark(false, "confirmed", "jo", "2026-11-07T10:00:00+02:00", null, null, null).Text.Should().Be("Confirmed by analyst (jo, 2026-11-07 08:00 UTC)");
    }
}
