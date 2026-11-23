using EzForensics.Core.Execution;
using EzForensics.Core.Runtimes;
using EzForensics.Core.Zeek;

namespace EzForensics.Core.Spectre;

/// <summary>Steps to run (in order) and, when something stops them from being built, the reason.</summary>
public sealed record SpectrePlan(IReadOnlyList<RuntimeStep> Steps, string? Problem = null)
{
    public bool Ok => Problem == null;
}

/// <summary>
/// Install / update of the Spectre engine's tools in the private EzForensics distro, as Maintenance jobs (logged, in the Jobs list on Ingest).
/// Everything runs as root inside the distro; nothing here touches Windows or the user's own distros. Never started automatically.
/// </summary>
public static class SpectreEngineSetup
{
    public const string ToolIdApt = "spectre-apt", ToolIdVenv = "spectre-venv", ToolIdPlaso = "spectre-plaso",
        ToolIdMemProcFs = "spectre-memprocfs", ToolIdZeek = "spectre-zeek";
    public const string SourceEnvVar = "EZFORENSICS_SPECTRE_SRC";

    private const string Head = "set -e\nexport DEBIAN_FRONTEND=noninteractive\n";
    private static string Q(string s) => "'" + s.Replace("'", "'\\''") + "'";

    // ------------------------------------------------------------------ scripts (pure)

    /// <summary>One apt-get update + install for these tools (their post-install lines after it). Installing an installed package updates it.</summary>
    public static string AptScript(IEnumerable<SpectreEngineTool> tools)
    {
        var list = tools.ToList();
        var pkgs = list.SelectMany(t => t.Packages).Distinct().ToList();
        var s = Head + "apt-get update\n";
        // tshark asks whether non-root users may capture; answer no (we only read files) so nothing prompts
        if (pkgs.Contains("tshark")) s += "echo 'wireshark-common wireshark-common/install-setuid boolean false' | debconf-set-selections\n";
        s += "apt-get install -y --no-install-recommends " + string.Join(" ", pkgs) + "\n";
        foreach (var t in list) if (t.PostInstall != null) s += t.PostInstall + "\n";
        return EzDistro.Lf(s);
    }

    /// <summary>
    /// Spectre's python venv: python + venv + libmagic (and pango/harfbuzz/a font for WeasyPrint's PDF report) from apt, <c>pip install</c>
    /// from <paramref name="sourceWsl"/> (the Spectre checkout or wheel as seen from the distro, e.g. /mnt/d/...) with the report extra
    /// (PDF/DOCX reports), optionally plus plaso. The venv's vol / plaso entry points are linked into /usr/local/bin so Spectre's PATH
    /// lookups find them.
    /// </summary>
    public static string VenvScript(string sourceWsl, bool withPlaso)
    {
        var target = withPlaso ? $"{sourceWsl}[report,plaso]" : $"{sourceWsl}[report]";
        var v = SpectreEngineTools.VenvDir;
        return EzDistro.Lf(Head +
            "apt-get update\n" +
            $"apt-get install -y --no-install-recommends {string.Join(' ', SpectreEngineTools.Find("spectre")!.Packages)}\n" +
            $"[ -x {v}/bin/python ] || python3 -m venv {v}\n" +
            $"{v}/bin/pip install --upgrade pip\n" +
            $"{v}/bin/pip install --upgrade {Q(target)}\n" +
            $"for b in vol log2timeline.py psort.py; do [ -x {v}/bin/$b ] && ln -sf {v}/bin/$b /usr/local/bin/$b || true; done\n" +
            $"{v}/bin/pip show {SpectreEngineTools.PipPackage}\n");
    }

