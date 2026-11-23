using System.Text.Json.Nodes;
using EzForensics.Core.Assistant;
using EzForensics.Core.Execution;
using EzForensics.Core.Spectre;
using FluentAssertions;

namespace EzForensics.Core.Tests;

public class SpectreTimelineTests
{
    private sealed class FakeCaller : ISpectreToolCaller
    {
        public readonly List<(string Tool, JsonObject Args)> Calls = new();
        public Func<string, JsonObject, CancellationToken, Task<McpToolResult>> Handler = (_, _, _) => Task.FromResult(new McpToolResult(false, "{}"));
        public Task<McpToolResult> CallAsync(string tool, JsonObject args, CancellationToken ct)
        {
            lock (Calls) Calls.Add((tool, (JsonObject)args.DeepClone()));
            return Handler(tool, args, ct);
        }
    }

    private const string TimelineJson = """
        {"case":"c1","generated_at":"2026-01-02T05:00:00Z","time_basis":"UTC","extra_field":1,
         "lanes":{"tactic":["Execution","Persistence"],"host":["H1"],"artifact":["evtx","prefetch"]},
         "findings":[
          {"id":"1","finding_id":"F-1","title":"A","severity":"High","confidence":"High","tactic":"Execution","technique":"T1059","host":"H1","artifact_type":"evtx",
           "start":"2026-01-02T03:00:00Z","end":"2026-01-02T03:00:00Z","occurrence_count":3,"cluster_ids":["C1"],"approval":null,"include_in_report":true,"edited":false,
           "key_moment":"pin","source_file":"H1/EventLogs/x.csv","source_record_id":"42","case_path":"H1/EventLogs/x.csv"},
          {"id":"2","finding_id":"F-2","title":"B","severity":"Low","tactic":"Execution","host":"H1","artifact_type":"evtx","start":"2026-01-02T03:00:03Z"},
          {"id":"3","finding_id":"F-3","title":"C","severity":"Critical","tactic":"Persistence","host":"H1","artifact_type":"prefetch",
           "start":"2026-01-02T03:10:00Z","end":"2026-01-02T03:20:00Z"},
          {"id":"4","finding_id":"F-4","title":"D","severity":"Info","tactic":"Persistence","start":"02/03/2026 10:00"},
          {"id":"5","finding_id":"F-5","title":"E","severity":"Medium","start":"2026-01-02T03:30:00+02:00"}
         ],
         "undated":[{"id":"6","finding_id":"F-6","title":"F","severity":"Low"}],
         "clusters":[{"id":"C1","kind":"temporal","start":"2026-01-02T03:00:00Z","end":"2026-01-02T03:01:00Z","finding_ids":["F-1","F-2"],"label":"Burst"}],
         "moments":[
          {"id":"M2","rank":2,"time":"2026-01-02T03:10:00Z","primary_finding_id":"F-3","finding_ids":["F-3"],"headline":"Second","reason_kind":"high","reason":"High","caption":null,"pinned":false,"score":7},
          {"id":"M1","rank":1,"time":"2026-01-02T03:00:00Z","primary_finding_id":"F-1","finding_ids":["F-1","F-2"],"headline":"First","reason_kind":"burst","reason":"Burst","caption":"mine","pinned":true,"score":9.5}
         ]}
        """;

    private static SpectreTimeline Timeline() => SpectreTimeline.From((JsonObject)JsonNode.Parse(TimelineJson)!);

    // ---------- client / parsing ----------

