using System.IO.Compression;
using System.Text;
using EzForensics.Core.Execution;
using EzForensics.Core.Plaso;
using FluentAssertions;

namespace EzForensics.Core.Tests;

public sealed class PlasoReleaseCheckTests
{
    // Trimmed copy of https://pypi.org/pypi/plaso/json (2026-10-08): info + urls of the latest version, releases elided.
    private const string PyPiFixture = """
    {
      "info": {
        "author": "Log2Timeline maintainers",
        "name": "plaso",
        "package_url": "https://pypi.org/project/plaso/",
        "project_urls": { "Documentation": "https://plaso.readthedocs.io/en/latest", "Homepage": "https://github.com/log2timeline/plaso" },
        "requires_python": ">=3.10",
        "version": "20260720",
        "yanked": false
      },
      "last_serial": 1,
      "releases": { "20260512": [], "20260720": [] },
      "urls": [
        { "filename": "plaso-20260720-py3-none-any.whl", "packagetype": "bdist_wheel", "python_version": "py3", "size": 2954028,
          "upload_time": "2026-07-21T08:50:25", "upload_time_iso_8601": "2026-07-21T08:50:25.943015Z", "yanked": false },
        { "filename": "plaso-20260720.tar.gz", "packagetype": "sdist", "python_version": "source", "size": 2919228,
          "upload_time": "2026-07-21T08:50:27", "upload_time_iso_8601": "2026-07-21T08:50:27.913680Z", "yanked": false }
      ],
      "vulnerabilities": []
    }
    """;

    [Fact]
    public void Parses_latest_version_wheel_size_and_upload_time_from_pypi_json()
    {
        var r = PlasoReleaseCheck.ParsePyPiJson(PyPiFixture)!;
        r.Version.Should().Be("20260720");
        r.WheelSize.Should().Be(2954028);
        r.RequiresPython.Should().Be(">=3.10");
        r.UploadedUtc.Should().Be(new DateTime(2026, 7, 21, 8, 50, 25, 943, DateTimeKind.Utc).AddTicks(150));
        r.UploadedUtc!.Value.Kind.Should().Be(DateTimeKind.Utc);
    }

    [Theory]
    [InlineData("")]
    [InlineData("not json")]
    [InlineData("{}")]
    [InlineData("""{"info":{"version":""}}""")]
    [InlineData("""{"info":[]}""")]
    public void Unusable_pypi_json_returns_null(string json) => PlasoReleaseCheck.ParsePyPiJson(json).Should().BeNull();

    [Fact]
    public void Missing_urls_still_gives_the_version() =>
        PlasoReleaseCheck.ParsePyPiJson("""{"info":{"version":"20260928"}}""")!.Version.Should().Be("20260928");

    [Theory]
    [InlineData("20260928", "20260720", 1)]
    [InlineData("20260720", "20260720", 0)]
    [InlineData("20260512", "20260720", -1)]
    [InlineData("plaso-20260720", "20260720", 0)]
    [InlineData("20260720.post1", "20260720", 1)]
    [InlineData("20260720", null, 1)]
    [InlineData("unknown", "20231224", -1)]
    [InlineData(null, null, 0)]
    [InlineData("20260720", "1.5.1", 1)]
    [InlineData("1.10.0", "1.9.2", 1)]
    public void Compares_plaso_versions(string? a, string? b, int expected) =>
        Math.Sign(PlasoReleaseCheck.Compare(a, b)).Should().Be(expected);

    [Theory]
    [InlineData("20260928", "20260720", true)]
    [InlineData("20260720", "20260720", false)]
    [InlineData("20260720", "20260928", false)]
    [InlineData(null, "20260720", false)]
    [InlineData("20260720", "unknown", false)]
    public void Newer_only_when_both_versions_are_known(string? latest, string? installed, bool newer) =>
        PlasoReleaseCheck.IsNewer(latest, installed).Should().Be(newer);

    [Theory]
    [InlineData(true, null, false, null, null, PlasoToolStatus.Detecting)]
    [InlineData(false, "20260720", true, "20260720", true, PlasoToolStatus.Installed)]
    [InlineData(false, "20260720", true, null, true, PlasoToolStatus.Installed)]
    [InlineData(true, "20260512", true, "20260720", null, PlasoToolStatus.UpdateAvailable)]
    [InlineData(false, null, false, "20260720", true, PlasoToolStatus.NotInstalled)]
    [InlineData(false, null, false, "20260720", null, PlasoToolStatus.NotInstalled)]
    [InlineData(false, null, false, null, false, PlasoToolStatus.PythonMissing)]
    public void Evaluates_tool_status(bool detecting, string? installed, bool isInstalled, string? latest, bool? python, PlasoToolStatus expected) =>
        PlasoToolState.Evaluate(detecting, installed, isInstalled, latest, python).Should().Be(expected);

