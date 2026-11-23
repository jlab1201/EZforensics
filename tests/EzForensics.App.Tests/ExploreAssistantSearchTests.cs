using System.Text.Json.Nodes;
using EzForensics.App.ViewModels.Explore;
using EzForensics.Core.Explore.Search;
using FluentAssertions;

namespace EzForensics.App.Tests;

public class ExploreAssistantSearchTests
{
    private static readonly string Dir = System.IO.Path.GetTempPath().TrimEnd('\\');

    [Fact]
    public void Search_arguments_fill_the_criteria_and_default_to_the_open_folder()
    {
        var q = ExploreViewModel.ParseSearch(new JsonObject { ["name"] = "*.ps1", ["text"] = "Invoke-WebRequest", ["modified_after"] = "2026-10-01" }, Dir);
        q.Error.Should().BeNull();
        (q.LookIn, q.NameMode, q.Name, q.Text, q.After).Should().Be((Dir, NameMatchMode.Wildcard, "*.ps1", "Invoke-WebRequest", new DateTime(2026, 10, 1)));
        ExploreViewModel.ParseSearch(new JsonObject { ["name"] = "report" }, Dir).NameMode.Should().Be(NameMatchMode.Contains);
    }

    [Theory]
    [InlineData("{\"name\":\"x\",\"look_in\":\"Z:\\\\no\\\\such\\\\dir\"}", "No such folder")]
    [InlineData("{\"name\":\"(\",\"name_mode\":\"regex\"}", "regular expression")]
    [InlineData("{\"name\":\"x\",\"modified_before\":\"last tuesday\"}", "not a date")]
    [InlineData("{}", "name pattern or text")]
    public void Bad_search_arguments_are_an_error_for_the_model(string json, string error) =>
        ExploreViewModel.ParseSearch(JsonNode.Parse(json)!.AsObject(), Dir).Error.Should().Contain(error);

    [Fact]
    public void Nothing_to_search_without_a_folder() =>
        ExploreViewModel.ParseSearch(new JsonObject { ["name"] = "x" }, null).Error.Should().Contain("Nothing to search");
}
