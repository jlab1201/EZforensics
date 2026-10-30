using EzForensics.Core.Settings;
using EzForensics.Core.Setup;
using FluentAssertions;

namespace EzForensics.Core.Tests;

/// <summary>Cancel of the first setup: only what this setup installed (Done / stopped part-way, new since the snapshot, under the data root).</summary>
public sealed class SetupRevertTests : IDisposable
{
    private readonly string _root = Path.Combine(Path.GetTempPath(), "ezf-revert-" + Guid.NewGuid().ToString("N"));
    private readonly string _outside = Path.Combine(Path.GetTempPath(), "ezf-revert-out-" + Guid.NewGuid().ToString("N"));
    private readonly SetupRevertPaths _p;

    public SetupRevertTests()
    {
        Directory.CreateDirectory(_root);
        _p = new SetupRevertPaths
        {
            DataRoot = _root,
            ToolsRoot = Path.Combine(_root, "Tools"),
            DotnetDir = Path.Combine(_root, "dotnet"),
            PlasoRoot = Path.Combine(_root, "plaso"),
            PythonDir = Path.Combine(_root, "plaso", "python"),
            PlasoVenv = Path.Combine(_root, "plaso", "venv"),
            VolatilityRoot = Path.Combine(_root, "volatility"),
            WslRoot = Path.Combine(_root, "wsl"),
            DistroInstallDir = Path.Combine(_root, "wsl", "EzForensics"),
        };
    }

    public void Dispose()
    {
        foreach (var d in new[] { _root, _outside })
            try { if (Directory.Exists(d)) Directory.Delete(d, true); } catch (IOException) { }
    }

    private static SetupStep Step(SetupStepKind kind, SetupStepState state, string? id = null) =>
        new() { Id = id ?? kind.ToString(), Kind = kind, Title = kind.ToString(), State = state };

    private static string Make(string dir, string file = "x.txt")
    {
        Directory.CreateDirectory(dir);
        File.WriteAllText(Path.Combine(dir, file), "x");
        return dir;
    }

    [Fact]
    public void Done_steps_are_reverted_already_installed_and_not_run_steps_are_not()
    {
        var before = SetupSnapshot.Take(_p, ezDistroExists: false);
        Make(_p.DotnetDir);
        Make(_p.PythonDir);
        Make(_p.PlasoVenv);
        var steps = new[]
        {
            Step(SetupStepKind.InstallDotnet, SetupStepState.Done),
            Step(SetupStepKind.ProvisionPython, SetupStepState.AlreadyInstalled),
            Step(SetupStepKind.CreatePlasoVenv, SetupStepState.Cancelled),
            Step(SetupStepKind.EnableWsl, SetupStepState.Done),
        };
        var t = SetupRevert.Targets(steps, _p, before).Where(x => x.Kind == RevertKind.Path).Select(x => x.Path).ToList();
        t.Should().BeEquivalentTo(new[] { SetupRevert.Norm(_p.DotnetDir), SetupRevert.Norm(_p.PlasoVenv) },
            "Done and stopped part-way count; Already installed never does; enabling WSL has nothing to remove");
    }

    [Fact]
    public void Folders_that_existed_before_the_setup_are_kept_even_when_the_step_ran()
    {
        Make(_p.DotnetDir);
        Make(Path.Combine(_p.ToolsRoot, "EvtxECmd"));
        File.WriteAllText(Path.Combine(_p.ToolsRoot, "MFTECmd.exe"), "old");
        var before = SetupSnapshot.Take(_p, ezDistroExists: true);
        Make(Path.Combine(_p.ToolsRoot, "RECmd"));
        File.WriteAllText(Path.Combine(_p.ToolsRoot, "PECmd.exe"), "new");
        Make(_p.ToolsNet9);
        Make(_p.AutorunsRoot);
        Make(_p.DistroInstallDir);

        var steps = new[]
        {
            Step(SetupStepKind.InstallDotnet, SetupStepState.Done),
            Step(SetupStepKind.DownloadEzTools, SetupStepState.Done),
            Step(SetupStepKind.InstallEzDistro, SetupStepState.Done),
        };
        var t = SetupRevert.Targets(steps, _p, before);
        t.Where(x => x.Kind == RevertKind.Path).Select(x => x.Path).Should().BeEquivalentTo(new[]
        {
            SetupRevert.Norm(Path.Combine(_p.ToolsRoot, "RECmd")), SetupRevert.Norm(Path.Combine(_p.ToolsRoot, "PECmd.exe")), SetupRevert.Norm(_p.ToolsNet9),
            SetupRevert.Norm(_p.AutorunsRoot),
        }, "only new tool entries (the Sysinternals folder is not one of them), plus the legacy Autoruns folder, which is always removed");
        t.Should().NotContain(x => x.Kind == RevertKind.WslDistro, "the distro already existed");
    }

