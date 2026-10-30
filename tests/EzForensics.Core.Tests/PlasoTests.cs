using EzForensics.Core.Execution;
using EzForensics.Core.Plaso;
using EzForensics.Core.Results;
using FluentAssertions;

namespace EzForensics.Core.Tests;

/// <summary>Scripted file system / process probe for PlasoLocator.</summary>
internal sealed class FakePlasoProbe : IPlasoProbe
{
    public readonly HashSet<string> Files = new(StringComparer.OrdinalIgnoreCase);
    public readonly HashSet<string> Dirs = new(StringComparer.OrdinalIgnoreCase);
    public readonly Dictionary<string, string> OnPath = new(StringComparer.OrdinalIgnoreCase);
    /// <summary>Key: exe + " " + args joined by spaces. Value: output (null = failure).</summary>
    public readonly Dictionary<string, string?> Outputs = new(StringComparer.OrdinalIgnoreCase);
    public readonly List<string> Calls = new();

    public bool FileExists(string path) => Files.Contains(path);
    public bool DirectoryExists(string path) => Dirs.Contains(path);
    public string? FindOnPath(string fileName) => OnPath.TryGetValue(fileName, out var p) ? p : null;

    public string? Run(string exe, IReadOnlyList<string> args, TimeSpan timeout)
    {
        var key = exe + " " + string.Join(" ", args);
        Calls.Add(key);
        return Outputs.TryGetValue(key, out var o) ? o : null;
    }
}

public sealed class PlasoLocatorTests
{
    private const string Venv = @"C:\L\EzHub\plaso\venv";

    private static FakePlasoProbe Probe() => new();

    [Fact]
    public void Configured_folder_wins_over_venv_path_and_docker()
    {
        var p = Probe();
        p.Dirs.Add(@"D:\plaso");
        p.Files.UnionWith(new[] { @"D:\plaso\log2timeline.exe", @"D:\plaso\psort.exe", Venv + @"\Scripts\log2timeline.exe", Venv + @"\Scripts\psort.exe" });
        p.OnPath["log2timeline.exe"] = @"C:\bin\log2timeline.exe";
        p.OnPath["psort.exe"] = @"C:\bin\psort.exe";
        p.Outputs[@"D:\plaso\log2timeline.exe --version"] = "plaso - log2timeline version 20240308";
        p.Outputs[Venv + @"\Scripts\log2timeline.exe --version"] = "plaso - log2timeline version 20260720";

        var d = new PlasoLocator(p, Venv).Discover(@"D:\plaso");
        d.Selected!.Origin.Should().Be(PlasoOrigin.Configured);
        d.Selected.Kind.Should().Be(PlasoEngineKind.Executable);
        d.Selected.Version.Should().Be("20240308");
        d.Selected.Psort.Exe.Should().Be(@"D:\plaso\psort.exe");
        p.Calls.Should().Equal(@"D:\plaso\log2timeline.exe --version");
    }

    [Fact]
    public void Configured_python_exe_runs_the_plaso_module()
    {
        var p = Probe();
        p.Files.Add(@"D:\py\python.exe");
        p.Outputs[@"D:\py\python.exe -m plaso.scripts.log2timeline --version"] = "plaso - log2timeline version 20260720";
        var d = new PlasoLocator(p, Venv).Discover(@"D:\py\python.exe");
        d.Selected!.Kind.Should().Be(PlasoEngineKind.Python);
        d.Selected.Log2Timeline.PrefixArgs.Should().Equal("-m", "plaso.scripts.log2timeline");
        d.Selected.Psort.PrefixArgs.Should().Equal("-m", "plaso.scripts.psort");
    }

    [Fact]
    public void Broken_configured_path_falls_back_to_the_managed_venv()
    {
        var p = Probe();
        p.Dirs.Add(@"D:\plaso");
        p.Files.UnionWith(new[] { @"D:\plaso\log2timeline.exe", @"D:\plaso\psort.exe", Venv + @"\Scripts\log2timeline.exe", Venv + @"\Scripts\psort.exe" });
        p.Outputs[Venv + @"\Scripts\log2timeline.exe --version"] = "plaso - log2timeline version 20260720";

        var d = new PlasoLocator(p, Venv).Discover(@"D:\plaso");
        d.Selected!.Origin.Should().Be(PlasoOrigin.ManagedVenv);
        d.Selected.Version.Should().Be("20260720");
        d.Notes.Should().Contain(n => n.Contains("did not answer"));
    }

