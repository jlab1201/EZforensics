using EzForensics.Core.Execution;
using EzForensics.Core.Runtimes;
using EzForensics.Core.Settings;
using EzForensics.Core.Setup;
using FluentAssertions;

namespace EzForensics.Core.Tests;

/// <summary>Install order (admin first, WSL last), one restart at the end, resume after it, and the elevated helper. No real WSL.</summary>
public class SetupOrderTests
{
    private static readonly SetupCatalog Cat = SetupCatalog.Default;

    internal static readonly SetupStepKind[] WslKinds = { SetupStepKind.InstallEzDistro, SetupStepKind.InstallZeekWsl, SetupStepKind.InstallRita };

    /// <summary>The user's fresh VM: Everything, WSL not enabled, no .NET 9, no Python.</summary>
    internal static SetupPlan FreshVmEverything(WslStatus wsl = WslStatus.NotEnabled)
    {
        var sys = SetupCatalogTests.Sys(net9: false, python: null, wsl: wsl, distro: null);
        var sel = SetupSelection.ForProfile(SetupProfile.Everything, Cat, sys);
        sel.Items.Add(SetupIds.VolatilitySymbols);
        return SetupPlanner.Plan(Cat, sel, sys);
    }

    [Fact]
    public void Admin_step_runs_first_and_wsl_steps_run_last()
    {
        var plan = FreshVmEverything();
        var toRun = plan.Steps.Where(s => s.Kind != SetupStepKind.BuiltIn).ToList();
        toRun[0].Kind.Should().Be(SetupStepKind.EnableWsl, "the single UAC prompt comes right after Install");
        plan.Steps.Count(s => s.RequiresAdmin).Should().Be(1, "every admin task is one elevated invocation");

        var kinds = toRun.Select(s => s.Kind).ToList();
        int firstWsl = kinds.FindIndex(k => WslKinds.Contains(k));
        firstWsl.Should().BeGreaterThan(0);
        kinds.Skip(firstWsl).Should().OnlyContain(k => WslKinds.Contains(k), "nothing that does not need WSL runs after the WSL steps");
        kinds.Take(firstWsl).Should().Contain(new[] { SetupStepKind.InstallDotnet, SetupStepKind.DownloadEzTools, SetupStepKind.ProvisionPython,
            SetupStepKind.InstallPlaso, SetupStepKind.InstallVolatility, SetupStepKind.InstallVolatilitySymbols });
        kinds.Skip(firstWsl).Should().Equal(SetupStepKind.InstallEzDistro, SetupStepKind.InstallZeekWsl, SetupStepKind.InstallRita);
    }

    [Fact]
    public void Dependencies_always_point_to_earlier_steps()
    {
        foreach (var plan in new[] { FreshVmEverything(), FreshVmEverything(WslStatus.Enabled) })
        {
            var index = plan.Steps.Select((s, i) => (s.Id, i)).ToDictionary(x => x.Id, x => x.i);
            foreach (var s in plan.Steps)
                foreach (var d in s.DependsOn)
                    index[d].Should().BeLessThan(index[s.Id], $"{s.Id} depends on {d}");
        }
    }

    [Fact]
    public void No_admin_or_restart_when_wsl_already_works()
    {
        var plan = FreshVmEverything(WslStatus.Enabled);
        plan.NeedsAdmin.Should().BeFalse();
        plan.NeedsRestart.Should().BeFalse();
        plan.Steps.Should().NotContain(s => s.Kind == SetupStepKind.EnableWsl);
        plan.Steps.Where(s => s.Kind != SetupStepKind.BuiltIn).TakeLast(3).Select(s => s.Kind).Should().Equal(WslKinds);
    }

