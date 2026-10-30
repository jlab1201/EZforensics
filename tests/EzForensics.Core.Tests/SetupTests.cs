using EzForensics.Core.Zeek;
using EzForensics.Core.Runtimes;
using EzForensics.Core.Settings;
using EzForensics.Core.Setup;
using FluentAssertions;

namespace EzForensics.Core.Tests;

public class SetupCatalogTests
{
    private static readonly SetupCatalog Cat = SetupCatalog.Default;

    [Fact]
    public void Catalog_has_the_five_bundles_with_the_decided_defaults()
    {
        Cat.Bundles.Select(b => b.Id).Should().Equal(SetupIds.BundleArtifacts, SetupIds.BundleTimeline, SetupIds.BundleNetwork, SetupIds.BundleMemory);
        Cat.Bundles.Single(b => b.Id == SetupIds.BundleArtifacts).DefaultOn.Should().BeTrue();
        Cat.Bundles.Single(b => b.Id == SetupIds.BundleTimeline).DefaultOn.Should().BeTrue();
        Cat.Bundles.Single(b => b.Id == SetupIds.BundleArtifacts).Items.Where(i => i.Selectable && !i.OptIn).Should().OnlyContain(i => i.DefaultOn,
            "Minimal ticks Windows artifacts fully");
        Cat.Bundles.Single(b => b.Id == SetupIds.BundleNetwork).DefaultOn.Should().BeFalse();
        var memory = Cat.Bundles.Single(b => b.Id == SetupIds.BundleMemory);
        memory.ComingSoon.Should().BeFalse();
        memory.DefaultOn.Should().BeFalse("Minimal leaves Memory out");
        Cat.Find(SetupIds.Volatility)!.ComingSoon.Should().BeFalse();
        Cat.Find(SetupIds.Volatility)!.DefaultOn.Should().BeTrue();
        Cat.Find(SetupIds.VolatilitySymbols)!.DefaultOn.Should().BeFalse();
        Cat.Find(SetupIds.VolatilitySymbols)!.OptIn.Should().BeTrue();
        Cat.Find(SetupIds.Rita)!.ComingSoon.Should().BeFalse();
        Cat.Find(SetupIds.Rita)!.DefaultOn.Should().BeFalse("RITA is Everything-only");
        Cat.Find(SetupIds.Rita)!.Requires.Should().BeEquivalentTo(new[] { SetupIds.EzDistro, SetupIds.Zeek });
        Cat.Find(SetupIds.Rita)!.Description.Should().Contain("GPL-3.0");
        Cat.Find("autoruns").Should().BeNull("Sysinternals Autoruns was removed; the built-in persistence collector replaces it");
    }

    [Fact]
    public void Cli_item_lists_every_cli_zip_and_no_gui_zip()
    {
        var cli = Cat.Find(SetupIds.EzCli)!;
        cli.Zips.Should().Contain(new[] { "MFTECmd.zip", "EvtxECmd.zip", "RECmd.zip", "SQLECmd.zip" });
        cli.Zips.Should().NotContain(new[] { "RegistryExplorer.zip", "ShellBagsExplorer.zip" });
        Cat.AllItems.SelectMany(i => i.Zips).Should().OnlyContain(z => cli.Zips.Contains(z), "setup installs only the EZ command-line parsers");
        Cat.Find("ez-timeline-explorer").Should().BeNull();
        Cat.Find("ez-gui-viewers").Should().BeNull();
        cli.DownloadMB.Should().BeInRange(40, 200);
        Cat.IsNet9Only("SQLECmd.zip").Should().BeTrue();
        Cat.IsNet9Only("WxTCmd.zip").Should().BeTrue();
        Cat.IsNet9Only("RegistryExplorer.zip").Should().BeFalse("only zips setup installs are listed");
        Cat.IsNet9Only("MFTECmd.zip").Should().BeFalse();
    }

    [Theory]
    [InlineData("minimal", SetupProfile.Minimal)]
    [InlineData("Everything", SetupProfile.Everything)]
    [InlineData(" CUSTOM ", SetupProfile.Custom)]
    [InlineData("recommended", SetupProfile.Minimal)]
    [InlineData(" Recommended ", SetupProfile.Minimal)]
    public void Profiles_parse_like_a_cli_argument(string text, SetupProfile expected)
    {
        SetupSelection.TryParseProfile(text, out var p).Should().BeTrue();
        p.Should().Be(expected);
        SetupSelection.TryParseProfile("bogus", out _).Should().BeFalse();
        SetupSelection.TryParseProfile("7", out _).Should().BeFalse();
    }

    [Fact]
    public void Minimal_ticks_all_of_windows_artifacts_and_the_private_net9_when_net9_is_missing()
    {
        var s = SetupSelection.ForProfile(SetupProfile.Minimal, Cat, Sys(net9: false));
        s.Items.Should().Contain(new[] { SetupIds.EzCli, SetupIds.EzSync, SetupIds.Dotnet9,
            SetupIds.Plaso, SetupIds.PersistenceBuiltIn, SetupIds.ComputerDetails });
        s.Items.Should().NotContain(new[] { SetupIds.Zeek, SetupIds.Volatility, SetupIds.VolatilitySymbols, SetupIds.Rita });

        var withNet9 = SetupSelection.ForProfile(SetupProfile.Minimal, Cat, Sys(net9: true));
        withNet9.Items.Should().Contain(new[] { SetupIds.EzCli, SetupIds.EzSync });
        withNet9.Items.Should().NotContain(SetupIds.Dotnet9, ".NET 9 is already installed");
    }