    [Fact]
    public void Venv_without_console_scripts_uses_python_module()
    {
        var p = Probe();
        p.Files.Add(Venv + @"\Scripts\python.exe");
        p.Outputs[Venv + @"\Scripts\python.exe -m plaso.scripts.log2timeline --version"] = "plaso - log2timeline version 20231224";
        var d = new PlasoLocator(p, Venv).Discover(null);
        d.Selected!.Origin.Should().Be(PlasoOrigin.ManagedVenv);
        d.Selected.Kind.Should().Be(PlasoEngineKind.Python);
    }

    [Fact]
    public void Path_executables_then_py_scripts_are_found()
    {
        var p = Probe();
        p.OnPath["log2timeline.exe"] = @"C:\bin\log2timeline.exe";
        p.OnPath["psort.exe"] = @"C:\bin\psort.exe";
        p.Outputs[@"C:\bin\log2timeline.exe --version"] = "plaso - log2timeline version 20260720";
        new PlasoLocator(p, Venv).Discover(null).Selected!.Origin.Should().Be(PlasoOrigin.Path);

        var q = Probe();
        q.OnPath["log2timeline.py"] = @"C:\src\log2timeline.py";
        q.OnPath["psort.py"] = @"C:\src\psort.py";
        q.OnPath["python.exe"] = @"C:\Python312\python.exe";
        q.Outputs[@"C:\Python312\python.exe C:\src\log2timeline.py --version"] = "plaso - log2timeline version 20260720";
        var d = new PlasoLocator(q, Venv).Discover(null);
        d.Selected!.Kind.Should().Be(PlasoEngineKind.Python);
        d.Selected.Psort.PrefixArgs.Should().Equal(@"C:\src\psort.py");
    }

    [Fact]
    public void Docker_is_last_and_needs_a_running_engine_and_the_image()
    {
        var p = Probe();
        p.OnPath["docker.exe"] = @"C:\Docker\docker.exe";
        p.Outputs[@"C:\Docker\docker.exe version --format {{.Server.Version}}"] = "29.6.1";
        var none = new PlasoLocator(p, Venv).Discover(null);
        none.Selected.Should().BeNull();
        none.DockerAvailable.Should().BeTrue();
        none.DockerImagePresent.Should().BeFalse();
        none.Notes.Should().Contain(n => n.Contains("docker pull log2timeline/plaso"));

        p.Outputs[@"C:\Docker\docker.exe image inspect --format {{.Id}} log2timeline/plaso"] = "sha256:abc";
        p.Outputs[@"C:\Docker\docker.exe run --rm log2timeline/plaso log2timeline --version"] = "plaso - log2timeline version 20260720";
        var d = new PlasoLocator(p, Venv).Discover(null);
        d.Selected!.Kind.Should().Be(PlasoEngineKind.Docker);
        d.Selected.Version.Should().Be("20260720");

        // A venv install is preferred over Docker.
        p.Files.UnionWith(new[] { Venv + @"\Scripts\log2timeline.exe", Venv + @"\Scripts\psort.exe" });
        p.Outputs[Venv + @"\Scripts\log2timeline.exe --version"] = "plaso - log2timeline version 20260720";
        new PlasoLocator(p, Venv).Discover(null).Selected!.Origin.Should().Be(PlasoOrigin.ManagedVenv);
    }

    [Fact]
    public void Nothing_found_reports_unavailable()
    {
        var d = new PlasoLocator(Probe(), Venv).Discover(null);
        d.IsAvailable.Should().BeFalse();
    }

    [Theory]
    [InlineData("plaso - log2timeline version 20260720", "20260720")]
    [InlineData("plaso - psort version 20240308\r\n", "20240308")]
    [InlineData("garbage", null)]
    public void Parses_version(string text, string? expected) => PlasoLocator.ParseVersion(text).Should().Be(expected);

    [Fact]
    public void Python_discovery_prefers_py_launcher_and_ignores_store_stub()
    {
        var p = Probe();
        p.OnPath["py.exe"] = @"C:\Windows\py.exe";
        p.OnPath["python.exe"] = @"C:\Users\u\AppData\Local\Microsoft\WindowsApps\python.exe";
        p.Outputs[@"C:\Windows\py.exe -3 -c import sys; print(sys.executable)"] = "C:\\Python312\\python.exe\r\n";
        p.Outputs[@"C:\Python312\python.exe --version"] = "Python 3.12.10";
        var loc = new PlasoLocator(p, Venv);
        var all = loc.FindPythons();
        all.Should().ContainSingle();
        all[0].Version.Should().Be(new Version(3, 12, 10));
        loc.FindSupportedPython()!.Exe.Should().Be(@"C:\Python312\python.exe");

        var old = Probe();
        old.OnPath["python.exe"] = @"C:\Python39\python.exe";
        old.Outputs[@"C:\Python39\python.exe --version"] = "Python 3.9.13";
        new PlasoLocator(old, Venv).FindSupportedPython().Should().BeNull();
    }

