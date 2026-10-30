using EzForensics.Core.Settings;
using EzForensics.Core.Triage;
using FluentAssertions;

namespace EzForensics.Core.Tests;

public class IngestSelectionTests
{
    [Fact]
    public void Default_is_seeded_when_missing()
    {
        var s = new AppSettings();
        s.IngestSelections.Should().ContainSingle().Which.Name.Should().Be("SANS Triage");
        s.IngestSelections[0].RuleIds.Should().BeEquivalentTo(IngestDefaults.FastForensicsRuleIds);
        IngestSelections.Normalize(null)[0].Name.Should().Be("SANS Triage");
        IngestSelections.Normalize(new[] { new IngestSelection("Mine", new[] { "mft" }) }).Select(x => x.Name).Should().Equal("SANS Triage", "Mine");
    }

    [Fact]
    public void Store_and_load_round_trip_and_old_settings_get_Default()
    {
        using var t = new TempDir();
        var store = new JsonSettingsStore(Path.Combine(t.Path, "settings.json"));
        var s = new AppSettings();
        s.IngestSelections = new List<IngestSelection> { new("Default", new[] { "mft" }), new("Quick", new[] { "evtx", "prefetch" }) };
        store.Save(s);
        var back = store.Load();
        back.IngestSelections.Select(x => x.Name).Should().Equal("SANS Triage", "Quick");
        back.IngestSelections[0].RuleIds.Should().Equal("mft");
        back.IngestSelections[1].RuleIds.Should().Equal("evtx", "prefetch");

        File.WriteAllText(store.Path, "{\"maxParallel\":2}");
        store.Load().IngestSelections.Should().ContainSingle().Which.Name.Should().Be("SANS Triage");
    }

    [Fact]
    public void Names_are_trimmed_unique_case_insensitively_and_Default_keeps_its_name()
    {
        var n = IngestSelections.Normalize(new[]
        {
            new IngestSelection(" Quick ", new[] { "mft", "MFT", " " }),
            new IngestSelection("quick", new[] { "evtx" }),
            new IngestSelection("", new[] { "lnk" }),
            new IngestSelection("DEFAULT", new[] { "lnk" }),
        });
        n.Select(x => x.Name).Should().Equal("SANS Triage", "Quick");
        n[0].RuleIds.Should().Equal("lnk");
        n[1].RuleIds.Should().Equal("mft");

        IngestSelections.ValidateName("  ", new string[0]).Should().NotBeNull();
        IngestSelections.ValidateName("quick ", new[] { "Quick" }).Should().Contain("already exists");
        IngestSelections.ValidateName("New", new[] { "Quick" }).Should().BeNull();
    }

    [Fact]
    public void Apply_matches_rule_ids_case_insensitively_and_unknown_name_falls_back_to_Default()
    {
        var sel = new IngestSelection("X", new[] { "MFT", "evtx" });
        IngestSelections.Includes(sel, "mft").Should().BeTrue();
        IngestSelections.Includes(sel, "lnk").Should().BeFalse();
        var list = new[] { sel };
        IngestSelections.Find(list, "x").Name.Should().Be("X");
        IngestSelections.Find(list, "nope").Name.Should().Be("SANS Triage");
    }

    [Fact]
    public void Legacy_Default_is_renamed_to_SANS_Triage_and_keeps_its_rules()
    {
        var n = IngestSelections.Normalize(new[]
        {
            new IngestSelection("SANS Triage", new[] { "evtx" }), // a custom one named like the new built-in before the rename
            new IngestSelection("Default", new[] { "mft" }),
            new IngestSelection("Quick", new[] { "lnk" }),
        });
        n.Select(x => x.Name).Should().Equal("SANS Triage", "Quick");
        n[0].RuleIds.Should().Equal("mft");
        IngestSelections.IsDefault("default").Should().BeTrue();
        IngestSelections.IsDefault("sans triage").Should().BeTrue();
        IngestSelections.Find(n, "Default").Name.Should().Be("SANS Triage");
    }

    [Fact]
    public void Rule_list_covers_every_default_rule_and_all_four_groups()
    {
        var rules = IngestSelections.AllRules();
        rules.Select(r => r.Id).Should().Contain(IngestDefaults.FastForensicsRuleIds);
        rules.Select(r => r.Group).Distinct().Should().BeEquivalentTo(IngestSelections.Groups);
        rules.Select(r => r.Id).Should().OnlyHaveUniqueItems();
    }
}