    /// <summary>
    /// Latest linux x64 release of MemProcFS from GitHub into /opt/memprocfs (replaced as a whole), plus its runtime libraries. The
    /// release page is used, not api.github.com: the API answers 403 once its unauthenticated rate limit is used up.
    /// </summary>
    public static string MemProcFsScript()
    {
        var d = SpectreEngineTools.MemProcFsDir;
        var repo = "https://github.com/ufrisk/MemProcFS";
        return EzDistro.Lf(Head +
            "apt-get update\n" +
            "apt-get install -y --no-install-recommends ca-certificates curl libusb-1.0-0 libfuse2t64 liblz4-1\n" +
            $"TAG=$(curl -fsSLI -o /dev/null -w '%{{url_effective}}' {repo}/releases/latest | sed 's#.*/tag/##')\n" +
            $"P=$(curl -fsSL {repo}/releases/expanded_assets/$TAG | grep -o 'href=\"[^\"]*linux_x64[^\"]*\\.tar\\.gz\"' | head -n1 | sed 's/^href=\"//; s/\"$//')\n" +
            "URL=${P:+https://github.com$P}\n" +
            "if [ -z \"$URL\" ]; then echo \"EZFORENSICS: no linux x64 MemProcFS release found at " + repo + "/releases\" >&2; exit 4; fi\n" +
            "echo \"EZFORENSICS: MemProcFS $TAG: $URL\"\n" +
            "T=$(mktemp -d)\n" +
            "curl -fSL -o \"$T/m.tar.gz\" \"$URL\"\n" +
            $"rm -rf {d}\nmkdir -p {d}\n" +
            $"tar -xzf \"$T/m.tar.gz\" -C {d}\n" +
            $"chmod +x {d}/memprocfs\n" +
            $"echo \"${{TAG:-installed}}\" > {d}/.ezf-version\n" +
            "rm -rf \"$T\"\n" +
            $"echo \"EZFORENSICS: MemProcFS ${{TAG}} in {d}\"\n");
    }

    // ------------------------------------------------------------------ source of Spectre

    /// <summary>The Spectre source checkout (Windows path): <c>EZFORENSICS_SPECTRE_SRC</c>, else a sibling "Spectre" folder of this repo. Null = none. The app itself prefers a bundled wheel: <see cref="FindInstallSource"/>.</summary>
    public static string? FindSource(string? startDir = null, string? envValue = null)
    {
        envValue ??= Environment.GetEnvironmentVariable(SourceEnvVar);
        if (!string.IsNullOrWhiteSpace(envValue))
            return File.Exists(Path.Combine(envValue.Trim(), "pyproject.toml")) ? Path.GetFullPath(envValue.Trim()) : null;
        for (var d = new DirectoryInfo(startDir ?? AppContext.BaseDirectory); d != null; d = d.Parent)
        {
            var candidate = Path.Combine(d.FullName, "Spectre");
            if (File.Exists(Path.Combine(candidate, "pyproject.toml")) && Directory.Exists(Path.Combine(candidate, "src", "spectre"))) return candidate;
        }
        return null;
    }

    /// <summary>
    /// What to pip install: the wheel bundled with the app (<c>spectre\*.whl</c> under <paramref name="baseDir"/>, newest name last wins), else
    /// <see cref="FindSource"/> (env var, then the dev walk-up). Windows path of a wheel file or a source folder; null = none.
    /// </summary>
    public static string? FindInstallSource(string? baseDir = null, string? startDir = null, string? envValue = null)
    {
        var dir = Path.Combine(baseDir ?? AppContext.BaseDirectory, "spectre");
        if (Directory.Exists(dir))
        {
            var wheel = Directory.EnumerateFiles(dir, "*.whl").OrderBy(f => f, StringComparer.OrdinalIgnoreCase).LastOrDefault();
            if (wheel != null) return Path.GetFullPath(wheel);
        }
        return FindSource(startDir ?? baseDir, envValue);
    }

    // ------------------------------------------------------------------ steps

    private static RuntimeStep Step(string wslExe, string id, string script, string label, int minutes) =>
        new(id, wslExe, EzDistro.ScriptArgs(script), label, TimeSpan.FromMinutes(minutes), WslRuntime.Utf8Env);