    [Fact]
    public void Order_is_stable_inside_each_group()
    {
        SetupStep S(string id, SetupStepKind k, bool admin = false) => new() { Id = id, Kind = k, Title = id, RequiresAdmin = admin };
        var ordered = SetupPlanner.Order(new[]
        {
            S("zeek", SetupStepKind.InstallZeekWsl), S("ez", SetupStepKind.DownloadEzTools), S("wsl", SetupStepKind.EnableWsl, admin: true),
            S("rita", SetupStepKind.InstallRita), S("plaso", SetupStepKind.InstallPlaso), S("builtin", SetupStepKind.BuiltIn),
        });
        ordered.Select(s => s.Id).Should().Equal("builtin", "wsl", "ez", "plaso", "zeek", "rita");
    }

    [Fact]
    public async Task Restart_needed_everything_else_installs_and_only_wsl_steps_wait()
    {
        var plan = FreshVmEverything();
        var exec = new Exec(s => s.Kind == SetupStepKind.EnableWsl ? SetupStepResult.RestartRequired("restart") : SetupStepResult.Ok("ok"));
        await new SetupRunner(exec).RunAsync(plan);

        exec.Ran.First().Should().Be("enable-wsl");
        plan.Steps.Where(s => !WslKinds.Contains(s.Kind) && s.Kind != SetupStepKind.EnableWsl).Should().OnlyContain(s => s.Succeeded);
        plan.Steps.Where(s => WslKinds.Contains(s.Kind)).Should().OnlyContain(s => s.State == SetupStepState.WaitingForRestart);
        exec.Ran.Should().NotContain(new[] { "ez-distro", "zeek", "rita" });

        var pending = SetupWizardPolicy.PendingAfterRestart(plan)!;
        pending.Items.Should().BeEquivalentTo(new[] { SetupIds.EzDistro, SetupIds.Zeek, SetupIds.Rita }, "only what waits for the restart is resumed");
    }

    [Fact]
    public async Task No_restart_needed_continues_into_distro_zeek_and_rita_in_the_same_run()
    {
        var plan = FreshVmEverything();
        var exec = new Exec(_ => SetupStepResult.Ok("ok"));
        await new SetupRunner(exec).RunAsync(plan);
        exec.Ran.TakeLast(3).Should().Equal("ez-distro", "zeek", "rita");
        plan.AllSucceeded.Should().BeTrue();
        SetupWizardPolicy.PendingAfterRestart(plan).Should().BeNull();
    }

    [Fact]
    public async Task Declined_uac_skips_only_the_wsl_steps()
    {
        var plan = FreshVmEverything();
        var exec = new Exec(s => s.Kind == SetupStepKind.EnableWsl ? SetupStepResult.Fail(DefaultSetupExecutor.DeclinedMessage) : SetupStepResult.Ok("ok"));
        await new SetupRunner(exec).RunAsync(plan);
        plan.Steps.Where(s => WslKinds.Contains(s.Kind)).Should().OnlyContain(s => s.State == SetupStepState.Skipped);
        plan.Steps.Where(s => !WslKinds.Contains(s.Kind) && s.Kind != SetupStepKind.EnableWsl).Should().OnlyContain(s => s.Succeeded);
        SetupWizardPolicy.PendingAfterRestart(plan).Should().BeNull();
    }