    [Fact]
    public async Task Latest_lookup_times_out_with_a_clear_error()
    {
        using var http = new HttpClient(new SlowHandler());
        var act = () => PlasoReleaseCheck.GetLatestAsync(http, TimeSpan.FromMilliseconds(100));
        await act.Should().ThrowAsync<TimeoutException>();
    }

    [Fact]
    public async Task Latest_lookup_reads_the_pypi_json_api()
    {
        var h = new StubHandler(Encoding.UTF8.GetBytes(PyPiFixture));
        using var http = new HttpClient(h);
        (await PlasoReleaseCheck.GetLatestAsync(http)).Version.Should().Be("20260720");
        h.Requested.Should().Equal(PlasoLinks.PyPIJson);
    }

    [Fact]
    public void Links_are_official_https_urls()
    {
        foreach (var url in new[] { PlasoLinks.Docs, PlasoLinks.GitHub, PlasoLinks.Releases, PlasoLinks.PyPI, PlasoLinks.PyPIJson, PlasoLinks.DockerHub, PlasoLinks.PythonDownloads })
            url.Should().StartWith("https://");
        PlasoLinks.GitHub.Should().Be("https://github.com/log2timeline/plaso");
        PlasoLinks.DockerHub.Should().EndWith("/log2timeline/plaso");
    }

    private sealed class SlowHandler : HttpMessageHandler
    {
        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken ct)
        {
            await Task.Delay(TimeSpan.FromSeconds(30), ct);
            return new HttpResponseMessage(System.Net.HttpStatusCode.OK);
        }
    }
}

internal sealed class StubHandler : HttpMessageHandler
{
    private readonly byte[] _body;
    public StubHandler(byte[] body) => _body = body;
    public List<string> Requested { get; } = new();

    protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken ct)
    {
        Requested.Add(request.RequestUri!.ToString());
        var resp = new HttpResponseMessage(System.Net.HttpStatusCode.OK) { Content = new ByteArrayContent(_body) };
        resp.Content.Headers.ContentLength = _body.Length;
        return Task.FromResult(resp);
    }
}

public sealed class PrivatePythonTests : IDisposable
{
    private readonly TempDir _tmp = new();
    public void Dispose() => _tmp.Dispose();

    [Fact]
    public void Default_is_the_pinned_official_nuget_python_312()
    {
        var s = PrivatePython.Default;
        s.Version.Should().Be("3.12.10");
        s.Url.Should().Be("https://api.nuget.org/v3-flatcontainer/python/3.12.10/python.3.12.10.nupkg");
        PrivatePython.IsOfficialUrl(s.Url).Should().BeTrue();
        Convert.FromBase64String(s.Sha512Base64).Should().HaveCount(64);
        s.Size.Should().Be(14_515_433);
        Version.Parse(s.Version).Should().BeGreaterThanOrEqualTo(PlasoLocator.MinPython).And.BeLessThanOrEqualTo(PlasoLocator.MaxPython);
    }

    [Theory]
    [InlineData("https://api.nuget.org/v3-flatcontainer/python/3.12.10/python.3.12.10.nupkg", true)]
    [InlineData("https://www.python.org/ftp/python/3.12.10/python-3.12.10-amd64.exe", true)]
    [InlineData("http://api.nuget.org/v3-flatcontainer/python/3.12.10/python.3.12.10.nupkg", false)]
    [InlineData("https://api.nuget.org.evil.example/python.nupkg", false)]
    [InlineData("https://example.com/python.nupkg", false)]
    [InlineData("not a url", false)]
    public void Only_official_https_urls_are_allowed(string url, bool ok) => PrivatePython.IsOfficialUrl(url).Should().Be(ok);

    [Fact]
    public void Package_url_is_lower_case() =>
        PrivatePython.PackageUrl("3.13.0-RC1").Should().Be("https://api.nuget.org/v3-flatcontainer/python/3.13.0-rc1/python.3.13.0-rc1.nupkg");