    [Fact]
    public void Install_commands_create_the_venv_and_pip_install_plaso()
    {
        var (venv, py, pip) = new PlasoLocator(Probe(), Venv).InstallCommands();
        venv.Should().Equal("-m", "venv", Venv);
        py.Should().Be(Venv + @"\Scripts\python.exe");
        pip.Should().ContainInOrder("-m", "pip", "install").And.Contain("plaso");
    }
}

public sealed class PlasoCommandBuilderTests : IDisposable
{
    private readonly TempDir _tmp = new();
    public void Dispose() => _tmp.Dispose();

    private static PlasoInstall Exe(string? version = "20260720") => new(PlasoOrigin.ManagedVenv, PlasoEngineKind.Executable,
        PlasoCommand.Of(@"C:\v\Scripts\log2timeline.exe"), PlasoCommand.Of(@"C:\v\Scripts\psort.exe"), version, @"C:\v\Scripts");

    private static PlasoInstall Docker() => new(PlasoOrigin.Docker, PlasoEngineKind.Docker,
        PlasoCommand.Of(@"C:\Docker\docker.exe"), PlasoCommand.Of(@"C:\Docker\docker.exe"), "20260720", "log2timeline/plaso");

    private PlasoRunOptions Opts(string? source = null) => new()
    {
        Source = source ?? Path.Combine(_tmp.Path, "evidence"),
        CaseDir = Path.Combine(_tmp.Path, "case dir"),
        Stamp = "20261007T120000Z",
    };

    [Fact]
    public void Log2timeline_argv_has_storage_status_view_and_options()
    {
        var o = Opts();
        o.Parsers = "win7,!filestat";
        o.Workers = 4;
        var inv = PlasoCommandBuilder.BuildLog2Timeline(Exe(), o);
        inv.Exe.Should().Be(@"C:\v\Scripts\log2timeline.exe");
        inv.Args.Should().Equal(
            "--status_view", "linear", "--unattended",
            "--logfile", Path.Combine(o.CaseDir, "Timeline", "plaso", "20261007T120000Z.log2timeline.log.gz"),
            "--storage_file", Path.Combine(o.CaseDir, "Timeline", "plaso", "20261007T120000Z.plaso"),
            "--parsers", "win7,!filestat",
            "--timezone", "UTC",
            "--workers", "4",
            "--vss_stores", "none",
            o.Source);
        inv.CancelCommand.Should().BeNull();
    }

    [Fact]
    public void Options_off_vss_all_and_old_version_flags()
    {
        var o = Opts();
        o.Unattended = false;
        o.TimezoneUtc = false;
        o.Vss = PlasoVssMode.All;
        var args = PlasoCommandBuilder.BuildLog2Timeline(Exe(), o).Args;
        args.Should().NotContain(new[] { "--unattended", "--timezone", "--parsers", "--workers" });
        args.Should().ContainInConsecutiveOrder("--vss_stores", "all");

        o.Vss = PlasoVssMode.None;
        PlasoCommandBuilder.BuildLog2Timeline(Exe("20190331"), o).Args.Should().Contain("--no_vss").And.NotContain("--vss_stores");
    }

    [Fact]
    public void Image_file_source_processes_all_partitions_and_volumes()
    {
        var img = Path.Combine(_tmp.Path, "disk.E01");
        File.WriteAllText(img, "x");
        var args = PlasoCommandBuilder.BuildLog2Timeline(Exe(), Opts(img)).Args;
        args.Should().ContainInConsecutiveOrder("--partitions", "all").And.ContainInConsecutiveOrder("--volumes", "all");
        args[^1].Should().Be(img);
    }

