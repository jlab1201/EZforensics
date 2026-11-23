using System.Diagnostics;
using System.IO;
using System.Windows;
using System.Windows.Controls;
using EzForensics.App.ViewModels.Learn;
using EzForensics.App.Views.Learn;
using FluentAssertions;
using Microsoft.Data.Sqlite;

namespace EzForensics.App.Tests;

/// <summary>The Learn page parses, binds and lays out on every tab (with rows in the Inbox) without binding errors.</summary>
[Collection("WpfApp")]
public sealed class LearnViewSmokeTests
{
    private sealed class Collector : TraceListener
    {
        public readonly List<string> Lines = new();
        public override void Write(string? message) { }
        public override void WriteLine(string? message) { if (message != null) lock (Lines) Lines.Add(message); }
    }

    private sealed class QueueContext : SynchronizationContext
    {
        private readonly System.Collections.Concurrent.ConcurrentQueue<(SendOrPostCallback, object?)> _q = new();
        public override void Post(SendOrPostCallback d, object? state) => _q.Enqueue((d, state));
        public override void Send(SendOrPostCallback d, object? state) => d(state);
        public void Drain() { while (_q.TryDequeue(out var x)) x.Item1(x.Item2); }
    }

    private static void Pump(QueueContext ctx, Func<bool> until, int timeoutMs = 10000)
    {
        var sw = Stopwatch.StartNew();
        while (sw.ElapsedMilliseconds < timeoutMs) { ctx.Drain(); if (until()) return; Thread.Sleep(10); }
        ctx.Drain();
    }