    [Fact]
    public void Sha512_verification_accepts_the_right_hash_and_rejects_others()
    {
        var f = Path.Combine(_tmp.Path, "x.bin");
        File.WriteAllText(f, "abc");
        // SHA-512("abc"), base64
        const string abc = "3a81oZNherrMQXNJriBBMRLm+k6JqX6iCp7u5ktV05ohkpkqJ0/BqDa6PCOj/uu9RU1EI2Q86A4qmslPpUyknw==";
        PrivatePython.VerifySha512(f, abc).Should().BeTrue();
        PrivatePython.VerifySha512(f, PrivatePython.Default.Sha512Base64).Should().BeFalse();
        PrivatePython.VerifySha512(f, "not base64!").Should().BeFalse();
    }

    [Fact]
    public void Extracts_only_the_tools_folder_and_rejects_path_escape()
    {
        var pkg = MakePackage(("tools/python.exe", "exe"), ("tools/Lib/venv/__init__.py", "x"), ("python.nuspec", "n"), ("build/native/python.props", "p"));
        var dest = Path.Combine(_tmp.Path, "py");
        PrivatePython.ExtractTools(pkg, dest).Should().Be(2);
        File.Exists(Path.Combine(dest, "python.exe")).Should().BeTrue();
        File.Exists(Path.Combine(dest, "Lib", "venv", "__init__.py")).Should().BeTrue();
        File.Exists(Path.Combine(dest, "python.nuspec")).Should().BeFalse();

        var evil = MakePackage(("tools/python.exe", "exe"), ("tools/../../evil.txt", "x"));
        var act = () => PrivatePython.ExtractTools(evil, Path.Combine(_tmp.Path, "py2"));
        act.Should().Throw<InvalidDataException>();
        File.Exists(Path.Combine(_tmp.Path, "evil.txt")).Should().BeFalse();

        var noPython = MakePackage(("tools/readme.txt", "x"));
        ((Action)(() => PrivatePython.ExtractTools(noPython, Path.Combine(_tmp.Path, "py3")))).Should().Throw<InvalidDataException>();
    }

    [Fact]
    public async Task Provision_downloads_verifies_and_extracts()
    {
        var bytes = File.ReadAllBytes(MakePackage(("tools/python.exe", "exe"), ("tools/Lib/os.py", "x")));
        var spec = SpecFor(bytes);
        var target = Path.Combine(_tmp.Path, "python");
        var log = new List<string>();
        double last = 0;
        using var http = new HttpClient(new StubHandler(bytes));
        var exe = await PrivatePython.ProvisionAsync(http, spec, target, log.Add, new SyncProgress(p => last = p));
        exe.Should().Be(Path.Combine(target, "python.exe"));
        File.Exists(exe).Should().BeTrue();
        last.Should().Be(1);
        log.Should().Contain(l => l.Contains("SHA-512 OK"));
        Directory.Exists(target + ".download").Should().BeFalse();
        Directory.Exists(target + ".tmp").Should().BeFalse();
    }

    [Fact]
    public async Task Provision_with_a_wrong_hash_discards_everything_and_keeps_the_old_python()
    {
        var bytes = File.ReadAllBytes(MakePackage(("tools/python.exe", "exe")));
        var spec = SpecFor(bytes) with { Sha512Base64 = PrivatePython.Default.Sha512Base64 };
        var target = Path.Combine(_tmp.Path, "python");
        Directory.CreateDirectory(target);
        File.WriteAllText(Path.Combine(target, "keep.txt"), "old");
        using var http = new HttpClient(new StubHandler(bytes));
        var act = () => PrivatePython.ProvisionAsync(http, spec, target);
        (await act.Should().ThrowAsync<InvalidDataException>()).WithMessage("*SHA-512*");
        File.Exists(Path.Combine(target, "keep.txt")).Should().BeTrue();
        Directory.Exists(target + ".download").Should().BeFalse();
        Directory.Exists(target + ".tmp").Should().BeFalse();
    }

