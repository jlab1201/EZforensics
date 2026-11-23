using EzForensics.App.ViewModels;
using FluentAssertions;

namespace EzForensics.App.Tests;

/// <summary>The Columns menu binds each optional column to its Show… property by name.</summary>
public sealed class IngestColumnsTests
{
    [Fact]
    public void Every_optional_column_maps_to_a_real_property()
    {
        foreach (var col in IngestColumnsViewModel.Optional)
            typeof(IngestColumnsViewModel).GetProperty(IngestColumnsViewModel.PropertyOf(col))
                .Should().NotBeNull($"column \"{col}\" needs its Show… property");
    }

    [Fact]
    public void Matched_path_maps_to_ShowMatchedPath() =>
        IngestColumnsViewModel.PropertyOf(IngestColumnsViewModel.MatchedPath).Should().Be("ShowMatchedPath");
}
