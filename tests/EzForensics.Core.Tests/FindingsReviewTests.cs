using System.Text.Json.Nodes;
using EzForensics.Core.Spectre;
using FluentAssertions;

namespace EzForensics.Core.Tests;

public class FindingsReviewTests
{
    private static SpectreFinding F(string id, string title = "T", string sev = "Medium", string? approval = null, string[]? mitre = null, bool include = true,
        SpectreAnalystEdit? analyst = null, string? time = null, string desc = "D") =>
        new(id, id, title, sev, null, mitre ?? new[] { "T1059" }, "evtx", "open", desc, approval, include, false, time, null, null, analyst, Array.Empty<string>());

    private static SpectreAnalystEdit A(string? title = null, string? sev = null, string[]? mitre = null, string? notes = null, bool? inc = null) =>
        new(title, null, sev, mitre, notes, inc, "me", null, 1);

    private static readonly SpectreFinding[] Sample =
    {
        F("F-1", "Low thing", "Low", time: "2026-01-02T00:00:00Z"),
        F("F-2", "Credential dumping", "Critical", approval: "APPROVED", mitre: new[] { "T1003" }),
        F("F-3", "Odd logon", "Critical", approval: "REJECTED", include: false, time: "2026-01-01T00:00:00Z"),
        F("F-4", "Renamed", "Low", analyst: A(title: "Mimikatz", sev: "High")),
    };

    [Fact]
    public void From_reads_case_path_next_to_source_file()
    {
        var f = SpectreFinding.From(new JsonObject { ["finding_id"] = "F-1", ["source_file"] = "evidence/Sec__ab12cd34.evtx", ["case_path"] = "WS01/Sec.evtx" });
        f.CasePath.Should().Be("WS01/Sec.evtx");
        f.SourceFile.Should().Be("evidence/Sec__ab12cd34.evtx");
        SpectreFinding.From(new JsonObject { ["finding_id"] = "F-2" }).CasePath.Should().BeNull();
    }

    [Fact]
    public void Filter_sorts_most_severe_first_then_by_time_and_id()
    {
        new FindingsFilter().Apply(Sample).Select(f => f.Id).Should().Equal("F-3", "F-2", "F-4", "F-1");   // Critical (F-3 earlier), High (edited), Low
    }

    [Fact]
    public void Filter_by_severity_uses_the_effective_value()
    {
        new FindingsFilter(Severity: "High").Apply(Sample).Select(f => f.Id).Should().Equal("F-4");
        new FindingsFilter(Severity: "Any").Apply(Sample).Should().HaveCount(4);
    }

    [Fact]
    public void Filter_by_approval_maps_none_to_Pending()
    {
        new FindingsFilter(Approval: "Pending").Apply(Sample).Select(f => f.Id).Should().BeEquivalentTo("F-1", "F-4");
        new FindingsFilter(Approval: SpectreFinding.Rejected).Apply(Sample).Select(f => f.Id).Should().Equal("F-3");
    }

    [Fact]
    public void Filter_included_only_and_search_match_effective_text_and_all_words()
    {
        new FindingsFilter(IncludedOnly: true).Apply(Sample).Select(f => f.Id).Should().NotContain("F-3");
        new FindingsFilter(Search: "mimikatz").Apply(Sample).Select(f => f.Id).Should().Equal("F-4");   // the examiner's title
        new FindingsFilter(Search: "t1003 credential").Apply(Sample).Select(f => f.Id).Should().Equal("F-2");
        new FindingsFilter(Search: "t1003 logon").Apply(Sample).Should().BeEmpty();
    }

    [Fact]
    public void Draft_with_no_change_is_empty()
    {
        var f = F("F-1", "Title", "Low", desc: "Desc");
        FindingDraft.BuildChanges(f, "Title", "Desc", "Low", "T1059", "", true).IsEmpty.Should().BeTrue();
        FindingDraft.BuildChanges(f, " Title ", "Desc", "low", "t1059", "  ", true).IsEmpty.Should().BeTrue();   // spacing and case do not count
    }

    [Fact]
    public void Draft_sends_only_changed_fields()
    {
        var f = F("F-1", "Title", "Low", desc: "Desc");
        var c = FindingDraft.BuildChanges(f, "New title", "Desc", "High", "T1059, T1003.001", "why", false);
        c.Fields.Should().BeEquivalentTo("title", "severity", "mitre_attack", "notes", "include_in_report");
        var j = c.ToJson();
        j["title"]!.ToString().Should().Be("New title");
        j["mitre_attack"]!.AsArray().Select(x => x!.ToString()).Should().Equal("T1059", "T1003.001");
        j["include_in_report"]!.GetValue<bool>().Should().BeFalse();
    }

