using System.Text.Json.Nodes;
using EzForensics.Core.Spectre;
using FluentAssertions;

namespace EzForensics.Core.Tests;

public class StoryGraphicsTests
{
    private static JsonObject F(string id, string tactic, string sev, string? host = null, string? technique = null)
    {
        var o = new JsonObject { ["id"] = id, ["title"] = "t " + id, ["severity"] = sev, ["tactic"] = tactic };
        if (host != null) o["host"] = host;
        if (technique != null) o["technique"] = technique;
        return o;
    }

    /// <summary>All undated (the user's real case has no dates).</summary>
    private static SpectreTimeline Tl(JsonObject[] findings, params (string id, string primary)[] moments)
    {
        var u = new JsonArray(); foreach (var f in findings) u.Add(f);
        var m = new JsonArray(); int r = 1;
        foreach (var (id, p) in moments) m.Add(new JsonObject { ["id"] = id, ["rank"] = r++, ["headline"] = "h", ["primary_finding_id"] = p, ["finding_ids"] = new JsonArray(p) });
        return SpectreTimeline.From(new JsonObject { ["undated"] = u, ["moments"] = m });
    }

    [Fact]
    public void Stages_count_findings_and_moments_with_max_severity_without_dates()
    {
        var tl = Tl(new[] { F("a", "Execution", "Medium"), F("b", "Execution", "High"), F("c", "Persistence", "Low"), F("d", "Command and Control", "Critical", technique: "T1105") },
                    ("m1", "b"), ("m2", "d"));
        var s = StoryGraphics.Stages(tl.Findings.Concat(tl.Undated), tl.Moments);
        s.Should().HaveCount(StoryGraphics.StageOrder.Count);
        var run = s.Single(x => x.Stage == "Execution");
        run.Findings.Should().Be(2); run.Moments.Should().Be(1); run.Severity.Should().Be("High");
        s.Single(x => x.Stage == "Persistence").Severity.Should().Be("Low");
        s.Single(x => x.Stage == "Command and Control").Severity.Should().Be("Critical");
        s.Single(x => x.Stage == "Exfiltration").HasEvidence.Should().BeFalse();
        s.Single(x => x.Stage == "Initial Access").Severity.Should().Be("");
    }

    [Fact]
    public void Strip_keeps_lit_stages_and_neighbours_and_folds_the_rest()
    {
        var tl = Tl(new[] { F("a", "Execution", "Medium"), F("b", "Impact", "High") });
        var strip = StoryGraphics.Strip(StoryGraphics.Stages(tl.Undated, tl.Moments));
        strip.Select(i => i.IsGap ? "…" : i.Stage!.Stage).Should().Equal(
            "Initial Access", "Execution", "Persistence", "…", "Exfiltration", "Impact");
        strip[3].Hidden.Should().Contain("Discovery");
    }

    [Fact]
    public void Strip_drops_neighbours_when_too_many()
    {
        var tl = Tl(new[] { F("a", "Initial Access", "Low"), F("b", "Persistence", "Low"), F("c", "Defense Evasion", "Low"), F("d", "Discovery", "Low"), F("e", "Collection", "Low"), F("f", "Exfiltration", "Low") });
        var strip = StoryGraphics.Strip(StoryGraphics.Stages(tl.Undated, tl.Moments), maxItems: 9);
        strip.Where(i => !i.IsGap).Should().OnlyContain(i => i.Stage!.HasEvidence);
        strip.Count(i => !i.IsGap).Should().Be(6);
    }

    [Fact]
    public void Strip_is_empty_without_evidence_and_shows_other_activity_last()
    {
        StoryGraphics.Strip(StoryGraphics.Stages(Array.Empty<SpectreTimelineFinding>(), Array.Empty<SpectreMoment>())).Should().BeEmpty();
        var tl = Tl(new[] { F("a", "Execution", "Low"), F("b", "", "Info") });
        StoryGraphics.Strip(StoryGraphics.Stages(tl.Undated, tl.Moments)).Last().Stage!.Stage.Should().Be(PlainLanguage.OtherActivity);
    }

    [Fact]
    public void Hosts_group_by_computer_most_severe_first()
    {
        var tl = Tl(new[] { F("a", "Execution", "Low", "PC-2"), F("b", "Execution", "High", "pc-1"), F("c", "Execution", "Medium", "PC-1"), F("d", "Execution", "Info") },
                    ("m1", "b"));
        var h = StoryGraphics.Hosts(tl.Undated, tl.Moments);
        h.Select(x => x.Host).Should().Equal("pc-1", "PC-2", StoryGraphics.NoHost);
        h[0].Findings.Should().Be(2); h[0].Moments.Should().Be(1); h[0].Severity.Should().Be("High");
        h[0].BySeverity[SpectreFinding.Rank("Medium")].Should().Be(1);
        StoryGraphics.MomentHost(tl.Moments[0], StoryGraphics.ById(tl.Undated)).Should().Be("pc-1");
    }