    [Fact]
    public void Minimal_without_net9_plans_the_private_runtime_and_net9_builds()
    {
        var sys = Sys(net9: false);
        var sel = SetupSelection.ForProfile(SetupProfile.Minimal, Cat, sys);
        var plan = SetupPlanner.Plan(Cat, sel, sys);
        plan.EzNetVersion.Should().Be(9);
        plan.Steps.Should().Contain(s => s.Kind == SetupStepKind.InstallDotnet);
        plan.Steps.Single(s => s.Kind == SetupStepKind.DownloadEzTools).Zips.Should().Contain(new[] { "SQLECmd.zip", "WxTCmd.zip" }).And.NotContain("RegistryExplorer.zip");
    }

    [Fact]
    public void Everything_selects_all_available_items_but_never_coming_soon()
    {
        var s = SetupSelection.ForProfile(SetupProfile.Everything, Cat, Sys(net9: false));
        s.Items.Should().Contain(new[] { SetupIds.Zeek, SetupIds.EzCli, SetupIds.Dotnet9 });
        s.Items.Should().Contain(SetupIds.Volatility);
        s.Items.Should().Contain(SetupIds.Rita);
        s.Items.Should().Contain(SetupIds.VolatilitySymbols, "Everything includes the opt-in symbol pack");
        SetupSelection.ForProfile(SetupProfile.Everything, Cat, Sys(net9: true)).Items.Should().NotContain(SetupIds.Dotnet9);
    }

    [Fact]
    public void Third_party_licences_list_every_downloaded_engine_with_https_links()
    {
        ThirdPartyLicenses.All.Select(l => l.Component).Should().Contain(c => c.Contains("Zimmerman"))
            .And.Contain(c => c.Contains("plaso")).And.Contain(c => c.Contains("Volatility")).And.Contain(c => c.Contains("Zeek"))
            .And.Contain(c => c.Contains("RITA")).And.Contain(c => c.Contains("Ubuntu")).And.Contain(c => c.Contains(".NET"));
        ThirdPartyLicenses.All.Should().OnlyContain(l => l.Url.StartsWith("https://"));
        ThirdPartyLicenses.Text.Should().Contain("Zeek: BSD 3-Clause License");
    }

    internal static SystemCheckResult Sys(bool net9 = true, string? python = null, WslStatus wsl = WslStatus.Enabled, string? distro = "Ubuntu",
        bool dockerRunning = false, bool dockerInstalled = false, string? plaso = null, params string[] installedZips) => new()
    {
        OsName = "Windows 11", OsBuild = 26200, HasSystemNet9 = net9, Python = python, Wsl = wsl, WslZeekDistro = distro,
        Docker = new DockerState(dockerRunning || dockerInstalled ? "docker.exe" : null, dockerRunning, dockerInstalled || dockerRunning ? "Docker Desktop.exe" : null, null),
        PlasoVersion = plaso, InstalledZips = installedZips, FreeDiskBytes = 100L << 30,
    };
}

public class SetupPlannerTests
{
    private static readonly SetupCatalog Cat = SetupCatalog.Default;
    private static SystemCheckResult Sys(bool net9 = true, string? python = null, WslStatus wsl = WslStatus.Enabled, string? distro = "Ubuntu",
        bool dockerRunning = false, bool dockerInstalled = false, string? plaso = null, params string[] installedZips) =>
        SetupCatalogTests.Sys(net9, python, wsl, distro, dockerRunning, dockerInstalled, plaso, installedZips);

    private static SetupSelection Sel(params string[] ids)
    {
        var s = new SetupSelection();
        s.Items.UnionWith(ids);
        return s;
    }

    [Fact]
    public void Minimal_without_net9_items_or_python_plans_net4_tools_then_python_venv_pip()
    {
        var sys = Sys(net9: false, python: null);
        var sel = SetupSelection.ForProfile(SetupProfile.Minimal, Cat, sys);
        sel.Items.Remove(SetupIds.Dotnet9); // the user unticked it
        var plan = SetupPlanner.Plan(Cat, sel, sys);

        plan.EzNetVersion.Should().Be(4);
        plan.Steps.Select(s => s.Kind).Should().ContainInOrder(SetupStepKind.DownloadEzTools, SetupStepKind.SyncMaps,
            SetupStepKind.ProvisionPython, SetupStepKind.CreatePlasoVenv, SetupStepKind.InstallPlaso);
        plan.Steps.Should().NotContain(s => s.Kind == SetupStepKind.InstallDotnet);
        var ez = plan.Steps.Single(s => s.Kind == SetupStepKind.DownloadEzTools);
        ez.Args["netVersion"].Should().Be("4");
        ez.Zips.Should().NotContain("SQLECmd.zip").And.Contain("MFTECmd.zip");
        plan.Notes.Should().Contain(n => n.Contains("SQLECmd"));
        plan.Steps.Single(s => s.Kind == SetupStepKind.SyncMaps).DependsOn.Should().Equal(ez.Id);
        plan.Steps.Single(s => s.Kind == SetupStepKind.CreatePlasoVenv).DependsOn.Should().Equal("python");
        plan.Steps.Single(s => s.Kind == SetupStepKind.InstallPlaso).DependsOn.Should().Equal("plaso-venv");
        plan.Steps.Where(s => s.Kind == SetupStepKind.BuiltIn).Should().HaveCount(2).And.OnlyContain(s => s.State == SetupStepState.Done);
        plan.NeedsAdmin.Should().BeFalse();
        plan.TotalDownloadMB.Should().BeGreaterThan(50);
        plan.EstimatedDuration.Should().BeGreaterThan(TimeSpan.FromMinutes(1));
    }