    [Fact]
    public void Learn_view_binds_on_every_tab_with_inbox_rows()
    {
        var dir = Path.Combine(Path.GetTempPath(), "ezf-learn-smoke-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(dir);
        var a = Path.Combine(dir, "a.txt");
        var b = Path.Combine(dir, "b.txt");
        File.WriteAllText(a, "Prefetch files record program execution evidence for the examiner. They show when a program first ran and how often.");
        File.WriteAllText(b, "Shellbags record folders the user browsed, even after the folder is deleted. Useful for finding removable media use.");
        Exception? failure = null;
        var listener = new Collector();
        var t = new Thread(() =>
        {
            try
            {
                if (Application.Current == null) new App().InitializeComponent();
                var ctx = new QueueContext(); SynchronizationContext.SetSynchronizationContext(ctx);
                foreach (var d in Application.Current.Resources.MergedDictionaries) foreach (var k in d.Keys.Cast<object>().ToList()) _ = d[k];
                PresentationTraceSources.DataBindingSource.Listeners.Add(listener);
                PresentationTraceSources.DataBindingSource.Switch.Level = SourceLevels.Error;
                var vm = new LearnViewModel(new KnowledgeWorkspace(Path.Combine(dir, "k.db"), () => null));
                var view = new LearnView { DataContext = vm };
                void Layout() { view.Measure(new Size(1200, 800)); view.Arrange(new Rect(0, 0, 1200, 800)); view.UpdateLayout(); }
                Layout();

                vm.SelectedTab.Should().Be(LearnTab.Brain);
                vm.SelectedTab = LearnTab.Inbox; Layout();
                var add = vm.Inbox.AddPathsAsync(new[] { a, b });
                Pump(ctx, () => add.IsCompleted);
                add.IsCompleted.Should().BeTrue();
                Layout();
                vm.Inbox.Rows.Should().HaveCount(2);
                vm.InboxHeader.Should().Be("To review (2)");

                vm.Inbox.ToggleExpandCommand.Execute(vm.Inbox.Rows[0]); Layout();
                vm.Inbox.SetSelected(new[] { vm.Inbox.Rows[0] }); Layout();
                var accept = vm.Inbox.AcceptCommand.ExecuteAsync(null);
                Pump(ctx, () => accept.IsCompleted);
                Layout();
                vm.Inbox.Rows.Should().HaveCount(1);

                vm.Inbox.SetSelected(vm.Inbox.Rows.ToList());
                vm.Inbox.RejectCommand.Execute(null); Layout();
                vm.Inbox.IsConfirmingReject.Should().BeTrue();
                var reject = vm.Inbox.ConfirmRejectCommand.ExecuteAsync(null);
                Pump(ctx, () => reject.IsCompleted);
                Layout();
                vm.Inbox.Rejected.Should().HaveCount(1);
                vm.Inbox.ShowRejected = true; Layout();

                vm.SelectedTab = LearnTab.Library; Layout();
                var lib = vm.Library.RefreshAsync();
                Pump(ctx, () => lib.IsCompleted);
                Layout();
                vm.Library.Sources.Should().ContainSingle();
                vm.Library.Sources[0].IsExpanded = true; Layout();
                Pump(ctx, () => vm.Library.Sources[0].Documents.Count > 0); Layout();
                vm.Library.Sources[0].Documents.Should().ContainSingle();

                vm.SelectedTab = LearnTab.Brain; Layout();
            }
            catch (Exception ex) { failure = ex; }
        });
        t.SetApartmentState(ApartmentState.STA);
        t.Start();
        t.Join();
        SqliteConnection.ClearAllPools();
        try { Directory.Delete(dir, true); } catch (IOException) { }
        if (failure != null) throw new Exception("Learn smoke failed: " + failure, failure);
        listener.Lines.Should().BeEmpty("binding errors: " + string.Join("\n", listener.Lines));
    }

    [Fact]
    public void Intake_tab_binds_in_light_and_dark()
    {
        var dir = Path.Combine(Path.GetTempPath(), "ezf-intake-smoke-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(dir);
        var a = Path.Combine(dir, "a.txt");
        File.WriteAllText(a, "Prefetch files record program execution evidence for the examiner. They show when a program first ran and how often.");
        Exception? failure = null;
        var listener = new Collector();
        var t = new Thread(() =>
        {
            try
            {
                if (Application.Current == null) new App().InitializeComponent();
                var ctx = new QueueContext(); SynchronizationContext.SetSynchronizationContext(ctx);
                foreach (var d in Application.Current.Resources.MergedDictionaries) foreach (var k in d.Keys.Cast<object>().ToList()) _ = d[k];
                PresentationTraceSources.DataBindingSource.Listeners.Add(listener);
                PresentationTraceSources.DataBindingSource.Switch.Level = SourceLevels.Error;
                foreach (var theme in new[] { Core.Settings.AppTheme.Light, Core.Settings.AppTheme.Dark })
                {
                    Services.ThemeManager.Apply(theme);
                    var vm = new LearnViewModel(new KnowledgeWorkspace(Path.Combine(dir, theme + ".db"), () => null), () => (null, ""));
                    var view = new LearnView { DataContext = vm };
                    void Layout() { view.Measure(new Size(1200, 800)); view.Arrange(new Rect(0, 0, 1200, 800)); view.UpdateLayout(); }
                    vm.SelectedTab = LearnTab.Intake; Layout();
                    var add = vm.Intake.AddPathsAsync(new[] { a });
                    Pump(ctx, () => add.IsCompleted);
                    add.IsCompleted.Should().BeTrue();
                    Layout();
                    vm.Intake.Rows.Should().ContainSingle();
                    vm.Intake.HasAi.Should().BeFalse();
                    vm.IntakeHeader.Should().Be("Intake (1)");
                }
            }
            catch (Exception ex) { failure = ex; }
            finally { try { Services.ThemeManager.Apply(Core.Settings.AppTheme.Dark); } catch { } }
        });
        t.SetApartmentState(ApartmentState.STA);
        t.Start();
        t.Join();
        SqliteConnection.ClearAllPools();
        try { Directory.Delete(dir, true); } catch (IOException) { }
        if (failure != null) throw new Exception("Intake smoke failed: " + failure, failure);
        listener.Lines.Should().BeEmpty("binding errors: " + string.Join("\n", listener.Lines));
    }

    [Fact]
    public void Inbox_review_list_binds_in_light_and_dark()
    {
        var dir = Path.Combine(Path.GetTempPath(), "ezf-inbox-smoke-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(dir);
        var a = Path.Combine(dir, "a.txt");
        var b = Path.Combine(dir, "b.txt");
        File.WriteAllText(a, "Prefetch files record program execution evidence for the examiner. They show when a program first ran and how often.");
        File.WriteAllText(b, "Shellbags record folders the user browsed, even after the folder is deleted. Useful for finding removable media use.");
        Exception? failure = null;
        var listener = new Collector();
        var t = new Thread(() =>
        {
            try
            {
                if (Application.Current == null) new App().InitializeComponent();
                var ctx = new QueueContext(); SynchronizationContext.SetSynchronizationContext(ctx);
                foreach (var d in Application.Current.Resources.MergedDictionaries) foreach (var k in d.Keys.Cast<object>().ToList()) _ = d[k];
                PresentationTraceSources.DataBindingSource.Listeners.Add(listener);
                PresentationTraceSources.DataBindingSource.Switch.Level = SourceLevels.Error;
                foreach (var theme in new[] { Core.Settings.AppTheme.Light, Core.Settings.AppTheme.Dark })
                {
                    Services.ThemeManager.Apply(theme);
                    var ws = new KnowledgeWorkspace(Path.Combine(dir, theme + ".db"), () => null);
                    var vm = new InboxViewModel(ws, null, () => ((_, _) => Task.FromResult("[]"), "m"));
                    var view = new InboxView { DataContext = vm };
                    void Layout() { view.Measure(new Size(1200, 800)); view.Arrange(new Rect(0, 0, 1200, 800)); view.UpdateLayout(); }
                    Layout();
                    var add = vm.AddPathsAsync(new[] { a, b });
                    Pump(ctx, () => add.IsCompleted);
                    add.IsCompleted.Should().BeTrue();
                    Layout();
                    vm.PageRows.Should().HaveCount(2);

                    using (var st = new Core.Knowledge.KnowledgeStore(ws.DbPath))
                    {
                        st.SetLabels(vm.Rows[0].Id, "About prefetch", "Windows artifacts", "Reference", new[] { "a", "b", "c", "d", "e" }, "m", DateTime.UtcNow.ToString("o"));
                        st.SetAiFailed(new[] { vm.Rows[1].Id });
                    }
                    var refresh = vm.RefreshAsync();
                    Pump(ctx, () => refresh.IsCompleted);
                    Layout();
                    vm.Rows[1].HasAiFailed.Should().BeTrue();
                    vm.Rows[0].MoreTags.Should().Be("+2");

                    vm.ToggleExpandCommand.Execute(vm.Rows[0]); Layout();
                    vm.ToggleExpandCommand.Execute(vm.Rows[1]); Layout();
                    vm.AddFilter("Category", "Windows artifacts"); Layout();
                    vm.PageRows.Should().ContainSingle();
                    vm.ClearFiltersCommand.Execute(null);
                    vm.Search = "shellbags";
                    Pump(ctx, () => vm.PendingBodies.IsCompleted); Layout();
                    vm.PageRows.Should().ContainSingle();
                    vm.Search = ""; Layout();

                    var list = (ListBox)view.FindName("RowList");
                    list.SelectAll(); Layout();
                    vm.SelectedCount.Should().Be(2, "the list's selection reaches the view model");
                    vm.RejectCommand.Execute(null); Layout();
                    vm.IsConfirmingReject.Should().BeTrue();
                    vm.ClearSelectionCommand.Execute(null); Layout();
                    list.SelectedItems.Count.Should().Be(0, "clearing in the view model clears the list");
                    vm.HasSelection.Should().BeFalse();
                    vm.IsConfirmingReject.Should().BeFalse();
                }
            }
            catch (Exception ex) { failure = ex; }
            finally { try { Services.ThemeManager.Apply(Core.Settings.AppTheme.Dark); } catch { } }
        });
        t.SetApartmentState(ApartmentState.STA);
        t.Start();
        t.Join();
        SqliteConnection.ClearAllPools();
        try { Directory.Delete(dir, true); } catch (IOException) { }
        if (failure != null) throw new Exception("Inbox smoke failed: " + failure, failure);
        listener.Lines.Should().BeEmpty("binding errors: " + string.Join("\n", listener.Lines));
    }
}