    [Fact]
    public async Task Resume_state_survives_a_settings_round_trip_and_plans_only_the_wsl_steps()
    {
        var plan = FreshVmEverything();
        await new SetupRunner(new Exec(s => s.Kind == SetupStepKind.EnableWsl ? SetupStepResult.RestartRequired("r") : SetupStepResult.Ok("ok"))).RunAsync(plan);

        var path = Path.Combine(Path.GetTempPath(), "ezf-resume-" + Guid.NewGuid().ToString("N") + ".json");
        try
        {
            var store = new JsonSettingsStore(path);
            store.Save(new AppSettings { SetupWizardSeen = true, SetupResume = SetupWizardPolicy.PendingAfterRestart(plan) });
            var loaded = store.Load();
            SetupWizardPolicy.Startup(loaded).Should().Be(StartupMode.ResumeWizard, "after the restart the wizard reopens on its Install page and finishes the setup");

            var sel = SetupWizardPolicy.FromResume(loaded.SetupResume!);
            // After the restart WSL works: only distro, Zeek, RITA; no admin, so it continues by itself.
            var after = SetupPlanner.Plan(Cat, sel, SetupCatalogTests.Sys(wsl: WslStatus.Enabled, distro: null));
            after.ToRun.Select(s => s.Kind).Should().Equal(WslKinds);
            SetupWizardPolicy.Resume(after).Should().Be(ResumeAction.Run);

            // Not restarted yet (WSL still off): no automatic UAC prompt; the user is asked to restart Windows.
            var notYet = SetupPlanner.Plan(Cat, sel, SetupCatalogTests.Sys(wsl: WslStatus.NotEnabled, distro: null));
            SetupWizardPolicy.Resume(notYet).Should().Be(ResumeAction.RestartWindows);

            // Everything already there (e.g. finished by hand): nothing to do, the pending resume is cleared.
            var ready = new EzDistroInfo(true, true, 2, "Stopped", true, "ubuntu-base 24.04.5 / schema 1", @"C:\d", null, "wsl.exe");
            var done = SetupPlanner.Plan(Cat, sel, SetupCatalogTests.Sys(wsl: WslStatus.Enabled) with
                { EzDistroState = ready, ZeekSummary = "Zeek 9", RitaVersion = EzForensics.Core.Rita.RitaRelease.Version });
            SetupWizardPolicy.Resume(done).Should().Be(ResumeAction.Finished);
        }
        finally { File.Delete(path); }
    }

    [Fact]
    public void Startup_shows_the_wizard_alone_on_first_start_and_after_the_restart()
    {
        SetupWizardPolicy.Startup(new AppSettings()).Should().Be(StartupMode.WizardFirst);
        SetupWizardPolicy.Startup(new AppSettings { SetupWizardSeen = true }).Should().Be(StartupMode.MainWindow);
        SetupWizardPolicy.Startup(new AppSettings { SetupWizardSeen = true, SetupWizardDontShow = true }).Should().Be(StartupMode.MainWindow);
        SetupWizardPolicy.Startup(new AppSettings { SetupWizardDontShow = true, SetupResume = new SetupResumeState { Items = { SetupIds.Zeek } } })
            .Should().Be(StartupMode.ResumeWizard);
        SetupWizardPolicy.Startup(new AppSettings { SetupResume = new SetupResumeState { Items = { SetupIds.Zeek } } })
            .Should().Be(StartupMode.ResumeWizard, "a pending resume reopens the wizard, seen or not");
    }

    private sealed class Exec : ISetupExecutor
    {
        private readonly Func<SetupStep, SetupStepResult> _r;
        public readonly List<string> Ran = new();
        public Exec(Func<SetupStep, SetupStepResult> r) => _r = r;
        public Task<SetupStepResult> ExecuteAsync(SetupStepContext c, CancellationToken ct) { Ran.Add(c.Step.Id); return Task.FromResult(_r(c.Step)); }
    }
}

public class ElevatedSetupHelperTests
{
    /// <summary>The real executor for some kinds (EnableWsl through the elevated helper), "ok" for the rest.</summary>
    private sealed class Mixed : ISetupExecutor
    {
        private readonly ISetupExecutor _real;
        private readonly Func<SetupStepKind, bool> _useReal;
        public Mixed(ISetupExecutor real, Func<SetupStepKind, bool> useReal) { _real = real; _useReal = useReal; }
        public Task<SetupStepResult> ExecuteAsync(SetupStepContext c, CancellationToken ct) =>
            _useReal(c.Step.Kind) ? _real.ExecuteAsync(c, ct) : Task.FromResult(SetupStepResult.Ok("ok"));
    }

