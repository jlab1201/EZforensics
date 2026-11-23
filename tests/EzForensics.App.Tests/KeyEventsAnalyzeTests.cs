using System.Collections.Concurrent;
using System.Diagnostics;
using System.IO;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Text;
using EzForensics.App.Services;
using EzForensics.App.ViewModels;
using EzForensics.Core.Analysis;
using EzForensics.Core.Results;
using EzForensics.Core.Settings;
using FluentAssertions;

namespace EzForensics.App.Tests;

/// <summary>Analyze key events: bundles across CSVs, add-to, the unflag prompt and removal. Only the workspace section of analysis.json is written.</summary>
[Collection("WpfApp")]
public sealed class KeyEventsAnalyzeTests : IDisposable
{
    private sealed class QueueContext : SynchronizationContext
    {
        private readonly ConcurrentQueue<(SendOrPostCallback, object?)> _q = new();
        public override void Post(SendOrPostCallback d, object? state) => _q.Enqueue((d, state));
        public override void Send(SendOrPostCallback d, object? state) => d(state);
        public void Drain() { while (_q.TryDequeue(out var x)) x.Item1(x.Item2); }
    }

    private readonly string _dir = Path.Combine(Path.GetTempPath(), "ezf-keyev-" + Guid.NewGuid().ToString("N")[..8]);
    private string Csv => Path.Combine(_dir, "H1", "Evtx", "Events.csv");
    private string Csv2 => Path.Combine(_dir, "H1", "Evtx", "Other.csv");

    public KeyEventsAnalyzeTests()
    {
        Directory.CreateDirectory(Path.GetDirectoryName(Csv)!);
        File.WriteAllText(Csv, "RecordNumber,Message\r\n1,a\r\n2,b\r\n3,c\r\n4,d\r\n5,e\r\n", new UTF8Encoding(false));
        File.WriteAllText(Csv2, "RecordNumber,Message\r\n1,x\r\n2,y\r\n", new UTF8Encoding(false));
    }
    public void Dispose() { try { Directory.Delete(_dir, true); } catch (IOException) { } }

    private static void Pump(QueueContext ctx, Func<bool> until, int timeoutMs = 20000)
    {
        var sw = Stopwatch.StartNew();
        while (sw.ElapsedMilliseconds < timeoutMs) { ctx.Drain(); if (until()) return; Thread.Sleep(10); }
        ctx.Drain();
    }

    private void Run(Action<QueueContext, ResultsViewModel, AppServices> body)
    {
        Exception? failure = null;
        var t = new Thread(() =>
        {
            var prev = AppServices.Current;
            try
            {
                var ctx = new QueueContext();
                SynchronizationContext.SetSynchronizationContext(ctx);
                var s = new AppServices(new JsonSettingsStore(Path.Combine(_dir, "settings.json")), new AppSettings());
                AppServices.Current = s;
                body(ctx, new ResultsViewModel(), s);
            }
            catch (Exception ex) { failure = ex; }
            finally { AppServices.Current = prev; }
        });
        t.SetApartmentState(ApartmentState.STA);
        t.Start();
        t.Join();
        if (failure != null) throw new Exception("STA test failed", failure);
    }

    private void Open(QueueContext ctx, ResultsViewModel vm, string csv, int rows)
    {
        var task = vm.ShowCsvAsync(_dir, csv);
        Pump(ctx, () => task.IsCompleted && vm.Rows != null && vm.Rows.Count == rows);
        task.IsCompleted.Should().BeTrue();
        vm.Rows!.Count.Should().Be(rows);
    }

    private static IReadOnlyCollection<RowItem> Pick(ResultsViewModel vm, params int[] pos) => pos.Select(p => (RowItem)vm.Rows![p]).ToList();
    private static CaseAnalysis Disk(string dir) => CaseAnalysisStore.Open(dir).Data;
    private static readonly KeyEventDraft Draft = new("Lateral movement", "High", new[] { "T1021.001" });