    [Fact]
    public void Draft_going_back_to_the_engine_value_reverts_the_override()
    {
        var f = F("F-1", "Engine", "Low", analyst: A(title: "Mine", sev: "High", mitre: new[] { "T1" }, notes: "n", inc: false));
        var c = FindingDraft.BuildChanges(f, "Engine", "D", "Low", "T1059", "", true);
        c.Reverts("title").Should().BeTrue();
        c.Reverts("severity").Should().BeTrue();
        c.Reverts("mitre_attack").Should().BeTrue();
        c.Reverts("notes").Should().BeTrue();
        c.Reverts("include_in_report").Should().BeTrue();
        c.Fields.Should().NotContain("description");
    }

    [Fact]
    public void Draft_unchanged_override_is_not_resent()
    {
        var f = F("F-1", "Engine", "Low", analyst: A(title: "Mine", sev: "High"));
        FindingDraft.BuildChanges(f, "Mine", "D", "High", "T1059", "", true).IsEmpty.Should().BeTrue();
    }

    [Fact]
    public void ParseMitre_splits_on_commas_spaces_and_lines_and_dedupes()
    {
        FindingDraft.ParseMitre("t1059, T1003.001;\nT1059 ").Should().Equal("T1059", "T1003.001");
        FindingDraft.ParseMitre(null).Should().BeEmpty();
    }

    [Fact]
    public void Suggestion_diff_lists_current_and_proposed_values()
    {
        var f = F("F-1", "Old", "Low");
        var s = new FindingSuggestion("F-1", new SpectreFindingChanges().Severity("High").MitreAttack(new[] { "T1003" }).Revert("notes"), "looks like LSASS dumping");
        s.DiffLines(f).Should().Equal("Severity: Low → High", "MITRE: T1059 → T1003", "Notes: (empty) → (engine value)");
    }

    [Fact]
    public void Evidence_finds_an_existing_csv_relative_or_absolute()
    {
        var dir = Path.Combine(Path.GetTempPath(), "ezf-findings-" + Guid.NewGuid().ToString("N")[..8]);
        Directory.CreateDirectory(Path.Combine(dir, "H"));
        var csv = Path.Combine(dir, "H", "evtx.csv");
        File.WriteAllText(csv, "a,b");
        try
        {
            var rows = new[]
            {
                new JsonObject { ["source_file"] = "missing.csv" },
                new JsonObject { ["file"] = @"H\evtx.csv" },
            };
            FindingsEvidence.FindSourceCsv(rows, dir).Should().Be(csv);
            FindingsEvidence.FindSourceCsv(new[] { new JsonObject { ["path"] = csv } }, "elsewhere").Should().Be(csv);
            FindingsEvidence.FindSourceCsv(new[] { new JsonObject { ["x"] = "y" } }, dir).Should().BeNull();
        }
        finally { Directory.Delete(dir, true); }
    }

    [Fact]
    public void Evidence_line_joins_fields_and_cuts_long_rows()
    {
        FindingsEvidence.Line(new JsonObject { ["a"] = 1, ["b"] = "x" }).Should().Be("a: 1 · b: x");
        FindingsEvidence.Line(new JsonObject { ["a"] = new string('z', 600) }).Should().EndWith("…").And.HaveLength(401);
    }

    [Fact]
    public async Task Source_file_and_record_id_find_the_exact_row_and_never_guess()
    {
        var dir = Path.Combine(Path.GetTempPath(), "ezf-evidence-" + Guid.NewGuid().ToString("N")[..8]);
        var sub = Path.Combine(dir, "EventLogs");
        Directory.CreateDirectory(sub);
        try
        {
            var csv = Path.Combine(sub, "Security.csv");
            File.WriteAllText(csv, "TimeCreated,EventId,EventRecordId\n2026-01-01,4688,100\n2026-01-01,4624,0205\n2026-01-01,4688,300\n");
            var full = Path.GetFullPath(csv);
            FindingsEvidence.ResolveSourceFile("EventLogs/Security.csv", dir).Should().Be(full);
            FindingsEvidence.ResolveSourceFile(full, dir).Should().Be(full);
            FindingsEvidence.ResolveSourceFile("/var/lib/spectre/cases/x/" + Path.GetFileName(dir) + "/EventLogs/Security.csv", dir).Should().Be(full);
            FindingsEvidence.ResolveSourceFile("Security.csv", dir).Should().Be(full);   // unique name in the case
            FindingsEvidence.ResolveSourceFile("Missing.csv", dir).Should().BeNull();
            File.WriteAllText(Path.Combine(dir, "Security.csv"), "a\n1\n");
            FindingsEvidence.ResolveSourceFile("other/Security.csv", dir).Should().BeNull();   // two files of that name: no guess

            using var idx = await Results.CsvIndex.OpenAsync(csv, null, CancellationToken.None);
            await idx.IndexingCompleted;
            FindingsEvidence.FindRecordRow(idx, "300").Should().Be(2);
            FindingsEvidence.FindRecordRow(idx, "205").Should().Be(1);   // numeric match ignores leading zeros
            FindingsEvidence.FindRecordRow(idx, "999").Should().BeNull();
            FindingsEvidence.FindRecordRow(idx, "").Should().BeNull();

            var noId = Path.Combine(dir, "plain.csv");
            File.WriteAllText(noId, "A,B\n1,2\n");
            using var idx2 = await Results.CsvIndex.OpenAsync(noId, null, CancellationToken.None);
            await idx2.IndexingCompleted;
            FindingsEvidence.FindRecordRow(idx2, "1").Should().BeNull();   // no record-id column: the row is not guessed
        }
        finally { try { Directory.Delete(dir, true); } catch (IOException) { } }
    }

