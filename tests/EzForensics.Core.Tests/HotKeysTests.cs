using EzForensics.Core.Settings;
using Xunit;

namespace EzForensics.Core.Tests;

public class HotKeysTests
{
    [Theory]
    [InlineData("ctrl+shift+f", "Ctrl+Shift+F")]
    [InlineData("Shift+Control+x", "Ctrl+Shift+X")]
    [InlineData("Alt+Ctrl+Shift+F2", "Ctrl+Alt+Shift+F2")]
    [InlineData("escape", "Esc")]
    [InlineData("Ctrl+Return", "Ctrl+Enter")]
    [InlineData("Ctrl++", "Ctrl++")]
    public void Normalize_canonicalises(string input, string expected) => Assert.Equal(expected, HotKeys.Normalize(input));

    [Theory]
    [InlineData("")]
    [InlineData(null)]
    [InlineData("Ctrl+Shift")]
    [InlineData("Ctrl+A+B")]
    [InlineData("Ctrl+Ctrl+A")]
    public void Normalize_rejects_invalid(string? input) => Assert.Null(HotKeys.Normalize(input));

    [Fact]
    public void TryParse_sets_modifiers()
    {
        Assert.True(HotKeys.TryParse("Ctrl+Alt+K", out var g));
        Assert.Equal(new HotKeyGesture(true, true, false, "K"), g);
        Assert.Equal("Ctrl+Alt+K", HotKeys.Format(g));
    }

    [Fact]
    public void Defaults_have_unique_ids_valid_gestures_and_no_conflicts()
    {
        Assert.Equal(HotKeys.Actions.Count, HotKeys.Actions.Select(a => a.Id).Distinct().Count());
        foreach (var a in HotKeys.Actions) Assert.NotNull(HotKeys.Normalize(a.Default));
        Assert.Empty(HotKeys.Conflicts(HotKeys.Resolve(null)));
    }

    [Fact]
    public void Resolve_uses_override_default_or_unassigned()
    {
        var eff = HotKeys.Resolve(new Dictionary<string, string>
        {
            [HotKeys.FilterContains] = "ctrl+alt+c",
            [HotKeys.CopyRow] = "",
            [HotKeys.FilterEquals] = "Ctrl+Shift",
        });
        Assert.Equal("Ctrl+Alt+C", eff[HotKeys.FilterContains]);
        Assert.Equal("", eff[HotKeys.CopyRow]);
        Assert.Equal("Ctrl+Shift+E", eff[HotKeys.FilterEquals]);
        Assert.Equal("Ctrl+Shift+X", eff[HotKeys.FilterNotContains]);
    }

    [Fact]
    public void ToOverrides_stores_only_changes()
    {
        var eff = HotKeys.Resolve(null);
        eff[HotKeys.CopyRow] = "";
        eff[HotKeys.ClearFilters] = "Ctrl+Alt+L";
        var o = HotKeys.ToOverrides(eff);
        Assert.Equal(2, o.Count);
        Assert.Equal("", o[HotKeys.CopyRow]);
        Assert.Equal("Ctrl+Alt+L", o[HotKeys.ClearFilters]);
    }

    [Fact]
    public void Conflicts_detects_shared_gesture_but_not_unassigned()
    {
        var eff = HotKeys.Resolve(null);
        eff[HotKeys.FilterEquals] = "Ctrl+Shift+F";
        var c = HotKeys.Conflicts(eff);
        Assert.Single(c);
        Assert.Equal("Ctrl+Shift+F", c[0].Gesture);
        Assert.Equal(2, c[0].Names.Count);
        eff[HotKeys.CopyRow] = ""; eff[HotKeys.ClearFilters] = "";
        Assert.Single(HotKeys.Conflicts(eff));
    }

    [Fact]
    public void Conflicts_include_fixed_keys()
    {
        var eff = HotKeys.Resolve(null);
        eff[HotKeys.CopyRow] = "Ctrl+C";
        Assert.Single(HotKeys.Conflicts(eff));
        Assert.Equal("Copy selection", HotKeys.FindConflict(HotKeys.Resolve(null), HotKeys.FilterEquals, "ctrl+c"));
    }

    [Fact]
    public void FindConflict_names_other_action_and_ignores_self()
    {
        var eff = HotKeys.Resolve(null);
        Assert.Null(HotKeys.FindConflict(eff, HotKeys.FilterContains, "Ctrl+Shift+F"));
        Assert.Equal("Filter: contains this value", HotKeys.FindConflict(eff, HotKeys.FilterEquals, "ctrl+shift+f"));
        Assert.Null(HotKeys.FindConflict(eff, HotKeys.FilterEquals, "Ctrl+Alt+Q"));
    }

    [Fact]
    public void ActionFor_looks_up_gesture()
    {
        var eff = HotKeys.Resolve(null);
        Assert.Equal(HotKeys.ClearFilters, HotKeys.ActionFor(eff, new HotKeyGesture(true, false, true, "L")));
        Assert.Null(HotKeys.ActionFor(eff, new HotKeyGesture(false, false, false, "Q")));
    }

    [Fact]
    public void Back_action_defaults_to_Alt_Left_in_Navigation_group()
    {
        var a = HotKeys.Find(HotKeys.Back);
        Assert.NotNull(a);
        Assert.Equal("Navigation", a!.Group);
        Assert.Equal("Alt+Left", HotKeys.Resolve(null)[HotKeys.Back]);
        Assert.Equal(HotKeys.Back, HotKeys.ActionFor(HotKeys.Resolve(null), new HotKeyGesture(false, true, false, "Left")));
        var custom = HotKeys.Resolve(new Dictionary<string, string> { [HotKeys.Back] = "Ctrl+Left" });
        Assert.Equal(HotKeys.Back, HotKeys.ActionFor(custom, new HotKeyGesture(true, false, false, "Left")));
        Assert.Null(HotKeys.ActionFor(custom, new HotKeyGesture(false, true, false, "Left")));
    }
}