    [Fact]
    public async Task Timeline_parses_the_engine_json()
    {
        var c = new FakeCaller { Handler = (_, _, _) => Task.FromResult(new McpToolResult(false, TimelineJson)) };
        var tl = await new SpectreFindingsClient(c, null, null).TimelineAsync("c1", default);
        c.Calls.Single().Tool.Should().Be("spectre_timeline");
        c.Calls.Single().Args["case_id"]!.GetValue<string>().Should().Be("c1");

        tl.TimeBasis.Should().Be("UTC");
        tl.GeneratedAt.Should().Be(new DateTimeOffset(2026, 1, 2, 5, 0, 0, TimeSpan.Zero));
        tl.TacticLanes.Should().Equal("Execution", "Persistence");
        tl.Findings.Should().HaveCount(5);
        tl.Undated.Should().ContainSingle().Which.FindingId.Should().Be("F-6");

        var f = tl.Findings[0];
        f.Start.Should().Be(new DateTimeOffset(2026, 1, 2, 3, 0, 0, TimeSpan.Zero));
        f.OccurrenceCount.Should().Be(3);
        f.ClusterIds.Should().Equal("C1");
        f.KeyMoment.Should().Be("pin");
        f.SourceFile.Should().Be("H1/EventLogs/x.csv");
        f.SourceRecordId.Should().Be("42");
        f.CasePath.Should().Be("H1/EventLogs/x.csv");
        tl.Findings[1].CasePath.Should().BeNull();
        tl.Findings[1].KeyMoment.Should().Be("auto");        // missing optional -> default
        tl.Findings[1].OccurrenceCount.Should().Be(1);
        tl.Findings[2].IsRange.Should().BeTrue();
        tl.Findings[3].Start.Should().BeNull();               // US-style date is never guessed
        tl.Findings[4].Start.Should().Be(new DateTimeOffset(2026, 1, 2, 1, 30, 0, TimeSpan.Zero)); // offset converted to UTC

        tl.Clusters.Should().ContainSingle().Which.FindingIds.Should().Equal("F-1", "F-2");
        tl.Moments.Select(m => m.Id).Should().Equal("M1", "M2"); // by time order
        tl.Moments[0].Pinned.Should().BeTrue();
        tl.Moments[0].Score.Should().Be(9.5);
        tl.Moments[0].Caption.Should().Be("mine");
        tl.Moments[1].Caption.Should().BeNull();
    }

    [Fact]
    public void Moment_numbers_follow_time_order_with_rank_tiebreak_and_undated_last()
    {
        var json = """
            {"moments":[
              {"id":"A","rank":1,"time":"2026-01-02T09:00:00Z","headline":"a"},
              {"id":"B","rank":2,"time":null,"headline":"b"},
              {"id":"C","rank":3,"time":"2026-01-02T03:00:00Z","headline":"c"},
              {"id":"D","rank":5,"time":"2026-01-02T06:00:00Z","headline":"d"},
              {"id":"E","rank":4,"time":"2026-01-02T06:00:00Z","headline":"e"},
              {"id":"F","rank":6,"headline":"f"}
            ]}
            """;
        var tl = SpectreTimeline.From((JsonObject)JsonNode.Parse(json)!);
        tl.Moments.Select(m => m.Id).Should().Equal("C", "E", "D", "A", "B", "F");
        tl.Moments.Select(m => m.Number).Should().Equal(1, 2, 3, 4, 5, 6);
        tl.Moments.Single(m => m.Id == "A").Rank.Should().Be(1); // importance kept
        tl.Moments.Single(m => m.Id == "E").Rank.Should().Be(4);
    }

    [Fact]
    public async Task Timeline_of_an_empty_object_is_empty_and_garbage_throws()
    {
        var c = new FakeCaller { Handler = (_, _, _) => Task.FromResult(new McpToolResult(false, "{}")) };
        var tl = await new SpectreFindingsClient(c, null, null).TimelineAsync("c1", default);
        tl.Findings.Should().BeEmpty();
        tl.Moments.Should().BeEmpty();
        c.Handler = (_, _, _) => Task.FromResult(new McpToolResult(false, "not json"));
        var act = () => new SpectreFindingsClient(c, null, null).TimelineAsync("c1", default);
        await act.Should().ThrowAsync<SpectreToolException>();
    }

    [Fact]
    public async Task MomentExplain_sends_refresh_and_parses_the_reply()
    {
        var c = new FakeCaller { Handler = (_, _, _) => Task.FromResult(new McpToolResult(false, """{"moment_id":"M1","text":"Read.","cites":["F-1","F-2"],"cached":true}""")) };
        var r = await new SpectreFindingsClient(c, null, null).MomentExplainAsync("c1", "M1", true, default);
        c.Calls.Single().Tool.Should().Be("spectre_moment_explain");
        c.Calls.Single().Args["moment_id"]!.GetValue<string>().Should().Be("M1");
        c.Calls.Single().Args["refresh"]!.GetValue<bool>().Should().BeTrue();
        r.Text.Should().Be("Read.");
        r.Cites.Should().Equal("F-1", "F-2");
        r.Cached.Should().BeTrue();
    }

