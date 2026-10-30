using EzForensics.Core.Triage;
using FluentAssertions;

namespace EzForensics.Core.Tests;

public class CustomCommandTests : IDisposable
{
    private readonly string _dir = Path.Combine(Path.GetTempPath(), "ezcc_" + Guid.NewGuid().ToString("N"));
    public CustomCommandTests() => Directory.CreateDirectory(_dir);
    public void Dispose() { try { Directory.Delete(_dir, true); } catch { } }

    private static TriageRule Rule(RuleMode mode = RuleMode.File) => new()
    {
        Id = "evtx", Label = "Event logs", ToolId = "EvtxECmd", ExeName = "EvtxECmd.exe", Mode = mode,
        Args = new() { "-f", "{file}", "--csv", "{out}" },
    };

    [Theory]
    [InlineData(@"EvtxECmd.exe -f {file} --csv {out}")]
    [InlineData(@"""C:\Tools\EZ\EvtxECmd.exe"" -f {file} --csv {out}")]
    [InlineData(@"EvtxECmd -f {file} --csv {out}")]
    [InlineData(@"& 'C:\x\EvtxECmd.exe' '-f' '{file}' '--csv' '{out}'")]
    public void Exe_is_stripped(string text)
    {
        var r = CustomCommand.Parse(text, Rule());
        r.IsValid.Should().BeTrue();
        r.Args.Should().Equal("-f", "{file}", "--csv", "{out}");
        r.Warnings.Should().BeEmpty();
    }

    [Fact]
    public void Single_quoted_token_with_spaces_stays_one_arg()
    {
        var r = CustomCommand.Parse(@"& 'C: b\EvtxECmd.exe' '-d' '{dir}' '--csv' '{out}'", Rule(RuleMode.Dir));
        r.Args.Should().Equal("-d", "{dir}", "--csv", "{out}");
        var r2 = CustomCommand.Parse(@"EvtxECmd.exe -f {file} --csv '{out}\my dir'", Rule());
        r2.Args.Should().Equal("-f", "{file}", "--csv", @"{out}\my dir");
    }

    [Fact]
    public void Fixed_csvf_on_per_match_rule_warns()
    {
        var rule = Rule(); rule.PerMatch = true;
        CustomCommand.Parse("EvtxECmd.exe -f {file} --csv {out} --csvf a.csv", rule).Warnings.Should().Contain(w => w.Contains("--csvf"));
        CustomCommand.Parse("EvtxECmd.exe -f {file} --csv {out} --csvf a.csv", Rule()).Warnings.Should().BeEmpty();
    }

    [Fact]
    public void Quoted_path_with_spaces_stays_one_arg_and_kape_vars_are_mapped()
    {
        var r = CustomCommand.Parse(@"EvtxECmd.exe -f %sourceFile% --csv %destinationDirectory% --maps ""C:\my maps""", Rule());
        r.Args.Should().Equal("-f", "{file}", "--csv", "{out}", "--maps", @"C:\my maps");
    }

    [Fact]
    public void Missing_out_unknown_placeholder_and_empty_are_errors()
    {
        CustomCommand.Parse("EvtxECmd.exe -f {file}", Rule()).Errors.Should().ContainSingle(e => e.Contains("{out} is required"));
        CustomCommand.Parse("EvtxECmd.exe -f {file} --csv {out} --x {foo}", Rule()).Errors.Should().ContainSingle(e => e.Contains("{foo}"));
        CustomCommand.Parse("  ", Rule()).IsValid.Should().BeFalse();
        CustomCommand.Parse("EvtxECmd.exe", Rule()).IsValid.Should().BeFalse();
    }

    [Fact]
    public void Source_warnings()
    {
        CustomCommand.Parse("EvtxECmd.exe --csv {out}", Rule()).Errors.Should().ContainSingle(w => w.StartsWith("Add {file}"));
        CustomCommand.Parse("EvtxECmd.exe --csv {out}", Rule(RuleMode.Dir)).Errors.Should().ContainSingle(w => w.StartsWith("Add {dir}"));
        CustomCommand.Parse("EvtxECmd.exe -d {dir} --csv {out}", Rule()).Warnings.Should().ContainSingle(w => w.Contains("file's folder"));
        CustomCommand.Parse("EvtxECmd.exe -f {file} --csv {out}", Rule(RuleMode.Dir)).Warnings.Should().ContainSingle(w => w.Contains("matched folder"));
    }

    [Fact]
    public void DefaultText_round_trips_for_every_embedded_rule()
    {
        foreach (var rule in TriageRules.LoadEmbedded())
        {
            var r = CustomCommand.Parse(CustomCommand.DefaultText(rule), rule);
            r.Args.Should().Equal(rule.Args, rule.Id);
            r.Errors.Should().BeEmpty(rule.Id);
        }
    }

