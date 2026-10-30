using EzForensics.Core.EventLogs;
using EzForensics.Core.Execution;
using FluentAssertions;

namespace EzForensics.Core.Tests;

public class EvtxSelectionTests : IDisposable
{
    private readonly string _dir = Path.Combine(Path.GetTempPath(), "ezevtx_" + Guid.NewGuid().ToString("N"));
    public EvtxSelectionTests() => Directory.CreateDirectory(_dir);
    public void Dispose() { try { Directory.Delete(_dir, true); } catch { } }

    [Fact]
    public void Recommended_loads_78_events_51_default_in_11_groups()
    {
        var s = EvtxSelection.Recommended();
        s.TotalCount.Should().Be(78);
        s.SelectedCount.Should().Be(51);
        s.GroupCount.Should().Be(11);
        EvtxArgs.Summary(s).Should().Be("51 of 78 events · 11 groups");
    }

    [Fact]
    public void Include_list_is_sorted_distinct_ids_of_ticked_events()
    {
        var s = EvtxSelection.Recommended();
        var list = EvtxArgs.IncludeList(s).Split(',').Select(int.Parse).ToList();
        list.Should().BeInAscendingOrder().And.OnlyHaveUniqueItems();
        list.Should().Contain(new[] { 4624, 4625, 1102, 7045, 4104 });
        list.Should().NotContain(4634);   // not a default event
        s.AllEvents.Where(e => e.Selected).Select(e => e.EventId).Distinct().Count().Should().Be(list.Count);
    }

    [Fact]
    public void Apply_appends_inc_only_when_on_and_nothing_user_typed()
    {
        var s = EvtxSelection.Recommended();
        var args = new[] { "-d", @"C:\logs", "--csv", @"C:\out" };
        var on = EvtxArgs.Apply(args, true, s);
        on.Should().HaveCount(6);
        on[4].Should().Be("--inc");
        on[5].Should().Be(EvtxArgs.IncludeList(s));
        EvtxArgs.Apply(args, false, s).Should().BeEquivalentTo(args, o => o.WithStrictOrdering());
        EvtxArgs.Apply(new[] { "-d", "x", "--inc", "4624" }, true, s).Should().HaveCount(4);
        EvtxArgs.Apply(new[] { "-d", "x", "--exc", "4624" }, true, s).Should().HaveCount(4);
    }

    [Fact]
    public void Nothing_ticked_means_no_filter()
    {
        var s = EvtxSelection.Recommended();
        foreach (var e in s.AllEvents) e.Selected = false;
        EvtxArgs.Apply(new[] { "-d", "x" }, true, s).Should().HaveCount(2);
    }

    [Fact]
    public void Export_then_merge_roundtrips_the_selection()
    {
        var s = EvtxSelection.Recommended();
        var first = s.AllEvents.First(e => e.EventId == 4624);
        first.Selected = false;
        s.AllEvents.First(e => e.EventId == 4634).Selected = true;
        var json = s.ToJson();

        var t = EvtxSelection.Recommended();
        t.MergeFrom(json);
        t.AllEvents.Where(e => e.Selected).Select(e => e.Key).Should().BeEquivalentTo(s.AllEvents.Where(e => e.Selected).Select(e => e.Key));
        t.TotalCount.Should().Be(78);
        EvtxArgs.IncludeList(t).Should().Be(EvtxArgs.IncludeList(s));
    }

    [Fact]
    public void Export_has_only_ticked_events_in_the_recommended_schema()
    {
        var s = EvtxSelection.Recommended();
        var doc = System.Text.Json.JsonDocument.Parse(s.ToJson());
        var n = doc.RootElement.GetProperty("groups").EnumerateArray().Sum(g => g.GetProperty("events").GetArrayLength());
        n.Should().Be(51);
        EvtxSelection.Parse(s.ToJson()).AllEvents.Should().OnlyContain(e => e.Selected);
    }

    [Fact]
    public void Import_of_unknown_event_lands_in_imported_group()
    {
        var s = EvtxSelection.Recommended();
        const string json = """{ "groups": [ { "id": "mine", "title": "Mine", "events": [ { "channel": "Application", "provider": "Foo", "eventId": 999, "title": "x", "selected": true } ] } ] }""";
        s.MergeFrom(json);
        s.SelectedCount.Should().Be(1);
        s.TotalCount.Should().Be(79);
        s.Groups.Last().Title.Should().Be(EvtxSelection.ImportedGroupTitle);
        EvtxArgs.IncludeList(s).Should().Be("999");
    }

    [Fact]
    public void Saved_selection_is_stored_and_reloaded_and_reset_restores_defaults()
    {
        var store = new EvtxSelectionStore(Path.Combine(_dir, "sel.json"));
        store.Load().SelectedCount.Should().Be(51);   // no file: recommended
        var s = store.Load();
        s.AllEvents.First(e => e.EventId == 1102).Selected = false;
        store.Save(s);
        var back = store.Load();
        back.SelectedCount.Should().Be(50);
        back.Reset();
        back.SelectedCount.Should().Be(51);
        File.WriteAllText(store.Path, "not json");
        store.Load().SelectedCount.Should().Be(51);   // corrupt file: recommended
    }