    [Fact]
    public void Python_present_skips_the_private_python()
    {
        var plan = SetupPlanner.Plan(Cat, Sel(SetupIds.Plaso), Sys(python: @"C:\Python312\python.exe"));
        plan.Steps.Select(s => s.Kind).Should().Equal(SetupStepKind.CreatePlasoVenv, SetupStepKind.InstallPlaso);
        plan.Steps[0].DependsOn.Should().BeEmpty();
    }

    [Fact]
    public void Installed_plaso_is_already_installed_and_costs_nothing()
    {
        var plan = SetupPlanner.Plan(Cat, Sel(SetupIds.Plaso), Sys(plaso: "20260720"));
        plan.Steps.Should().ContainSingle().Which.State.Should().Be(SetupStepState.AlreadyInstalled);
        plan.TotalDownloadMB.Should().Be(0);
        plan.IsEmpty.Should().BeTrue();
    }

    [Fact]
    public void Net9_item_on_a_machine_without_net9_adds_the_private_runtime_first()
    {
        // No item of the default catalog needs .NET 9 any more (the GUI viewers left setup): a catalog with one that does.
        var cat = new SetupCatalog(new[]
        {
            new SetupBundle
            {
                Id = "b", Title = "b", Description = "",
                Items = new[]
                {
                    new SetupItem { Id = "net9-tool", Title = "net9 tool", Description = "", RequiresNet9 = true, Zips = new[] { "SQLECmd.zip" } },
                    new SetupItem { Id = SetupIds.Dotnet9, Title = ".NET 9", Description = "" },
                },
            },
        }, new[] { "SQLECmd.zip" });
        var plan = SetupPlanner.Plan(cat, Sel("net9-tool"), Sys(net9: false));
        plan.Selection.Items.Should().Contain(SetupIds.Dotnet9);
        plan.Notes.Should().Contain(n => n.Contains(".NET 9"));
        plan.EzNetVersion.Should().Be(9);
        var dotnet = plan.Steps.Single(s => s.Kind == SetupStepKind.InstallDotnet);
        var ez = plan.Steps.Single(s => s.Kind == SetupStepKind.DownloadEzTools);
        plan.Steps.ToList().IndexOf(dotnet).Should().BeLessThan(plan.Steps.ToList().IndexOf(ez));
        ez.DependsOn.Should().Equal(dotnet.Id);
        ez.Zips.Should().Equal("SQLECmd.zip");
    }

    [Fact]
    public void Dotnet_item_on_a_machine_with_net9_is_already_installed()
    {
        var plan = SetupPlanner.Plan(Cat, Sel(SetupIds.Dotnet9, SetupIds.EzCli), Sys(net9: true));
        plan.Steps.Single(s => s.Kind == SetupStepKind.InstallDotnet).State.Should().Be(SetupStepState.AlreadyInstalled);
        plan.Steps.Single(s => s.Kind == SetupStepKind.DownloadEzTools).DependsOn.Should().BeEmpty();
    }

    [Fact]
    public void Sync_requires_the_cli_tools()
    {
        var plan = SetupPlanner.Plan(Cat, Sel(SetupIds.EzSync), Sys());
        plan.Selection.Items.Should().Contain(SetupIds.EzCli);
        plan.Steps.Select(s => s.Kind).Should().Equal(SetupStepKind.DownloadEzTools, SetupStepKind.SyncMaps);
    }

    [Fact]
    public void Tools_already_present_are_not_downloaded_again()
    {
        var zips = Cat.Find(SetupIds.EzCli)!.Zips.ToArray();
        var plan = SetupPlanner.Plan(Cat, Sel(SetupIds.EzCli), Sys(installedZips: zips));
        plan.Steps.Single().State.Should().Be(SetupStepState.AlreadyInstalled);
        plan.TotalDownloadMB.Should().Be(0);
    }

    private static EzDistroInfo ReadyDistro => new(true, true, 2, "Stopped", true, "ubuntu-base 24.04.5 / schema 1", @"C:\d\wsl\EzForensics", 400L << 20, "wsl.exe");