    [Fact]
    public async Task Provision_rejects_size_mismatch_and_unofficial_urls()
    {
        var bytes = File.ReadAllBytes(MakePackage(("tools/python.exe", "exe")));
        using var http = new HttpClient(new StubHandler(bytes));
        var target = Path.Combine(_tmp.Path, "python");
        await ((Func<Task>)(() => PrivatePython.ProvisionAsync(http, SpecFor(bytes) with { Size = bytes.Length + 1 }, target)))
            .Should().ThrowAsync<InvalidDataException>().WithMessage("*size*");
        await ((Func<Task>)(() => PrivatePython.ProvisionAsync(http, SpecFor(bytes) with { Url = "https://example.com/python.nupkg" }, target)))
            .Should().ThrowAsync<InvalidOperationException>();
        Directory.Exists(target).Should().BeFalse();
    }

    [Fact]
    public async Task Provision_cancellation_cleans_up()
    {
        var bytes = File.ReadAllBytes(MakePackage(("tools/python.exe", "exe")));
        using var http = new HttpClient(new StubHandler(bytes));
        using var cts = new CancellationTokenSource();
        cts.Cancel();
        var target = Path.Combine(_tmp.Path, "python");
        await ((Func<Task>)(() => PrivatePython.ProvisionAsync(http, SpecFor(bytes), target, ct: cts.Token))).Should().ThrowAsync<OperationCanceledException>();
        Directory.Exists(target).Should().BeFalse();
        Directory.Exists(target + ".download").Should().BeFalse();
    }

    private static PrivatePythonSpec SpecFor(byte[] bytes)
    {
        using var ms = new MemoryStream(bytes);
        return new PrivatePythonSpec("3.12.10", PrivatePython.PackageUrl("3.12.10"), PrivatePython.ComputeSha512Base64(ms), bytes.Length);
    }

    private string MakePackage(params (string Name, string Content)[] entries)
    {
        var p = Path.Combine(_tmp.Path, Guid.NewGuid().ToString("N") + ".nupkg");
        using (var zip = ZipFile.Open(p, ZipArchiveMode.Create))
            foreach (var (name, content) in entries)
            {
                using var w = new StreamWriter(zip.CreateEntry(name).Open());
                w.Write(content);
            }
        return p;
    }

    private sealed class SyncProgress : IProgress<double>
    {
        private readonly Action<double> _a;
        public SyncProgress(Action<double> a) => _a = a;
        public void Report(double value) => _a(value);
    }
}

public sealed class PlasoSetupTests : IDisposable
{
    private readonly TempDir _tmp = new();
    public void Dispose() => _tmp.Dispose();

    [Fact]
    public void Private_python_is_found_first_and_python_314_is_not_supported()
    {
        const string venv = @"C:\Users\u\AppData\Local\EzHub\plaso\venv";
        var p = new FakePlasoProbe();
        p.Files.Add(@"C:\Users\u\AppData\Local\EzHub\plaso\python\python.exe");
        p.Outputs[@"C:\Users\u\AppData\Local\EzHub\plaso\python\python.exe --version"] = "Python 3.12.10";
        p.OnPath["python.exe"] = @"C:\Python314\python.exe";
        p.Outputs[@"C:\Python314\python.exe --version"] = "Python 3.14.0";
        var loc = new PlasoLocator(p, venv);
        loc.PrivatePythonExe.Should().Be(@"C:\Users\u\AppData\Local\EzHub\plaso\python\python.exe");
        var all = loc.FindPythons();
        all.Select(x => x.Exe).Should().Equal(@"C:\Users\u\AppData\Local\EzHub\plaso\python\python.exe", @"C:\Python314\python.exe");
        all[0].IsPrivate.Should().BeTrue();
        all[1].IsSupported.Should().BeFalse();
        loc.FindSupportedPython()!.IsPrivate.Should().BeTrue();

        var onlyNew = new FakePlasoProbe();
        onlyNew.OnPath["python.exe"] = @"C:\Python314\python.exe";
        onlyNew.Outputs[@"C:\Python314\python.exe --version"] = "Python 3.14.0";
        new PlasoLocator(onlyNew, venv).FindSupportedPython().Should().BeNull();
    }

