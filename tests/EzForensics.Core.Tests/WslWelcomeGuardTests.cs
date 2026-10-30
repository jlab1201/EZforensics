using EzForensics.Core.Runtimes;
using EzForensics.Core.Setup;
using FluentAssertions;

namespace EzForensics.Core.Tests;

public class WslWelcomeGuardTests
{
    private static readonly DateTime Start = new(2026, 10, 17, 10, 0, 0);

    [Theory]
    [InlineData(null, true)]
    [InlineData(0, true)]
    [InlineData(2, true)]
    [InlineData(1, false)]
    public void Oobe_flag_is_written_unless_it_is_already_1(int? current, bool needed) =>
        WslWelcomeGuard.NeedsOobeFlag(current).Should().Be(needed);

    [Fact]
    public void Marks_once_and_logs_only_when_it_wrote()
    {
        var store = new Store { Value = null };
        var g = new WslWelcomeGuard(store, new Procs(), Start);
        g.MarkOobeComplete().Should().Contain("OOBEComplete");
        store.Value.Should().Be(1);
        store.Writes.Should().Be(1);
        g.MarkOobeComplete().Should().BeNull("once per guard");
        store.Writes.Should().Be(1);

        var already = new Store { Value = 1 };
        new WslWelcomeGuard(already, new Procs(), Start).MarkOobeComplete().Should().BeNull();
        already.Writes.Should().Be(0);

        var readOnly = new Store { Value = 0, Fail = true };
        new WslWelcomeGuard(readOnly, new Procs(), Start).MarkOobeComplete().Should().Contain("could not set");
    }

    [Fact]
    public void Closes_only_wslsettings_windows_that_started_during_setup()
    {
        var procs = new Procs
        {
            Items =
            {
                new WslSettingsProcess(1, "wslsettings", Start.AddMinutes(5), true),   // opened during setup: closed
                new WslSettingsProcess(2, "wslsettings", Start.AddMinutes(-5), true),  // the user's own, older: kept
                new WslSettingsProcess(3, "WslSettings", Start.AddMinutes(1), false),  // no window: nothing to close
                new WslSettingsProcess(4, "notepad", Start.AddMinutes(1), true),
                new WslSettingsProcess(5, "wslsettings", null, true),                  // start time unknown: kept
            },
        };
        WslWelcomeGuard.ToClose(procs.Items, Start).Select(p => p.Id).Should().Equal(1);
        var lines = new WslWelcomeGuard(new Store(), procs, Start).CloseWelcomeWindows();
        procs.Closed.Should().Equal(1);
        lines.Should().ContainSingle().Which.Should().Contain("closed").And.Contain("process 1");
    }

    [Fact]
    public void Executor_marks_before_and_closes_after_every_wsl_step_only()
    {
        DefaultSetupExecutor.IsWslStep(SetupStepKind.EnableWsl).Should().BeTrue();
        DefaultSetupExecutor.IsWslStep(SetupStepKind.InstallEzDistro).Should().BeTrue();
        DefaultSetupExecutor.IsWslStep(SetupStepKind.InstallZeekWsl).Should().BeTrue();
        DefaultSetupExecutor.IsWslStep(SetupStepKind.InstallRita).Should().BeTrue();
        DefaultSetupExecutor.IsWslStep(SetupStepKind.InstallPlaso).Should().BeFalse();
        DefaultSetupExecutor.IsWslStep(SetupStepKind.ZeekGuidance).Should().BeFalse();
    }

    private sealed class Store : IWslOobeStore
    {
        public int? Value;
        public bool Fail;
        public int Writes;
        public int? Read() => Value;
        public bool MarkComplete()
        {
            if (Fail) return false;
            Writes++;
            Value = 1;
            return true;
        }
    }

    private sealed class Procs : IWslSettingsProcesses
    {
        public List<WslSettingsProcess> Items { get; } = new();
        public List<int> Closed { get; } = new();
        public IReadOnlyList<WslSettingsProcess> List() => Items;
        public bool CloseMainWindow(int id) { Closed.Add(id); return true; }
    }
}
