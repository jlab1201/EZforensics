using EzForensics.Core.Catalog;
using FluentAssertions;

namespace EzForensics.Core.Tests;

public class CatalogTests
{
    private static readonly string[] CliTools =
    {
        "AmcacheParser", "AppCompatCacheParser", "EvtxECmd", "JLECmd", "LECmd", "MFTECmd", "PECmd",
        "RBCmd", "RecentFileCacheParser", "RECmd", "rla", "SBECmd", "SQLECmd", "SrumECmd", "SumECmd", "WxTCmd",
        "iisGeolocate",
    };

    private static readonly string[] GuiTools =
    {
        "RegistryExplorer", "ShellBagsExplorer", "MFTExplorer", "JumpListExplorer", "SDBExplorer", "TimelineExplorer",
    };

    [Fact]
    public void Catalog_LoadsEveryTool()
    {
        var all = ToolCatalog.LoadEmbedded();
        all.Select(t => t.Id).Should().BeEquivalentTo(CliTools.Concat(GuiTools));
        all.Select(t => t.Id).Should().OnlyHaveUniqueItems();
        all.Where(t => t.Kind == ToolKind.Cli).Select(t => t.Id).Should().BeEquivalentTo(CliTools);
        all.Where(t => t.Kind == ToolKind.Gui).Should().OnlyContain(t => t.Parameters.Count == 0);
    }

    [Fact]
    public void Catalog_EveryToolIsWellFormed()
    {
        foreach (var t in ToolCatalog.LoadEmbedded())
        {
            t.DisplayName.Should().NotBeNullOrWhiteSpace(t.Id);
            t.Category.Should().NotBeNullOrWhiteSpace(t.Id);
            t.Description.Should().NotBeNullOrWhiteSpace(t.Id);
            t.Usage.Should().NotBeNullOrWhiteSpace(t.Id);
            t.Usage.Split(new[] { ". " }, StringSplitOptions.RemoveEmptyEntries).Length.Should().BeInRange(1, 3, t.Id + " usage is 1-3 sentences");
            t.Exe.Should().EndWith(".exe", t.Id);
            t.Zip.Should().EndWith(".zip", t.Id);
            t.Builds.Should().NotBeEmpty(t.Id).And.OnlyContain(b => b == "net9" || b == "net4", t.Id);

            t.Parameters.Select(p => p.Id).Should().OnlyHaveUniqueItems(t.Id);
            t.Parameters.Select(p => p.Flag).Should().OnlyHaveUniqueItems(t.Id);
            foreach (var p in t.Parameters)
            {
                p.Id.Should().NotBeNullOrWhiteSpace(t.Id);
                p.Flag.Should().StartWith("-", $"{t.Id}.{p.Id}");
                p.Label.Should().NotBeNullOrWhiteSpace($"{t.Id}.{p.Id}");
                p.Description.Should().NotBeNullOrWhiteSpace($"{t.Id}.{p.Id}");
                if (p.VisibleWhen is not null)
                    t.FindParameter(p.VisibleWhen).Should().NotBeNull($"{t.Id}.{p.Id} visibleWhen");
                if (p.Type == ParamType.Bool)
                    p.Default.Should().BeOneOf(new[] { null, "true", "false" }, $"{t.Id}.{p.Id}");
                if (p.Type == ParamType.Enum)
                    p.Values.Should().NotBeNullOrEmpty($"{t.Id}.{p.Id}");
            }
            foreach (var g in t.Groups)
            {
                g.Params.Should().HaveCountGreaterThan(1, t.Id);
                g.Params.Should().OnlyContain(id => t.FindParameter(id) != null, $"{t.Id} group members");
            }
        }
    }

