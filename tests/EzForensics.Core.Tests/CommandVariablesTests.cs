using EzForensics.Core.Results;
using EzForensics.Core.Triage;
using FluentAssertions;

namespace EzForensics.Core.Tests;

public class CommandVariablesTests
{
    [Fact]
    public void Value_replaces_token_and_keeps_flag()
    {
        var r = CommandVariables.Expand(new[] { "--dt", "{dateFormat}", "--csv", "o" }, "dateFormat", "yyyy-MM-dd", "--dt");
        r.Should().Equal("--dt", "yyyy-MM-dd", "--csv", "o");
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("  ")]
    public void Blank_value_drops_token_and_preceding_flag(string? v)
    {
        var r = CommandVariables.Expand(new[] { "-f", "x", "--DT", "{dateFormat}", "--csv", "o" }, "dateFormat", v, "--dt");
        r.Should().Equal("-f", "x", "--csv", "o");
    }

    [Fact]
    public void Blank_value_without_matching_flag_only_drops_token()
    {
        CommandVariables.Expand(new[] { "a", "{x}", "b" }, "x", null, "--dt").Should().Equal("a", "b");
    }

    [Fact]
    public void Missing_token_returns_same_list()
    {
        var args = new[] { "a" };
        CommandVariables.Expand(args, "x", "v").Should().BeSameAs(args);
    }

    [Fact]
    public void Batch_blank_setting_is_built_in()
        => CommandVariables.ResolveBatchFile("").Should().Be(CommandVariables.BuiltInBatchFile);

    [Fact]
    public void Batch_missing_file_falls_back_and_warns()
    {
        string? w = null;
        CommandVariables.ResolveBatchFile(@"C:\nope\missing.reb", m => w = m).Should().Be(CommandVariables.BuiltInBatchFile);
        w.Should().Contain("missing.reb");
    }

    [Fact]
    public void Batch_existing_file_is_used()
    {
        var f = Path.GetTempFileName();
        try { CommandVariables.ResolveBatchFile(f).Should().Be(f); } finally { File.Delete(f); }
    }

    [Fact]
    public void Dt_literal_not_parsed_by_analyze_warns()
    {
        var rule = new TriageRule { Id = "r", Label = "r", ToolId = "T", ExeName = "T.exe", Mode = RuleMode.File, Args = new() { "-f", "{file}", "--csv", "{out}" } };
        CustomCommand.Parse("T.exe -f {file} --dt dd/MM/yyyy --csv {out}", rule).Warnings.Should().Contain(w => w.Contains("--dt"));
        CustomCommand.Parse("T.exe -f {file} --dt \"yyyy-MM-dd HH:mm:ss\" --csv {out}", rule).Warnings.Should().BeEmpty();
        CustomCommand.Parse("T.exe -f {file} --dt {dateFormat} --csv {out}", rule).Warnings.Should().BeEmpty();
        CustomCommand.Parse("T.exe -f {file} --dt {dateFormat} --bn {batchFile} --csv {out}", rule).IsValid.Should().BeTrue();
    }
}