    [Fact]
    public async Task List_reads_source_fields_and_analyst_moment_fields_and_edit_sends_them()
    {
        var c = new FakeCaller { Handler = (_, _, _) => Task.FromResult(new McpToolResult(false,
            """{"findings":[{"finding_id":"F-1","source_file":"a.csv","source_record_id":"7","analyst":{"key_moment":"pin","moment_caption":"cap"}}]}""")) };
        var f = (await new SpectreFindingsClient(c, null, null).ListAsync("c1", default)).Findings.Single();
        f.SourceFile.Should().Be("a.csv");
        f.SourceRecordId.Should().Be("7");
        f.Analyst!.KeyMoment.Should().Be("pin");
        f.Analyst.MomentCaption.Should().Be("cap");
        f.IsEdited.Should().BeTrue();

        var ch = new SpectreFindingChanges().KeyMoment("unpin").MomentCaption(null);
        var json = ch.ToJson();
        json["key_moment"]!.GetValue<string>().Should().Be("unpin");
        json.ContainsKey("moment_caption").Should().BeTrue();
        ch.Reverts("moment_caption").Should().BeTrue();
    }

    // ---------- layout ----------

    private static readonly DateTimeOffset T0 = new(2026, 1, 2, 3, 0, 0, TimeSpan.Zero);

    [Fact]
    public void Layout_places_lanes_items_bars_and_bands()
    {
        var r = FindingsTimelineLayout.Compute(Timeline(), TimelineLaneMode.Tactic, new TimeRange(T0, T0.AddHours(1)), 1000, 40);
        r.Lanes.Select(l => l.Name).Should().Equal("Execution", "Persistence", "(none)"); // F-5 has no tactic
        r.Lanes[1].Y.Should().Be(40);
        var bar = r.Items.Single(i => i.IsRange);
        bar.FindingId.Should().Be("3");
        bar.X.Should().BeApproximately(1000 * 10 / 60.0, 0.01);
        bar.X2.Should().BeApproximately(1000 * 20 / 60.0, 0.01);
        bar.Y.Should().Be(60);
        r.Bands.Should().ContainSingle().Which.Should().Match<LayoutBand>(b => b.Count == 2 && b.X2 > b.X1);
        r.TotalHeight.Should().Be(120);
        // F-5 (01:30 UTC) is outside the window; F-4 has no usable time
        r.Items.Concat(Array.Empty<LayoutItem>()).Select(i => i.FindingId).Should().NotContain("4").And.NotContain("5");
    }

    [Fact]
    public void Layout_collapses_close_items_into_a_bubble_and_expands_on_zoom()
    {
        var wide = FindingsTimelineLayout.Compute(Timeline(), TimelineLaneMode.Tactic, new TimeRange(T0, T0.AddHours(1)), 1000, 40);
        var b = wide.Bubbles.Should().ContainSingle().Subject; // F-1 and F-2 are 3 s apart = 0.8 px
        b.Count.Should().Be(2);
        b.FindingIds.Should().BeEquivalentTo("1", "2");
        b.MaxSeverity.Should().Be("High");
        b.From.Should().Be(T0);
        b.To.Should().Be(T0.AddSeconds(3));
        wide.Items.Select(i => i.FindingId).Should().NotContain(new[] { "1", "2" });

        var zoomed = FindingsTimelineLayout.Compute(Timeline(), TimelineLaneMode.Tactic, new TimeRange(T0.AddSeconds(-1), T0.AddSeconds(5)), 1000, 40);
        zoomed.Bubbles.Should().BeEmpty();
        zoomed.Items.Select(i => i.FindingId).Should().Contain(new[] { "1", "2" });
    }

    [Fact]
    public void Layout_lane_modes_use_host_and_artifact()
    {
        var r = FindingsTimelineLayout.Compute(Timeline(), TimelineLaneMode.Artifact, new TimeRange(T0, T0.AddHours(1)), 1000, 30);
        r.Lanes.Select(l => l.Name).Should().Equal("evtx", "prefetch", "(none)");
    }

    [Fact]
    public void Pins_use_rows_so_captions_do_not_overlap()
    {
        var ms = new[]
        {
            Moment("A", 1, T0.AddMinutes(1), "A long caption for the first moment"),
            Moment("B", 2, T0.AddMinutes(1.5), "Another long caption right next to it"),
            Moment("C", 3, T0.AddMinutes(50), "Far away"),
        };
        var tl = SpectreTimeline.Empty with { Moments = ms };
        var r = FindingsTimelineLayout.Compute(tl, TimelineLaneMode.Tactic, new TimeRange(T0, T0.AddHours(1)), 1000, 40);
        r.Pins.Select(p => p.MomentId).Should().Equal("A", "B", "C");
        r.Pins[0].Row.Should().Be(0);
        r.Pins[1].Row.Should().Be(1);
        r.Pins[2].Row.Should().Be(0);
        r.PinRows.Should().Be(2);
        // no two pins in the same row overlap
        foreach (var g in r.Pins.GroupBy(p => p.Row))
            g.OrderBy(p => p.X).Zip(g.OrderBy(p => p.X).Skip(1), (a, b) => a.X + a.LabelWidth <= b.X).Where(ok => !ok).Should().BeEmpty();
        r.Pins[0].Label.Should().Contain("first moment");
    }

