using System.IO.Compression;
using EzForensics.Core.Memory;
using EzForensics.Core.Plaso;
using EzForensics.Core.Setup;
using FluentAssertions;

namespace EzForensics.Core.Tests;

public sealed class VolatilityLocatorTests
{
    private const string Root = @"C:\L\EzForensicsData\volatility";
    private const string Exe = Root + @"\venv\Scripts\vol.exe";
    private const string Py = Root + @"\venv\Scripts\python.exe";
    private const string Probe = Py + " -c import importlib.metadata as m;print(m.version('volatility3'))";

    [Fact]
    public void Managed_venv_is_found_and_version_probed()
    {
        var p = new FakePlasoProbe();
        p.Files.UnionWith(new[] { Exe, Py });
        p.Outputs[Probe] = "2.28.2\r\n";
        var loc = new VolatilityLocator(p, Root);
        var i = loc.Discover(null)!;
        i.Exe.Should().Be(Exe);
        i.Python.Should().Be(Py);
        i.Version.Should().Be("2.28.2");
        i.SymbolsDir.Should().Be(Root + @"\symbols");
        i.CacheDir.Should().Be(Root + @"\cache");
        i.Root.Should().Be(Root);
    }

    [Fact]
    public void Configured_path_wins_over_managed_and_path()
    {
        var p = new FakePlasoProbe();
        p.Dirs.Add(@"D:\vol");
        p.Files.UnionWith(new[] { @"D:\vol\Scripts\vol.exe", @"D:\vol\Scripts\python.exe", Exe, Py });
        p.OnPath["vol.exe"] = @"C:\bin\vol.exe";
        p.Outputs[@"D:\vol\Scripts\python.exe -c import importlib.metadata as m;print(m.version('volatility3'))"] = "2.26.0";
        p.Outputs[Probe] = "2.28.2";
        var loc = new VolatilityLocator(p, Root);
        loc.Discover(@"D:\vol")!.Exe.Should().Be(@"D:\vol\Scripts\vol.exe");
        loc.Discover(@"D:\vol")!.Version.Should().Be("2.26.0");
        loc.Discover(@"D:\vol")!.SymbolsDir.Should().Be(Root + @"\symbols", "symbols always live in the managed root");
        loc.Candidates(null).Select(c => c.Exe).Should().Equal(Exe, @"C:\bin\vol.exe");
    }

    [Fact]
    public void Configured_exe_file_and_bad_paths()
    {
        var p = new FakePlasoProbe();
        p.Files.UnionWith(new[] { @"D:\x\vol.exe", @"D:\x\python.exe", @"D:\x\notvol.exe" });
        p.Outputs[@"D:\x\python.exe -c import importlib.metadata as m;print(m.version('volatility3'))"] = "2.27.0";
        var loc = new VolatilityLocator(p, Root);
        loc.Discover(@"D:\x\vol.exe")!.Version.Should().Be("2.27.0");
        loc.Candidates(@"D:\x\notvol.exe").Should().BeEmpty();
        loc.Candidates(@"D:\nowhere").Should().BeEmpty();
        loc.Discover(null).Should().BeNull();
    }

    [Fact]
    public void Candidate_whose_python_does_not_answer_is_skipped_and_PATH_is_the_fallback()
    {
        var p = new FakePlasoProbe();
        p.Files.UnionWith(new[] { Exe, Py, @"C:\bin\vol.exe", @"C:\bin\python.exe" });
        p.OnPath["vol.exe"] = @"C:\bin\vol.exe";
        p.Outputs[@"C:\bin\python.exe -c import importlib.metadata as m;print(m.version('volatility3'))"] = "2.25.0";
        new VolatilityLocator(p, Root).Discover(null)!.Exe.Should().Be(@"C:\bin\vol.exe");
    }

    [Theory]
    [InlineData("2.28.2\n", "2.28.2")]
    [InlineData("warning: x\r\n2.28.2\r\n", "2.28.2")]
    [InlineData("Traceback...\nPackageNotFoundError", null)]
    [InlineData("", null)]
    public void Version_output_is_parsed(string output, string? expected) => VolatilityLocator.ParseVersion(output).Should().Be(expected);
}

public sealed class VolatilitySetupTests : IDisposable
{
    private readonly TempDir _tmp = new();
    public void Dispose() => _tmp.Dispose();

    [Fact]
    public void Pinned_version_is_a_recent_release()
    {
        Version.TryParse(VolatilitySetup.PinnedVersion, out var v).Should().BeTrue();
        v!.Should().BeGreaterThanOrEqualTo(new Version(2, 26));
        VolatilitySetup.SymbolPackUrl.Should().Be("https://downloads.volatilityfoundation.org/volatility3/symbols/windows.zip");
    }