    [Fact]
    public void Engine_evidence_list_is_parsed_and_old_engines_give_none()
    {
        var f = SpectreFinding.From(JsonNode.Parse("""{"id":"F1","title":"t","evidence":[{"case_path":"EventLogs/Security.csv","row":4,"record_id":"900"},{"case_path":"","row":1},{"case_path":"a.csv","row":-2}],"evidence_total":12}""")!.AsObject());
        f.Evidence.Should().ContainSingle().Which.Should().Be(new SpectreEvidenceRef("EventLogs/Security.csv", 4, "900"));
        f.EvidenceTotal.Should().Be(12);
        SpectreFinding.From(JsonNode.Parse("""{"id":"F2","title":"t"}""")!.AsObject()).Evidence.Should().BeEmpty();
        var t = SpectreTimelineFinding.From(JsonNode.Parse("""{"id":"F3","evidence":[{"case_path":"x.csv","row":"7"}]}""")!.AsObject());
        t.Evidence.Should().ContainSingle().Which.Row.Should().Be(7);
        t.EvidenceTotal.Should().Be(1);
    }

    [Fact]
    public async Task Evidence_rows_come_from_engine_rows_then_id_column_then_row_number()
    {
        var dir = Path.Combine(Path.GetTempPath(), "ezf-evrows-" + Guid.NewGuid().ToString("N")[..8]);
        Directory.CreateDirectory(Path.Combine(dir, "EventLogs"));
        try
        {
            var withId = Path.Combine(dir, "EventLogs", "Security.csv");
            File.WriteAllText(withId, "TimeCreated,EventRecordId\n2026-01-01,100\n2026-01-01,200\n2026-01-01,300\n");
            var plain = Path.Combine(dir, "EventLogs", "plain.csv");
            File.WriteAllText(plain, "A,B\n1,2\n3,4\n5,6\n");
            using var i1 = await Results.CsvIndex.OpenAsync(withId, null, CancellationToken.None); await i1.IndexingCompleted;
            using var i2 = await Results.CsvIndex.OpenAsync(plain, null, CancellationToken.None); await i2.IndexingCompleted;

            // engine rows win and are cut to the file; ids add to them
            FindingsEvidence.ResolveRows(i1, new EvidenceTarget(withId, new long[] { 2, 0, 99 }, new[] { "200" })).Should().Equal(0, 1, 2);
            // id column lookup
            FindingsEvidence.ResolveRows(i1, new EvidenceTarget(withId, Array.Empty<long>(), new[] { "300", "100" })).Should().Equal(0, 2);
            // no id column: a numeric id is the 1-based data row; 0 and past-the-end are dropped; text is never guessed
            FindingsEvidence.ResolveRows(i2, new EvidenceTarget(plain, Array.Empty<long>(), new[] { "1", "3", "0", "4", "abc" })).Should().Equal(0, 2);

            var plan = FindingsEvidence.Plan(dir, new[]
            {
                new EvidenceSource("EventLogs/Security.csv", null, null, new[] { new SpectreEvidenceRef("EventLogs/Security.csv", 2, "300"), new SpectreEvidenceRef("EventLogs/Security.csv", 0, null), new SpectreEvidenceRef("EventLogs/plain.csv", 1, null) }),
                new EvidenceSource("EventLogs/plain.csv", null, "3", Array.Empty<SpectreEvidenceRef>()),
                new EvidenceSource("missing.csv", null, "5", Array.Empty<SpectreEvidenceRef>()),
            });
            plan.Should().HaveCount(2);
            plan[0].Csv.Should().EndWith("Security.csv"); plan[0].Rows.Should().Equal(0, 2); plan[0].RecordIds.Should().BeEmpty();
            plan[1].Csv.Should().EndWith("plain.csv"); plan[1].Rows.Should().Equal(1); plan[1].RecordIds.Should().Equal("3");
        }
        finally { try { Directory.Delete(dir, true); } catch (IOException) { } }
    }
}