    private static SpectreMoment Moment(string id, int rank, DateTimeOffset t, string caption) =>
        new(id, rank, t, null, Array.Empty<string>(), "h", null, null, caption, false, 1);

    [Fact]
    public void Ticks_pick_a_step_of_at_least_80px_and_align_to_utc()
    {
        var hour = FindingsTimelineLayout.Ticks(new TimeRange(T0, T0.AddHours(1)), 1000);
        hour.Should().HaveCountGreaterThan(5);
        hour.Zip(hour.Skip(1), (a, b) => b.X - a.X).Should().OnlyContain(d => d >= 80 && d <= 200);
        hour.Should().OnlyContain(t => t.Time.Minute % 5 == 0 && t.Time.Second == 0);
        hour[0].Label.Should().Be(hour[0].Time.ToString("HH:mm"));

        var secs = FindingsTimelineLayout.Ticks(new TimeRange(T0, T0.AddSeconds(30)), 1000);
        secs[0].Label.Should().MatchRegex(@"^\d\d:\d\d:\d\d$");

        var days = FindingsTimelineLayout.Ticks(new TimeRange(T0, T0.AddDays(30)), 1000);
        days.Should().OnlyContain(t => t.Time.TimeOfDay == TimeSpan.Zero);
        days[0].Label.Should().MatchRegex(@"^[A-Z][a-z]{2} \d\d$");

        var months = FindingsTimelineLayout.Ticks(new TimeRange(T0, T0.AddDays(900)), 1000);
        months.Should().OnlyContain(t => t.Time.Day == 1);
        months.Zip(months.Skip(1), (a, b) => b.X - a.X).Should().OnlyContain(d => d >= 80);

        // a midnight crossing inside an hours view is a major tick labelled with the date
        var cross = FindingsTimelineLayout.Ticks(new TimeRange(T0.AddHours(18), T0.AddHours(30)), 1000);
        cross.Should().Contain(t => t.Major && t.Time.Hour == 0 && t.Label == "Jan 03");
    }

    [Fact]
    public void Fit_zoom_and_pan()
    {
        var tl = Timeline();
        var fit = FindingsTimelineLayout.FitRange(tl)!;
        fit.From.Should().BeBefore(new DateTimeOffset(2026, 1, 2, 1, 30, 0, TimeSpan.Zero)); // earliest dated finding is F-5 at 01:30Z
        fit.To.Should().BeAfter(T0.AddMinutes(20));
        FindingsTimelineLayout.FitRange(SpectreTimeline.Empty).Should().BeNull();

        var single = SpectreTimeline.Empty with { Findings = new[] { tl.Findings[0] } };
        FindingsTimelineLayout.FitRange(single)!.Duration.Should().BeGreaterThan(TimeSpan.FromMinutes(30)); // zero span gets a default window

        var range = new TimeRange(T0, T0.AddHours(1));
        var anchor = T0.AddMinutes(15);
        var z = FindingsTimelineLayout.Zoom(range, anchor, 2);
        z.Duration.Should().Be(TimeSpan.FromMinutes(30));
        ((anchor - z.From).TotalMinutes / z.Duration.TotalMinutes).Should().BeApproximately(0.25, 1e-9); // anchor keeps its screen position
        FindingsTimelineLayout.Zoom(z, anchor, 0.5).Duration.Should().Be(TimeSpan.FromHours(1));
        FindingsTimelineLayout.Zoom(range, anchor, 1e12).Duration.Should().Be(FindingsTimelineLayout.MinSpan);

        var p = FindingsTimelineLayout.Pan(range, 0.5);
        p.From.Should().Be(T0.AddMinutes(30));
        var d = FindingsTimelineLayout.PanByPixels(range, 100, 1000); // drag right -> earlier
        d.From.Should().Be(T0.AddMinutes(-6));
        FindingsTimelineLayout.TimeAt(range, 500, 1000).Should().Be(T0.AddMinutes(30));
    }

    // ---------- evidence changed ----------