    [Fact]
    public void Zeek_on_wsl_requires_the_private_distro_then_installs_in_it()
    {
        var plan = SetupPlanner.Plan(Cat, Sel(SetupIds.Zeek), Sys(wsl: WslStatus.Enabled, distro: "Ubuntu-24.04"));
        plan.Selection.Items.Should().Contain(SetupIds.EzDistro, "Zeek requires the EZ Forensics Linux environment");
        plan.Steps.Select(s => s.Id).Should().Equal("ez-distro", "zeek");
        var ez = plan.Steps[0];
        ez.Kind.Should().Be(SetupStepKind.InstallEzDistro);
        ez.RequiresAdmin.Should().BeFalse();
        ez.DownloadMB.Should().Be(EzDistro.DownloadMB);
        var z = plan.Steps[1];
        z.Kind.Should().Be(SetupStepKind.InstallZeekWsl);
        z.Args["distro"].Should().Be(EzDistro.Name, "the user's own Ubuntu is no longer the target");
        z.DependsOn.Should().Equal("ez-distro");
        plan.NeedsAdmin.Should().BeFalse();
    }

    [Fact]
    public void Ready_private_distro_is_already_installed_and_zeek_does_not_wait_for_it()
    {
        var plan = SetupPlanner.Plan(Cat, Sel(SetupIds.Zeek), Sys() with { EzDistroState = ReadyDistro });
        plan.Steps[0].State.Should().Be(SetupStepState.AlreadyInstalled);
        plan.Steps[1].DependsOn.Should().BeEmpty();
        plan.TotalDownloadMB.Should().Be(ZeekSetup.WslDownloadMB);
    }

    [Fact]
    public void Zeek_wsl_not_enabled_plans_elevated_enable_then_distro_then_zeek_after_restart()
    {
        var plan = SetupPlanner.Plan(Cat, Sel(SetupIds.Zeek), Sys(wsl: WslStatus.NotEnabled, distro: null));
        plan.Steps.Select(s => s.Kind).Should().Equal(SetupStepKind.EnableWsl, SetupStepKind.InstallEzDistro, SetupStepKind.InstallZeekWsl);
        plan.Steps[0].RequiresAdmin.Should().BeTrue();
        plan.Steps[0].RequiresRestart.Should().BeTrue();
        plan.Steps[1].DependsOn.Should().Equal("enable-wsl");
        plan.Steps[2].DependsOn.Should().Equal("ez-distro");
        plan.NeedsAdmin.Should().BeTrue();
        plan.NeedsRestart.Should().BeTrue();
    }

    [Fact]
    public void Zeek_already_available_needs_no_private_distro()
    {
        var plan = SetupPlanner.Plan(Cat, Sel(SetupIds.Zeek), Sys() with { ZeekSummary = "Zeek 9.0.0 · WSL · distro Ubuntu" });
        plan.Selection.Items.Should().NotContain(SetupIds.EzDistro);
        plan.Steps.Single().State.Should().Be(SetupStepState.AlreadyInstalled);
    }

    [Fact]
    public void Private_distro_alone_can_be_installed()
    {
        var plan = SetupPlanner.Plan(Cat, Sel(SetupIds.EzDistro), Sys());
        plan.Steps.Single().Kind.Should().Be(SetupStepKind.InstallEzDistro);
    }

    [Fact]
    public void Wsl_not_available_explains_what_to_do()
    {
        var plan = SetupPlanner.Plan(Cat, Sel(SetupIds.Zeek), Sys(wsl: WslStatus.NotAvailable, distro: null));
        plan.Steps.Single().Kind.Should().Be(SetupStepKind.ZeekGuidance);
        plan.Steps.Single().Detail.Should().Contain("WSL is not available").And.NotContain("Docker", "setup installs Network in WSL only");
    }

    [Fact]
    public void Rita_pulls_in_distro_and_zeek_and_installs_last()
    {
        var plan = SetupPlanner.Plan(Cat, Sel(SetupIds.Rita), Sys());
        plan.Selection.Items.Should().Contain(new[] { SetupIds.EzDistro, SetupIds.Zeek, SetupIds.Rita });
        var ids = plan.Steps.Select(s => s.Id).ToList();
        ids.Should().ContainInOrder("ez-distro", "zeek", "rita");
        var rita = plan.Steps.Single(s => s.Kind == SetupStepKind.InstallRita);
        rita.DependsOn.Should().BeEquivalentTo(new[] { "ez-distro", "zeek" });
        rita.DownloadMB.Should().BeGreaterThan(200);
    }

    [Fact]
    public void Installed_rita_is_skipped_but_an_outdated_one_is_updated()
    {
        var current = SetupPlanner.Plan(Cat, Sel(SetupIds.Rita), Sys() with { RitaVersion = "5.1.2" });
        current.Steps.Single(s => s.Kind == SetupStepKind.InstallRita).State.Should().Be(SetupStepState.AlreadyInstalled);
        var outdated = SetupPlanner.Plan(Cat, Sel(SetupIds.Rita), Sys() with { RitaVersion = "5.1.1", RitaUpdateAvailable = true });
        outdated.Steps.Single(s => s.Kind == SetupStepKind.InstallRita).State.Should().NotBe(SetupStepState.AlreadyInstalled);
    }

    [Theory]
    [InlineData(SetupIds.Zeek)]
    [InlineData(SetupIds.Rita)]
    public void Zeek_or_rita_always_adds_the_hidden_distro_step_and_never_docker(string id)
    {
        var plan = SetupPlanner.Plan(Cat, Sel(id), Sys(dockerRunning: true, dockerInstalled: true));
        plan.Selection.Items.Should().Contain(SetupIds.EzDistro);
        plan.Steps.Should().Contain(s => s.Kind == SetupStepKind.InstallEzDistro);
        plan.Steps.Should().NotContain(s => s.Title.Contains("docker", StringComparison.OrdinalIgnoreCase));
        plan.Notes.Should().NotContain(n => n.Contains("Linux environment (WSL) added"), "the distro is hidden: no note about it");
    }

