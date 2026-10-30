using EzForensics.App.ViewModels;
using EzForensics.Core.Settings;
using FluentAssertions;

namespace EzForensics.App.Tests;

/// <summary>Banner "Hide hints": only a confirmed yes turns the permanent setting off and saves it.</summary>
public sealed class AdvisoryHideHintsTests
{
    [Fact]
    public void Confirmed_hides_hints_and_saves()
    {
        var s = new AppSettings();
        var saves = 0;
        AdvisoryBannerViewModel.HideHintsPermanently(s, () => true, () => saves++).Should().BeTrue();
        s.ShowAnalyzeHints.Should().BeFalse();
        saves.Should().Be(1);
    }

    [Fact]
    public void Cancelled_changes_nothing()
    {
        var s = new AppSettings();
        var saves = 0;
        AdvisoryBannerViewModel.HideHintsPermanently(s, () => false, () => saves++).Should().BeFalse();
        s.ShowAnalyzeHints.Should().BeTrue();
        saves.Should().Be(0);
    }
}