    [Fact]
    public async Task Elevated_wsl_exit_3010_waits_for_the_restart_and_saves_distro_zeek_and_rita_for_the_resume()
    {
        var dir = Path.Combine(Path.GetTempPath(), "ezf-3010-" + Guid.NewGuid().ToString("N"));
        var probe = new Probe { WslWorks = false };
        // The real helper command line: wsl --install and the classic component both answer 3010 (restart required).
        var elev = new Elev(args => ElevatedSetupHelper.RunCommandLine(args.Skip(1).ToList(), null, (_, _, _) => 3010));
        var env = new SetupEnvironment
        {
            Queue = new JobQueue(new FakeLocator(null), 1, TimeSpan.FromSeconds(10)),
            CreateHttp = () => new HttpClient(),
            Settings = new AppSettings(),
            SaveSettings = () => { },
            Locator = new FakeLocator(null),
            MaintenanceDir = dir,
            Elevation = elev,
            Probe = probe,
            ElevatedHelperExe = "C:\\app\\EzForensics.exe",
            RebootPending = () => false,
        };
        var plan = SetupOrderTests.FreshVmEverything();
        var hostLog = new List<string>();
        try
        {
            await new SetupRunner(new Mixed(new DefaultSetupExecutor(env), k => k == SetupStepKind.EnableWsl), hostLog.Add).RunAsync(plan);

            elev.Calls.Should().Be(1);
            var wsl = plan.Steps.Single(s => s.Kind == SetupStepKind.EnableWsl);
            wsl.State.Should().Be(SetupStepState.WaitingForRestart);
            plan.Steps.Where(s => SetupOrderTests.WslKinds.Contains(s.Kind)).Should().OnlyContain(s => s.State == SetupStepState.WaitingForRestart);
            SetupWizardPolicy.PendingAfterRestart(plan)!.Items.Should().Contain(new[] { SetupIds.EzDistro, SetupIds.Zeek, SetupIds.Rita });

            wsl.LogPath.Should().Be(Path.Combine(dir, "elevated", ElevatedSetupHelper.EnableWslTask + ".log"), "the elevated helper's log is the step's log");
            hostLog.Should().Contain(l => l.StartsWith("Setup step end enable-wsl WaitingForRestart "));
            File.ReadAllText(Path.Combine(dir, DefaultSetupExecutor.StepRecordFile)).Should().Contain("\"state\":\"WaitingForRestart\"").And.Contain("enable-wsl.log");
        }
        finally { try { Directory.Delete(dir, true); } catch { } }
    }

    private sealed class Runner
    {
        public readonly List<string> Calls = new();
        public Func<string, IReadOnlyList<string>, int> Exit = (_, _) => 0;
        public int Run(string exe, IReadOnlyList<string> args, Action<string> log)
        {
            Calls.Add(Path.GetFileName(exe) + " " + string.Join(" ", args));
            return Exit(exe, args);
        }
    }

    [Fact]
    public void Wsl_install_success_also_enables_the_classic_component_for_wsl1()
    {
        var r = new Runner();
        var res = ElevatedSetupHelper.EnableWsl(r.Run, "wsl.exe", "dism.exe", _ => { });
        res.Ok.Should().BeTrue();
        res.RestartRequired.Should().BeFalse();
        r.Calls.Should().Equal("wsl.exe --install --no-distribution",
            "dism.exe /online /enable-feature /featurename:Microsoft-Windows-Subsystem-Linux /all /norestart");
        res.Commands.Should().OnlyContain(c => c.ExitCode == 0);
    }

    [Fact]
    public void Classic_component_failure_after_wsl_install_is_not_fatal_and_3010_asks_restart()
    {
        var failing = new Runner { Exit = (exe, _) => exe.StartsWith("dism") ? 50 : 0 };
        ElevatedSetupHelper.EnableWsl(failing.Run, "wsl.exe", "dism.exe", _ => { }).Ok.Should().BeTrue();
        var restart = new Runner { Exit = (exe, _) => exe.StartsWith("dism") ? 3010 : 0 };
        ElevatedSetupHelper.EnableWsl(restart.Run, "wsl.exe", "dism.exe", _ => { }).RestartRequired.Should().BeTrue();
    }