    [Fact]
    public void Update_and_uninstall_targets_are_the_managed_folders_only()
    {
        const string venv = @"C:\Users\u\AppData\Local\EzHub\plaso\venv";
        var loc = new PlasoLocator(new FakePlasoProbe(), venv);
        var (py, args) = loc.UpdateCommand();
        py.Should().Be(venv + @"\Scripts\python.exe");
        args.Should().Equal("-m", "pip", "install", "--upgrade", "--disable-pip-version-check", "plaso==" + PlasoSetup.PinnedVersion);
        loc.UpdateCommand("20260928").PipUpgrade[^1].Should().Be("plaso==20260928");
        loc.UpdateCommand("1 --index-url http://x").PipUpgrade[^1].Should().Be("plaso==" + PlasoSetup.PinnedVersion);
        loc.UninstallTargets().Should().Equal(venv, @"C:\Users\u\AppData\Local\EzHub\plaso\python");
        PlasoSetup.Estimate(true).Should().Be((49, 142));
        PlasoSetup.Estimate(false).DownloadMB.Should().Be(PlasoSetup.PackagesDownloadMB);

        var managed = new PlasoInstall(PlasoOrigin.Configured, PlasoEngineKind.Executable, PlasoCommand.Of(venv + @"\Scripts\log2timeline.exe"),
            PlasoCommand.Of(venv + @"\Scripts\psort.exe"), "20260720", venv + @"\Scripts");
        loc.IsManaged(managed).Should().BeTrue();
        loc.IsManaged(managed with { Location = @"D:\plaso", Log2Timeline = PlasoCommand.Of(@"D:\plaso\log2timeline.exe") }).Should().BeFalse();
    }

    [Fact]
    public void Pinfo_command_follows_the_install_kind()
    {
        var exe = new PlasoInstall(PlasoOrigin.ManagedVenv, PlasoEngineKind.Executable, PlasoCommand.Of(@"C:\v\Scripts\log2timeline.exe"),
            PlasoCommand.Of(@"C:\v\Scripts\psort.exe"), "1", @"C:\v\Scripts");
        PlasoLocator.PinfoCommand(exe)!.Exe.Should().Be(@"C:\v\Scripts\pinfo.exe");
        var mod = new PlasoInstall(PlasoOrigin.Configured, PlasoEngineKind.Python, PlasoCommand.Of(@"C:\py\python.exe", "-m", "plaso.scripts.log2timeline"),
            PlasoCommand.Of(@"C:\py\python.exe", "-m", "plaso.scripts.psort"), "1", @"C:\py");
        PlasoLocator.PinfoCommand(mod)!.PrefixArgs.Should().Equal("-m", "plaso.scripts.pinfo");
        var py = new PlasoInstall(PlasoOrigin.Path, PlasoEngineKind.Python, PlasoCommand.Of(@"C:\py\python.exe", @"C:\src\log2timeline.py"),
            PlasoCommand.Of(@"C:\py\python.exe", @"C:\src\psort.py"), "1", @"C:\src");
        PlasoLocator.PinfoCommand(py)!.PrefixArgs.Should().Equal(@"C:\src\pinfo.py");
        PlasoLocator.PinfoCommand(exe with { Kind = PlasoEngineKind.Docker }).Should().BeNull();
    }

    [Fact]
    public void Psort_and_pinfo_versions_come_from_their_version_output()
    {
        var p = new FakePlasoProbe();
        p.Files.Add(@"C:\v\Scripts\psort.exe");
        p.Outputs[@"C:\v\Scripts\psort.exe --version"] = "plaso - psort version 20260720";
        var loc = new PlasoLocator(p, @"C:\v");
        loc.ProbeVersion(PlasoCommand.Of(@"C:\v\Scripts\psort.exe")).Should().Be("20260720");
        loc.ProbeVersion(PlasoCommand.Of(@"C:\v\Scripts\pinfo.exe")).Should().BeNull(); // missing file: no process started
    }

    [Fact]
    public async Task Update_step_runs_as_a_job_with_the_pip_argv()
    {
        // The fake tool stands in for the venv's python.exe: it ignores the pip argv and exits 0.
        var loc = new PlasoLocator(SystemPlasoProbe.Instance, Path.Combine(_tmp.Path, "venv"));
        var step = PlasoSetup.PipStep(loc, update: true) with { Exe = FakeToolPaths.Exe };
        var q = new JobQueue(new FakeLocator(null), 2, TimeSpan.FromMinutes(1));
        Job? seen = null;
        var job = await PlasoSetup.RunStepAsync(q, step, Path.Combine(_tmp.Path, "maint"), j => seen = j);
        seen.Should().BeSameAs(job);
        job.State.Should().Be(JobState.Succeeded);
        job.Spec.ToolId.Should().Be(PlasoSetup.ToolIdPip);
        job.Spec.Category.Should().Be("Maintenance");
        job.ExePath.Should().Be(FakeToolPaths.Exe);
        job.Spec.Args.Should().ContainInOrder("install", "--upgrade", "plaso==" + PlasoSetup.PinnedVersion);
        job.Spec.Label.Should().Contain("update");
    }