    [Fact]
    public void Rita_with_zeek_already_available_still_creates_the_distro()
    {
        var plan = SetupPlanner.Plan(Cat, Sel(SetupIds.Rita), Sys() with { ZeekSummary = "Zeek 9.0.0 · Docker" });
        plan.Steps.Select(s => s.Kind).Should().Contain(new[] { SetupStepKind.InstallEzDistro, SetupStepKind.InstallRita });
        plan.Steps.Single(s => s.Kind == SetupStepKind.InstallRita).DependsOn.Should().Contain("ez-distro");
    }

    [Fact]
    public void Unknown_ids_are_ignored()
    {
        var plan = SetupPlanner.Plan(Cat, Sel("nope"), Sys());
        plan.Steps.Should().BeEmpty();
        plan.Selection.Items.Should().BeEmpty();
    }

    [Fact]
    public void Plan_text_lists_steps_and_totals()
    {
        var plan = SetupPlanner.Plan(Cat, Sel(SetupIds.EzCli, SetupIds.Plaso), Sys(python: "py"));
        var text = plan.ToText();
        text.Should().Contain("Download Eric Zimmerman's tools").And.Contain("pip install plaso").And.Contain("Total download");
    }

    [Fact]
    public void Formatting_helpers()
    {
        SetupPlan.FormatDuration(TimeSpan.FromSeconds(20)).Should().Be("under a minute");
        SetupPlan.FormatDuration(TimeSpan.FromSeconds(200)).Should().Be("about 4 min");
        SetupPlan.FormatMB(512).Should().Be("512 MB");
        SetupPlan.FormatMB(2048).Should().Be("2.0 GB");
    }
}

public class SetupRunnerTests
{
    private sealed class FakeExecutor : ISetupExecutor
    {
        public readonly List<string> Ran = new();
        public Func<SetupStep, SetupStepResult> Result = _ => SetupStepResult.Ok("ok");
        public Task<SetupStepResult> ExecuteAsync(SetupStepContext c, CancellationToken ct)
        {
            ct.ThrowIfCancellationRequested();
            Ran.Add(c.Step.Id);
            c.Log("working");
            c.Report(0.5);
            return Task.FromResult(Result(c.Step));
        }
    }

    private static SetupPlan Plan(params SetupStep[] steps) => new() { Selection = new SetupSelection(), Steps = steps };
    private static SetupStep S(string id, params string[] deps) => new() { Id = id, Kind = SetupStepKind.DownloadEzTools, Title = id, DependsOn = deps };

    [Fact]
    public async Task Runs_in_order_and_reports_progress()
    {
        var plan = Plan(S("a"), S("b", "a"), S("c"));
        var exec = new FakeExecutor();
        var reports = new List<SetupRunProgress>();
        await new SetupRunner(exec).RunAsync(plan, new SyncProgress<SetupRunProgress>(reports.Add));
        exec.Ran.Should().Equal("a", "b", "c");
        plan.AllSucceeded.Should().BeTrue();
        plan.Steps[0].LogText.Should().Contain("working");
        reports.Last().Fraction.Should().Be(1);
    }

    [Fact]
    public async Task A_failure_skips_dependents_but_not_unrelated_steps_and_retry_resumes()
    {
        var plan = Plan(S("a"), S("b", "a"), S("c"));
        var exec = new FakeExecutor { Result = s => s.Id == "a" ? SetupStepResult.Fail("network down") : SetupStepResult.Ok("ok") };
        var runner = new SetupRunner(exec);
        await runner.RunAsync(plan);
        plan.Steps[0].State.Should().Be(SetupStepState.Failed);
        plan.Steps[0].Message.Should().Be("network down");
        plan.Steps[1].State.Should().Be(SetupStepState.Skipped);
        plan.Steps[1].Message.Should().Contain("\"a\"");
        plan.Steps[2].State.Should().Be(SetupStepState.Done);
        plan.AnyFailed.Should().BeTrue();

        exec.Ran.Clear();
        exec.Result = _ => SetupStepResult.Ok("ok");
        await runner.RunAsync(plan);
        exec.Ran.Should().Equal("a", "b"); // c already succeeded
        plan.AllSucceeded.Should().BeTrue();
    }

    [Fact]
    public async Task Exceptions_become_plain_failures()
    {
        var plan = Plan(S("a"));
        var exec = new FakeExecutor { Result = _ => throw new HttpRequestException("boom") };
        await new SetupRunner(exec).RunAsync(plan);
        plan.Steps[0].State.Should().Be(SetupStepState.Failed);
        plan.Steps[0].Message.Should().Contain("proxy");
    }

    [Fact]
    public async Task A_download_timeout_is_a_failure_not_a_cancel()
    {
        var plan = Plan(S("a"));
        var exec = new FakeExecutor { Result = _ => throw new TaskCanceledException("The request was canceled due to the configured HttpClient.Timeout.", new TimeoutException()) };
        await new SetupRunner(exec).RunAsync(plan);
        plan.Steps[0].State.Should().Be(SetupStepState.Failed);
        plan.Steps[0].Message.Should().Contain("timed out");
    }