    [Fact]
    public void Additional_sources_get_their_own_log2timeline_into_the_same_storage_file()
    {
        var o = Opts();
        var second = Path.Combine(_tmp.Path, "extracted", "b1c2d3e4", "Collection-HOST2");
        o.AdditionalSources = new[] { second };
        o.AllSources.Should().Equal(o.Source, second);

        var first = PlasoCommandBuilder.BuildLog2Timeline(Exe(), o, 0).Args;
        var next = PlasoCommandBuilder.BuildLog2Timeline(Exe(), o, 1).Args;
        first.Should().Equal(PlasoCommandBuilder.BuildLog2Timeline(Exe(), o).Args);
        first[^1].Should().Be(o.Source);
        next[^1].Should().Be(second);
        next.Should().ContainInConsecutiveOrder("--storage_file", o.StorageFile);
        next.Should().ContainInConsecutiveOrder("--logfile", Path.Combine(o.CaseDir, "Timeline", "plaso", "20261007T120000Z.log2timeline-2.log.gz"));
        FluentActions.Invoking(() => PlasoCommandBuilder.BuildLog2Timeline(Exe(), o, 2)).Should().Throw<ArgumentOutOfRangeException>();

        var d = PlasoCommandBuilder.BuildLog2Timeline(Docker(), o, 1);
        d.Args.Should().Contain(a => a.Contains("source=" + Path.GetFullPath(second)));
        d.ContainerName.Should().EndWith("-l2t2");
    }

    [Fact]
    public void Source_box_text_is_parsed_into_sources()
    {
        PlasoRunOptions.ParseSources(null).Should().BeEmpty();
        PlasoRunOptions.ParseSources(@"  C:\a  ").Should().Equal(@"C:\a");
        PlasoRunOptions.ParseSources(@"C:\a; C:\b;;C:\A", _ => false).Should().Equal(@"C:\a", @"C:\b");
        // A path that really contains ';' stays one source.
        PlasoRunOptions.ParseSources(@"C:\x;y", p => p == @"C:\x;y").Should().Equal(@"C:\x;y");
        PlasoRunOptions.JoinSources(new[] { @"C:\a", " ", @"C:\b " }).Should().Be(@"C:\a; C:\b");
        PlasoRunOptions.ParseSources(PlasoRunOptions.JoinSources(new[] { @"C:\a", @"C:\b" }), _ => false).Should().Equal(@"C:\a", @"C:\b");
    }

    [Fact]
    public void Psort_dynamic_default_with_macb_and_date_filter()
    {
        var o = Opts();
        o.FromUtc = new DateTime(2020, 1, 1, 0, 0, 0, DateTimeKind.Utc);
        o.ToUtc = new DateTime(2020, 1, 31, 23, 59, 59, DateTimeKind.Utc);
        var inv = PlasoCommandBuilder.BuildPsort(Exe(), o);
        inv.Exe.Should().Be(@"C:\v\Scripts\psort.exe");
        inv.Args.Should().Equal(
            "--status_view", "linear", "--unattended",
            "--logfile", Path.Combine(o.CaseDir, "Timeline", "plaso", "20261007T120000Z.psort.log.gz"),
            "-o", "dynamic", "--fields", "datetime,timestamp_desc,macb,source,source_long,message,parser,display_name,tag",
            "--output_time_zone", "UTC",
            "-w", Path.Combine(o.CaseDir, "Timeline", "SuperTimeline_plaso_20261007T120000Z.csv"),
            Path.Combine(o.CaseDir, "Timeline", "plaso", "20261007T120000Z.plaso"),
            "date >= DATETIME('2020-01-01T00:00:00') AND date <= DATETIME('2020-01-31T23:59:59')");
    }

    [Fact]
    public void Psort_l2tcsv_and_no_filter()
    {
        var o = Opts();
        o.Format = PlasoOutputFormat.L2tCsv;
        var args = PlasoCommandBuilder.BuildPsort(Exe(), o).Args;
        args.Should().ContainInConsecutiveOrder("-o", "l2tcsv").And.NotContain("--fields");
        args[^1].Should().EndWith(".plaso");
        PlasoCommandBuilder.BuildDateFilter(null, null).Should().BeNull();
        PlasoCommandBuilder.BuildDateFilter(new DateTime(2021, 5, 6, 7, 8, 9, DateTimeKind.Utc), null)
            .Should().Be("date >= DATETIME('2021-05-06T07:08:09')");
    }

    [Fact]
    public void Python_module_install_prefixes_the_module()
    {
        var py = new PlasoInstall(PlasoOrigin.Configured, PlasoEngineKind.Python,
            PlasoCommand.Of(@"C:\py\python.exe", "-m", "plaso.scripts.log2timeline"), PlasoCommand.Of(@"C:\py\python.exe", "-m", "plaso.scripts.psort"), "20260720", @"C:\py");
        var inv = PlasoCommandBuilder.BuildPsort(py, Opts());
        inv.Exe.Should().Be(@"C:\py\python.exe");
        inv.Args.Take(4).Should().Equal("-m", "plaso.scripts.psort", "--status_view", "linear");
    }

