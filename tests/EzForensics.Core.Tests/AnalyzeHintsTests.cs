using EzForensics.Core.Advisories;
using EzForensics.Core.Settings;
using FluentAssertions;

namespace EzForensics.Core.Tests;

public class AnalyzeHintsTests
{
    [Fact]
    public void Setting_defaults_to_show_and_round_trips()
    {
        using var t = new TempDir();
        var store = new JsonSettingsStore(Path.Combine(t.Path, "settings.json"));
        new AppSettings().ShowAnalyzeHints.Should().BeTrue();
        store.Load().ShowAnalyzeHints.Should().BeTrue();
        store.Save(new AppSettings { ShowAnalyzeHints = false });
        store.Load().ShowAnalyzeHints.Should().BeFalse();
        store.Save(new AppSettings { ShowAnalyzeHints = true });
        store.Load().ShowAnalyzeHints.Should().BeTrue();
    }

    [Fact]
    public void Setting_raises_property_changed_for_live_updates()
    {
        var s = new AppSettings();
        var seen = new List<string?>();
        s.PropertyChanged += (_, e) => seen.Add(e.PropertyName);
        s.ShowAnalyzeHints = false;
        seen.Should().Contain(nameof(AppSettings.ShowAnalyzeHints));
    }

    [Theory]
    [InlineData(true, false, true)]
    [InlineData(true, true, false)]
    [InlineData(false, false, false)]
    [InlineData(false, true, false)]
    public void Hints_are_visible_only_when_the_setting_shows_and_the_session_switch_is_off(bool show, bool hideSession, bool visible)
        => AnalyzeHints.IsVisible(show, hideSession).Should().Be(visible);

    [Theory]
    [InlineData(true, false, false, true)]
    [InlineData(true, true, true, true)]
    [InlineData(false, false, true, false)]
    [InlineData(false, true, true, false)]
    public void Actions_item_is_checked_when_hidden_and_locked_when_the_setting_hides(bool show, bool hideSession, bool isChecked, bool enabled)
    {
        AnalyzeHints.MenuChecked(show, hideSession).Should().Be(isChecked);
        AnalyzeHints.MenuEnabled(show).Should().Be(enabled);
    }
}