    [Fact]
    public void Enable_wsl1_runs_only_the_classic_component()
    {
        var r = new Runner { Exit = (_, _) => 3010 };
        var res = ElevatedSetupHelper.EnableWsl1(r.Run, "dism.exe", _ => { });
        res.Ok.Should().BeTrue();
        res.RestartRequired.Should().BeTrue();
        r.Calls.Should().Equal("dism.exe /online /enable-feature /featurename:Microsoft-Windows-Subsystem-Linux /all /norestart");
        ElevatedSetupHelper.EnableWsl1(new Runner { Exit = (_, _) => 87 }.Run, "dism.exe", _ => { }).Ok.Should().BeFalse();
    }

    [Fact]
    public void Wsl1_not_supported_and_no_virtualization_are_recognised()
    {
        const string v1 = "Exited with code -1. wsl.exe: Please enable the \"Windows Subsystem for Linux\" optional component to use WSL1. / Error code: Wsl/Service/RegisterDistro/WSL_E_WSL1_NOT_SUPPORTED";
        const string v2 = "Exited with code -1. wsl.exe: Error code: Wsl/Service/RegisterDistro/CreateVm/HCS/HCS_E_HYPERV_NOT_INSTALLED";
        EzForensics.Core.Runtimes.EzDistro.NeedsWsl1Component(v1).Should().BeTrue();
        EzForensics.Core.Runtimes.EzDistro.NeedsWsl1Component(v2).Should().BeFalse();
        EzForensics.Core.Runtimes.EzDistro.IsNoVirtualization(v2).Should().BeTrue();
    }

    [Fact]
    public void Exit_3010_means_restart_required()
    {
        var r = new Runner { Exit = (_, _) => 3010 };
        var res = ElevatedSetupHelper.EnableWsl(r.Run, "wsl.exe", "dism.exe", _ => { });
        res.Ok.Should().BeTrue();
        res.RestartRequired.Should().BeTrue();
    }

    [Fact]
    public void Old_wsl_falls_back_to_dism_for_both_features()
    {
        var r = new Runner { Exit = (exe, _) => exe.StartsWith("wsl") ? -1 : 3010 };
        var res = ElevatedSetupHelper.EnableWsl(r.Run, "wsl.exe", "dism.exe", _ => { });
        res.Ok.Should().BeTrue();
        res.RestartRequired.Should().BeTrue();
        r.Calls.Skip(1).Should().Equal(
            "dism.exe /online /enable-feature /featurename:Microsoft-Windows-Subsystem-Linux /all /norestart",
            "dism.exe /online /enable-feature /featurename:VirtualMachinePlatform /all /norestart");
    }

    [Fact]
    public void Dism_failure_is_reported_with_exit_codes()
    {
        var r = new Runner { Exit = (exe, a) => exe.StartsWith("wsl") ? 1 : a.Any(x => x.Contains("VirtualMachinePlatform")) ? 50 : 0 };
        var res = ElevatedSetupHelper.EnableWsl(r.Run, "wsl.exe", "dism.exe", _ => { });
        res.Ok.Should().BeFalse();
        res.Message.Should().Contain("50");
        ElevatedSetupHelper.Interpret(res, false, false).State.Should().Be(SetupStepState.Failed);
    }

    [Fact]
    public void Interpret_continues_only_when_wsl_works_and_no_restart_is_pending()
    {
        var ok = new ElevatedRunResult { Ok = true };
        ElevatedSetupHelper.Interpret(ok, wslUsableNow: true, rebootPending: false).State.Should().Be(SetupStepState.Done);
        ElevatedSetupHelper.Interpret(ok, wslUsableNow: false, rebootPending: false).State.Should().Be(SetupStepState.WaitingForRestart);
        ElevatedSetupHelper.Interpret(ok, wslUsableNow: true, rebootPending: true).State.Should().Be(SetupStepState.WaitingForRestart);
        ElevatedSetupHelper.Interpret(new ElevatedRunResult { Ok = true, RestartRequired = true }, true, false).State.Should().Be(SetupStepState.WaitingForRestart);
    }

