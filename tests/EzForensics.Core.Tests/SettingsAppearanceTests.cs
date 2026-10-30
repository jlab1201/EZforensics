using EzForensics.Core.Results;
using EzForensics.Core.Settings;
using FluentAssertions;

namespace EzForensics.Core.Tests;

public class SettingsAppearanceTests
{
    [Theory]
    [InlineData(1.0, 1.0)]
    [InlineData(0.1, 0.5)]
    [InlineData(9.0, 2.5)]
    [InlineData(double.NaN, 1.0)]
    [InlineData(double.PositiveInfinity, 1.0)]
    [InlineData(1.234, 1.23)]
    public void Zoom_is_clamped(double input, double expected) => UiZoom.Clamp(input).Should().Be(expected);

    [Fact]
    public void Zoom_steps_by_ten_percent_within_range()
    {
        UiZoom.In(1.0).Should().Be(1.1);
        UiZoom.Out(1.0).Should().Be(0.9);
        UiZoom.In(2.5).Should().Be(2.5);
        UiZoom.Out(0.5).Should().Be(0.5);
        UiZoom.In(1.05).Should().Be(1.1);   // off-grid values snap to the next step
        UiZoom.Out(1.05).Should().Be(1.0);
        var z = 0.5;
        for (int i = 0; i < 25; i++) z = UiZoom.In(z);
        z.Should().Be(2.5);
        UiZoom.Format(1.1).Should().Be("110%");
        UiZoom.Format(0.5).Should().Be("50%");
    }

    [Fact]
    public void Settings_clamp_and_default_appearance_values()
    {
        var s = new AppSettings();
        s.UiZoom.Should().Be(1.0);
        s.UiFontSize.Should().Be(13);
        s.MonoFontSize.Should().Be(12);
        s.UiFontFamily.Should().NotBeNullOrWhiteSpace();
        s.MonoFontFamily.Should().NotBeNullOrWhiteSpace();
        s.UiZoom = 7; s.UiZoom.Should().Be(2.5);
        s.UiFontSize = 100; s.UiFontSize.Should().Be(UiFonts.MaxSize);
        s.MonoFontSize = -1; s.MonoFontSize.Should().Be(UiFonts.DefaultMonoSize);
        s.UiFontFamily = "  "; s.UiFontFamily.Should().Be(UiFonts.DefaultUiFamily);
        UiFonts.GridRowHeight(12).Should().Be(24);
        UiFonts.GridRowHeight(16).Should().Be(32);
    }

    [Fact]
    public void Appearance_settings_round_trip()
    {
        using var t = new TempDir();
        var store = new JsonSettingsStore(Path.Combine(t.Path, "settings.json"));
        var s = new AppSettings { UiZoom = 1.3, UiFontFamily = "Arial", UiFontSize = 15, MonoFontFamily = "Consolas", MonoFontSize = 14 };
        store.Save(s);
        var back = store.Load();
        back.UiZoom.Should().Be(1.3);
        back.UiFontFamily.Should().Be("Arial");
        back.UiFontSize.Should().Be(15);
        back.MonoFontFamily.Should().Be("Consolas");
        back.MonoFontSize.Should().Be(14);
    }

    [Fact]
    public void Old_settings_file_without_appearance_gets_defaults()
    {
        using var t = new TempDir();
        var p = Path.Combine(t.Path, "settings.json");
        File.WriteAllText(p, "{ \"theme\": \"light\" }");
        var s = new JsonSettingsStore(p).Load();
        s.UiZoom.Should().Be(1.0);
        s.UiFontFamily.Should().Be(UiFonts.DefaultUiFamily);
    }

    [Fact]
    public void Case_tree_puts_super_timeline_first()
    {
        var items = new (string Name, bool Dir)[]
        {
            ("Registry", true), ("a.csv", false), ("EventLogs", true), ("Timeline", true), ("FileSystem", true),
        };
        CaseTreeOrder.Sort(items, i => i.Name, i => i.Dir).Select(i => i.Name)
            .Should().Equal("Timeline", "EventLogs", "FileSystem", "Registry", "a.csv");

        var inTimeline = new (string Name, bool Dir)[]
        {
            ("plaso", true), ("SuperTimeline_20240101T000000Z.csv", false), ("other.csv", false), ("SuperTimeline_20250101T000000Z.csv", false),
        };
        CaseTreeOrder.Sort(inTimeline, i => i.Name, i => i.Dir).Select(i => i.Name)
            .Should().Equal("SuperTimeline_20250101T000000Z.csv", "SuperTimeline_20240101T000000Z.csv", "plaso", "other.csv");

        CaseTreeOrder.IsSuperTimeline("timeline", true).Should().BeTrue();
        CaseTreeOrder.IsSuperTimeline("SuperTimeline_x.manifest.json", false).Should().BeFalse();
    }
}
