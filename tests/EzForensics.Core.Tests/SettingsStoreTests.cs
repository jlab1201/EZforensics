using EzForensics.Core.Settings;
using FluentAssertions;

namespace EzForensics.Core.Tests;

public class SettingsStoreTests
{
    [Fact]
    public void Round_trips()
    {
        using var t = new TempDir();
        var store = new JsonSettingsStore(Path.Combine(t.Path, "sub", "settings.json"));
        var s = new AppSettings
        {
            ToolsRoot = @"D:\tools", PreferredFlavor = FlavorPreference.Net4, ProxyMode = ProxyMode.Manual,
            ProxyUrl = "http://p:8080", MaxParallel = 3, JobTimeoutMinutes = 10, CaseRoot = @"D:\cases",
            DateFormat = "yyyy-MM-dd", Theme = AppTheme.Light, NetVersionToDownload = 4,
            PlasoParsers = "win7", PlasoFormat = EzForensics.Core.Plaso.PlasoOutputFormat.L2tCsv, PlasoWorkers = "3", PlasoTimezoneUtc = false, PlasoVss = true,
            PlasoUnattended = false, PlasoFromDate = new DateTime(2026, 1, 2), PlasoToDate = new DateTime(2026, 3, 4),
        };
        store.Save(s);
        File.ReadAllText(store.Path).Should().Contain("\"net4\"");
        store.Load().Should().BeEquivalentTo(s);
    }

    [Fact]
    public void Side_drawer_is_unpinned_by_default_and_on_first_run()
    {
        new AppSettings().NavPinned.Should().BeFalse();
        using var t = new TempDir();
        new JsonSettingsStore(Path.Combine(t.Path, "settings.json")).Load().NavPinned.Should().BeFalse();
    }

    [Fact]
    public void Missing_or_corrupt_file_gives_defaults()
    {
        using var t = new TempDir();
        var p = Path.Combine(t.Path, "settings.json");
        var store = new JsonSettingsStore(p);
        store.Load().MaxParallel.Should().BeGreaterOrEqualTo(1);
        File.WriteAllText(p, "{ not json");
        store.Load().JobTimeoutMinutes.Should().Be(120);
        new AppSettings().NetVersionToDownload.Should().Be(9);
    }

    [Fact]
    public void Removed_auto_build_settings_of_older_files_are_ignored()
    {
        // The super timeline is a planned Ingest row now (ticked when plaso is available): autoBuildSuperTimeline / autoBuildEngine are gone.
        using var t = new TempDir();
        var p = Path.Combine(t.Path, "settings.json");
        File.WriteAllText(p, """{ "autoBuildSuperTimeline": true, "autoBuildEngine": "ezCsv", "maxParallel": 3 }""");
        var store = new JsonSettingsStore(p);
        store.Load().MaxParallel.Should().Be(3, "the rest of the file still loads");
        store.Save(store.Load());
        File.ReadAllText(p).Should().NotContain("autoBuild");
    }

    [Fact]
    public void Ingest_memory_plugins_default_to_the_catalog_and_round_trip()
    {
        using var t = new TempDir();
        var store = new JsonSettingsStore(Path.Combine(t.Path, "settings.json"));
        new AppSettings().VolatilityPlugins.Should().BeNull();
        var defaults = EzForensics.Core.Memory.MemoryPlugins.SelectedFor(EzForensics.Core.Memory.MemoryOs.Windows, null);
        defaults.Should().Contain("info").And.BeEquivalentTo(EzForensics.Core.Memory.MemoryPlugins.ForOs(EzForensics.Core.Memory.MemoryOs.Windows)
            .Where(d => d.DefaultSelected || d.IsInfo).Select(d => d.Id));

        store.Save(new AppSettings { VolatilityPlugins = new List<string> { "pslist" } });
        var loaded = store.Load().VolatilityPlugins;
        loaded.Should().Equal("pslist");
        EzForensics.Core.Memory.MemoryPlugins.SelectedFor(EzForensics.Core.Memory.MemoryOs.Windows, loaded).Should().BeEquivalentTo("info", "pslist");
        EzForensics.Core.Memory.MemoryPlugins.SelectedFor(EzForensics.Core.Memory.MemoryOs.Linux, loaded).Should().BeEmpty("no Linux plugin chosen");
    }

    [Fact]
    public void Old_per_tab_ingest_columns_load_as_defaults()
    {
        using var t = new TempDir();
        var path = Path.Combine(t.Path, "settings.json");
        File.WriteAllText(path, "{\"ingestColumns\":{\"thisRun\":{\"showStarted\":true,\"showExit\":true},\"allJobs\":{\"showDuration\":false}}}");
        var c = new JsonSettingsStore(path).Load().IngestColumns;
        c.ShowMatchedPath.Should().BeFalse();
        c.ShowDuration.Should().BeTrue();
        c.ShowResult.Should().BeTrue();
        c.ShowOutput.Should().BeFalse();
    }
}