    [Fact]
    public void Command_line_runs_the_tasks_and_writes_the_result_file()
    {
        var path = Path.Combine(Path.GetTempPath(), "ezf-elev-" + Guid.NewGuid().ToString("N"), "r.json");
        try
        {
            var args = ElevatedSetupHelper.BuildArgs(new[] { ElevatedSetupHelper.EnableWslTask }, path);
            args[0].Should().Be(ElevatedSetupHelper.Switch);
            var r = new Runner { Exit = (_, _) => 3010 };
            ElevatedSetupHelper.RunCommandLine(args.Skip(1).ToList(), null, r.Run).Should().Be(0);
            var back = ElevatedSetupHelper.ReadResult(path)!;
            back.Ok.Should().BeTrue();
            back.RestartRequired.Should().BeTrue();
            back.Commands.Should().HaveCount(2).And.OnlyContain(c => c.ExitCode == 3010); // wsl --install, then the classic component

            // The helper never overwrites a result (the wizard deletes the old one first).
            ElevatedSetupHelper.RunCommandLine(new[] { ElevatedSetupHelper.EnableWsl1Task, "--result", path }, null, r.Run).Should().Be(1);
            File.Delete(path);
            ElevatedSetupHelper.RunCommandLine(new[] { ElevatedSetupHelper.EnableWsl1Task, "--result", path }, null, r.Run).Should().Be(0);
            ElevatedSetupHelper.ReadResult(path)!.Commands.Should().ContainSingle(c => c.Command.Contains("Microsoft-Windows-Subsystem-Linux"));

            File.Delete(path);
            ElevatedSetupHelper.RunCommandLine(new[] { "bogus", "--result", path }, null, r.Run).Should().Be(1);
        }
        finally { try { Directory.Delete(Path.GetDirectoryName(path)!, true); } catch { } }
    }

    [Fact]
    public async Task Executor_asks_for_elevation_once_through_the_helper_and_reads_its_result()
    {
        var probe = new Probe { WslWorks = false };
        var elev = new Elev(write: (args) =>
        {
            var p = args[args.ToList().IndexOf("--result") + 1];
            ElevatedSetupHelper.WriteResult(p, new ElevatedRunResult { Ok = true, Commands = { new ElevatedCommandLog { Command = "wsl.exe --install --no-distribution", ExitCode = 0 } } });
            probe.WslWorks = true; // works right away: no restart
            return 0;
        });
        var (plan, dir) = await RunEnableWsl(probe, elev);
        try
        {
            elev.Calls.Should().Be(1);
            elev.LastExe.Should().Be("C:\\app\\EzForensics.exe");
            elev.LastArgs.Should().StartWith(new[] { ElevatedSetupHelper.Switch, ElevatedSetupHelper.EnableWslTask });
            plan.Steps[0].State.Should().Be(SetupStepState.Done);
        }
        finally { Directory.Delete(dir, true); }
    }