    [Fact]
    public void Docker_variant_mounts_source_read_only_and_timeline_folder_and_stops_container_on_cancel()
    {
        var o = Opts();
        var l2t = PlasoCommandBuilder.BuildLog2Timeline(Docker(), o);
        l2t.Exe.Should().Be(@"C:\Docker\docker.exe");
        l2t.ContainerName.Should().Be("ezforensics-plaso-20261007t120000z-l2t");
        l2t.Args.Take(12).Should().Equal(
            "run", "--rm", "--name", "ezforensics-plaso-20261007t120000z-l2t", "-e", "PYTHONUNBUFFERED=1",
            "--mount", $"type=bind,source={o.Source},target=/data/source,readonly",
            "--mount", $"type=bind,source={Path.Combine(o.CaseDir, "Timeline")},target=/data/timeline",
            "log2timeline/plaso", "log2timeline");
        l2t.Args.Should().ContainInConsecutiveOrder("--storage_file", "/data/timeline/plaso/20261007T120000Z.plaso");
        l2t.Args.Should().ContainInConsecutiveOrder("--logfile", "/data/timeline/plaso/20261007T120000Z.log2timeline.log.gz");
        l2t.Args[^1].Should().Be("/data/source");
        l2t.CancelCommand.Should().Equal(@"C:\Docker\docker.exe", "stop", "--time", "5", "ezforensics-plaso-20261007t120000z-l2t");

        var ps = PlasoCommandBuilder.BuildPsort(Docker(), o);
        ps.Args[11].Should().Be("psort");
        ps.Args.Should().ContainInConsecutiveOrder("-w", "/data/timeline/SuperTimeline_plaso_20261007T120000Z.csv");
        ps.Args[^1].Should().Be("/data/timeline/plaso/20261007T120000Z.plaso");
        ps.CancelCommand![^1].Should().Be("ezforensics-plaso-20261007t120000z-psort");
    }

    [Fact]
    public void Docker_image_file_mounts_its_folder()
    {
        var img = Path.Combine(_tmp.Path, "img dir", "disk,1.vhdx");
        Directory.CreateDirectory(Path.GetDirectoryName(img)!);
        File.WriteAllText(img, "x");
        var args = PlasoCommandBuilder.BuildLog2Timeline(Docker(), Opts(img)).Args;
        args[7].Should().Be($"type=bind,source={Path.GetDirectoryName(img)},target=/data/source,readonly");
        args[^1].Should().Be("/data/source/disk,1.vhdx");
        PlasoCommandBuilder.MountSpec(@"C:\a,b", "/x", true).Should().Be("type=bind,\"source=C:\\a,b\",target=/x,readonly");
    }
}

public sealed class PlasoStatusParserTests
{
    [Fact]
    public void Parses_linear_log2timeline_status_blocks()
    {
        var p = new PlasoStatusParser(PlasoPhase.Extracting);
        p.Feed("Processing started.").Should().BeNull();
        p.Feed("Processing time: 00:00:36").Should().BeNull();
        var s = p.Feed(@"Main (PID: 36824) status: running, event data produced: 35144, events produced: 41129, file: OS:D:\ev\C\Windows\System32\config\SYSTEM")!;
        s.Status.Should().Be("running");
        s.ProcessingTime.Should().Be("00:00:36");
        s.Events.Should().Be(41129);
        s.EventData.Should().Be(35144);
        s.CurrentFile.Should().Be(@"D:\ev\C\Windows\System32\config\SYSTEM");
        p.Feed(@"Worker_00 (PID: 127932) status: idle, event data produced: 0, file: OS:D:\x").Should().BeNull();
        s.ToString().Should().Contain("41,129 events").And.Contain("00:00:36");

        p.Feed("Main (PID: 36824) status: completed, event data produced: 188321, events produced: 194388, file: ")!.CurrentFile.Should().BeNull();
        p.Feed("Processing completed.")!.Status.Should().Be("completed");
        p.Feed("Number of warnings generated while extracting events: 23.")!.Warnings.Should().Be(23);
        p.Last!.Events.Should().Be(194388);
    }

    [Fact]
    public void Parses_psort_export_status()
    {
        var p = new PlasoStatusParser(PlasoPhase.Exporting);
        p.Feed("Processing time: 00:00:04");
        var s = p.Feed("Main (PID: 121852) status: exporting, events consumed: 37973")!;
        s.Status.Should().Be("exporting");
        s.Events.Should().Be(37973);
        p.Feed("Main (PID: 121852) status: idle, events consumed: 0")!.Events.Should().Be(0);
    }
}

public sealed class PlasoOutputTests : IDisposable
{
    private readonly TempDir _tmp = new();
    public void Dispose() => _tmp.Dispose();