    private static string MakeCase(TempDir t)
    {
        var dir = t.Path;
        Directory.CreateDirectory(Path.Combine(dir, "H1", "EventLogs"));
        Directory.CreateDirectory(Path.Combine(dir, "logs"));
        File.WriteAllText(Path.Combine(dir, "H1", "EventLogs", "a.csv"), "aaaa");
        File.WriteAllText(Path.Combine(dir, "b.csv"), "bbbb");
        File.WriteAllText(Path.Combine(dir, "logs", "job.log"), "log");
        var (args, error, files) = SpectreCaseHandoff.BuildWithFiles(dir);
        args.Should().NotBeNull(error);
        SpectreCaseLink.Save(dir, new SpectreCaseLink("e1", 5, DateTime.UtcNow, files));
        return dir;
    }

    [Fact]
    public void Evidence_check_is_unknown_without_a_file_list()
    {
        using var t = new TempDir();
        SpectreCaseLink.EvidenceChanged(t.Path).Known.Should().BeFalse();
        File.WriteAllText(Path.Combine(t.Path, "a.csv"), "x");
        SpectreCaseLink.Save(t.Path, new SpectreCaseLink("e1", 5, DateTime.UtcNow));
        SpectreCaseLink.EvidenceChanged(t.Path).Known.Should().BeFalse();
    }

    [Fact]
    public void Evidence_check_sees_added_removed_and_modified_files_but_ignores_logs()
    {
        using var t = new TempDir();
        var dir = MakeCase(t);
        var r = SpectreCaseLink.EvidenceChanged(dir);
        r.Known.Should().BeTrue();
        r.Changed.Should().BeFalse();

        File.AppendAllText(Path.Combine(dir, "logs", "job.log"), "more");           // bookkeeping: ignored
        File.WriteAllText(Path.Combine(dir, "logs", "new.log"), "new");
        SpectreCaseLink.EvidenceChanged(dir).Changed.Should().BeFalse();

        File.WriteAllText(Path.Combine(dir, "c.csv"), "new");
        File.Delete(Path.Combine(dir, "b.csv"));
        File.AppendAllText(Path.Combine(dir, "H1", "EventLogs", "a.csv"), "!");     // size changed
        r = SpectreCaseLink.EvidenceChanged(dir);
        r.Changed.Should().BeTrue();
        r.Added.Should().Equal("c.csv");
        r.Removed.Should().Equal("b.csv");
        r.Modified.Should().Equal("H1/EventLogs/a.csv");
    }

    [Fact]
    public void Evidence_check_hashes_only_when_the_time_changed_and_ignores_touch()
    {
        using var t = new TempDir();
        var dir = MakeCase(t);
        var b = Path.Combine(dir, "b.csv");
        File.SetLastWriteTimeUtc(b, DateTime.UtcNow.AddDays(1));                    // touched, same bytes
        SpectreCaseLink.EvidenceChanged(dir).Changed.Should().BeFalse();
        File.WriteAllText(b, "BBBB");                                               // same size, different bytes
        var r = SpectreCaseLink.EvidenceChanged(dir);
        r.Changed.Should().BeTrue();
        r.Modified.Should().Equal("b.csv");
    }

    [Fact]
    public async Task OpenCase_saves_the_handoff_file_list_in_the_link()
    {
        using var t = new TempDir();
        File.WriteAllText(Path.Combine(t.Path, "a.csv"), "x");
        var c = new FakeCaller { Handler = (_, _, _) => Task.FromResult(new McpToolResult(false, """{"case_id":"E9"}""")) };
        await new SpectreFindingsClient(c, null, null).OpenCaseAsync(t.Path, default);
        var link = SpectreCaseLink.Load(t.Path)!;
        link.EngineCaseId.Should().Be("E9");
        link.Files.Should().ContainSingle().Which.Should().Match<SpectreHandoffFile>(f => f.Path == "a.csv" && f.Size == 1 && f.Sha256.Length == 64);
        SpectreCaseLink.EvidenceChanged(t.Path).Changed.Should().BeFalse();
    }

    // ---------- analysis job ----------

    private static JobQueue NewQueue()
    {
        var prev = SynchronizationContext.Current;
        SynchronizationContext.SetSynchronizationContext(null);
        try { return new JobQueue(new FakeLocator(null), 2, TimeSpan.FromSeconds(60)); }
        finally { SynchronizationContext.SetSynchronizationContext(prev); }
    }

