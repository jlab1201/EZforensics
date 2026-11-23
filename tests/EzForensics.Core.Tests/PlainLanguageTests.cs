using System.Text.Json.Nodes;
using EzForensics.Core.Spectre;
using FluentAssertions;

namespace EzForensics.Core.Tests;

public class PlainLanguageTests
{
    [Theory]
    [InlineData("Initial Access", "Initial Access")]
    [InlineData("initial-access", "Initial Access")]
    [InlineData("Execution", "Execution")]
    [InlineData("Persistence", "Persistence")]
    [InlineData("Privilege Escalation", "Privilege Escalation")]
    [InlineData("Defense Evasion", "Defense Evasion")]
    [InlineData("Credential Access", "Credential Access")]
    [InlineData("Discovery", "Discovery")]
    [InlineData("Lateral Movement", "Lateral Movement")]
    [InlineData("Collection", "Collection")]
    [InlineData("Exfiltration", "Exfiltration")]
    [InlineData("Command and Control", "Command and Control")]
    [InlineData("impact", "Impact")]
    [InlineData("Unmapped", "Unmapped")]
    [InlineData("", "Unmapped")]
    [InlineData(null, "Unmapped")]
    public void Tactics(string? tactic, string expected) => PlainLanguage.Tactic(tactic).Should().Be(expected);

    [Theory]
    [InlineData("Command and Control", "T1105", "Command and Control")]
    [InlineData("Command and Control", "T1071.001", "Command and Control")]
    [InlineData("Defense Evasion", null, "Defense Evasion")]
    public void Phase_is_the_tactic_name(string tactic, string? technique, string expected) =>
        PlainLanguage.Phase(tactic, technique).Should().Be(expected);

    [Theory]
    [InlineData("Getting in", "Initial Access")]
    [InlineData("Staying in", "Persistence")]
    [InlineData("Bringing in tools", "Command and Control")]
    [InlineData("Remote control", "Command and Control")]
    [InlineData("Spreading to other computers", "Lateral Movement")]
    [InlineData("Other activity", "Unmapped")]
    [InlineData("Persistence", "Persistence")]
    public void Old_plain_stage_names_read_as_tactic_names(string stored, string expected) =>
        PlainLanguage.MigratePhase(stored).Should().Be(expected);

    [Theory]
    [InlineData("mft", "MFT ($MFT)")]
    [InlineData("evtx", "Windows Event Log")]
    [InlineData("evtx security", "Windows Security Event Log")]
    [InlineData("prefetch", "Prefetch")]
    [InlineData("logfile_record", "Log entry")]
    [InlineData("amcache", "Amcache")]
    [InlineData("shimcache", "Shimcache (AppCompatCache)")]
    [InlineData("registry", "Registry")]
    [InlineData("lnk", "LNK files")]
    [InlineData("jumplist", "Jump Lists")]
    [InlineData("browser", "Browser history")]
    [InlineData("srum", "SRUM")]
    [InlineData("usn_journal", "USN Journal")]
    public void Artifacts(string type, string expected) => PlainLanguage.Artifact(type).Should().Be(expected);

    [Fact]
    public void Paths_in_a_headline_become_their_file_name()
    {
        PlainLanguage.TitleWithoutPaths(@"Run of C:\Users\bob\AppData\evil.exe from /tmp/x/dropper.sh").Should().Be("Run of evil.exe from dropper.sh");
        PlainLanguage.TitleWithoutPaths("No path here").Should().Be("No path here");
    }

    [Fact]
    public void Summary_sentence_handles_zero_one_many_and_undated()
    {
        var a = DateTimeOffset.Parse("2026-01-02T00:00:00Z");
        var b = DateTimeOffset.Parse("2026-03-05T00:00:00Z");
        PlainLanguage.SummarySentence(0, null, null, 0, 0).Should().Be("No key events were found.");
        PlainLanguage.SummarySentence(1, a, a, 1, 0).Should().Be("Spectre found 1 key event on 2 Jan 2026 (UTC).");
        PlainLanguage.SummarySentence(4, a, b, 3, 2).Should().Be("Spectre found 4 key events between 2 Jan 2026 and 5 Mar 2026 (UTC) across 3 hosts. 2 findings have no date.");
        PlainLanguage.SummarySentence(2, null, null, 1, 0).Should().Contain("none has a date").And.Contain("cannot be placed in time");
    }
}

public class SpectreTimelineStoryFieldsTests
{
    private static JsonObject Finding(string id, string title, string? tactic, string artifact, string? start, string? host = "H1", int occ = 1)
    {
        var o = new JsonObject { ["finding_id"] = id, ["title"] = title, ["artifact_type"] = artifact, ["host"] = host, ["occurrence_count"] = occ };
        if (tactic != null) o["tactic"] = tactic;
        if (start != null) o["start"] = start;
        return o;
    }

