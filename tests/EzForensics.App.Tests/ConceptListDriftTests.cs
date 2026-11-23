using EzForensics.Core.Advisories;
using EzForensics.Core.Catalog;
using EzForensics.Core.Knowledge;
using FluentAssertions;

namespace EzForensics.App.Tests;

public sealed class ConceptListDriftTests
{
    [Fact]
    public void ConceptList_CoversToolCatalogAndArtifactCategories()
    {
        var tools = ConceptExtractor.KnownToolTerms();
        foreach (var t in ToolCatalog.LoadEmbedded())
        {
            tools.Should().Contain(x => x.Equals(t.DisplayName, StringComparison.OrdinalIgnoreCase), $"tool display name '{t.DisplayName}'");
            tools.Should().Contain(x => x.Equals(t.Id, StringComparison.OrdinalIgnoreCase), $"tool id '{t.Id}'");
        }

        var artifacts = ConceptExtractor.KnownArtifactTerms();
        foreach (var a in AdvisoryRuleSet.LoadEmbedded().Artifacts)
        {
            var i = a.Label.IndexOf(" (", StringComparison.Ordinal);
            var label = (i > 0 ? a.Label[..i] : a.Label).Trim();
            artifacts.Should().Contain(x => x.Equals(label, StringComparison.OrdinalIgnoreCase), $"artifact category '{label}'");
        }
    }
}
