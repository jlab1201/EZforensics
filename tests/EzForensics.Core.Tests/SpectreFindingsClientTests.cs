using System.Text.Json.Nodes;
using EzForensics.Core.Assistant;
using EzForensics.Core.Spectre;
using FluentAssertions;

namespace EzForensics.Core.Tests;

public class SpectreFindingsClientTests
{
    private sealed class FakeCaller : ISpectreToolCaller
    {
        public readonly List<(string Tool, JsonObject Args)> Calls = new();
        public McpToolResult Reply = new(false, "{}");
        public Task<McpToolResult> CallAsync(string tool, JsonObject args, CancellationToken ct)
        {
            Calls.Add((tool, (JsonObject)args.DeepClone()));
            return Task.FromResult(Reply);
        }
    }

    private static SpectreFindingsClient Client(FakeCaller c, string? caseDir = null, string? distro = "EzForensics") =>
        new(c, caseDir == null ? null : () => caseDir, distro);

    private const string ListJson = """
        {"findings":[
          {"id":7,"finding_id":"F-001","title":"Mimikatz","severity":"High","confidence":"Medium","mitre_attack":["T1003.001"],
           "artifact_type":"evtx","status":"open","description":"LSASS access","approval":null,"include_in_report":true,"edited":true,
           "event_time":"2026-01-02T03:04:05Z","analyst":{"title":"Credential dumping","severity":"Critical","mitre_attack":"T1003, T1055",
             "notes":"seen twice","include_in_report":false,"edited_by":"analyst","edited_at":"2026-02-01T00:00:00Z","revision":3}},
          {"finding_id":"F-002","title":"Odd logon"}
        ],"unregistered":["x","y"]}
        """;

    [Fact]
    public async Task Timeline_parses_a_50KB_reply_whole()
    {
        var pad = new string('x', 50_000);
        var c = new FakeCaller { Reply = new(false, "{\"case\":\"c1\",\"moments\":[{\"id\":\"M1\",\"rank\":1,\"headline\":\"" + pad + "\"}]}") };
        var tl = await Client(c).TimelineAsync("c1", default);
        tl.Moments.Single().Headline.Length.Should().Be(50_000);
    }

    [Fact]
    public async Task Unreadable_timeline_reply_has_a_short_message_and_keeps_the_text_aside()
    {
        var c = new FakeCaller { Reply = new(false, "not json " + new string('z', 5000)) };
        var ex = await Assert.ThrowsAsync<SpectreToolException>(() => Client(c).TimelineAsync("c1", default));
        ex.Message.Length.Should().BeLessThan(100);
        ex.Message.Should().NotContain("zzz");
        ex.RawReply.Should().Contain("zzz");
    }

    [Fact]
    public async Task List_parses_findings_with_analyst_overrides_and_tolerates_missing_fields()
    {
        var c = new FakeCaller { Reply = new(false, ListJson) };
        var r = await Client(c).ListAsync("case1", default);
        c.Calls.Single().Should().Match<(string Tool, JsonObject Args)>(x => x.Tool == "spectre_findings_list" && (string?)x.Args["case_id"] == "case1");
        r.Unregistered.Should().Be(2);
        r.Findings.Should().HaveCount(2);

        var f = r.Findings[0];
        f.Id.Should().Be("7"); // numeric id read as text
        f.FindingId.Should().Be("F-001");
        f.Analyst!.Revision.Should().Be(3);
        f.EffectiveTitle.Should().Be("Credential dumping");
        f.EffectiveSeverity.Should().Be("Critical");
        f.EffectiveDescription.Should().Be("LSASS access"); // not overridden
        f.EffectiveMitreAttack.Should().Equal("T1003", "T1055"); // string form split
        f.Notes.Should().Be("seen twice");
        f.IsIncluded.Should().BeFalse(); // analyst exclusion wins over the engine's true
        f.IsEdited.Should().BeTrue();
        f.SeverityRank.Should().Be(4);
        f.ApprovalText.Should().Be("Pending");

        var g = r.Findings[1];
        g.Id.Should().Be("F-002");
        g.Severity.Should().Be("Info");
        g.Analyst.Should().BeNull();
        g.EffectiveTitle.Should().Be("Odd logon");
        g.IsIncluded.Should().BeTrue();
        g.IsEdited.Should().BeFalse();
        g.EffectiveMitreAttack.Should().BeEmpty();
    }

    [Fact]
    public async Task Garbage_reply_gives_an_empty_list()
    {
        var r = await Client(new FakeCaller { Reply = new(false, "not json") }).ListAsync("c", default);
        r.Findings.Should().BeEmpty();
        r.Unregistered.Should().Be(0);
    }