    [Fact]
    public async Task A_cancel_requested_by_the_user_stays_cancelled()
    {
        using var cts = new CancellationTokenSource();
        var plan = Plan(S("a"));
        var exec = new FakeExecutor { Result = _ => { cts.Cancel(); throw new OperationCanceledException(cts.Token); } };
        await new SetupRunner(exec).RunAsync(plan, null, cts.Token);
        plan.Steps[0].State.Should().Be(SetupStepState.Cancelled);
    }

    [Fact]
    public async Task Restart_required_makes_dependents_wait()
    {
        var plan = Plan(S("enable-wsl"), S("zeek", "enable-wsl"));
        var exec = new FakeExecutor { Result = s => s.Id == "enable-wsl" ? SetupStepResult.RestartRequired("restart") : SetupStepResult.Ok("ok") };
        await new SetupRunner(exec).RunAsync(plan);
        plan.Steps[1].State.Should().Be(SetupStepState.WaitingForRestart);
        plan.WaitingForRestart.Should().BeTrue();
        exec.Ran.Should().Equal("enable-wsl");
    }

    [Fact]
    public async Task Cancel_marks_remaining_steps_cancelled()
    {
        using var cts = new CancellationTokenSource();
        var plan = Plan(S("a"), S("b"));
        var exec = new FakeExecutor { Result = s => { cts.Cancel(); return SetupStepResult.Ok("ok"); } };
        await new SetupRunner(exec).RunAsync(plan, null, cts.Token);
        plan.Steps[0].State.Should().Be(SetupStepState.Done);
        plan.Steps[1].State.Should().Be(SetupStepState.Cancelled);
    }

    [Fact]
    public async Task Already_installed_steps_are_not_run()
    {
        var a = S("a");
        a.State = SetupStepState.AlreadyInstalled;
        var plan = Plan(a, S("b", "a"));
        var exec = new FakeExecutor();
        await new SetupRunner(exec).RunAsync(plan);
        exec.Ran.Should().Equal("b");
    }

    private sealed class SyncProgress<T> : IProgress<T>
    {
        private readonly Action<T> _a;
        public SyncProgress(Action<T> a) => _a = a;
        public void Report(T value) => _a(value);
    }
}

public class SetupWizardPolicyTests
{
    [Fact]
    public void The_wizard_opens_by_itself_only_on_the_first_start()
    {
        var s = new AppSettings();
        SetupWizardPolicy.Startup(s).Should().Be(StartupMode.WizardFirst);
        s.SetupWizardSeen = true; // closed, skipped or finished once: never again by itself (nothing installed shows a banner instead)
        SetupWizardPolicy.Startup(s).Should().Be(StartupMode.MainWindow);
        SetupWizardPolicy.Startup(new AppSettings { SetupWizardDontShow = true }).Should().Be(StartupMode.MainWindow);
    }

    [Fact]
    public void A_pending_restart_always_resumes_and_licences_are_not_carried_over()
    {
        var sel = new SetupSelection();
        sel.Items.UnionWith(new[] { SetupIds.Zeek, SetupIds.Plaso });
        sel.AcceptedLicenses.Add("some-licence");
        var s = new AppSettings { SetupWizardSeen = true, SetupWizardDontShow = true, SetupResume = SetupWizardPolicy.ToResume(sel) };
        SetupWizardPolicy.Startup(s).Should().Be(StartupMode.ResumeWizard);
        var back = SetupWizardPolicy.FromResume(s.SetupResume!);
        back.Items.Should().BeEquivalentTo(sel.Items);
        back.AcceptedLicenses.Should().BeEmpty();
    }

    [Fact]
    public void Old_settings_with_a_zeek_runtime_still_load()
    {
        var path = Path.Combine(Path.GetTempPath(), "ezf-settings-" + Guid.NewGuid().ToString("N") + ".json");
        try
        {
            File.WriteAllText(path, """{ "setupWizardSeen": true, "setupResume": { "items": ["zeek", "rita"], "zeekRuntime": "Docker", "savedUtc": "2026-10-01T00:00:00Z" } }""");
            var s = new JsonSettingsStore(path).Load();
            s.SetupWizardSeen.Should().BeTrue("the file was read, not replaced by defaults");
            SetupWizardPolicy.FromResume(s.SetupResume!).Items.Should().BeEquivalentTo(new[] { SetupIds.Zeek, SetupIds.Rita });
        }
        finally { File.Delete(path); }
    }

    [Fact]
    public void Plan_is_stale_when_the_ticks_change()
    {
        var a = new SetupSelection(); a.Items.UnionWith(new[] { SetupIds.Zeek, SetupIds.Plaso });
        var same = new SetupSelection(); same.Items.UnionWith(new[] { "PLASO", SetupIds.Zeek });
        var other = new SetupSelection(); other.Items.Add(SetupIds.Plaso);
        var key = SetupWizardPolicy.SelectionKey(a);
        SetupWizardPolicy.PlanIsStale(key, same).Should().BeFalse("order and case do not matter");
        SetupWizardPolicy.PlanIsStale(key, other).Should().BeTrue();
        SetupWizardPolicy.PlanIsStale(null, a).Should().BeTrue("no plan yet");
    }