    [Fact]
    public async Task Analysis_job_hands_off_runs_through_phase_5_and_succeeds_in_the_queue()
    {
        using var t = new TempDir();
        File.WriteAllText(Path.Combine(t.Path, "a.csv"), "x");
        var c = new FakeCaller
        {
            Handler = (tool, _, _) => Task.FromResult(new McpToolResult(false, tool switch
            {
                "spectre_open_ezf_case" => """{"case_id":"E1"}""",
                "spectre_run" => """{"ok":true}""",
                "spectre_timeline" => TimelineJson,
                _ => "{}",
            })),
        };
        var q = NewQueue();
        var spec = SpectreAnalysisJob.CreateSpec(t.Path, new SpectreFindingsClient(c, () => t.Path, null));
        spec.Exclusive.Should().BeTrue();
        spec.Hidden.Should().BeTrue();
        var job = q.Enqueue(spec, t.Path);
        await q.WhenAllAsync();

        job.State.Should().Be(JobState.Succeeded);
        job.StatusReason.Should().Contain("6 finding(s)").And.Contain("2 key event(s)");
        SpectreAnalysisJob.Is(job).Should().BeTrue();
        c.Calls.Select(x => x.Tool).Should().Equal("spectre_open_ezf_case", "spectre_run", "spectre_timeline");
        c.Calls[1].Args["through_phase"]!.GetValue<int>().Should().Be(5);
        c.Calls[1].Args["case_id"]!.GetValue<string>().Should().Be("E1");
        SpectreCaseLink.Load(t.Path)!.LastPhase.Should().Be(5);
        var log = File.ReadAllText(job.LogPath!);
        log.Should().Contain("Handing the case to Spectre").And.Contain("The analysis finished");
        q.GetManifestPath(t.Path).Should().NotBeNull();
    }

    [Fact]
    public async Task Analysis_job_fails_when_the_engine_reports_a_problem()
    {
        using var t = new TempDir();
        File.WriteAllText(Path.Combine(t.Path, "a.csv"), "x");
        var c = new FakeCaller
        {
            Handler = (tool, _, _) => Task.FromResult(tool == "spectre_open_ezf_case" ? new McpToolResult(false, """{"case_id":"E1"}""")
                : new McpToolResult(false, """{"ok":false,"error":"phase 3 broke"}""")),
        };
        var q = NewQueue();
        var job = q.Enqueue(SpectreAnalysisJob.CreateSpec(t.Path, new SpectreFindingsClient(c, () => t.Path, null)), t.Path);
        await q.WhenAllAsync();
        job.State.Should().Be(JobState.Failed);
        job.StatusReason.Should().Contain("phase 3 broke");
    }

    [Fact]
    public async Task Analysis_job_honours_cancel_while_the_engine_runs()
    {
        using var t = new TempDir();
        File.WriteAllText(Path.Combine(t.Path, "a.csv"), "x");
        var running = new TaskCompletionSource();
        var c = new FakeCaller
        {
            Handler = async (tool, _, ct) =>
            {
                if (tool == "spectre_open_ezf_case") return new McpToolResult(false, """{"case_id":"E1"}""");
                running.TrySetResult();
                await Task.Delay(Timeout.Infinite, ct);
                return new McpToolResult(false, "{}");
            },
        };
        var q = NewQueue();
        var job = q.Enqueue(SpectreAnalysisJob.CreateSpec(t.Path, new SpectreFindingsClient(c, () => t.Path, null)), t.Path);
        await running.Task.WaitAsync(TimeSpan.FromSeconds(30));
        job.Cancel();
        await q.WhenAllAsync();
        job.State.Should().Be(JobState.Cancelled);
        job.StatusReason.Should().Be(JobOutcomeClassifier.CancelledReason);
    }

    [Fact]
    public async Task Exclusive_in_process_jobs_run_one_at_a_time()
    {
        using var t = new TempDir();
        int active = 0, maxActive = 0;
        var q = NewQueue();
        JobSpec Spec(string id) => new(id, "", Array.Empty<string>(), Path.Combine(t.Path, "out", id), id, "Test", Exclusive: true,
            InProcess: async (ctx, ct) =>
            {
                var n = Interlocked.Increment(ref active);
                lock (this) maxActive = Math.Max(maxActive, n);
                ctx.Log("working");
                await Task.Delay(100, ct);
                Interlocked.Decrement(ref active);
                ctx.Summary = "done " + id;
            });
        var a = q.Enqueue(Spec("a"), t.Path);
        var b = q.Enqueue(Spec("b"), t.Path);
        await q.WhenAllAsync();
        maxActive.Should().Be(1);
        a.State.Should().Be(JobState.Succeeded);
        b.StatusReason.Should().Be("done b");
    }
}