    [Fact]
    public void Matrix_crosses_computers_with_stages_that_have_evidence()
    {
        var tl = Tl(new[]
        {
            F("a", "Execution", "Low", "PC-2"), F("b", "Execution", "High", "pc-1"), F("c", "Persistence", "Medium", "PC-1"),
            F("d", "Credential Access", "Critical", "PC-2"), F("e", "Execution", "Medium", "PC-2"),
        }, ("m1", "b"), ("m2", "d"));
        var mx = StoryGraphics.Matrix(tl.Undated, tl.Moments);
        mx.IsEmpty.Should().BeFalse();
        mx.Hosts.Select(h => h.Host).Should().Equal("PC-2", "pc-1");   // most severe first
        mx.Stages.Select(s => s.Stage).Should().Equal("Execution", "Persistence", "Credential Access");   // reference order, only stages with evidence
        mx.Cells.Should().HaveCount(2).And.OnlyContain(r => r.Count == 3);

        var pc2Run = mx.Cells[0][0];
        pc2Run.Host.Should().Be("PC-2"); pc2Run.Stage.Should().Be("Execution");
        pc2Run.Findings.Should().Be(2); pc2Run.Severity.Should().Be("Medium"); pc2Run.Count.Should().Be(2);
        mx.Cells[0][1].HasEvidence.Should().BeFalse();
        mx.Cells[0][1].Severity.Should().Be("");
        mx.Cells[0][2].Moments.Should().Be(1); mx.Cells[0][2].Severity.Should().Be("Critical");
        mx.Cells[1][0].Findings.Should().Be(1); mx.Cells[1][0].Moments.Should().Be(1); mx.Cells[1][0].Severity.Should().Be("High");
        mx.Cells[1][1].Severity.Should().Be("Medium");   // host names match whatever the case
        mx.Cells.SelectMany(r => r).Sum(c => c.Findings).Should().Be(5);
    }

    [Fact]
    public void Matrix_is_empty_without_findings_and_puts_findings_without_a_computer_on_their_own_row()
    {
        StoryGraphics.Matrix(Array.Empty<SpectreTimelineFinding>(), Array.Empty<SpectreMoment>()).IsEmpty.Should().BeTrue();
        var tl = Tl(new[] { F("a", "Execution", "Low") });
        var mx = StoryGraphics.Matrix(tl.Undated, tl.Moments);
        mx.Hosts.Single().Host.Should().Be(StoryGraphics.NoHost);
        mx.Cells[0][0].Count.Should().Be(1);
    }

    [Fact]
    public void Per_host_severity_counts_add_up_to_the_host_total_and_columns_skip_empty_levels_highest_first()
    {
        var tl = Tl(new[]
        {
            F("a", "Execution", "Low", "PC-2"), F("b", "Execution", "High", "pc-1"), F("c", "Persistence", "Medium", "PC-1"),
            F("d", "Credential Access", "Critical", "PC-2"), F("e", "Execution", "Medium", "PC-2"), F("f", "Execution", "High", "PC-2"),
        }, ("m1", "b"));
        var mx = StoryGraphics.Matrix(tl.Undated, tl.Moments);
        foreach (var h in mx.Hosts) h.BySeverity.Sum().Should().Be(h.Findings);
        var pc2 = mx.Hosts.Single(h => h.Host == "PC-2");
        pc2.BySeverity[SpectreFinding.Rank("Critical")].Should().Be(1);
        pc2.BySeverity[SpectreFinding.Rank("High")].Should().Be(1);
        pc2.BySeverity[SpectreFinding.Rank("Low")].Should().Be(1);
        var cols = StoryGraphics.SeverityColumns(mx.Hosts);
        cols.Select(r => SpectreFinding.Severities[r]).Should().Equal("Critical", "High", "Medium", "Low");   // no Info findings: skipped
    }

    [Fact]
    public void Matrix_cells_carry_counts_only_no_severity_is_needed_to_read_them()
    {
        var tl = Tl(new[] { F("a", "Execution", "Low", "PC"), F("b", "Execution", "Critical", "PC") });
        var cell = StoryGraphics.Matrix(tl.Undated, tl.Moments).Cells[0][0];
        cell.Count.Should().Be(2);   // the Tactic tile shows this count, not the highest severity
    }

    [Fact]
    public void Every_stage_has_a_glyph()
    {
        StoryGraphics.StageOrder.Select(StoryGraphics.Glyph).Should().OnlyHaveUniqueItems();
        StoryGraphics.StageOf("nonsense").Should().Be(PlainLanguage.OtherActivity);
    }

