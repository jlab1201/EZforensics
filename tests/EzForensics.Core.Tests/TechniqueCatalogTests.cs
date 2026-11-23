using EzForensics.Core.Spectre;
using FluentAssertions;

namespace EzForensics.Core.Tests;

public sealed class TechniqueCatalogTests
{
    [Theory]
    [InlineData("T1059", true)]
    [InlineData("T1059.001", true)]
    [InlineData("t1059.001", true)]
    [InlineData("T105", false)]
    [InlineData("T1059.01", false)]
    [InlineData("T1059.0011", false)]
    [InlineData("1059", false)]
    [InlineData("", false)]
    public void Ids_are_validated_like_the_engine(string id, bool valid) => TechniqueCatalog.IsValid(id).Should().Be(valid);

    [Fact]
    public void Invalid_lists_only_the_bad_entries() =>
        TechniqueCatalog.Invalid(new[] { "T1059", "banana", "T12" }).Should().Equal("banana", "T12");

    [Fact]
    public void The_tactic_follows_the_technique_and_a_sub_technique_uses_its_parent()
    {
        TechniqueCatalog.TacticOf("T1105").Should().Be("Command and Control");
        TechniqueCatalog.TacticOf("T1003.001").Should().Be("Credential Access");
        TechniqueCatalog.TacticOf("T1059.003").Should().Be("Execution");
        TechniqueCatalog.TacticOf("T1059.999").Should().Be("Execution");   // not listed, its parent is
        TechniqueCatalog.TacticOf("T9999").Should().BeNull();
        TechniqueCatalog.Name("T1566.001").Should().Contain("Spearphishing Attachment");
    }

    [Fact]
    public void Search_finds_by_id_and_by_name_case_insensitively()
    {
        TechniqueCatalog.Search("masq").Should().Contain(m => m.Id == "T1036" && m.Display.StartsWith("T1036 · Masquerading"));
        TechniqueCatalog.Search("T1059.001").First().Id.Should().Be("T1059.001");
        TechniqueCatalog.Search("POWERSHELL").Should().Contain(m => m.Id == "T1059.001");
        TechniqueCatalog.Search("zzzznothing").Should().BeEmpty();
        TechniqueCatalog.Search("t10", 5).Should().HaveCount(5);
        TechniqueCatalog.IdOfChoice("T1036 · Masquerading — Defense Evasion").Should().Be("T1036");
        TechniqueCatalog.IdOfChoice(" T1059 ").Should().Be("T1059");
    }

    [Fact]
    public void Old_ids_the_matrix_dropped_still_resolve()
    {
        TechniqueCatalog.IsValid("T1070.001").Should().BeTrue();
        TechniqueCatalog.TacticOf("T1070.001").Should().Be("Defense Evasion");
    }

    [Fact]
    public void Every_known_id_is_valid_and_its_tactic_is_a_known_stage()
    {
        TechniqueCatalog.KnownIds.Should().OnlyContain(i => TechniqueCatalog.IsValid(i));
        TechniqueCatalog.KnownIds.Should().OnlyContain(i => !string.IsNullOrEmpty(TechniqueCatalog.TacticOf(i)));
        new[] { "T1059", "T1055", "T1027", "T1112", "T1105", "T1486", "T1070.001" }.Should().OnlyContain(i => StoryGraphics.StageOrder.Contains(TechniqueCatalog.TacticOf(i)!));
        TechniqueCatalog.KnownIds.Count.Should().BeGreaterThan(600);
        TechniqueCatalog.KnownIds.Should().OnlyHaveUniqueItems();
    }

    [Fact]
    public void The_summary_sentence_says_key_events()
    {
        PlainLanguage.SummarySentence(3, DateTimeOffset.Parse("2026-01-02T08:00:00Z"), DateTimeOffset.Parse("2026-01-02T09:00:00Z"), 2, 0).Should().Contain("3 key events").And.NotContain("moment");
        PlainLanguage.SummarySentence(1, DateTimeOffset.Parse("2026-01-02T08:00:00Z"), null, 1, 0).Should().Contain("1 key event");
    }

    [Fact]
    public void A_technique_with_several_tactics_shows_the_first_in_kill_chain_order()
    {
        TechniqueCatalog.TacticOf("T1078").Should().Be("Initial Access");   // also lists Persistence, Privilege Escalation, Defense Evasion
        TechniqueCatalog.TacticOf("T1078.002").Should().Be("Initial Access");
    }
}