    [Fact]
    public void Pip_command_pins_volatility_and_installs_the_optional_libraries_but_not_full()
    {
        var loc = new VolatilityLocator(new FakePlasoProbe(), @"C:\V");
        var args = VolatilitySetup.PipInstall(VolatilitySetup.ManagedInstall(loc));
        args.Should().Equal("-m", "pip", "install", "--upgrade", "--disable-pip-version-check", "volatility3==" + VolatilitySetup.PinnedVersion,
            "pefile", "capstone", "pycryptodome", "yara-python");
        args.Should().NotContain(a => a.Contains("[full]"));
        var step = VolatilitySetup.PipStep(loc);
        step.Exe.Should().Be(@"C:\V\venv\Scripts\python.exe");
        var venv = VolatilitySetup.CreateVenvStep(loc, new PythonInfo(@"C:\P\python.exe", new Version(3, 12, 10), PythonInfo.PrivateOrigin));
        venv.Exe.Should().Be(@"C:\P\python.exe");
        venv.Args.Should().Equal("-m", "venv", @"C:\V\venv");
        step.ToJobSpec(@"C:\M").Environment.Should().ContainKey("PYTHONNOUSERSITE");
    }

    [Fact]
    public void Uninstall_removes_venv_and_cache_but_keeps_symbols_unless_asked()
    {
        var root = Path.Combine(_tmp.Path, "volatility");
        foreach (var d in new[] { "venv\\Scripts", "cache", "symbols\\windows" }) Directory.CreateDirectory(Path.Combine(root, d));
        File.WriteAllText(Path.Combine(root, "symbols", "windows", "a.json.xz"), "x");
        VolatilitySetup.Uninstall(root).Should().HaveCount(2);
        Directory.Exists(Path.Combine(root, "venv")).Should().BeFalse();
        Directory.Exists(Path.Combine(root, "cache")).Should().BeFalse();
        File.Exists(Path.Combine(root, "symbols", "windows", "a.json.xz")).Should().BeTrue();
        VolatilitySetup.Uninstall(root, includeSymbols: true).Should().HaveCount(1);
        Directory.Exists(Path.Combine(root, "symbols")).Should().BeFalse();
        VolatilitySetup.Uninstall(root).Should().BeEmpty();
    }

    [Fact]
    public void Plaso_uninstall_keeps_the_private_python_while_the_volatility_venv_exists()
    {
        var plaso = Path.Combine(_tmp.Path, "plaso");
        var vol = Path.Combine(_tmp.Path, "volatility");
        Directory.CreateDirectory(Path.Combine(plaso, "venv"));
        Directory.CreateDirectory(Path.Combine(plaso, "python", "Lib"));
        Directory.CreateDirectory(Path.Combine(vol, "venv"));
        var loc = new PlasoLocator(SystemPlasoProbe.Instance, Path.Combine(plaso, "venv"));

        PlasoSetup.VolatilityVenvExists(loc).Should().BeTrue();
        PlasoSetup.Uninstall(loc).Should().ContainSingle().Which.Should().EndWith("venv");
        Directory.Exists(Path.Combine(plaso, "python")).Should().BeTrue();

        VolatilitySetup.Uninstall(vol);
        PlasoSetup.VolatilityVenvExists(loc).Should().BeFalse();
        PlasoSetup.Uninstall(loc).Should().ContainSingle().Which.Should().EndWith("python");
        Directory.Exists(Path.Combine(plaso, "python")).Should().BeFalse();
    }

    [Fact]
    public void Symbol_pack_extracts_with_structure_marker_and_rejects_path_traversal()
    {
        var zip = Path.Combine(_tmp.Path, "windows.zip");
        using (var z = ZipFile.Open(zip, ZipArchiveMode.Create))
        {
            using (var w = new StreamWriter(z.CreateEntry("windows/ntkrnlmp.pdb/ABC-1.json.xz").Open())) w.Write("x");
        }
        var sym = Path.Combine(_tmp.Path, "symbols");
        VolatilitySetup.SymbolPackInstalled(sym).Should().BeFalse();
        VolatilitySetup.ExtractSymbolPack(zip, sym).Should().Be(1);
        File.Exists(Path.Combine(sym, "windows", "ntkrnlmp.pdb", "ABC-1.json.xz")).Should().BeTrue();
        VolatilitySetup.SymbolPackInstalled(sym).Should().BeTrue();

        var evil = Path.Combine(_tmp.Path, "evil.zip");
        using (var z = ZipFile.Open(evil, ZipArchiveMode.Create))
        using (var w = new StreamWriter(z.CreateEntry("../outside.txt").Open())) w.Write("x");
        var sym2 = Path.Combine(_tmp.Path, "symbols2");
        ((Action)(() => VolatilitySetup.ExtractSymbolPack(evil, sym2))).Should().Throw<InvalidDataException>();
        File.Exists(Path.Combine(_tmp.Path, "outside.txt")).Should().BeFalse();
        VolatilitySetup.SymbolPackInstalled(sym2).Should().BeFalse();
    }

