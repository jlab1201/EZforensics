using System.IO;
using EzForensics.App.Services;
using EzForensics.App.ViewModels.Findings;
using EzForensics.Core.Case;
using EzForensics.Core.Settings;
using EzForensics.Core.Spectre;
using FluentAssertions;

namespace EzForensics.App.Tests;

/// <summary>Load demo case (Review's no-case state, Ingest's start state): copies the shipped demo, makes it the case, and Review shows it without Spectre.</summary>
[Collection("WpfApp")]
public sealed class LoadDemoCaseTests : IDisposable
{
    private readonly string _dir = Path.Combine(Path.GetTempPath(), "ezf-demo-" + Guid.NewGuid().ToString("N")[..8]);
    public LoadDemoCaseTests() { Directory.CreateDirectory(_dir); }
    public void Dispose() { try { Directory.Delete(_dir, true); } catch (IOException) { } }

    [Fact]
    public async Task Load_demo_case_sets_the_current_case_and_Review_shows_its_findings_and_key_events()
    {
        var prev = AppServices.Current;
        var s = new AppServices(new JsonSettingsStore(Path.Combine(_dir, "settings.json")), new AppSettings { CaseRoot = Path.Combine(_dir, "Cases") });
        AppServices.Current = s;
        try
        {
            string? opened = null;
            s.CaseOpenRequested += d => opened = d;
            var vm = new FindingsViewModel(() => s.LastCaseDir, new SpectreFindingsClient(new FindingsViewModelTests.FakeEngine(), () => s.LastCaseDir, null),
                _ => Task.FromResult(false), queue: () => null);
            vm.IsDemoAvailable.Should().BeTrue("the App ships samples\\demo-case as demo-case next to the exe");

            await vm.LoadDemoCaseCommand.ExecuteAsync(null);

            vm.Banner.Should().BeNull();
            s.LastCaseDir.Should().NotBeNull();
            Path.GetDirectoryName(s.LastCaseDir).Should().Be(Path.Combine(_dir, "Cases"));
            Path.GetFileName(s.LastCaseDir).Should().StartWith(DemoCase.CasePrefix);
            opened.Should().Be(s.LastCaseDir);   // Analyze (the shell) opens it too

            // Load from Review itself: the page opens the new case (the shell does not navigate to the page on screen)
            for (int i = 0; i < 200 && !(vm.HasAnalysis && vm.EngineChecked); i++) await Task.Delay(50);
            vm.HasCase.Should().BeTrue();
            vm.HasAnalysis.Should().BeTrue();
            vm.Rows.Should().HaveCountGreaterThan(0);
            vm.Moments.Should().HaveCountGreaterThan(0);
            vm.CanPresent.Should().BeTrue();
        }
        finally { AppServices.Current = prev; }
    }

    [Fact]
    public void The_demo_case_carries_computer_details_so_Analyze_shows_real_values()
    {
        var r = EzForensics.Core.SystemInfo.ComputerDetailsStore.Load(DemoCase.DefaultSource);
        r.Should().NotBeNull();
        r!.Systems.Should().ContainSingle().Which.Summary.Should().Contain("DEMO-WS01");
        r.Systems[0].Fields.Should().NotBeEmpty();
    }
}