    [Fact]
    public void Create_across_two_csvs_flags_the_rows_and_stores_only_the_workspace()
    {
        Run((ctx, vm, s) =>
        {
            Open(ctx, vm, Csv, 5);
            vm.SetFlagId(Pick(vm, 0), FlagCatalog.Malicious); // already flagged with another colour: kept
            var item = vm.CreateFinding(Pick(vm, 0, 2), Draft);
            item.Should().NotBeNull();
            vm.IsFlagged(0).Should().BeTrue(); vm.IsFlagged(2).Should().BeTrue();
            vm.FlagIdOf(0).Should().Be(FlagCatalog.Malicious);
            vm.FlagIdOf(2).Should().Be(FlagCatalog.Suspicious);

            Open(ctx, vm, Csv2, 2);
            vm.Findings.Should().ContainSingle();
            vm.AddToFinding(item!.Id, Pick(vm, 1)).Should().BeTrue();
            vm.IsFlagged(1).Should().BeTrue();

            var a = Disk(_dir);
            a.Items.Should().BeEmpty();
            var k = a.Workspace.Findings.Should().ContainSingle().Subject;
            k.Title.Should().Be("Lateral movement"); k.Severity.Should().Be("High"); k.Mitre.Should().Equal("T1021.001");
            k.Rows.Select(r => (r.CsvRelPath, r.Row)).Should().BeEquivalentTo(new[] { ("H1/Evtx/Events.csv", 0L), ("H1/Evtx/Events.csv", 2L), ("H1/Evtx/Other.csv", 1L) });
            k.Rows.Should().OnlyContain(r => !string.IsNullOrEmpty(r.Fingerprint));
            vm.Findings[0].RowCount.Should().Be(3);
            ((RowItem)vm.Rows![1]).InKeyEvent.Should().BeTrue();
            ((RowItem)vm.Rows![0]).InKeyEvent.Should().BeFalse();
        });
    }

    [Fact]
    public void Unflagging_a_key_event_row_asks_and_cancel_changes_nothing()
    {
        Run((ctx, vm, s) =>
        {
            Open(ctx, vm, Csv, 5);
            vm.CreateFinding(Pick(vm, 0, 1), Draft);
            string? asked = null;
            vm.UnflagPrompt = m => { asked = m; return false; };
            vm.SetFlagId(Pick(vm, 0), null);
            asked.Should().Be("This row is in finding Lateral movement. Remove it from the finding and unflag it?");
            vm.IsFlagged(0).Should().BeTrue();
            vm.SetFlags(Pick(vm, 0), false);
            vm.IsFlagged(0).Should().BeTrue();
            Disk(_dir).Workspace.Findings[0].Rows.Should().HaveCount(2);

            // a colour change needs no prompt
            asked = null;
            vm.SetFlagId(Pick(vm, 0), FlagCatalog.Malicious);
            asked.Should().BeNull();
            vm.FlagIdOf(0).Should().Be(FlagCatalog.Malicious);
        });
    }

    [Fact]
    public void Unflagging_after_yes_removes_the_row_and_the_last_row_deletes_the_key_event()
    {
        Run((ctx, vm, s) =>
        {
            Open(ctx, vm, Csv, 5);
            vm.CreateFinding(Pick(vm, 0, 1), Draft);
            vm.UnflagPrompt = _ => true;
            vm.SetFlagId(Pick(vm, 0), null);
            vm.IsFlagged(0).Should().BeFalse();
            Disk(_dir).Workspace.Findings.Single().Rows.Should().ContainSingle().Which.Row.Should().Be(1);
            vm.SetFlags(Pick(vm, 1), false);
            vm.IsFlagged(1).Should().BeFalse();
            Disk(_dir).Workspace.Findings.Should().BeEmpty();
            vm.Findings.Should().BeEmpty();
        });
    }

    [Fact]
    public void Evidence_and_notes_are_saved_with_the_finding_and_mixed_flags_can_be_recoloured()
    {
        Run((ctx, vm, s) =>
        {
            Open(ctx, vm, Csv, 5);
            vm.SetFlagId(Pick(vm, 0), FlagCatalog.Malicious);
            vm.SetFlagId(Pick(vm, 1), FlagCatalog.Interesting);
            vm.MixedFlagChoiceOf(Pick(vm, 0, 1)).Should().NotBeNull();
            vm.MixedFlagChoiceOf(Pick(vm, 0)).Should().BeNull();
            var item = vm.CreateFinding(Pick(vm, 0, 1), Draft with { Notes = "why", Evidence = "row a\nrow b", RecolourFlagId = FlagCatalog.Suspicious })!;
            Disk(_dir).Workspace.Findings.Single().Notes.Should().Be("Evidence:\nrow a\nrow b\n\nwhy");
            vm.FlagIdOf(0).Should().Be(FlagCatalog.Suspicious);
            vm.FlagIdOf(1).Should().Be(FlagCatalog.Suspicious);
            var d = vm.DraftOf(item.Id)!;
            d.Evidence.Should().Be("row a\nrow b");
            d.Notes.Should().Be("why");
        });
    }