    [Fact]
    public async Task Failed_and_cancelled_steps_are_reported()
    {
        var q = new JobQueue(new FakeLocator(null), 2, TimeSpan.FromMinutes(1));
        var failing = new PlasoSetupStep(PlasoSetup.ToolIdPip, FakeToolPaths.Exe, new[] { "--stderr", "--lines", "2", "--exit", "1" }, "pip", TimeSpan.FromMinutes(1));
        (await PlasoSetup.RunStepAsync(q, failing, Path.Combine(_tmp.Path, "m1"))).State.Should().Be(JobState.Failed);

        using var cts = new CancellationTokenSource(TimeSpan.FromMilliseconds(800));
        var hanging = failing with { Args = new[] { "--hang" } };
        (await PlasoSetup.RunStepAsync(q, hanging, Path.Combine(_tmp.Path, "m2"), ct: cts.Token)).State.Should().Be(JobState.Cancelled);
    }

    /// <summary>
    /// Real download (≈14 MB from nuget.org) into a temp folder, then the venv step as a job. Runs only with
    /// EZFORENSICS_ONLINE_TESTS=1 so the normal test run never touches the network.
    /// </summary>
    [Fact]
    public async Task Online_private_python_provisions_and_creates_a_venv()
    {
        if (Environment.GetEnvironmentVariable("EZFORENSICS_ONLINE_TESTS") != "1") return;
        var root = Path.Combine(_tmp.Path, "plaso");
        var loc = new PlasoLocator(SystemPlasoProbe.Instance, Path.Combine(root, "venv"));
        using var http = new HttpClient();
        var log = new List<string>();
        var exe = await PrivatePython.ProvisionAsync(http, PrivatePython.Default, loc.PrivatePythonDir, log.Add);
        exe.Should().Be(loc.PrivatePythonExe);
        var py = loc.FindSupportedPython()!;
        py.IsPrivate.Should().BeTrue();
        py.Version.ToString(3).Should().Be(PrivatePython.Default.Version);

        var q = new JobQueue(new FakeLocator(null), 2, TimeSpan.FromMinutes(5));
        var job = await PlasoSetup.RunStepAsync(q, PlasoSetup.CreateVenvStep(loc, py), Path.Combine(_tmp.Path, "maint"));
        job.State.Should().Be(JobState.Succeeded, job.StatusReason);
        File.Exists(loc.ManagedPython).Should().BeTrue();
        File.Exists(Path.Combine(loc.ManagedVenv, "Scripts", "pip.exe")).Should().BeTrue();

        PlasoSetup.Uninstall(loc).Should().HaveCount(2);
        Directory.Exists(root + "\\python").Should().BeFalse();
    }

    [Fact]
    public void Uninstall_removes_only_the_venv_and_private_python()
    {
        var root = Path.Combine(_tmp.Path, "plaso");
        foreach (var d in new[] { "venv\\Scripts", "python\\Lib", "python.download", "other" })
            Directory.CreateDirectory(Path.Combine(root, d));
        File.WriteAllText(Path.Combine(root, "venv", "Scripts", "log2timeline.exe"), "x");
        File.WriteAllText(Path.Combine(root, "other", "keep.txt"), "x");
        var loc = new PlasoLocator(SystemPlasoProbe.Instance, Path.Combine(root, "venv"));
        var removed = PlasoSetup.Uninstall(loc);
        removed.Should().HaveCount(2);
        Directory.Exists(Path.Combine(root, "venv")).Should().BeFalse();
        Directory.Exists(Path.Combine(root, "python")).Should().BeFalse();
        Directory.Exists(Path.Combine(root, "python.download")).Should().BeFalse();
        File.Exists(Path.Combine(root, "other", "keep.txt")).Should().BeTrue();
        PlasoSetup.Uninstall(loc).Should().BeEmpty();

        var odd = new PlasoLocator(SystemPlasoProbe.Instance, Path.Combine(root, "other"));
        ((Action)(() => PlasoSetup.Uninstall(odd))).Should().Throw<InvalidOperationException>();
        File.Exists(Path.Combine(root, "other", "keep.txt")).Should().BeTrue();
    }
}
