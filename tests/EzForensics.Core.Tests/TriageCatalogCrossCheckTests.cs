using EzForensics.Core.Catalog;
using EzForensics.Core.Triage;
using FluentAssertions;

namespace EzForensics.Core.Tests;

public class TriageCatalogCrossCheckTests
{
    public static IEnumerable<object[]> RuleIds() => TriageRules.LoadEmbedded().Select(r => new object[] { r.Id });

    [Theory]
    [MemberData(nameof(RuleIds))]
    public void Rule_flags_exist_in_catalog(string ruleId)
    {
        var rule = TriageRules.LoadEmbedded().Single(r => r.Id == ruleId);
        var def = ToolCatalog.Find(rule.ToolId);
        def.Should().NotBeNull($"rule '{rule.Id}' references tool '{rule.ToolId}'");
        def!.Exe.Should().Be(rule.ExeName);

        var flags = def.Parameters.Select(p => p.Flag).Where(f => !string.IsNullOrEmpty(f)).ToHashSet(StringComparer.Ordinal);
        // Flag tokens only: a value like "-" prefixed paths is not expected in rules, so every '-' token must be a catalog flag.
        var used = rule.Args.Where(a => a.StartsWith('-')).ToList();
        used.Should().NotBeEmpty();
        foreach (var f in used)
            flags.Should().Contain(f, $"rule '{rule.Id}' uses {f}, which {rule.ToolId} does not define");
    }
}