    [Fact]
    public void Edited_flag_alone_marks_a_finding_edited()
    {
        var f = SpectreFinding.From(JsonNode.Parse("""{"finding_id":"F-9","edited":true,"mitre_attack":"T1059"}""")!.AsObject());
        f.IsEdited.Should().BeTrue();
        f.MitreAttack.Should().Equal("T1059");
    }

    [Fact]
    public void Changes_serialize_only_set_fields_and_revert_as_null()
    {
        var ch = new SpectreFindingChanges().Title("New").IncludeInReport(false).MitreAttack(new[] { "T1003" }).Notes(null);
        var json = ch.ToJson();
        json.Select(kv => kv.Key).Should().BeEquivalentTo("title", "include_in_report", "mitre_attack", "notes");
        json.ToJsonString().Should().Contain("\"notes\":null");
        ((string?)json["title"]).Should().Be("New");
        ((bool?)json["include_in_report"]).Should().BeFalse();
        json["mitre_attack"]!.AsArray().Select(n => (string?)n).Should().Equal("T1003");
        ch.Reverts("notes").Should().BeTrue();
        ch.Reverts("title").Should().BeFalse();
        new SpectreFindingChanges().IsEmpty.Should().BeTrue();
        new SpectreFindingChanges().Revert("severity").ToJson().ToJsonString().Should().Be("{\"severity\":null}");
    }

    [Fact]
    public async Task Edit_sends_contract_arguments_and_parses_reply()
    {
        var c = new FakeCaller { Reply = new(false, """{"ok":true,"reason":"","finding_id":"F-001","revision":4,"sha256":"abc"}""") };
        var r = await Client(c).EditAsync("case1", "F-001", "analyst", "typo", new SpectreFindingChanges().Title("T").Description(null), default);
        r.Should().Be(new SpectreEditResult(true, "", "F-001", 4, "abc"));
        var (tool, a) = c.Calls.Single();
        tool.Should().Be("spectre_finding_edit");
        ((string?)a["case_id"]).Should().Be("case1");
        ((string?)a["finding_id"]).Should().Be("F-001");
        ((string?)a["examiner"]).Should().Be("analyst");
        ((string?)a["reason"]).Should().Be("typo");
        a["changes"]!.ToJsonString().Should().Be("{\"title\":\"T\",\"description\":null}");
    }

    [Fact]
    public async Task Approve_sends_decision_and_optional_superseded_by()
    {
        var c = new FakeCaller { Reply = new(false, """{"ok":false,"reason":"already","finding_id":"F-1","decision":"REJECTED","previous_approval":"APPROVED","sha256":"s"}""") };
        var r = await Client(c).ApproveAsync("c", "F-1", "me", SpectreFinding.Superseded, "dup", "F-2", default);
        r.Ok.Should().BeFalse();
        r.PreviousApproval.Should().Be("APPROVED");
        var a = c.Calls.Single().Args;
        ((string?)a["decision"]).Should().Be("SUPERSEDED");
        ((string?)a["superseded_by"]).Should().Be("F-2");
        ((string?)a["rationale"]).Should().Be("dup");

        await Client(c).ApproveAsync("c", "F-1", "me", SpectreFinding.Approved, "ok", null, default);
        c.Calls[1].Args.ContainsKey("superseded_by").Should().BeFalse();
    }

    [Fact]
    public async Task History_get_and_events_parse()
    {
        var c = new FakeCaller { Reply = new(false, """{"rows":[{"ts":"t1","action":"edit","tool":"spectre_finding_edit","examiner":"me","params":{"a":1}},{"ts":"t2","action":"approve"}]}""") };
        var rows = await Client(c).HistoryAsync("c", "F-1", default);
        rows.Select(r => r.Action).Should().Equal("edit", "approve");
        rows[0].Params!["a"]!.GetValue<int>().Should().Be(1);

        c.Reply = new(false, """{"finding_id":"F-1","title":"X","fk_caveats":["c1"]}""");
        (await Client(c).GetAsync("c", "F-1", default)).FkCaveats.Should().Equal("c1");

        c.Reply = new(false, """[{"a":1},{"a":2}]""");
        (await Client(c).EventsAsync("c", "evtx", "lsass", 50, default)).Rows.Should().HaveCount(2);
        var args = c.Calls.Last().Args;
        ((string?)args["artifact_type"]).Should().Be("evtx");
        ((int?)args["limit"]).Should().Be(50);
    }