    [Fact]
    public void Dynamic_output_datetime_column_is_guessed_and_parses_for_sort_and_filter()
    {
        var headers = PlasoCommandBuilder.DynamicFields.Split(',');
        TimestampColumnGuesser.Guess(headers).Should().Be("datetime");
        CsvValueParser.TryParseDate("2020-01-02T10:00:00.123456+00:00", out var dt).Should().BeTrue();
        dt.Should().Be(new DateTime(2020, 1, 2, 10, 0, 0, 123, DateTimeKind.Utc).AddTicks(4560));
        CsvValueParser.TryParseDate("1601-01-01T00:00:00.000000+00:00", out _).Should().BeTrue();
    }

    [Fact]
    public void L2tcsv_header_puts_date_and_time_in_separate_columns()
    {
        // Known limitation: l2tcsv splits date (MM/dd/yyyy) and time (HH:mm:ss); the Results guesser picks "time",
        // so chronological sort/date filtering in Results needs the dynamic format (EZ Forensics's default).
        var headers = "date,time,timezone,MACB,source,sourcetype,type,user,host,short,desc,version,filename,inode,notes,format,extra".Split(',');
        TimestampColumnGuesser.Guess(headers).Should().Be("time");
        CsvValueParser.TryParseDate("01/02/2020", out var d).Should().BeTrue();
        d.Month.Should().Be(1);
        headers.Should().Contain("MACB");
    }

    [Fact]
    public async Task Fake_plaso_dynamic_csv_opens_in_the_results_index_and_sorts_by_datetime()
    {
        var csv = Path.Combine(_tmp.Path, "t.csv");
        File.WriteAllText(csv,
            "datetime,timestamp_desc,macb,source,source_long,message,parser,display_name,tag\n" +
            "2022-05-06T12:00:00.000000+00:00,Last Access Time,.A..,LNK,Windows Shortcut,b.lnk,lnk,OS:C:/b.lnk,-\n" +
            "2020-01-02T10:00:00.123456+00:00,Creation Time,...B,FILE,NTFS $MFT,a.txt,mft,OS:C:/$MFT,-\n" +
            "2021-03-04T11:30:00.000000+00:00,Content Modification Time,M...,EVT,WinEVTX,\"[4624] logon, ok\",winevtx,OS:C:/Security.evtx,-\n");
        using var idx = await CsvIndex.OpenAsync(csv, null, CancellationToken.None);
        await idx.IndexingCompleted;
        idx.Headers.Should().Contain("macb");
        var all = Enumerable.Range(0, (int)idx.RowCount).ToArray();
        var sorted = await FilterEngine.SortAsync(idx, all, "datetime", false, CancellationToken.None, null);
        sorted.Select(i => idx.ReadRow(i)[2]).Should().Equal("...B", "M...", ".A..");
        var spec = new FilterSpec { DateColumn = "datetime", DateFrom = new DateTime(2021, 1, 1), DateTo = new DateTime(2021, 12, 31) };
        var hits = await FilterEngine.FilterAsync(idx, spec, null, CancellationToken.None);
        hits.Should().ContainSingle().Which.Should().Be(2);
    }
}

public sealed class PlasoRunnerTests : IDisposable
{
    private readonly TempDir _tmp = new();
    public void Dispose() => _tmp.Dispose();

    private static PlasoInstall Fake() => new(PlasoOrigin.Configured, PlasoEngineKind.Executable,
        PlasoCommand.Of(FakeToolPaths.Exe, "--fake-plaso", "log2timeline"),
        PlasoCommand.Of(FakeToolPaths.Exe, "--fake-plaso", "psort"), "20990101", FakeToolPaths.Dir);

    private PlasoRunOptions Opts(string sourceName = "evidence")
    {
        var src = Path.Combine(_tmp.Path, sourceName);
        Directory.CreateDirectory(src);
        return new PlasoRunOptions { Source = src, CaseDir = Path.Combine(_tmp.Path, "case"), Stamp = "20261007T120000Z" };
    }

    [Fact]
    public void Fake_tool_answers_version_probe()
    {
        var loc = new PlasoLocator(SystemPlasoProbe.Instance, Path.Combine(_tmp.Path, "novenv"));
        loc.ProbeVersion(Fake()).Should().Be("20990101");
    }