    [Fact]
    public void Apply_swaps_only_valid_entries_and_reports_invalid()
    {
        var rules = TriageRules.LoadEmbedded();
        var store = new CustomCommandStore(Path.Combine(_dir, "c.json"));
        store.Set("evtx", "EvtxECmd.exe -f {file} --csv {out} --vss");
        store.Set("MFT", "MFTECmd.exe -f {file}");   // no {out}: invalid
        var problems = new List<string>();
        var applied = CustomCommand.Apply(rules, store, problems);

        applied.Single(r => r.Id == "evtx").Args.Should().Equal("-f", "{file}", "--csv", "{out}", "--vss");
        applied.Single(r => r.Id == "mft").Should().BeSameAs(rules.Single(r => r.Id == "mft"));
        applied.Single(r => r.Id == "prefetch").Should().BeSameAs(rules.Single(r => r.Id == "prefetch"));
        problems.Should().ContainSingle().Which.Should().Contain("{out} is required");
        CustomCommand.IsCustom(store, "EVTX").Should().BeTrue();
        rules.Single(r => r.Id == "evtx").Args.Should().NotContain("--vss");
        applied.Single(r => r.Id == "evtx").Output.Should().BeSameAs(rules.Single(r => r.Id == "evtx").Output);
    }

    [Fact]
    public void Store_round_trips_and_corrupt_file_is_empty()
    {
        var path = Path.Combine(_dir, "sub", "c.json");
        var s = new CustomCommandStore(path);
        s.Set("Evtx", "x {out}");
        s.Set("gone", "y"); s.Set("gone", " ");
        s.Save();
        var l = new CustomCommandStore(path); l.Load();
        l.Get("evtx").Should().Be("x {out}");
        l.All.Should().HaveCount(1);
        File.ReadAllBytes(path)[0].Should().NotBe(0xEF);

        File.WriteAllText(path, "{ not json");
        l.Load();
        l.All.Should().BeEmpty();
        new CustomCommandStore(Path.Combine(_dir, "missing.json")).All.Should().BeEmpty();
    }

    [Fact]
    public void Planner_honours_custom_csvf_and_softwareHive_input()
    {
        var rule = Rule();
        rule.Output = new RuleOutput { Name = "EventLogs", ArtifactId = "eventlog", Csvf = true };
        rule.Args = new() { "-f", "{file}", "--csv", "{out}", "--csvf", @"C:\x\Mine.csv", "--sw", "{softwareHive}" };
        var a = new DetectedArtifact("evtx", "Event logs", "EvtxECmd", "EventLogs", @"C:\Win\a.evtx", false, 1, @"C:\Win");
        var job = TriagePlanner.PlanJobs(new[] { a }, new[] { rule }, Path.Combine(_dir, "case"), @"C:\Win").Single();

        job.Spec.Args.Count(x => x.Equals("--csvf", StringComparison.OrdinalIgnoreCase)).Should().Be(1);
        job.Spec.Output!.FixedFileName.Should().Be("Mine.csv");
        job.Spec.Provenance!.Inputs.Should().HaveCount(2);

        rule.Args = new() { "-f", "{file}", "--csv", "{out}" };
        var def = TriagePlanner.PlanJobs(new[] { a }, new[] { rule }, Path.Combine(_dir, "case"), @"C:\Win").Single();
        def.Spec.Args.Should().Contain("--csvf");
        def.Spec.Output!.FixedFileName.Should().EndWith(".csv");
    }

    private static TriageRule RuleWith(params string[] args) => new()
    {
        Id = "x", Label = "X", ToolId = "T", ExeName = "T.exe", Mode = RuleMode.File, Args = args.ToList(),
    };

    [Fact]
    public void Missing_default_variables_warn()
    {
        var rule = RuleWith("-f", "{file}", "-r", "{softwareHive}", "--csv", "{out}", "--inc", "{eventIds}", "--x", "{root}");
        var w = CustomCommand.Parse("T.exe -f {file} --csv {out}", rule).Warnings;
        w.Should().Contain(x => x.Contains("SOFTWARE hive"));
        w.Should().Contain("The default uses {eventIds}; it is missing here.");
        w.Should().Contain("The default uses {root}; it is missing here.");
        CustomCommand.Parse("T.exe -f {file} -r {softwareHive} --csv {out} --inc {eventIds} --x {root}", rule).Warnings.Should().BeEmpty();
    }

    [Theory]
    [InlineData(@"T.exe -f C:\x\a.hve --csv {out}", "-f", "{file}")]
    [InlineData(@"T.exe -f {file} --csv D:\out", "--csv", "{out}")]
    [InlineData(@"T.exe -f {file} --JSON \srv\share --csv {out}", "--JSON", "{out}")]
    [InlineData(@"T.exe -f {file} -r C:SOFT --csv {out}", "-r", "{softwareHive}")]
    public void Fixed_paths_after_flags_warn(string text, string flag, string suggest)
    {
        var w = CustomCommand.Parse(text, Rule()).Warnings;
        w.Should().Contain(x => x.Contains("is a fixed path") && x.Contains(" after " + flag + " ") && x.Contains("use " + suggest));
    }

    [Fact]
    public void Relative_values_and_placeholders_do_not_warn_as_fixed_paths()
    {
        CustomCommand.Parse(@"T.exe -f {file} --csv {out}\sub --html name.html", Rule()).Warnings.Should().BeEmpty();
    }
}