    /// <summary>Step(s) for one tool. Apt tools share one step id, so the per-row Install and "all" both give the same apt run.</summary>
    public static SpectrePlan PlanFor(string wslExe, SpectreEngineTool tool, string? spectreSource, string? sourceWsl = null)
    {
        switch (tool.Kind)
        {
            case SpectreInstallKind.Apt:
                return new(new[] { Step(wslExe, ToolIdApt, AptScript(new[] { tool }), $"Install {tool.Name} in {EzDistro.Name} (apt)", 30) });
            case SpectreInstallKind.MemProcFsRelease:
                return new(new[] { Step(wslExe, ToolIdMemProcFs, MemProcFsScript(), $"Install MemProcFS in {EzDistro.Name} (GitHub release)", 30) });
            case SpectreInstallKind.Zeek:
                return new(new[] { Step(wslExe, ToolIdZeek, ZeekSetup.WslInstallScript, $"Install Zeek in {EzDistro.Name} (apt, official repo)", 45) });
            default:
            {
                var wsl = sourceWsl ?? (spectreSource == null ? null : WslRuntime.ToWslPath(spectreSource));
                if (wsl == null)
                    return new(Array.Empty<RuntimeStep>(), $"The Spectre source folder was not found. Set {SourceEnvVar} to the Spectre checkout (the folder with pyproject.toml), or put it next to EZ Forensics.");
                var plaso = tool.Kind == SpectreInstallKind.VenvExtra;
                return new(new[] { Step(wslExe, plaso ? ToolIdPlaso : ToolIdVenv, VenvScript(wsl, plaso),
                    plaso ? $"Install plaso into the Spectre environment in {EzDistro.Name} (pip)" : $"Install / update the Spectre engine in {EzDistro.Name} (pip)", 60) });
            }
        }
    }

    /// <summary>
    /// "Install / update all": the required tools (one apt run, then the Spectre environment, which always runs so it updates), and
    /// updates of optional tools that are already installed (apt ones only; MemProcFS, Zeek and plaso keep their own row buttons).
    /// Missing optional tools are left out: each is installed from its own row when wanted.
    /// </summary>
    public static SpectrePlan PlanAll(string wslExe, IReadOnlyDictionary<string, SpectreToolStatus> status, string? spectreSource, string? sourceWsl = null)
    {
        bool Installed(SpectreEngineTool t) => status.TryGetValue(t.Id, out var s) && s.Installed;
        var steps = new List<RuntimeStep>
        {
            Step(wslExe, ToolIdApt, AptScript(SpectreEngineTools.All.Where(t => t.Kind == SpectreInstallKind.Apt && (t.Required || Installed(t)))),
                $"Install / update the Spectre engine's apt tools in {EzDistro.Name}", 30),
        };
        string? problem = null;
        foreach (var t in SpectreEngineTools.All.Where(t => t.Kind == SpectreInstallKind.SpectreVenv))
        {
            if (t.Id == "volatility3") continue;   // comes with the Spectre environment
            var p = PlanFor(wslExe, t, spectreSource, sourceWsl);
            problem ??= p.Problem;
            steps.AddRange(p.Steps);
        }
        return new(steps, problem);
    }

    /// <summary>Enqueues the step and waits for it; <paramref name="ct"/> cancels the job (process tree killed).</summary>
    public static async Task<Job> RunStepAsync(JobQueue queue, RuntimeStep step, string outputDir, Action<Job>? onJob = null, CancellationToken ct = default)
    {
        Directory.CreateDirectory(outputDir);
        var job = queue.Enqueue(step.ToJobSpec(outputDir), outputDir);
        onJob?.Invoke(job);
        using (ct.Register(job.Cancel))
            await Plaso.PlasoTimelineRunner.WaitAsync(job).ConfigureAwait(false);
        return job;
    }
}