    [Fact]
    public void Failure_text_is_one_short_line_per_failed_step_plus_the_hint()
    {
        var steps = new[]
        {
            new SetupStep { Id = "zeek", Kind = SetupStepKind.InstallZeekWsl, Title = "Zeek: install in the EZ Forensics Linux environment (after the restart)",
                Message = "apt-get failed with exit code 100. See the log, then click Retry (apt continues where it stopped).\nmore detail" },
            new SetupStep { Id = "rita", Kind = SetupStepKind.InstallRita, Title = "RITA", Message = new string('x', 300) },
            new SetupStep { Id = "py", Kind = SetupStepKind.ProvisionPython, Title = "Python" },
        };
        var lines = SetupWizardPolicy.FailureText(steps).Split(Environment.NewLine);
        lines.Should().HaveCount(4);
        lines[0].Should().Be("Zeek: install in the EZ Forensics Linux environment: apt-get failed with exit code 100.");
        lines[1].Length.Should().BeLessThan(100);
        lines[2].Should().Be("Python: failed.");
        lines[3].Should().Be("Fix it and click Retry, or Finish.");
    }
}

[Collection("EnvironmentVariables")]
public class AppPathsTests : IDisposable
{
    private readonly string _dir = Path.Combine(Path.GetTempPath(), "ezf-paths-" + Guid.NewGuid().ToString("N"));
    private readonly Dictionary<string, string?> _saved = new();

    public AppPathsTests()
    {
        foreach (var n in new[] { "EZFORENSICS_SETTINGS", "EZHUB_SETTINGS", "EZFORENSICS_DATA", "EZHUB_DATA", "EZFORENSICS_PLASO_ROOT", "EZHUB_PLASO_ROOT" })
            _saved[n] = Environment.GetEnvironmentVariable(n);
    }

    public void Dispose()
    {
        foreach (var (k, v) in _saved) Environment.SetEnvironmentVariable(k, v);
        try { Directory.Delete(_dir, true); } catch { }
    }

    [Fact]
    public void New_env_names_win_and_old_ones_still_work()
    {
        Environment.SetEnvironmentVariable("EZFORENSICS_SETTINGS", null);
        Environment.SetEnvironmentVariable("EZHUB_SETTINGS", @"C:\old\settings.json");
        AppPaths.SettingsPath.Should().Be(@"C:\old\settings.json");
        Environment.SetEnvironmentVariable("EZFORENSICS_SETTINGS", @"C:\new\settings.json");
        AppPaths.SettingsPath.Should().Be(@"C:\new\settings.json");
        JsonSettingsStore.DefaultPath.Should().Be(@"C:\new\settings.json");
    }

    [Fact]
    public void Default_data_root_is_outside_the_installer_folder()
    {
        Environment.SetEnvironmentVariable("EZFORENSICS_DATA", null);
        Environment.SetEnvironmentVariable("EZHUB_DATA", null);
        var install = AppPaths.InstallRoot.TrimEnd('\\') + "\\";
        (AppPaths.DataRoot + "\\").StartsWith(install, StringComparison.OrdinalIgnoreCase).Should().BeFalse();
        AppPaths.DataRoot.Should().EndWith("EzForensicsData");
        AppPaths.LegacyRoot.Should().EndWith("EzHub");
    }

    [Fact]
    public void Data_root_override_moves_everything_and_ignores_the_legacy_folder()
    {
        Environment.SetEnvironmentVariable("EZFORENSICS_DATA", _dir);
        Environment.SetEnvironmentVariable("EZFORENSICS_SETTINGS", null);
        Environment.SetEnvironmentVariable("EZHUB_SETTINGS", null);
        Environment.SetEnvironmentVariable("EZFORENSICS_PLASO_ROOT", null);
        Environment.SetEnvironmentVariable("EZHUB_PLASO_ROOT", null);
        AppPaths.SettingsPath.Should().Be(Path.Combine(_dir, "settings.json"));
        AppPaths.DefaultToolsRoot.Should().Be(Path.Combine(_dir, "Tools"));
        AppPaths.ComponentRoot("plaso", "PLASO_ROOT", _ => true).Should().Be(Path.Combine(_dir, "plaso"));
    }

    [Fact]
    public void Migration_copies_settings_and_flags_once_and_never_touches_the_old_folder()
    {
        var legacy = Path.Combine(_dir, "EzHub");
        var data = Path.Combine(_dir, "EzForensicsData");
        Directory.CreateDirectory(Path.Combine(legacy, "flags"));
        Directory.CreateDirectory(Path.Combine(legacy, "plaso", "venv"));
        File.WriteAllText(Path.Combine(legacy, "settings.json"), "{\"toolsRoot\":\"C:\\\\old\\\\Tools\"}");
        File.WriteAllText(Path.Combine(legacy, "flags", "abc.json"), "{}");

        var r = DataMigration.Run(legacy, data);
        r.Migrated.Should().BeTrue();
        File.ReadAllText(Path.Combine(data, "settings.json")).Should().Contain("old");
        File.Exists(Path.Combine(data, "flags", "abc.json")).Should().BeTrue();
        File.Exists(Path.Combine(data, DataMigration.MarkerName)).Should().BeTrue();
        Directory.Exists(Path.Combine(data, "plaso")).Should().BeFalse("venvs are not relocatable, so they are never copied");
        File.Exists(Path.Combine(legacy, "settings.json")).Should().BeTrue();
        Directory.Exists(Path.Combine(legacy, "plaso", "venv")).Should().BeTrue();

        // Second run: nothing to do; a changed new settings file is never overwritten.
        File.WriteAllText(Path.Combine(data, "settings.json"), "{}");
        DataMigration.Run(legacy, data).Migrated.Should().BeFalse();
        File.ReadAllText(Path.Combine(data, "settings.json")).Should().Be("{}");
    }

