using System.Text.Json.Nodes;
using EzForensics.Core.Analysis;
using FluentAssertions;

namespace EzForensics.Core.Tests;

public class AnalysisMigrationTests
{
    private static readonly DateTimeOffset Now = new(2026, 11, 10, 12, 0, 0, TimeSpan.Zero);

    private static JsonObject Row(string csv, int row) => new() { ["csvRelPath"] = csv, ["row"] = row };

    private static JsonObject Item(string id, string kind, Action<JsonObject>? f = null)
    {
        var o = new JsonObject
        {
            ["id"] = id, ["kind"] = kind, ["origin"] = "analyst", ["title"] = "T-" + id, ["severity"] = "High",
            ["mitre"] = new JsonArray("T1059"), ["notes"] = "", ["sourceRows"] = new JsonArray(Row("a/x.csv", 1)),
            ["source"] = new JsonObject(), ["editedFields"] = new JsonArray(), ["history"] = new JsonArray(),
        };
        f?.Invoke(o);
        return o;
    }

    private static JsonNode V1(params JsonObject[] items) => new JsonObject
    {
        ["version"] = 1,
        ["workspace"] = new JsonObject { ["keyEvents"] = new JsonArray(new JsonObject
        {
            ["id"] = "W-1", ["title"] = "WS", ["severity"] = "Low", ["mitre"] = new JsonArray("T1003"), ["notes"] = "nn",
            ["rows"] = new JsonArray(Row("a/x.csv", 7)),
        }) },
        ["items"] = new JsonArray(items.Select(i => (JsonNode)i).ToArray()),
        ["meta"] = new JsonObject(),
    };

    [Fact]
    public void FromV1_WorkspaceKeyEvents_BecomeWorkspaceFindings()
    {
        var a = CaseAnalysisMigration.FromV1(V1(), Now);
        a.Version.Should().Be(2);
        var w = a.Workspace.Findings.Should().ContainSingle().Subject;
        w.Id.Should().Be("W-1"); w.Title.Should().Be("WS"); w.Severity.Should().Be("Low");
        w.Mitre.Should().Equal("T1003"); w.Notes.Should().Be("nn"); w.Rows.Should().ContainSingle().Which.Row.Should().Be(7);
    }

    [Fact]
    public void FromV1_KeyEventItem_StaysFinding_AndGetsOwnKeyEvent()
    {
        var a = CaseAnalysisMigration.FromV1(V1(Item("I-1", "key_event")), Now);
        a.Items.Should().ContainSingle().Which.Id.Should().Be("I-1");
        var k = a.KeyEvents.Should().ContainSingle().Subject;
        k.FindingIds.Should().Equal("I-1"); k.Title.Should().Be("T-I-1"); k.EditedFields.Should().BeEmpty();
    }

    [Fact]
    public void FromV1_NotImportant_BecomesConfirmed_WithoutKeyEvent()
    {
        var a = CaseAnalysisMigration.FromV1(V1(Item("I-1", "key_event", o => o["review"] = "not_important")), Now);
        a.Items.Single().Review.Should().Be(ReviewState.Confirmed);
        a.KeyEvents.Should().BeEmpty();
    }

    private static JsonObject Group(string id, Action<JsonObject>? f = null) =>
        Item(id, "flag", o => { o["source"] = new JsonObject { ["csvRelPath"] = "a/x.csv", ["label"] = "suspicious" }; f?.Invoke(o); });

    [Fact]
    public void FromV1_FlagGroupWithoutEdits_IsDropped() =>
        CaseAnalysisMigration.FromV1(V1(Group("I-1")), Now).Items.Should().BeEmpty();

    [Fact]
    public void FromV1_FlagGroupWithEdits_BecomesAnalystFinding()
    {
        var a = CaseAnalysisMigration.FromV1(V1(Group("I-1", o => o["editedFields"] = new JsonArray("title"))), Now);
        var i = a.Items.Should().ContainSingle().Subject;
        i.Id.Should().Be("I-1"); i.MigratedFrom.Should().Be("flag-group"); i.Origin.Should().Be(ItemOrigin.Analyst);
        a.KeyEvents.Should().BeEmpty();
    }

    [Fact]
    public void FromV1_SpectreFlagItem_StaysFinding_NoKeyEvent()
    {
        var a = CaseAnalysisMigration.FromV1(V1(Item("I-1", "flag", o => { o["origin"] = "spectre"; o["source"] = new JsonObject { ["spectreFindingId"] = "F-1" }; })), Now);
        a.Items.Should().ContainSingle().Which.Source.SpectreFindingId.Should().Be("F-1");
        a.KeyEvents.Should().BeEmpty();
    }

    [Fact]
    public void FromV1_KeepsDuplicateAndTombstoneState()
    {
        var a = CaseAnalysisMigration.FromV1(V1(
            Item("A", "key_event"),
            Item("B", "key_event", o => { o["duplicateOf"] = "A"; o["dupDecisions"] = new JsonArray(new JsonObject { ["otherId"] = "A", ["decision"] = "keep_both", ["rowSetHash"] = "h" }); }),
            Item("C", "key_event", o => { o["mergedInto"] = "A"; o["sourceStatus"] = "removed_at_source"; o["keptByUser"] = true; }),
            Item("D", "key_event", o => o["dismissed"] = true)), Now);
        a.Find("B")!.DuplicateOf.Should().Be("A");
        a.Find("B")!.DupDecisions.Should().ContainSingle().Which.RowSetHash.Should().Be("h");
        var c = a.Find("C")!;
        c.MergedInto.Should().Be("A"); c.SourceStatus.Should().Be(SourceStatus.RemovedAtSource); c.KeptByUser.Should().BeTrue();
        a.Find("D")!.Dismissed.Should().BeTrue();
    }

    private static string WriteV1(string root)
    {
        var path = CaseAnalysisStore.PathFor(root);
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        File.WriteAllText(path, V1(Item("I-1", "key_event")).ToJsonString());
        return path;
    }

    [Fact]
    public void Open_V1File_WritesBackup_AndSavesV2()
    {
        using var d = new AnTempDir();
        var path = WriteV1(d.Path);
        var s = CaseAnalysisStore.Open(d.Path, Path.Combine(d.Path, "fb"));
        s.Data.Version.Should().Be(2);
        s.Data.KeyEvents.Should().ContainSingle();
        File.Exists(Path.Combine(Path.GetDirectoryName(path)!, "analysis.v1.json")).Should().BeTrue();
        JsonNode.Parse(File.ReadAllText(path))!["version"]!.GetValue<int>().Should().Be(2);
    }

    [Fact]
    public void Open_V1File_SecondOpen_DoesNotOverwriteBackup()
    {
        using var d = new AnTempDir();
        var path = WriteV1(d.Path);
        var backup = Path.Combine(Path.GetDirectoryName(path)!, "analysis.v1.json");
        CaseAnalysisStore.Open(d.Path, Path.Combine(d.Path, "fb"));
        File.WriteAllText(backup, "SENTINEL");
        File.WriteAllText(path, V1(Item("I-2", "key_event")).ToJsonString());   // a v1 file again
        CaseAnalysisStore.Open(d.Path, Path.Combine(d.Path, "fb"));
        File.ReadAllText(backup).Should().Be("SENTINEL");
    }
}