    [Fact]
    public void Evidence_scan_counts_known_events_and_groups_the_rest()
    {
        var s = EvtxSelection.Recommended();
        var key = s.AllEvents.First(e => e.EventId == 4624);
        var added = s.AddEvidence(new[]
        {
            new EvtxFoundEvent(key.Channel, key.Provider, 4624, 12),
            new EvtxFoundEvent("Application", "MsiInstaller", 11707, 3),
        });
        added.Should().Be(1);
        key.EvidenceCount.Should().Be(12);
        var other = s.Groups.Single(g => g.Id == EvtxSelection.OtherGroupId);
        other.Events.Should().ContainSingle().Which.EvidenceCount.Should().Be(3);
        other.Events[0].Selected.Should().BeFalse();
        s.GroupCount.Should().Be(11);   // the evidence group is not a recommended group
    }

    private sealed class FakeSource : IEvtxEventSource
    {
        public IEnumerable<(string Channel, string Provider, int EventId)> Read(string path, CancellationToken ct)
        {
            if (path.EndsWith("bad.evtx")) throw new IOException("corrupt");
            yield return ("Security", "P", 4624);
            yield return ("Security", "P", 4624);
            yield return ("System", "Q", 7045);
        }
    }

    [Fact]
    public void Scanner_aggregates_counts_and_reports_bad_files()
    {
        File.WriteAllText(Path.Combine(_dir, "a.evtx"), "");
        Directory.CreateDirectory(Path.Combine(_dir, "sub"));
        File.WriteAllText(Path.Combine(_dir, "sub", "b.evtx"), "");
        File.WriteAllText(Path.Combine(_dir, "bad.evtx"), "");
        var r = EvtxScanner.Scan(_dir, new FakeSource(), null, CancellationToken.None);
        r.FilesScanned.Should().Be(3);
        r.Errors.Should().ContainSingle();
        r.Events.Single(e => e.EventId == 4624).Count.Should().Be(4);
        r.Events.Single(e => e.EventId == 7045).Count.Should().Be(2);
    }

    [Fact]
    public void Scanner_honours_cancellation()
    {
        File.WriteAllText(Path.Combine(_dir, "a.evtx"), "");
        using var cts = new CancellationTokenSource();
        cts.Cancel();
        var act = () => EvtxScanner.Scan(_dir, new FakeSource(), null, cts.Token);
        act.Should().Throw<OperationCanceledException>();
    }

    // ------------------------------------------------------------------ replace / create new

    [Theory]
    [InlineData("EventLogs.csv", true)]
    [InlineData("EventLogs_2.csv", true)]
    [InlineData("20261007123456_EvtxECmd_Output.csv", true)]
    [InlineData("EvtxECmd_Output (2).csv", true)]
    [InlineData("EventLogs.csv.ezstats.json", false)]
    [InlineData("Prefetch.csv", false)]
    [InlineData("EventLogs_Notes.csv", false)]
    public void Recognises_previous_evtx_output(string name, bool expected) =>
        EvtxOutputPolicy.IsEvtxOutput(name).Should().Be(expected);

    [Fact]
    public void Replace_deletes_previous_output_and_sidecars_only()
    {
        File.WriteAllText(Path.Combine(_dir, "EventLogs.csv"), "a");
        File.WriteAllText(Path.Combine(_dir, "EventLogs.csv.ezstats.json"), "{}");
        File.WriteAllText(Path.Combine(_dir, "EventLogs_2.csv"), "b");
        File.WriteAllText(Path.Combine(_dir, "Prefetch.csv"), "c");
        var existing = EvtxOutputPolicy.FindExisting(_dir);
        existing.Should().HaveCount(2);
        EvtxOutputPolicy.Replace(existing);
        Directory.GetFiles(_dir).Select(Path.GetFileName).Should().BeEquivalentTo(new[] { "Prefetch.csv" });
    }

    [Fact]
    public void Create_new_gets_a_numbered_name_and_keeps_the_old_file()
    {
        var p = Path.Combine(_dir, "EventLogs.csv");
        File.WriteAllText(p, "a");
        EvtxOutputPolicy.NewOutputPath(p).Should().Be(Path.Combine(_dir, "EventLogs_2.csv"));
        File.WriteAllText(Path.Combine(_dir, "EventLogs_2.csv"), "b");
        EvtxOutputPolicy.NewOutputPath(p).Should().Be(Path.Combine(_dir, "EventLogs_3.csv"));
        File.ReadAllText(p).Should().Be("a");
    }

    [Fact]
    public void Apply_choice_only_wraps_the_job_for_replace()
    {
        var spec = new JobSpec("EvtxECmd", "EvtxECmd.exe", new[] { "-d", "x" }, _dir, "Event logs", "EventLogs");
        EvtxOutputPolicy.Apply(spec, EvtxRerunChoice.CreateNew).Should().BeSameAs(spec);
        EvtxOutputPolicy.Apply(spec, EvtxRerunChoice.Replace).PreProcess.Should().NotBeNull();
    }
}