    [Fact]
    public async Task Runs_log2timeline_then_psort_as_jobs_with_manifest_and_status()
    {
        var q = new JobQueue(new FakeLocator(null), 2, TimeSpan.FromMinutes(1));
        var o = Opts();
        var statuses = new List<PlasoStatus>();
        var r = await PlasoTimelineRunner.RunAsync(q, Fake(), o, onStatus: s => { lock (statuses) statuses.Add(s); });

        r.Succeeded.Should().BeTrue(r.Error);
        File.Exists(o.StorageFile).Should().BeTrue();
        File.Exists(o.OutputCsv).Should().BeTrue();
        Path.GetFileName(o.OutputCsv).Should().Be("SuperTimeline_plaso_20261007T120000Z.csv");
        r.Events.Should().Be(3);
        r.ExtractedEvents.Should().Be(24);
        r.Warnings.Should().Be(3);
        r.Jobs.Should().HaveCount(2);
        r.Jobs.Select(j => j.Spec.ToolId).Should().Equal(PlasoTimelineRunner.ToolIdLog2Timeline, PlasoTimelineRunner.ToolIdPsort);
        r.Jobs.Should().OnlyContain(j => j.State == JobState.Succeeded && j.ExePath == FakeToolPaths.Exe);
        r.Jobs[0].ResolvedArgs.Should().Contain("--storage_file");
        r.Jobs.Should().OnlyContain(j => j.Spec.Environment != null && j.Spec.Environment["PYTHONUNBUFFERED"] == "1");
        lock (statuses) statuses.Should().Contain(s => s.Phase == PlasoPhase.Extracting && s.Events == 12)
            .And.Contain(s => s.Phase == PlasoPhase.Exporting && s.Events == 3)
            .And.Contain(s => s.Phase == PlasoPhase.Done);

        await q.WhenAllAsync();
        var manifest = q.GetManifestPath(o.CaseDir)!;
        var text = await File.ReadAllTextAsync(manifest);
        text.Should().Contain("plaso-log2timeline").And.Contain("plaso-psort").And.Contain("SuperTimeline_plaso_20261007T120000Z.csv")
            .And.Contain("20990101");
        File.ReadLines(o.OutputCsv).First().Should().Be("datetime,timestamp_desc,macb,source,source_long,message,parser,display_name,tag");
    }

    [Fact]
    public async Task Date_filter_and_l2tcsv_reach_psort()
    {
        var q = new JobQueue(new FakeLocator(null), 2, TimeSpan.FromMinutes(1));
        var o = Opts();
        o.Format = PlasoOutputFormat.L2tCsv;
        o.FromUtc = new DateTime(2020, 1, 1);
        var r = await PlasoTimelineRunner.RunAsync(q, Fake(), o);
        r.Succeeded.Should().BeTrue(r.Error);
        r.Events.Should().Be(2);
        File.ReadLines(o.OutputCsv).First().Should().StartWith("date,time,timezone,MACB,");
    }

    [Fact]
    public async Task A_timeline_that_could_not_start_is_listed_as_a_failed_job_with_the_reason()
    {
        var q = new JobQueue(new FakeLocator(null), 2, TimeSpan.FromMinutes(1));
        var caseDir = Path.Combine(_tmp.Path, "case");
        var job = PlasoTimelineRunner.EnqueueNotStarted(q, caseDir, Path.Combine(caseDir, "H", "Timeline"), "H", "Not started: plaso is not installed.");
        job.State.Should().Be(JobState.Failed);
        job.IsFinished.Should().BeTrue();
        job.StatusReason.Should().Contain("plaso is not installed");
        q.Jobs.Should().Contain(job);
        await job.Recorded;
        File.ReadAllText(job.LogPath!).Should().Contain("plaso is not installed");
    }

    [Fact]
    public async Task Missing_source_fails_at_log2timeline_and_skips_psort()
    {
        var q = new JobQueue(new FakeLocator(null), 2, TimeSpan.FromMinutes(1));
        var o = Opts();
        o.Source = Path.Combine(_tmp.Path, "does-not-exist");
        var r = await PlasoTimelineRunner.RunAsync(q, Fake(), o);
        r.Succeeded.Should().BeFalse();
        r.Error.Should().StartWith("log2timeline:");
        r.Jobs.Should().ContainSingle().Which.State.Should().Be(JobState.Failed);
        File.Exists(o.OutputCsv).Should().BeFalse();
    }