    [Fact]
    public async Task Report_maps_linux_paths_to_wsl_share_and_keeps_dev_paths()
    {
        const string json = """
            {"ok":true,"markdown":"# R","report_md":"/var/lib/ezforensics/spectre/cases/c1/report.md","pdf":"/var/lib/ezforensics/spectre/cases/c1/report.pdf",
             "docx":null,"skipped":{"docx":"python-docx missing"},"findings_included":3,"findings_excluded":1}
            """;
        var c = new FakeCaller { Reply = new(false, json) };
        var r = await Client(c).GenerateReportAsync("c1", new[] { "md", "pdf" }, default);
        r.ReportMd.Should().Be(@"\\wsl.localhost\EzForensics\var\lib\ezforensics\spectre\cases\c1\report.md");
        r.Pdf.Should().EndWith(@"c1\report.pdf");
        r.Docx.Should().BeNull();
        r.Skipped.Should().ContainKey("docx");
        (r.FindingsIncluded, r.FindingsExcluded).Should().Be((3, 1));
        c.Calls.Single().Args["formats"]!.AsArray().Select(n => (string?)n).Should().Equal("md", "pdf");

        var dev = await Client(new FakeCaller { Reply = new(false, """{"ok":true,"report_md":"C:\\cases\\x\\report.md"}""") }, distro: null)
            .GenerateReportAsync("c", new[] { "md" }, default);
        dev.ReportMd.Should().Be(@"C:\cases\x\report.md");
        SpectreFindingsClient.ToWindowsPath("/a/b", null).Should().Be("/a/b");
        SpectreFindingsClient.ToWindowsPath(@"D:\x", "EzForensics").Should().Be(@"D:\x");
        SpectreFindingsClient.ToWindowsPath(null, "EzForensics").Should().BeNull();
    }

    [Fact]
    public async Task IsError_becomes_SpectreToolException_with_the_engine_text()
    {
        var c = new FakeCaller { Reply = new(true, "unknown case_id") };
        var act = () => Client(c).ListAsync("nope", default);
        var ex = (await act.Should().ThrowAsync<SpectreToolException>()).Which;
        ex.Message.Should().Be("unknown case_id");
        ex.Tool.Should().Be("spectre_findings_list");
    }

    [Fact]
    public async Task Run_records_the_phase_in_the_case_link()
    {
        var dir = Path.Combine(Path.GetTempPath(), "spectre-link-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(dir);
        try
        {
            SpectreCaseLink.Save(dir, new SpectreCaseLink("eng-1", null, DateTime.UtcNow));
            var c = new FakeCaller { Reply = new(false, """{"ok":true}""") };
            await Client(c, dir).RunAsync("eng-1", 5, default);
            c.Calls.Single().Args["through_phase"]!.GetValue<int>().Should().Be(5);
            SpectreCaseLink.Load(dir)!.LastPhase.Should().Be(5);
            await Client(c, dir).RunAsync("eng-1", 3, default);
            SpectreCaseLink.Load(dir)!.LastPhase.Should().Be(5); // never lowered
        }
        finally { Directory.Delete(dir, true); }
    }

    [Fact]
    public void Case_link_round_trips_and_missing_or_broken_files_give_null()
    {
        var dir = Path.Combine(Path.GetTempPath(), "spectre-link-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(dir);
        try
        {
            SpectreCaseLink.Load(dir).Should().BeNull();
            SpectreCaseLink.Load(null).Should().BeNull();
            var link = new SpectreCaseLink("eng-42", 5, new DateTime(2026, 11, 6, 1, 2, 3, DateTimeKind.Utc));
            SpectreCaseLink.Save(dir, link);
            File.Exists(Path.Combine(dir, ".ezassistant", "spectre.json")).Should().BeTrue();
            SpectreCaseLink.Load(dir).Should().Be(link);
            File.WriteAllText(Path.Combine(dir, ".ezassistant", "spectre.json"), "{ broken");
            SpectreCaseLink.Load(dir).Should().BeNull();
        }
        finally { Directory.Delete(dir, true); }
    }

    [Fact]
    public async Task OpenCase_without_a_case_folder_throws_and_status_parses()
    {
        var c = new FakeCaller { Reply = new(false, """{"version":"1.2","ok":true}""") };
        (await Client(c).StatusAsync(default)).Version.Should().Be("1.2");
        var act = () => Client(c).OpenCaseAsync(Path.Combine(Path.GetTempPath(), "does-not-exist-" + Guid.NewGuid().ToString("N")), default);
        await act.Should().ThrowAsync<SpectreToolException>();
    }
}