    private sealed class BodyHandler(byte[] body) : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken ct) =>
            Task.FromResult(new HttpResponseMessage(System.Net.HttpStatusCode.OK) { Content = new ByteArrayContent(body), RequestMessage = request });
    }

    [Fact]
    public async Task Symbol_pack_download_returns_logs_and_marks_the_sha256()
    {
        var body = System.Text.Encoding.UTF8.GetBytes("symbol pack bytes");
        var expected = Convert.ToHexString(System.Security.Cryptography.SHA256.HashData(body));
        var dest = Path.Combine(_tmp.Path, "dl", "windows.zip");
        var lines = new List<string>();
        var sha = await VolatilitySetup.DownloadFileAsync(new HttpClient(new BodyHandler(body)), VolatilitySetup.SymbolPackUrl, dest, null, lines.Add, CancellationToken.None);
        sha.Should().Be(expected);
        lines.Should().Contain(l => l.Contains(expected));

        var zip = Path.Combine(_tmp.Path, "w.zip");
        using (var z = ZipFile.Open(zip, ZipArchiveMode.Create))
        using (var w = new StreamWriter(z.CreateEntry("windows/a.json.xz").Open())) w.Write("x");
        var sym = Path.Combine(_tmp.Path, "symbols-sha");
        VolatilitySetup.ExtractSymbolPack(zip, sym, sha256: sha);
        File.ReadAllText(Path.Combine(sym, VolatilitySetup.SymbolPackMarker)).Should().Contain("sha256=" + expected);
    }

    [Theory]
    [InlineData("https://downloads.volatilityfoundation.org/volatility3/symbols/windows.zip", true)]
    [InlineData("http://downloads.volatilityfoundation.org/volatility3/symbols/windows.zip", false)]
    [InlineData("https://evil.example/windows.zip", false)]
    public void Only_the_official_symbol_host_is_accepted(string url, bool ok) => VolatilitySetup.IsOfficialSymbolUrl(url).Should().Be(ok);

    [Fact]
    public void Release_check_parses_pypi_json_and_compares_versions()
    {
        var r = VolatilityReleaseCheck.ParsePyPiJson("{\"info\":{\"version\":\"2.28.2\",\"requires_python\":\">=3.8.0\"},\"urls\":[]}");
        r!.Version.Should().Be("2.28.2");
        VolatilityReleaseCheck.IsNewer("2.26.0", "2.28.2").Should().BeTrue();
        VolatilityReleaseCheck.IsNewer("2.28.2", "2.28.2").Should().BeFalse();
        VolatilityReleaseCheck.IsNewer(null, "2.28.2").Should().BeFalse();
    }
}

public sealed class VolatilitySetupPlanTests
{
    private static readonly SetupCatalog Cat = SetupCatalog.Default;

    private static SystemCheckResult Sys(string? python = null, string? plaso = null, string? vol = null, bool symbols = false) =>
        SetupCatalogTests.Sys(python: python, plaso: plaso) with { VolatilityVersion = vol, VolatilitySymbolsInstalled = symbols };

    private static SetupSelection Sel(params string[] ids)
    {
        var s = new SetupSelection();
        s.Items.UnionWith(ids);
        return s;
    }

    [Fact]
    public void Volatility_alone_without_python_provisions_the_private_python_then_installs()
    {
        var plan = SetupPlanner.Plan(Cat, Sel(SetupIds.Volatility), Sys());
        plan.Steps.Select(s => s.Kind).Should().Equal(SetupStepKind.ProvisionPython, SetupStepKind.InstallVolatility);
        plan.Steps[1].DependsOn.Should().Equal("python");
        plan.Steps[1].Title.Should().Contain(VolatilitySetup.PinnedVersion);
        plan.TotalDownloadMB.Should().BeGreaterThan(PlasoSetup.PythonDownloadMB);
        plan.NeedsAdmin.Should().BeFalse();
    }