    [Fact]
    public void A_new_distro_is_unregistered_and_paths_outside_the_data_root_are_never_targets()
    {
        var before = SetupSnapshot.Take(_p, ezDistroExists: false);
        var p2 = new SetupRevertPaths
        {
            DataRoot = _p.DataRoot, ToolsRoot = Path.Combine(_outside, "Tools"), DotnetDir = Path.Combine(_outside, "dotnet"), PlasoRoot = _p.PlasoRoot,
            PythonDir = _p.PythonDir, PlasoVenv = _p.PlasoVenv, VolatilityRoot = _p.VolatilityRoot, WslRoot = _p.WslRoot,
            DistroInstallDir = _p.DistroInstallDir,
        };
        Make(p2.DotnetDir);
        Make(Path.Combine(p2.ToolsRoot, "RECmd"));
        var t = SetupRevert.Targets(new[]
        {
            Step(SetupStepKind.InstallDotnet, SetupStepState.Done),
            Step(SetupStepKind.DownloadEzTools, SetupStepState.Done),
            Step(SetupStepKind.InstallEzDistro, SetupStepState.Failed),
        }, p2, before);
        t.Should().ContainSingle(x => x.Kind == RevertKind.WslDistro);
        t.Should().NotContain(x => x.Path.StartsWith(SetupRevert.Norm(_outside), StringComparison.OrdinalIgnoreCase));
    }

    [Fact]
    public async Task Run_removes_the_targets_and_the_emptied_component_folders_only()
    {
        Make(_p.VolatilitySymbols, "isf-imported.json");
        var before = SetupSnapshot.Take(_p, ezDistroExists: false);
        Make(_p.PythonDir);
        Make(_p.PlasoVenv);
        Make(_p.VolatilityVenv);
        Make(Path.Combine(_p.VolatilitySymbols, "windows"));
        File.WriteAllText(Path.Combine(_p.VolatilitySymbols, EzForensics.Core.Memory.VolatilitySetup.SymbolPackMarker), "pack");

        var steps = new[]
        {
            Step(SetupStepKind.ProvisionPython, SetupStepState.Done),
            Step(SetupStepKind.CreatePlasoVenv, SetupStepState.Done, "plaso-venv"),
            Step(SetupStepKind.InstallPlaso, SetupStepState.Done, "plaso-pip"),
            Step(SetupStepKind.InstallVolatility, SetupStepState.Done),
            Step(SetupStepKind.InstallVolatilitySymbols, SetupStepState.Done),
        };
        var targets = SetupRevert.Targets(steps, _p, before);
        bool distroAsked = false;
        var r = await SetupRevert.RunAsync(targets, _root, _ => { distroAsked = true; return Task.FromResult((true, "removed")); }, null);

        r.Ok.Should().BeTrue();
        distroAsked.Should().BeFalse("no distro step ran");
        Directory.Exists(_p.PlasoRoot).Should().BeFalse("python and venv were new, so the emptied plaso folder goes too");
        Directory.Exists(_p.VolatilityVenv).Should().BeFalse();
        Directory.Exists(Path.Combine(_p.VolatilitySymbols, "windows")).Should().BeFalse();
        File.Exists(Path.Combine(_p.VolatilitySymbols, "isf-imported.json")).Should().BeTrue("the user's own symbols existed before the setup");
        Directory.Exists(_root).Should().BeTrue("the data root itself is never removed");
    }

    [Fact]
    public void Merge_keeps_one_of_each_and_targets_survive_a_settings_round_trip()
    {
        var a = new RevertTarget { Kind = RevertKind.Path, Path = _p.DotnetDir, What = "dotnet" };
        var b = new RevertTarget { Kind = RevertKind.WslDistro, Path = _p.DistroInstallDir, What = "distro" };
        SetupRevert.Merge(new[] { a }, new[] { a, b }).Should().HaveCount(2);

        var path = Path.Combine(_root, "settings.json");
        var store = new JsonSettingsStore(path);
        store.Save(new AppSettings { SetupResume = new SetupResumeState { Items = { SetupIds.Zeek }, FirstSetup = true, Installed = { a, b } } });
        var back = store.Load().SetupResume!;
        back.FirstSetup.Should().BeTrue();
        back.Installed.Select(x => (x.Kind, x.Path)).Should().Equal((RevertKind.Path, a.Path), (RevertKind.WslDistro, b.Path));
    }
}