    [Fact]
    public void Unflagging_a_row_outside_any_key_event_never_prompts()
    {
        Run((ctx, vm, s) =>
        {
            Open(ctx, vm, Csv, 5);
            vm.CreateFinding(Pick(vm, 0), Draft);
            vm.SetFlagId(Pick(vm, 3), FlagCatalog.Suspicious);
            vm.UnflagPrompt = _ => throw new InvalidOperationException("no prompt expected");
            vm.SetFlagId(Pick(vm, 3), null);
            vm.IsFlagged(3).Should().BeFalse();
        });
    }

    [Fact]
    public void Edit_and_remove_a_key_event_and_filter_to_its_rows()
    {
        Run((ctx, vm, s) =>
        {
            Open(ctx, vm, Csv, 5);
            var item = vm.CreateFinding(Pick(vm, 1, 3), Draft)!;
            vm.UpdateFinding(item.Id, new KeyEventDraft("Renamed", "Critical", new[] { "T1059" }));
            vm.Findings[0].Title.Should().Be("Renamed");
            Disk(_dir).Workspace.Findings[0].Severity.Should().Be("Critical");

            var show = vm.ToggleFindingFilterAsync(item.Id);
            Pump(ctx, () => show.IsCompleted && vm.Rows!.Count == 2);
            vm.Rows!.Count.Should().Be(2);
            vm.ActiveFindingId.Should().Be(item.Id);
            vm.Findings[0].IsActive.Should().BeTrue();
            var off = vm.ToggleFindingFilterAsync(item.Id);
            Pump(ctx, () => off.IsCompleted && vm.Rows!.Count == 5);
            vm.Rows!.Count.Should().Be(5);

            vm.RemoveFinding(item.Id);
            vm.Findings.Should().BeEmpty();
            Disk(_dir).Workspace.Findings.Should().BeEmpty();
            vm.IsFlagged(1).Should().BeTrue(); // the rows stay flagged
        });
    }

    private void SeedSpectre(string title, ItemOrigin origin, params (string Csv, long Row)[] rows)
    {
        var store = CaseAnalysisStore.Open(_dir);
        store.Data.Items.Add(new AnalysisItem
        {
            Id = AnalysisItem.NewId(), Origin = origin, Title = title, SourceStatus = SourceStatus.Current,
            SourceRows = rows.Select(r => new AnalysisRow(r.Csv, r.Row, null)).ToList(),
        });
        store.Save();
    }

    [Fact]
    public void SpectreFlaggedRows_FromImportedSpectreFindings_CurrentCsvOnly()
    {
        Run((ctx, vm, s) =>
        {
            SeedSpectre("Cleared logs", ItemOrigin.Spectre, ("H1/Evtx/Events.csv", 1), ("H1/Evtx/Events.csv", 3), ("H1/Evtx/Other.csv", 0));
            SeedSpectre("Analyst flag", ItemOrigin.Analyst, ("H1/Evtx/Events.csv", 4));
            Open(ctx, vm, Csv, 5);
            vm.RefreshFindings();
            vm.SpectreFlaggedRows.Should().BeEquivalentTo(new long[] { 1, 3 });
            vm.SpectreMarkerTip(1).Should().Be("Cleared logs");
            vm.HasSpectreFlagged.Should().BeTrue();
            Open(ctx, vm, Csv2, 2);
            vm.SpectreFlaggedRows.Should().BeEquivalentTo(new long[] { 0 });
        });
    }