    [Fact]
    public void Plaso_and_volatility_share_one_python_step()
    {
        var plan = SetupPlanner.Plan(Cat, Sel(SetupIds.Plaso, SetupIds.Volatility), Sys());
        plan.Steps.Count(s => s.Kind == SetupStepKind.ProvisionPython).Should().Be(1);
        plan.Steps.Select(s => s.Kind).Should().Equal(SetupStepKind.ProvisionPython, SetupStepKind.CreatePlasoVenv, SetupStepKind.InstallPlaso, SetupStepKind.InstallVolatility);
        plan.Steps.Single(s => s.Kind == SetupStepKind.InstallVolatility).DependsOn.Should().Equal("python");
        plan.Steps.Single(s => s.Kind == SetupStepKind.CreatePlasoVenv).DependsOn.Should().Equal("python");
    }

    [Fact]
    public void Volatility_with_plaso_already_installed_still_provisions_python_once_when_none_is_usable()
    {
        var plan = SetupPlanner.Plan(Cat, Sel(SetupIds.Plaso, SetupIds.Volatility), Sys(plaso: "20260720"));
        plan.Steps.Select(s => s.Kind).Should().Equal(SetupStepKind.ProvisionPython, SetupStepKind.InstallPlaso, SetupStepKind.InstallVolatility);
        plan.Steps[1].State.Should().Be(SetupStepState.AlreadyInstalled);
    }

    [Fact]
    public void Existing_python_means_no_python_step()
    {
        var plan = SetupPlanner.Plan(Cat, Sel(SetupIds.Volatility), Sys(python: @"C:\Py\python.exe"));
        plan.Steps.Should().ContainSingle().Which.Kind.Should().Be(SetupStepKind.InstallVolatility);
        plan.Steps[0].DependsOn.Should().BeEmpty();
    }

    [Fact]
    public void Installed_volatility_is_already_installed_and_needs_no_python()
    {
        var plan = SetupPlanner.Plan(Cat, Sel(SetupIds.Volatility), Sys(vol: "2.28.2"));
        plan.Steps.Should().ContainSingle().Which.State.Should().Be(SetupStepState.AlreadyInstalled);
        plan.IsEmpty.Should().BeTrue();
    }

    [Fact]
    public void Symbol_pack_pulls_in_volatility_and_depends_on_it()
    {
        var plan = SetupPlanner.Plan(Cat, Sel(SetupIds.VolatilitySymbols), Sys(python: "py"));
        plan.Steps.Select(s => s.Kind).Should().Equal(SetupStepKind.InstallVolatility, SetupStepKind.InstallVolatilitySymbols);
        plan.Steps[1].DependsOn.Should().Equal("volatility");
        plan.Steps[1].DownloadMB.Should().Be(VolatilitySetup.SymbolPackDownloadMB);
        plan.Notes.Should().Contain(n => n.Contains("Volatility 3 added"));
    }

    [Fact]
    public void Installed_symbol_pack_is_already_installed()
    {
        var plan = SetupPlanner.Plan(Cat, Sel(SetupIds.VolatilitySymbols), Sys(vol: "2.28.2", symbols: true));
        plan.IsEmpty.Should().BeTrue();
    }

    [Fact]
    public void Profiles_select_volatility_and_the_symbol_pack_only_in_everything()
    {
        var rec = SetupSelection.ForProfile(SetupProfile.Minimal, Cat, Sys());
        rec.Items.Should().NotContain(SetupIds.Volatility).And.NotContain(SetupIds.VolatilitySymbols);
        var all = SetupSelection.ForProfile(SetupProfile.Everything, Cat, Sys());
        all.Items.Should().Contain(SetupIds.Volatility).And.Contain(SetupIds.VolatilitySymbols);
    }

    [Fact]
    public void Everything_selects_every_visible_selectable_item_and_plans_the_symbol_pack()
    {
        var all = SetupSelection.ForProfile(SetupProfile.Everything, Cat, Sys());
        var expected = Cat.Bundles.Where(b => !b.ComingSoon).SelectMany(b => b.Items)
            .Where(i => i.Selectable && !i.Hidden && i.Id != SetupIds.Dotnet9 && i.Id != SetupIds.LocalAi).Select(i => i.Id);
        all.Items.Should().Contain(expected, "Everything means every item shown in the wizard");
        SetupPlanner.Plan(Cat, all, Sys(python: "py")).Steps.Should().Contain(s => s.Kind == SetupStepKind.InstallVolatilitySymbols);
    }
}
