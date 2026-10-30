using System.Collections.ObjectModel;
using System.Collections.Specialized;
using EzForensics.Core.Settings;
using EzForensics.Core.Setup;
using FluentAssertions;

namespace EzForensics.Core.Tests;

/// <summary>The wizard's install list: only the pending steps after the restart, and the live order while it runs.</summary>
public class SetupWizardListTests
{
    private static readonly SetupCatalog Cat = SetupCatalog.Default;

    private sealed class Exec : ISetupExecutor
    {
        public readonly List<string> Ran = new();
        public Task<SetupStepResult> ExecuteAsync(SetupStepContext c, CancellationToken ct) { Ran.Add(c.Step.Id); return Task.FromResult(SetupStepResult.Ok("ok")); }
    }

    /// <summary>What the resumed wizard plans: the pending items plus the ticks it always shows (built-in features, the private .NET 9 copy installed before the restart).</summary>
    private static SetupPlan ResumedPlan(WslStatus wsl)
    {
        var pending = new SetupResumeState { Items = { SetupIds.EzDistro, SetupIds.Zeek, SetupIds.Rita } };
        var sel = SetupWizardPolicy.FromResume(pending);
        sel.Items.UnionWith(new[] { SetupIds.PersistenceBuiltIn, SetupIds.ComputerDetails, SetupIds.Dotnet9 });
        var sys = SetupCatalogTests.Sys(net9: true, wsl: wsl, distro: null);
        return SetupWizardPolicy.PendingOnly(SetupPlanner.Plan(Cat, sel, sys));
    }

    [Fact]
    public void Resume_lists_only_the_pending_steps()
    {
        var plan = ResumedPlan(WslStatus.Enabled);
        plan.Steps.Select(s => s.Kind).Should().Equal(SetupStepKind.InstallEzDistro, SetupStepKind.InstallZeekWsl, SetupStepKind.InstallRita);
        plan.Steps.Should().NotContain(s => s.Kind == SetupStepKind.BuiltIn, "Persistence Collector and Computer details were ready before the restart");
        plan.Steps.Should().NotContain(s => s.State == SetupStepState.AlreadyInstalled);
        SetupWizardPolicy.Resume(plan).Should().Be(ResumeAction.Run);
    }

    [Fact]
    public void Resume_keeps_a_step_the_pending_items_still_need()
    {
        var plan = ResumedPlan(WslStatus.NotEnabled);
        plan.Steps.Select(s => s.Kind).Should().StartWith(new[] { SetupStepKind.EnableWsl }, "WSL is still off: its step is not done yet");
        SetupWizardPolicy.Resume(plan).Should().Be(ResumeAction.RestartWindows);
    }

    [Fact]
    public void Resume_with_everything_done_is_empty()
    {
        var pending = new SetupResumeState { Items = { SetupIds.PersistenceBuiltIn, SetupIds.Dotnet9 } };
        var plan = SetupWizardPolicy.PendingOnly(SetupPlanner.Plan(Cat, SetupWizardPolicy.FromResume(pending), SetupCatalogTests.Sys(net9: true)));
        plan.Steps.Should().BeEmpty();
        SetupWizardPolicy.Resume(plan).Should().Be(ResumeAction.Finished);
    }

    [Fact]
    public async Task Resumed_run_finishes_every_listed_step()
    {
        var plan = ResumedPlan(WslStatus.Enabled);
        var exec = new Exec();
        await new SetupRunner(exec).RunAsync(plan);
        exec.Ran.Should().Equal("ez-distro", "zeek", "rita");
        plan.AllSucceeded.Should().BeTrue();
        plan.Steps.Should().OnlyContain(s => s.State == SetupStepState.Done, "the Done page lists exactly these as installed");
    }

    // ------------------------------------------------------------------ live order

    private static SetupStep S(string id, SetupStepState state = SetupStepState.Queued) =>
        new() { Id = id, Kind = SetupStepKind.DownloadEzTools, Title = id, State = state };

    private static (ObservableCollection<SetupStep> List, SetupStepOrder Order, List<NotifyCollectionChangedAction> Changes) Make(params SetupStep[] steps)
    {
        var list = new ObservableCollection<SetupStep>(steps);
        var order = new SetupStepOrder(list);
        var changes = new List<NotifyCollectionChangedAction>();
        list.CollectionChanged += (_, e) => changes.Add(e.Action);
        return (list, order, changes);
    }

    private static string[] Ids(IEnumerable<SetupStep> l) => l.Select(s => s.Id).ToArray();

    [Fact]
    public void Running_first_then_queued_then_finished_newest_last()
    {
        SetupStep a = S("a"), b = S("b"), c = S("c"), d = S("d");
        var (list, order, changes) = Make(a, b, c, d);

        a.State = SetupStepState.Running; order.Update();
        Ids(list).Should().Equal("a", "b", "c", "d");

        a.State = SetupStepState.Done; b.State = SetupStepState.Running; order.Update();
        Ids(list).Should().Equal("b", "c", "d", "a");

        b.State = SetupStepState.Failed; c.State = SetupStepState.Running; order.Update();
        Ids(list).Should().Equal("c", "d", "a", "b");

        c.State = SetupStepState.Done; d.State = SetupStepState.Running; order.Update();
        Ids(list).Should().Equal("d", "a", "b", "c");

        d.State = SetupStepState.WaitingForRestart; order.Update();
        Ids(list).Should().Equal("a", "b", "c", "d");

        changes.Should().OnlyContain(x => x == NotifyCollectionChangedAction.Move, "the list is reordered, never rebuilt");
    }

    [Fact]
    public void Steps_finished_before_the_run_sit_above_newer_ones()
    {
        SetupStep builtIn = S("builtin", SetupStepState.Done), x = S("x"), have = S("have", SetupStepState.AlreadyInstalled), y = S("y");
        var (list, order, _) = Make(builtIn, x, have, y);

        x.State = SetupStepState.Running; order.Update();
        Ids(list).Should().Equal("x", "y", "builtin", "have");

        x.State = SetupStepState.Skipped; y.State = SetupStepState.Running; order.Update();
        Ids(list).Should().Equal("y", "builtin", "have", "x");
    }

    [Fact]
    public void Retried_steps_return_to_the_queue_in_run_sequence()
    {
        SetupStep a = S("a"), b = S("b"), c = S("c");
        var (list, order, _) = Make(a, b, c);
        c.State = SetupStepState.Failed; order.Update();
        a.State = SetupStepState.Failed; order.Update();
        b.State = SetupStepState.Done; order.Update();
        Ids(list).Should().Equal("c", "a", "b");

        a.State = SetupStepState.Queued; c.State = SetupStepState.Running; order.Update();
        Ids(list).Should().Equal("c", "a", "b");
        c.State = SetupStepState.Done; a.State = SetupStepState.Running; order.Update();
        Ids(list).Should().Equal("a", "b", "c");
    }

    [Fact]
    public void Reset_takes_the_new_plan_order()
    {
        SetupStep a = S("a"), b = S("b");
        var (list, order, _) = Make(a, b);
        a.State = SetupStepState.Done; order.Update();
        Ids(list).Should().Equal("b", "a");

        list.Clear();
        SetupStep p = S("p"), q = S("q");
        list.Add(p); list.Add(q);
        order.Reset();
        q.State = SetupStepState.Running; order.Update();
        Ids(list).Should().Equal("q", "p");
    }
}