    [Fact]
    public void SpectreFlaggedFilter_ShowsOnlyThoseRows()
    {
        Run((ctx, vm, s) =>
        {
            SeedSpectre("Cleared logs", ItemOrigin.Spectre, ("H1/Evtx/Events.csv", 1), ("H1/Evtx/Events.csv", 3));
            Open(ctx, vm, Csv, 5);
            vm.RefreshFindings();
            vm.ToggleSpectreFilter();
            Pump(ctx, () => vm.Rows!.Count == 2);
            vm.Rows!.Count.Should().Be(2);
            vm.SpectreFilterActive.Should().BeTrue();
            vm.ToggleSpectreFilter();
            Pump(ctx, () => vm.Rows!.Count == 5);
            vm.Rows!.Count.Should().Be(5);
            vm.SpectreFilterActive.Should().BeFalse();
        });
    }

    [Fact]
    public void SpectreMarker_DoesNotChangeFlagColour()
    {
        Run((ctx, vm, s) =>
        {
            Open(ctx, vm, Csv, 5);
            vm.SetFlagId(Pick(vm, 1), FlagCatalog.Malicious);
            SeedSpectre("Cleared logs", ItemOrigin.Spectre, ("H1/Evtx/Events.csv", 1), ("H1/Evtx/Events.csv", 2));
            vm.RefreshFindings();
            vm.FlagIdOf(1).Should().Be(FlagCatalog.Malicious);
            vm.IsFlagged(2).Should().BeFalse();
            vm.FlagIdOf(2).Should().BeNull();
            ((RowItem)vm.Rows![1]).IsSpectreFlagged.Should().BeTrue();
            ((RowItem)vm.Rows![1]).FlagId.Should().Be(FlagCatalog.Malicious);
        });
    }

    private sealed class Collector : TraceListener
    {
        public readonly List<string> Lines = new();
        public override void Write(string? message) { }
        public override void WriteLine(string? message) { if (message != null) lock (Lines) Lines.Add(message); }
    }

    [Fact]
    public void Analyze_view_shows_the_key_events_list_the_toolbar_button_and_the_gutter_mark_without_binding_errors()
    {
        var listener = new Collector();
        Run((ctx, vm, s) =>
        {
            if (Application.Current == null) new App().InitializeComponent();
            foreach (var d in Application.Current!.Resources.MergedDictionaries) foreach (var k in d.Keys.Cast<object>().ToList()) _ = d[k];
            PresentationTraceSources.DataBindingSource.Listeners.Add(listener);
            PresentationTraceSources.DataBindingSource.Switch.Level = SourceLevels.Error;
            var view = new EzForensics.App.Views.ResultsView { DataContext = vm };
            void Layout() { view.Measure(new Size(1600, 900)); view.Arrange(new Rect(0, 0, 1600, 900)); view.UpdateLayout(); }
            Layout();
            Open(ctx, vm, Csv, 5);
            Layout();
            vm.CreateFinding(Pick(vm, 1), Draft);
            Layout();
            Pump(ctx, () => false, 300);
            Layout();

            static IEnumerable<T> Descendants<T>(DependencyObject root) where T : DependencyObject
            {
                for (int i = 0; i < VisualTreeHelper.GetChildrenCount(root); i++)
                {
                    var c = VisualTreeHelper.GetChild(root, i);
                    if (c is T t) yield return t;
                    foreach (var x in Descendants<T>(c)) yield return x;
                }
            }
            static string? Id(DependencyObject d) => System.Windows.Automation.AutomationProperties.GetAutomationId(d);
            var ids = Descendants<FrameworkElement>(view).Select(Id).Where(i => !string.IsNullOrEmpty(i)).ToList();
            ids.Should().Contain("Results.KeyEvents.Item");
            Descendants<TextBlock>(view).Where(t => Id(t) == "Results.Grid.KeyEventMark" && t.Visibility == Visibility.Visible).Should().ContainSingle();
        });
        listener.Lines.Should().BeEmpty("binding errors: " + string.Join("\n", listener.Lines));
    }

    [Fact]
    public void Editor_mapping_derives_the_tactic_and_flags_bad_ids()
    {
        EzForensics.App.Views.Analyze.KeyEventEditorWindow.TacticOf("T1059.001").Should().Be("Execution");
        EzForensics.App.Views.Analyze.KeyEventEditorWindow.TacticOf("").Should().BeEmpty();
        EzForensics.App.Views.Analyze.KeyEventEditorWindow.MappingInvalid("T1059, nonsense").Should().BeTrue();
        EzForensics.App.Views.Analyze.KeyEventEditorWindow.MappingInvalid("T1059, T1021.001").Should().BeFalse();
    }
}