    [Fact]
    public async Task Executor_forwards_the_helpers_log_lines_while_it_still_runs()
    {
        var probe = new Probe { WslWorks = false };
        bool seenLive = false;
        SetupStep? step = null;
        var elev = new Elev(write: args =>
        {
            var list = args.ToList();
            list.Should().Contain("--log");
            var logPath = list[list.IndexOf("--log") + 1];
            Path.GetFileName(logPath).Should().Be(ElevatedSetupHelper.EnableWslTask + ".log");
            Path.GetDirectoryName(logPath).Should().Be(Path.GetDirectoryName(list[list.IndexOf("--result") + 1]));
            using (var f = LineLogFile.TryOpen(logPath)!)
            {
                f.WriteLine("> wsl.exe --install --no-distribution");
                f.WriteLine("Installing: Virtual Machine Platform");
                // the wizard shows the line before the helper exits
                var until = DateTime.UtcNow.AddSeconds(10);
                while (DateTime.UtcNow < until && !(step?.LogText.Contains("Installing: Virtual Machine Platform") ?? false)) Thread.Sleep(20);
                seenLive = step?.LogText.Contains("Installing: Virtual Machine Platform") ?? false;
            }
            ElevatedSetupHelper.WriteResult(list[list.IndexOf("--result") + 1], new ElevatedRunResult { Ok = true, Commands = { new ElevatedCommandLog { Command = "wsl.exe --install --no-distribution" } } });
            probe.WslWorks = true;
            return 0;
        });
        var (plan, dir) = await RunEnableWsl(probe, elev, s => step = s);
        try
        {
            seenLive.Should().BeTrue();
            plan.Steps[0].State.Should().Be(SetupStepState.Done);
            plan.Steps[0].LogText.Should().Contain("> wsl.exe --install --no-distribution");
            plan.Steps[0].LogText.Should().NotContain("-> exit code", "the live lines replace the summary from the result file");
        }
        finally { Directory.Delete(dir, true); }
    }

    [Fact]
    public async Task Executor_skips_elevation_when_wsl_already_works_and_reports_a_declined_prompt()
    {
        var already = new Elev(_ => 0);
        var (plan, dir) = await RunEnableWsl(new Probe { WslWorks = true }, already);
        Directory.Delete(dir, true);
        already.Calls.Should().Be(0);
        plan.Steps[0].State.Should().Be(SetupStepState.AlreadyInstalled);

        var declined = new Elev(_ => throw new OperationCanceledException("declined"));
        (plan, dir) = await RunEnableWsl(new Probe { WslWorks = false }, declined);
        Directory.Delete(dir, true);
        plan.Steps[0].State.Should().Be(SetupStepState.Failed);
        plan.Steps[0].Message.Should().Be(DefaultSetupExecutor.DeclinedMessage);
    }

    [Fact]
    public async Task Executor_marks_the_wsl_welcome_flag_before_and_closes_its_window_after_a_wsl_step()
    {
        var since = DateTime.Now.AddMinutes(-1);
        var store = new OobeStore();
        var procs = new WelcomeProcs();
        procs.Items.Add(new WslSettingsProcess(42, "wslsettings", DateTime.Now, true));
        var (plan, dir) = await RunEnableWsl(new Probe { WslWorks = true }, new Elev(_ => 0), guard: new WslWelcomeGuard(store, procs, since));
        Directory.Delete(dir, true);
        store.Value.Should().Be(1);
        procs.Closed.Should().Equal(42);
        var log = plan.Steps[0].LogText;
        log.Should().Contain("OOBEComplete").And.Contain("closed the 'Welcome to WSL' window");
        log.IndexOf("OOBEComplete", StringComparison.Ordinal).Should().BeLessThan(log.IndexOf("closed the", StringComparison.Ordinal));
    }

    private sealed class OobeStore : IWslOobeStore
    {
        public int? Value;
        public int? Read() => Value;
        public bool MarkComplete() { Value = 1; return true; }
    }

    private sealed class WelcomeProcs : IWslSettingsProcesses
    {
        public List<WslSettingsProcess> Items { get; } = new();
        public List<int> Closed { get; } = new();
        public IReadOnlyList<WslSettingsProcess> List() => Items;
        public bool CloseMainWindow(int id) { Closed.Add(id); return true; }
    }

