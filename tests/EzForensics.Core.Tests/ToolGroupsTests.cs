using EzForensics.Core.Catalog;
using FluentAssertions;

namespace EzForensics.Core.Tests;

public class ToolGroupsTests
{
    [Theory]
    [InlineData("EventLogs", ToolGroup.Disk)]
    [InlineData("Registry", ToolGroup.Disk)]
    [InlineData("ProgramExecution", ToolGroup.Disk)]
    [InlineData("Network", ToolGroup.Network)]
    [InlineData("Memory", ToolGroup.Memory)]
    [InlineData("SomethingNew", ToolGroup.Disk)]
    [InlineData("", ToolGroup.Disk)]
    public void Catalog_categories_fold_into_groups(string category, ToolGroup expected) =>
        ToolGroups.OfCatalogCategory(category).Should().Be(expected);

    [Fact]
    public void Every_group_has_info_and_every_catalog_tool_has_a_group()
    {
        foreach (var g in Enum.GetValues<ToolGroup>()) ToolGroups.Info(g).Group.Should().Be(g);
        foreach (var d in ToolCatalog.LoadEmbedded()) ToolGroups.All.Should().Contain(i => i.Group == ToolGroups.Of(d));
    }

    [Fact]
    public void Gui_tools_go_to_the_gui_group_and_cli_tools_never_do()
    {
        var all = ToolCatalog.LoadEmbedded();
        all.Where(d => d.Kind == ToolKind.Gui).Should().NotBeEmpty().And.OnlyContain(d => ToolGroups.Of(d) == ToolGroup.Gui);
        all.Where(d => d.Kind == ToolKind.Cli).Should().OnlyContain(d => ToolGroups.Of(d) != ToolGroup.Gui);
        ToolGroups.All.Last().Group.Should().Be(ToolGroup.Gui);
    }

    [Fact]
    public void Every_group_maps_to_a_page_and_every_page_is_listed()
    {
        foreach (var g in Enum.GetValues<ToolGroup>()) ToolGroups.Pages.Should().Contain(p => p.Page == ToolGroups.PageOf(g));
        ToolGroups.Pages.Select(p => p.Title).Should().Equal("Analyze", "Explore", "AI", "GUI");
        ToolGroups.PageOf(ToolGroup.Timeline).Should().Be(ToolPage.Analyze);
        ToolGroups.PageOf(ToolGroup.Spectre).Should().Be(ToolPage.Ai);
    }
}