    [Fact]
    public void Migration_without_ezhub_does_nothing()
    {
        var r = DataMigration.Run(Path.Combine(_dir, "none"), Path.Combine(_dir, "data"));
        r.Migrated.Should().BeFalse();
        Directory.Exists(Path.Combine(_dir, "data")).Should().BeFalse();
    }

    [Fact]
    public void Existing_settings_in_the_new_folder_are_kept()
    {
        var legacy = Path.Combine(_dir, "EzHub");
        var data = Path.Combine(_dir, "EzForensicsData");
        Directory.CreateDirectory(legacy);
        Directory.CreateDirectory(data);
        File.WriteAllText(Path.Combine(legacy, "settings.json"), "{\"a\":1}");
        File.WriteAllText(Path.Combine(data, "settings.json"), "{\"b\":2}");
        var r = DataMigration.Run(legacy, data);
        File.ReadAllText(Path.Combine(data, "settings.json")).Should().Be("{\"b\":2}");
        r.Notes.Should().NotBeEmpty();
    }

    [Fact]
    public void Legacy_tools_folder_is_moved_once()
    {
        var legacy = Path.Combine(_dir, "EzHub", "Tools");
        var fresh = Path.Combine(_dir, "EzForensicsData", "Tools");
        Directory.CreateDirectory(Path.Combine(legacy, "net9"));
        File.WriteAllText(Path.Combine(legacy, "net9", "JLECmd.exe"), "x");

        var (root, note) = DataMigration.MoveLegacyTools(legacy + "\\", legacy, fresh);
        root.Should().Be(fresh);
        note.Should().Contain("Moved");
        File.Exists(Path.Combine(fresh, "net9", "JLECmd.exe")).Should().BeTrue();
        Directory.Exists(legacy).Should().BeFalse();

        // the setting now points at the new folder: nothing more to do
        DataMigration.MoveLegacyTools(fresh, legacy, fresh).NewRoot.Should().BeNull();
    }

    [Fact]
    public void Legacy_tools_move_leaves_custom_roots_and_full_targets_alone()
    {
        var legacy = Path.Combine(_dir, "EzHub", "Tools");
        var fresh = Path.Combine(_dir, "EzForensicsData", "Tools");
        Directory.CreateDirectory(legacy);
        Directory.CreateDirectory(fresh);
        File.WriteAllText(Path.Combine(fresh, "keep.txt"), "x");

        DataMigration.MoveLegacyTools(Path.Combine(_dir, "MyTools"), legacy, fresh).Should().Be(((string?)null, (string?)null));
        var (root, note) = DataMigration.MoveLegacyTools(legacy, legacy, fresh);
        root.Should().BeNull();
        note.Should().Contain("already has files");
        Directory.Exists(legacy).Should().BeTrue();
    }

    [Fact]
    public void Missing_legacy_tools_folder_points_the_setting_at_the_new_default()
    {
        var legacy = Path.Combine(_dir, "EzHub", "Tools");
        var fresh = Path.Combine(_dir, "EzForensicsData", "Tools");
        DataMigration.MoveLegacyTools(legacy, legacy, fresh).NewRoot.Should().Be(fresh);
    }
}

public class RuntimeProbePrivateDotnetTests : IDisposable
{
    private readonly string _dir = Path.Combine(Path.GetTempPath(), "ezf-dotnet-" + Guid.NewGuid().ToString("N"));
    public void Dispose() { try { Directory.Delete(_dir, true); } catch { } }

    [Fact]
    public void HasFramework_looks_for_a_major_version_folder()
    {
        RuntimeProbeHas(_dir).Should().BeFalse();
        Directory.CreateDirectory(Path.Combine(_dir, "shared", "Microsoft.NETCore.App", "9.0.10"));
        RuntimeProbeHas(_dir).Should().BeTrue();
        EzForensics.Core.Tools.RuntimeProbe.HasFramework(_dir, "Microsoft.WindowsDesktop.App", 9).Should().BeFalse();
        EzForensics.Core.Tools.RuntimeProbe.HasFramework(_dir, "Microsoft.NETCore.App", 8).Should().BeFalse();
    }

    private static bool RuntimeProbeHas(string dir) => EzForensics.Core.Tools.RuntimeProbe.HasFramework(dir, "Microsoft.NETCore.App", 9);
}

/// <summary>Tests that change process environment variables run alone (no other test class in parallel).</summary>
[CollectionDefinition("EnvironmentVariables", DisableParallelization = true)]
public class EnvironmentVariablesCollection { }
