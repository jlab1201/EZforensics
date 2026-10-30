using EzForensics.Core.Catalog;
using EzForensics.Core.Commands;
using FluentAssertions;

namespace EzForensics.Core.Tests;

public class CommandsBuilderTests
{
    private static BuiltCommand Build(string toolId, params (string Key, string? Value)[] values) =>
        CommandBuilder.Build(ToolCatalog.Find(toolId)!, values.ToDictionary(v => v.Key, v => v.Value));

    [Fact]
    public void ExactlyOne_BothSet_IsError()
    {
        var cmd = Build("PECmd", ("f", @"C:\a.pf"), ("d", @"C:\Windows\Prefetch"), ("csv", @"C:\out"));
        cmd.IsValid.Should().BeFalse();
        cmd.Errors.Should().ContainSingle().Which.Should().Contain("only one of").And.Contain("-f").And.Contain("-d");
    }

    [Fact]
    public void ExactlyOne_NoneSet_IsError()
    {
        var cmd = Build("PECmd", ("csv", @"C:\out"));
        cmd.IsValid.Should().BeFalse();
        cmd.Errors.Should().ContainSingle().Which.Should().StartWith("Specify one of:");
    }

    [Fact]
    public void ValidCommand_EmitsFlagsInDefinitionOrder()
    {
        var cmd = Build("PECmd", ("csv", @"C:\out"), ("d", @"C:\Windows\Prefetch"), ("q", "true"), ("mp", "false"));
        cmd.IsValid.Should().BeTrue();
        cmd.Args.Should().Equal("-d", @"C:\Windows\Prefetch", "--csv", @"C:\out", "-q");
    }

    [Fact]
    public void Bool_DefaultTrue_SetFalse_EmitsFlagFalse()
    {
        var cmd = Build("EvtxECmd", ("d", @"C:\logs"), ("csv", @"C:\out"), ("dedupe", "false"), ("met", "true"));
        cmd.IsValid.Should().BeTrue();
        cmd.Args.Should().Equal("-d", @"C:\logs", "--csv", @"C:\out", "--dedupe", "false");
    }

    [Fact]
    public void Bool_DefaultFalse_EmitsBareFlagOnlyWhenTrue()
    {
        Build("EvtxECmd", ("d", "x"), ("vss", "true")).Args.Should().Equal("-d", "x", "--vss");
        Build("EvtxECmd", ("d", "x"), ("vss", "false")).Args.Should().Equal("-d", "x");
        Build("EvtxECmd", ("d", "x"), ("vss", null)).Args.Should().Equal("-d", "x");
        Build("EvtxECmd", ("d", "x"), ("vss", "maybe")).Errors.Should().ContainSingle();
    }

    [Fact]
    public void Required_Empty_IsError()
    {
        var cmd = Build("MFTECmd", ("f", "  "), ("csv", @"C:\out"));
        cmd.Errors.Should().ContainSingle().Which.Should().Contain("-f").And.Contain("required");
    }

    [Fact]
    public void AtLeastOne_And_AllOrNone()
    {
        Build("MFTECmd", ("f", "x")).Errors.Should().ContainSingle().Which.Should().Contain("--csv");

        var body = Build("MFTECmd", ("f", "x"), ("body", @"C:\out"));
        body.Errors.Should().ContainSingle().Which.Should().Contain("must be used together").And.Contain("--bdl");

        Build("MFTECmd", ("f", "x"), ("body", @"C:\out"), ("bdl", "C")).Args
            .Should().Equal("-f", "x", "--body", @"C:\out", "--bdl", "C");
    }

    [Fact]
    public void VisibleWhen_UnsetDependency_SkipsParam()
    {
        Build("EvtxECmd", ("f", "a.evtx"), ("csvf", "out.csv")).Args.Should().Equal("-f", "a.evtx");
        Build("EvtxECmd", ("f", "a.evtx"), ("csv", "o"), ("csvf", "out.csv")).Args
            .Should().Equal("-f", "a.evtx", "--csv", "o", "--csvf", "out.csv");
        // Hidden param does not count towards groups either: --bdl alone without --body is ignored.
        Build("MFTECmd", ("f", "x"), ("csv", "o"), ("bdl", "C")).IsValid.Should().BeTrue();
    }

    [Fact]
    public void ValueEqualToDefault_IsOmitted_AndIntIsValidated()
    {
        Build("EvtxECmd", ("f", "x"), ("tdt", "1")).Args.Should().Equal("-f", "x");
        Build("EvtxECmd", ("f", "x"), ("tdt", "8")).Args.Should().Equal("-f", "x", "--tdt", "8");
        Build("EvtxECmd", ("f", "x"), ("tdt", "eight")).Errors.Should().ContainSingle().Which.Should().Contain("whole number");
        Build("EvtxECmd", ("f", "x"), ("met", "false")).Args.Should().Equal("-f", "x", "--met", "false");
    }

    [Fact]
    public void PathsWithSpacesAndDollar_AreRawInArgs_QuotedInDisplayAndPowerShell()
    {
        var cmd = Build("MFTECmd", ("f", @"E:\Case 1\C\$MFT"), ("csv", @"E:\Case 1\out"), ("csvf", "it's.csv"));
        cmd.IsValid.Should().BeTrue();
        cmd.Args.Should().Equal("-f", @"E:\Case 1\C\$MFT", "--csv", @"E:\Case 1\out", "--csvf", "it's.csv");

        cmd.ToDisplayString(@"C:\EZ Tools\MFTECmd.exe").Should().Be(
            @"""C:\EZ Tools\MFTECmd.exe"" -f ""E:\Case 1\C\$MFT"" --csv ""E:\Case 1\out"" --csvf it's.csv");
        cmd.ToPowerShell(@"C:\EZ Tools\MFTECmd.exe").Should().Be(
            @"& 'C:\EZ Tools\MFTECmd.exe' '-f' 'E:\Case 1\C\$MFT' '--csv' 'E:\Case 1\out' '--csvf' 'it''s.csv'");
    }

    [Fact]
    public void DisplayString_EscapesQuotesAndTrailingBackslash()
    {
        BuiltCommand.QuoteWindows(@"C:\out dir\").Should().Be(@"""C:\out dir\\""");
        BuiltCommand.QuoteWindows("say \"hi\"").Should().Be(@"""say \""hi\""""");
        BuiltCommand.QuoteWindows("").Should().Be("\"\"");
        BuiltCommand.QuoteWindows(@"C:\plain").Should().Be(@"C:\plain");
    }

    [Fact]
    public void EveryCliTool_BuildsWithoutThrowing_OnEmptyInput()
    {
        foreach (var t in ToolCatalog.LoadEmbedded().Where(t => t.Kind == ToolKind.Cli))
        {
            var cmd = CommandBuilder.Build(t, new Dictionary<string, string?>());
            cmd.Args.Should().BeEmpty(t.Id);
        }
    }
}