    [Fact]
    public void Old_engine_fields_fall_back_to_the_glossary()
    {
        var o = new JsonObject
        {
            ["findings"] = new JsonArray(Finding("F1", @"Run of C:\Temp\bad.exe", "Execution", "evtx", "2026-01-02T03:00:00Z", occ: 3), Finding("F2", "Other", null, "mft", "2026-01-05T03:00:00Z", "H2")),
            ["undated"] = new JsonArray(Finding("F3", "Odd", null, "mft", null)),
            ["moments"] = new JsonArray(new JsonObject { ["id"] = "M1", ["rank"] = 1, ["time"] = "2026-01-02T03:00:00Z", ["primary_finding_id"] = "F1", ["finding_ids"] = new JsonArray("F1"),
                ["headline"] = @"Run of C:\Temp\bad.exe", ["reason"] = "First seen here" }),
        };
        var tl = SpectreTimeline.From(o);
        tl.Findings[0].PlainTitle.Should().Be("Run of bad.exe");
        tl.Findings[0].Phase.Should().Be("Execution");
        tl.Findings[1].Phase.Should().Be("Unmapped");
        var m = tl.Moments.Single();
        m.PlainTitle.Should().Be("Run of bad.exe");
        m.Why.Should().Be("First seen here");
        m.CantTell.Should().BeNull();
        m.EvidenceLine.Should().Be("Windows Event Log, 3 events on H1");
        m.Phase.Should().Be("Execution");
        tl.Summary.Sentence.Should().Be("Spectre found 1 key event on 2 Jan 2026 (UTC) across 2 hosts. 1 finding has no date.");
        tl.Summary.Hosts.Should().Be(2);
        tl.Summary.Undated.Should().Be(1);
        tl.AnalysisStale.Should().BeFalse();
    }

    [Fact]
    public void New_engine_fields_win_over_the_fallbacks()
    {
        var o = new JsonObject
        {
            ["findings"] = new JsonArray(new JsonObject
            {
                ["finding_id"] = "F1", ["title"] = "t", ["plain_title"] = "Program started", ["phase"] = "Initial Access", ["tactic"] = "Execution", ["start"] = "2026-01-02T00:00:00Z",
            }),
            ["moments"] = new JsonArray(new JsonObject
            {
                ["id"] = "M1", ["rank"] = 1, ["time"] = "2026-01-02T00:00:00Z", ["headline"] = "h", ["plain_title"] = "Plain", ["why"] = "Because", ["cant_tell"] = "Who ran it",
                ["evidence_line"] = "Ev", ["phase"] = "Persistence",
            }),
            ["summary"] = new JsonObject { ["sentence"] = "Engine says", ["moments"] = 1, ["first_seen"] = "2026-01-02T00:00:00Z", ["last_seen"] = "2026-01-03T00:00:00Z",
                ["hosts"] = new JsonArray("a", "b"), ["need_review"] = 4, ["undated"] = 0, ["minor"] = 2 },
            ["analysis_stale"] = true, ["stale_reason"] = "Re-run",
        };
        var tl = SpectreTimeline.From(o);
        tl.Findings[0].PlainTitle.Should().Be("Program started");
        tl.Findings[0].Phase.Should().Be("Initial Access");
        var m = tl.Moments[0];
        (m.PlainTitle, m.Why, m.CantTell, m.EvidenceLine, m.Phase).Should().Be(("Plain", "Because", "Who ran it", "Ev", "Persistence"));
        tl.Summary.Should().Match<SpectreTimelineSummary>(s => s.Sentence == "Engine says" && s.Hosts == 2 && s.NeedReview == 4 && s.Minor == 2);
        tl.AnalysisStale.Should().BeTrue();
        tl.StaleReason.Should().Be("Re-run");
    }

    [Fact]
    public void Stale_fallback_when_every_finding_is_undated_log_entries()
    {
        var o = new JsonObject
        {
            ["findings"] = new JsonArray(),
            ["undated"] = new JsonArray(Finding("F1", "a", null, "logfile_record", null), Finding("F2", "b", null, "logfile_record", null)),
        };
        var tl = SpectreTimeline.From(o);
        tl.AnalysisStale.Should().BeTrue();
        tl.StaleReason.Should().NotBeEmpty();
        tl.Summary.Sentence.Should().StartWith("No key events");

        var mixed = SpectreTimeline.From(new JsonObject { ["undated"] = new JsonArray(Finding("F1", "a", null, "logfile_record", null), Finding("F2", "b", null, "mft", null)) });
        mixed.AnalysisStale.Should().BeFalse();
        SpectreTimeline.From(new JsonObject()).AnalysisStale.Should().BeFalse();
        SpectreTimeline.From(new JsonObject { ["analysis_stale"] = false, ["undated"] = o["undated"]!.DeepClone() }).AnalysisStale.Should().BeFalse();
    }
}