    private static async Task<(SetupPlan, string)> RunEnableWsl(Probe probe, Elev elev, Action<SetupStep>? onStep = null, WslWelcomeGuard? guard = null)
    {
        var dir = Path.Combine(Path.GetTempPath(), "ezf-exec-" + Guid.NewGuid().ToString("N"));
        var env = new SetupEnvironment
        {
            Queue = new JobQueue(new FakeLocator(null), 1, TimeSpan.FromSeconds(10)),
            CreateHttp = () => new HttpClient(),
            Settings = new AppSettings(),
            SaveSettings = () => { },
            Locator = new FakeLocator(null),
            MaintenanceDir = dir,
            Elevation = elev,
            Probe = probe,
            ElevatedHelperExe = "C:\\app\\EzForensics.exe",
            RebootPending = () => false,
            WslWelcome = guard,
        };
        var plan = new SetupPlan
        {
            Selection = new SetupSelection(),
            Steps = new[] { new SetupStep { Id = "enable-wsl", Kind = SetupStepKind.EnableWsl, Title = "Enable WSL", RequiresAdmin = true } },
        };
        onStep?.Invoke(plan.Steps[0]);
        await new SetupRunner(new DefaultSetupExecutor(env)).RunAsync(plan);
        Directory.CreateDirectory(dir);
        return (plan, dir);
    }

    private sealed class Probe : IRuntimeProbe
    {
        public bool WslWorks;
        public bool FileExists(string path) => path.EndsWith("wsl.exe", StringComparison.OrdinalIgnoreCase);
        public bool DirectoryExists(string path) => false;
        public string? FindOnPath(string fileName) => null;
        public string? Run(string exe, IReadOnlyList<string> args, TimeSpan timeout, IReadOnlyDictionary<string, string>? environment = null) =>
            args.SequenceEqual(new[] { "--status" }) && WslWorks ? "Default Version: 2" : null;
    }

    private sealed class Elev : IElevatedLauncher
    {
        private readonly Func<IReadOnlyList<string>, int> _write;
        public Elev(Func<IReadOnlyList<string>, int> write) => _write = write;
        public int Calls;
        public string? LastExe;
        public IReadOnlyList<string> LastArgs = Array.Empty<string>();
        public Task<int> RunElevatedAsync(string exe, IReadOnlyList<string> args, CancellationToken ct)
        {
            Calls++;
            LastExe = exe;
            LastArgs = args;
            return Task.FromResult(_write(args));
        }
    }
}

public class SetupResumeLaunchTests
{
    [Fact]
    public void Command_quotes_the_exe_and_handles_dotnet_dev_builds()
    {
        SetupResumeLaunch.BuildCommand(@"C:\Users\u\AppData\Local\EzForensics\current\EzForensics.exe")
            .Should().Be("\"C:\\Users\\u\\AppData\\Local\\EzForensics\\current\\EzForensics.exe\" --resume-setup");
        SetupResumeLaunch.BuildCommand(@"C:\Program Files\dotnet\dotnet.exe", @"D:\src\EzForensics.dll")
            .Should().Be("\"C:\\Program Files\\dotnet\\dotnet.exe\" \"D:\\src\\EzForensics.dll\" --resume-setup");
        SetupResumeLaunch.BuildCommand(@"C:\Program Files\dotnet\dotnet.exe").Should().BeNull();
        SetupResumeLaunch.BuildCommand(null).Should().BeNull();
        SetupResumeLaunch.HasSwitch(new[] { "--RESUME-SETUP" }).Should().BeTrue();
    }

    [Fact]
    public void RunOnce_value_is_written_and_removed_under_hkcu()
    {
        if (!OperatingSystem.IsWindows()) return;
        var key = @"Software\EzForensicsTests\RunOnce-" + Guid.NewGuid().ToString("N");
        try
        {
            SetupResumeLaunch.Register("\"x.exe\" --resume-setup", key).Should().BeTrue();
            SetupResumeLaunch.Read(key).Should().Be("\"x.exe\" --resume-setup");
            SetupResumeLaunch.Unregister(key);
            SetupResumeLaunch.Read(key).Should().BeNull();
            SetupResumeLaunch.Unregister(key); // missing value: no error
        }
        finally { Microsoft.Win32.Registry.CurrentUser.DeleteSubKeyTree(@"Software\EzForensicsTests", throwOnMissingSubKey: false); }
    }
}
