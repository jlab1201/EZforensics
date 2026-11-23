using System.Text.Json.Nodes;
using EzForensics.Core.Assistant;
using EzForensics.Core.Spectre;
using FluentAssertions;

namespace EzForensics.Core.Tests;

/// <summary>The evidence-flag tools of docs/evidence-flags-contract.md, as the client sends and reads them.</summary>
public class SpectreEvidenceFlagsTests
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

    [Fact]
    public async Task Flag_sends_the_contract_fields_and_reads_the_flag_back()
    {
        var c = new FakeCaller { Reply = new(false, """{"ok":true,"finding_id":"F-1","flag":{"case_path":"PC1/a.csv","row":4,"label":"note","note":"odd","analyst":"jo","at":"2026-11-07T10:00:00Z"}}""") };
        var r = await new SpectreFindingsClient(c, null, null).FlagEvidenceAsync("case1", "F-1", "PC1/a.csv", 4, EvidenceFlagLabels.Note, "jo", "odd", null, default);
        var (tool, args) = c.Calls.Single();
        tool.Should().Be("spectre_evidence_flag");
        ((string?)args["case_dir"]).Should().Be("case1");
        ((string?)args["finding_id"]).Should().Be("F-1");
        ((string?)args["case_path"]).Should().Be("PC1/a.csv");
        ((long?)args["row"]).Should().Be(4);
        ((string?)args["label"]).Should().Be("note");
        ((string?)args["analyst"]).Should().Be("jo");
        ((string?)args["note"]).Should().Be("odd");
        args.ContainsKey("reason").Should().BeFalse();
        r.Ok.Should().BeTrue();
        r.Flag.Should().Be(new SpectreEvidenceFlag("F-1", "PC1/a.csv", 4, "note", "odd", "jo", "2026-11-07T10:00:00Z"));
    }

    [Fact]
    public async Task Flags_lists_the_current_state_and_skips_bad_entries()
    {
        var c = new FakeCaller { Reply = new(false, """{"flags":[{"finding_id":"F-1","case_path":"a.csv","row":1,"label":"confirmed","analyst":"jo","at":"t"},{"case_path":"a.csv","label":"added"},{"case_path":"b.csv","row":2,"label":"ADDED"}]}""") };
        var flags = await new SpectreFindingsClient(c, null, null).EvidenceFlagsAsync("case1", "F-9", default);
        c.Calls.Single().Tool.Should().Be("spectre_evidence_flags");
        ((string?)c.Calls.Single().Args["finding_id"]).Should().Be("F-9");
        flags.Should().HaveCount(2);
        flags[0].FindingId.Should().Be("F-1");
        flags[1].Should().Match<SpectreEvidenceFlag>(f => f.FindingId == "F-9" && f.Label == "added" && f.Row == 2);
    }

    [Fact]
    public async Task Create_sends_rows_sorted_and_severity_lower_case()
    {
        var c = new FakeCaller { Reply = new(false, """{"ok":true,"finding_id":"A-1"}""") };
        var r = await new SpectreFindingsClient(c, null, null).CreateFindingAsync("case1", "Odd logon", "High", "a.csv", new long[] { 9, 2, 2 }, "jo", null, null, null, default);
        var (tool, args) = c.Calls.Single();
        tool.Should().Be("spectre_finding_create");
        ((string?)args["severity"]).Should().Be("high");
        args["rows"]!.AsArray().Select(n => (long)n!).Should().Equal(2, 9);
        args.ContainsKey("note").Should().BeFalse();
        r.Should().Be(new SpectreCreateResult(true, null, "A-1"));
    }

    [Fact]
    public async Task Unknown_tool_is_recognised()
    {
        var c = new FakeCaller { Reply = new(true, "unknown tool 'spectre_evidence_flag'") };
        var ex = await Assert.ThrowsAsync<SpectreToolException>(() => new SpectreFindingsClient(c, null, null).EvidenceFlagsAsync("c", null, default));
        SpectreErrors.IsUnknownTool(ex).Should().BeTrue();
        SpectreErrors.IsUnknownTool(new SpectreToolException("x", "locked")).Should().BeFalse();
    }

    [Fact]
    public void Findings_read_analyst_flags_origin_and_evidence_by()
    {
        var f = SpectreFinding.From(JsonNode.Parse("""
            {"finding_id":"F-1","title":"t","origin":"analyst",
             "evidence":[{"case_path":"a.csv","row":1,"by":"spectre"},{"case_path":"a.csv","row":5,"by":"analyst","analyst_label":"added"}],
             "analyst_flags":[{"case_path":"a.csv","row":5,"label":"added","analyst":"jo","at":"t"}]}
            """)!.AsObject());
        f.IsAnalystFinding.Should().BeTrue();
        f.Evidence.Select(e => e.IsAnalyst).Should().Equal(false, true);
        f.Evidence[1].AnalystLabel.Should().Be("added");
        f.AnalystFlags.Single().Should().Match<SpectreEvidenceFlag>(x => x.FindingId == "F-1" && x.Row == 5);
    }

    [Fact]
    public void Case_path_prefers_the_engine_spelling_then_the_relative_path()
    {
        var dir = Path.Combine(Path.GetTempPath(), "ezf-cp-" + Guid.NewGuid().ToString("N")[..8]);
        Directory.CreateDirectory(Path.Combine(dir, "PC1"));
        var csv = Path.Combine(dir, "PC1", "a.csv");
        File.WriteAllText(csv, "A\n1\n");
        try
        {
            FindingsEvidence.CasePathOf(dir, csv, new[] { "PC1/a.csv" }).Should().Be("PC1/a.csv");
            FindingsEvidence.CasePathOf(dir, csv).Should().Be("PC1/a.csv");
            EvidenceFlagLabels.Text(EvidenceFlagLabels.Added).Should().Contain("analyst");
            EvidenceFlagLabels.Text(EvidenceFlagLabels.Evidence).Should().StartWith("Spectre");
        }
        finally { Directory.Delete(dir, true); }
    }
}