    [Fact]
    public void Catalog_SharedParametersAreResolvedAndOverridable()
    {
        var evtx = ToolCatalog.Find("evtxecmd")!;
        evtx.Should().NotBeNull();
        var dedupe = evtx.FindParameter("dedupe")!;
        dedupe.Flag.Should().Be("--dedupe");
        dedupe.Type.Should().Be(ParamType.Bool);
        dedupe.DefaultIsTrue.Should().BeTrue();
        evtx.FindParameter("dt")!.Default.Should().Be("yyyy-MM-dd HH:mm:ss.fffffff");
        evtx.FindParameter("csvf")!.VisibleWhen.Should().Be("csv");
        ToolCatalog.Find("PECmd")!.FindParameter("dt")!.Default.Should().Be("yyyy-MM-dd HH:mm:ss");
    }

    [Fact]
    public void Catalog_FlagsAndExeLayout()
    {
        ToolCatalog.Find("MFTECmd")!.Exclusive.Should().BeTrue();
        ToolCatalog.LoadEmbedded().Where(t => t.Exclusive).Select(t => t.Id).Should().Equal("MFTECmd");
        ToolCatalog.LoadEmbedded().Where(t => t.Kind == ToolKind.Cli).Should().OnlyContain(t => !t.RequiresAdmin);
        ToolCatalog.Find("RegistryExplorer")!.Exe.Should().Be("RegistryExplorer.exe");
    }

    [Fact]
    public void Catalog_UnknownSharedReferenceThrows()
    {
        var act = () => ToolCatalog.Parse("""{"id":"X","parameters":[{"use":"nope"}]}""",
            new Dictionary<string, System.Text.Json.Nodes.JsonObject>());
        act.Should().Throw<InvalidDataException>().WithMessage("*nope*");
    }

    [Fact]
    public void CatalogValidator_ParsesSystemCommandLineOptions()
    {
        const string help = """
            Description:
              Something -f <f> in the description

            Usage:
              Tool [options]

            Options:
              -f, --file <file>  File to process
              --csv <csv>        Directory to save CSV
              --mp               When true, display higher precision
              --dt <dt>          Format
                                  [default: yyyy-MM-dd HH:mm:ss]
              -?, -h, --help     Show help and usage information
              --version          Show version information
            """;
        var opts = CatalogValidator.ParseHelp(help);
        opts.Select(o => string.Join("|", o.Aliases)).Should()
            .Equal("-f|--file", "--csv", "--mp", "--dt", "-?|-h|--help", "--version");
        opts[0].ArgumentName.Should().Be("file");
        opts[2].IsFlag.Should().BeTrue();

        var def = new ToolDefinition
        {
            Id = "Tool",
            Parameters =
            {
                new ParameterDef { Id = "f", Flag = "-f" },
                new ParameterDef { Id = "csv", Flag = "--csv" },
                new ParameterDef { Id = "x", Flag = "--extra" },
            },
        };
        var drift = CatalogValidator.Validate(def, help);
        drift.HasDrift.Should().BeTrue();
        drift.MissingFromHelp.Should().Equal("--extra");
        drift.MissingFromDefinition.Should().Equal("--mp", "--dt");
    }

    [Fact]
    public void CatalogValidator_NoDriftAgainstRealHelpOutput()
    {
        var helpDir = FindHelpDir();
        if (helpDir is null) return; // .devtools\help not present (e.g. CI): nothing to compare against.

        var files = Directory.GetFiles(helpDir, "*.txt");
        files.Should().NotBeEmpty();
        var problems = new List<string>();
        foreach (var file in files)
        {
            var id = Path.GetFileNameWithoutExtension(file);
            var def = ToolCatalog.Find(id);
            if (def is null) { problems.Add($"{id}: no definition for help file"); continue; }
            var drift = CatalogValidator.Validate(def, File.ReadAllText(file));
            if (drift.HasDrift) problems.Add(drift.ToString());
        }
        problems.Should().BeEmpty();
    }

    private static string? FindHelpDir()
    {
        for (var dir = new DirectoryInfo(AppContext.BaseDirectory); dir is not null; dir = dir.Parent)
        {
            if (File.Exists(Path.Combine(dir.FullName, "EzForensics.sln")))
            {
                var help = Path.Combine(dir.FullName, ".devtools", "help");
                return Directory.Exists(help) ? help : null;
            }
        }
        return null;
    }
}
