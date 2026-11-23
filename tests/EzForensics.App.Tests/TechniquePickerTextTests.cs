using EzForensics.App.Controls;
using FluentAssertions;

namespace EzForensics.App.Tests;

public class TechniquePickerTextTests
{
    [Fact]
    public void Box_shows_name_and_tactic_and_text_keeps_only_ids()
    {
        var shown = TechniquePicker.DisplayOf("T1007, t1059.001");
        shown.Should().StartWith("T1007 · System Service Discovery — Discovery, T1059.001 · ");
        TechniquePicker.IdsOf(shown).Should().Be("T1007, T1059.001");
    }

    [Fact]
    public void Unknown_or_partial_entries_stay_as_typed() =>
        TechniquePicker.IdsOf("T1007 · System Service Discovery — Discovery, masq").Should().Be("T1007, masq");
}
