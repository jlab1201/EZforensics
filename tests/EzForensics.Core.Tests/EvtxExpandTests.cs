using EzForensics.Core.EventLogs;
using FluentAssertions;

namespace EzForensics.Core.Tests;

public class EvtxExpandTests
{
    [Fact]
    public void Token_is_replaced_with_the_include_list()
    {
        var s = EvtxSelection.Recommended();
        var r = EvtxArgs.ExpandEventIds(new[] { "-d", "x", "--inc", "{eventIds}", "--csv", "o" }, true, s);
        r.Should().Equal("-d", "x", "--inc", EvtxArgs.IncludeList(s), "--csv", "o");
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void Empty_list_drops_token_and_its_flag(bool useRecommended)
    {
        var s = EvtxSelection.Recommended();
        if (useRecommended) foreach (var e in s.AllEvents) e.Selected = false;
        var r = EvtxArgs.ExpandEventIds(new[] { "-d", "x", "--inc", "{eventIds}", "--csv", "o" }, useRecommended, s);
        r.Should().Equal("-d", "x", "--csv", "o");
    }

    [Fact]
    public void Without_token_args_are_unchanged()
    {
        var args = new[] { "-d", "x", "--csv", "o" };
        EvtxArgs.ExpandEventIds(args, true, EvtxSelection.Recommended()).Should().BeSameAs(args);
    }
}

public class EvtxDefaultTests
{
    private static EzForensics.Core.Triage.TriageRule EvtxRule() =>
        EzForensics.Core.Triage.TriageRules.LoadEmbedded().First(r => r.Id == "evtx");

    [Fact]
    public void Embedded_evtx_rule_has_no_inc_filter()
    {
        var args = EvtxRule().Args;
        args.Should().NotContain("--inc").And.NotContain("{eventIds}");
        args.Should().Equal("-d", "{dir}", "--csv", "{out}");
    }

    [Fact]
    public void Default_evtx_command_parses_without_warnings()
    {
        var rule = EvtxRule();
        var r = EzForensics.Core.Triage.CustomCommand.Parse(EzForensics.Core.Triage.CustomCommand.DefaultText(rule), rule);
        r.Warnings.Should().BeEmpty();
    }

    [Fact]
    public void Apply_adds_inc_only_when_selected_mode_and_no_user_filter()
    {
        var s = EvtxSelection.Recommended();
        var args = new[] { "-d", "x", "--csv", "o" };
        EvtxArgs.Apply(args, false, s).Should().BeSameAs(args);
        EvtxArgs.Apply(args, true, s).Should().Equal("-d", "x", "--csv", "o", "--inc", EvtxArgs.IncludeList(s));
        var own = new[] { "-d", "x", "--inc", "1" };
        EvtxArgs.Apply(own, true, s).Should().BeSameAs(own);
    }

    [Fact]
    public void Filter_mode_defaults_to_all()
        => new EzForensics.Core.Settings.AppSettings().EvtxFilterMode.Should().Be(EzForensics.Core.Settings.EvtxFilterMode.All);
}