    [Fact]
    public void Glyph_ignores_case()
    {
        StoryGraphics.Glyph("execution").Should().Be(StoryGraphics.Glyph("Execution"));
        StoryGraphics.Glyph("COMMAND AND CONTROL").Should().Be(StoryGraphics.Glyph("Command and Control"));
    }

    [Fact]
    public void Icon_follows_the_technique_when_its_tactic_is_shown_else_the_tactic_glyph()
    {
        var c2 = StoryGraphics.Glyph("Command and Control");
        var download = StoryGraphics.Icon("Command and Control", new[] { "T1105" });
        download.Should().NotBe(c2);
        StoryGraphics.Icon("Command and Control", new[] { "T1027, T1059.001, T1105" }).Should().Be(download);   // list text, first match with an icon here
        StoryGraphics.Icon("Execution", new[] { "T1105" }).Should().Be(StoryGraphics.Glyph("Execution"));       // not its tactic
        StoryGraphics.Icon("Command and Control", null).Should().Be(c2);
        StoryGraphics.Icon("Command and Control", new[] { "T9999" }).Should().Be(c2);
        StoryGraphics.Icon("Execution", new[] { "T1053.005" }).Should().NotBe(StoryGraphics.Glyph("Execution"));
        StoryGraphics.Icon("Persistence", new[] { "T1547.001" }).Should().NotBe(StoryGraphics.Glyph("Persistence"));
    }

    [Fact]
    public void SceneKey_picks_the_download_scene_only_for_T1105_under_command_and_control()
    {
        StoryGraphics.SceneKey("Command and Control", new[] { "T1105" }).Should().Be("Ingress Tool Transfer");
        StoryGraphics.SceneKey("command and control", new[] { "T1071, T1105" }).Should().Be("Ingress Tool Transfer");
        StoryGraphics.SceneKey("Command and Control", new[] { "T1071" }).Should().Be("Command and Control");
        StoryGraphics.SceneKey("Execution", new[] { "T1105" }).Should().Be("Execution");
        StoryGraphics.SceneKey(null, null).Should().Be(PlainLanguage.OtherActivity);
    }

    [Fact]
    public void Radar_has_the_12_tactics_in_kill_chain_order_counts_per_host_and_apart_the_unmapped()
    {
        var tl = Tl(new[]
        {
            F("a", "Execution", "Medium", "H1"), F("b", "execution", "High", "H1"), F("c", "Persistence", "Low", "H2"),
            F("d", "Impact", "Critical", "H2"), F("e", "", "Info", "H2"), F("f", "Something odd", "Info", "H1"),
        });
        var all = tl.Findings.Concat(tl.Undated).ToList();
        var r = StoryGraphics.Radar(all);
        r.Axes.Should().Equal("Initial Access", "Execution", "Persistence", "Privilege Escalation", "Defense Evasion", "Credential Access", "Discovery",
            "Lateral Movement", "Collection", "Command and Control", "Exfiltration", "Impact");
        r.Axes.Should().NotContain(PlainLanguage.OtherActivity);
        r.Counts.Should().Equal(0, 2, 1, 0, 0, 0, 0, 0, 0, 0, 0, 1);
        r.Unmapped.Should().Be(2);
        r.Max.Should().Be(2);
        r.CountOf("execution").Should().Be(2);

        var h1 = StoryGraphics.Radar(all, "H1");
        h1.Axes.Should().Equal(r.Axes);
        h1.Counts.Should().Equal(0, 2, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0);
        h1.Unmapped.Should().Be(1);
        StoryGraphics.Radar(all, "H2").Counts.Should().Equal(0, 0, 1, 0, 0, 0, 0, 0, 0, 0, 0, 1);
        StoryGraphics.Radar(all, "nobody").Total.Should().Be(0);
    }

    [Fact]
    public void A_moment_lists_each_technique_id_once_even_when_its_primary_finding_is_also_in_its_list()
    {
        var f = new JsonObject { ["id"] = "f1", ["title"] = "t", ["start"] = "2026-03-14T09:30:12Z", ["technique"] = "T1078, T1566.001" };
        var g = new JsonObject { ["id"] = "f2", ["title"] = "t", ["start"] = "2026-03-14T09:31:12Z", ["technique"] = "T1078" };
        var tl = SpectreTimeline.From(new JsonObject
        {
            ["findings"] = new JsonArray(f, g),
            ["moments"] = new JsonArray(new JsonObject { ["id"] = "m", ["rank"] = 1, ["headline"] = "h", ["time"] = "2026-03-14T09:30:12Z", ["primary_finding_id"] = "f1", ["finding_ids"] = new JsonArray("f1", "f2") }),
        });
        var byId = tl.Findings.ToDictionary(x => x.Id);
        StoryGraphics.MomentTechniques(tl.Moments[0], byId).Should().Equal("T1078", "T1566.001");
    }
}
