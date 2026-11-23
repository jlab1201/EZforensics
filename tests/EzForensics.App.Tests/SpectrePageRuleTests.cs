using EzForensics.App.ViewModels;
using FluentAssertions;
using Xunit;

namespace EzForensics.App.Tests;

public sealed class SpectrePageRuleTests
{
    private sealed class Other : PageViewModel { public override string Title => "Other"; }

    [Fact]
    public void Spectre_shows_only_on_Analyze_and_Explore()
    {
        new Other().ShowsAssistant.Should().BeFalse();
        typeof(EzForensics.App.ViewModels.Explore.ExploreViewModel).GetProperty(nameof(PageViewModel.ShowsAssistant))!.DeclaringType.Should().Be(typeof(EzForensics.App.ViewModels.Explore.ExploreViewModel));
        typeof(ResultsViewModel).GetProperty(nameof(PageViewModel.ShowsAssistant))!.DeclaringType.Should().Be(typeof(ResultsViewModel));
    }
}