    [Fact]
    public async Task Several_sources_run_log2timeline_sequentially_into_one_storage_file_then_one_psort()
    {
        var q = new JobQueue(new FakeLocator(null), 2, TimeSpan.FromMinutes(1));
        var o = Opts("root-a");
        var second = Opts("root-b").Source;
        o.AdditionalSources = new[] { second };
        var r = await PlasoTimelineRunner.RunAsync(q, Fake(), o);

        r.Succeeded.Should().BeTrue(r.Error);
        r.Jobs.Select(j => j.Spec.ToolId).Should().Equal(PlasoTimelineRunner.ToolIdLog2Timeline, PlasoTimelineRunner.ToolIdLog2Timeline, PlasoTimelineRunner.ToolIdPsort);
        r.Jobs[0].ResolvedArgs![^1].Should().Be(o.Source);
        r.Jobs[1].ResolvedArgs![^1].Should().Be(second);
        r.Jobs[0].Spec.Label.Should().EndWith("(1/2)");
        r.Jobs[1].Spec.Label.Should().EndWith("(2/2)");
        // The second run started only after the first ended (sequential appends to the same storage file).
        r.Jobs[1].StartUtc.Should().BeOnOrAfter(r.Jobs[0].EndUtc!.Value);
        new FileInfo(o.StorageFile).Length.Should().Be(12, "the second log2timeline appended a session");
        r.ExtractedEvents.Should().Be(48);
        r.Warnings.Should().Be(6);
        r.Jobs[1].ResolvedArgs.Should().ContainInConsecutiveOrder("--logfile", o.Log2TimelineLogFor(1));
    }

    [Fact]
    public async Task A_missing_additional_source_fails_naming_it_and_skips_psort()
    {
        var q = new JobQueue(new FakeLocator(null), 2, TimeSpan.FromMinutes(1));
        var o = Opts();
        o.AdditionalSources = new[] { Path.Combine(_tmp.Path, "gone") };
        var r = await PlasoTimelineRunner.RunAsync(q, Fake(), o);
        r.Succeeded.Should().BeFalse();
        r.Error.Should().StartWith("log2timeline (source 2 of 2):");
        r.Jobs.Should().HaveCount(2);
        r.Jobs.Should().NotContain(j => j.Spec.ToolId == PlasoTimelineRunner.ToolIdPsort);
    }

    [Fact]
    public async Task Cancel_kills_the_process_tree_and_runs_the_cleanup_command()
    {
        var q = new JobQueue(new FakeLocator(null), 2, TimeSpan.FromMinutes(1));
        var o = Opts("hang-evidence");
        using var cts = new CancellationTokenSource();
        Job? job = null;
        var marker = Path.Combine(_tmp.Path, "cleanup-ran.csv");
        // Use the fake's --csv mode as an observable "docker stop" stand-in.
        var install = Fake();
        var run = Task.Run(async () =>
        {
            var spec = PlasoTimelineRunner.Log2TimelineSpec(install, o) with
            {
                CancelCommand = new[] { FakeToolPaths.Exe, "--csv", Path.GetDirectoryName(marker)! },
            };
            job = q.Enqueue(spec, o.CaseDir);
            using (cts.Token.Register(job.Cancel)) await PlasoTimelineRunner.WaitAsync(job);
        });
        var sw = System.Diagnostics.Stopwatch.StartNew();
        while ((job == null || job.State != JobState.Running) && sw.Elapsed < TimeSpan.FromSeconds(20)) await Task.Delay(50);
        await Task.Delay(500);
        cts.Cancel();
        (await Task.WhenAny(run, Task.Delay(TimeSpan.FromSeconds(30)))).Should().Be(run);
        job!.State.Should().Be(JobState.Cancelled);
        File.Exists(Path.Combine(_tmp.Path, "out.csv")).Should().BeTrue("the cancel command ran");
    }

    [Fact]
    public async Task Runner_cancellation_reports_cancelled()
    {
        var q = new JobQueue(new FakeLocator(null), 2, TimeSpan.FromMinutes(1));
        var o = Opts("hang-evidence");
        using var cts = new CancellationTokenSource();
        var statuses = 0;
        var t = PlasoTimelineRunner.RunAsync(q, Fake(), o, onStatus: s => { if (Interlocked.Increment(ref statuses) == 1) cts.CancelAfter(300); }, ct: cts.Token);
        (await Task.WhenAny(t, Task.Delay(TimeSpan.FromSeconds(30)))).Should().Be(t);
        var r = await t;
        r.Cancelled.Should().BeTrue();
        r.Jobs.Should().ContainSingle().Which.State.Should().Be(JobState.Cancelled);
        File.Exists(o.StorageFile).Should().BeFalse("the partial storage file is removed");
        Directory.GetDirectories(o.PlasoDir, "tmp*").Should().BeEmpty("plaso's temp task folder is removed");
        Directory.GetFiles(Path.Combine(o.CaseDir, "logs")).Should().NotBeEmpty("the job log is kept");
    }
}
