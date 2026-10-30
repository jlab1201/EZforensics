using System.Diagnostics;
using System.Text.Json;
using EzForensics.Core.Runtimes;

namespace EzForensics.Core.Setup;

/// <summary>One command the elevated helper ran, with its exit code (logged by the wizard).</summary>
public sealed class ElevatedCommandLog
{
    public string Command { get; set; } = "";
    public int ExitCode { get; set; }
}

/// <summary>What the elevated helper did. Written as JSON to the <c>--result</c> file so the (non-elevated) wizard can read it.</summary>
public sealed class ElevatedRunResult
{
    public bool Ok { get; set; }
    /// <summary>A command said so (exit code 3010) — Windows must restart before WSL works.</summary>
    public bool RestartRequired { get; set; }
    public string Message { get; set; } = "";
    public List<ElevatedCommandLog> Commands { get; set; } = new();
}

/// <summary>
/// Every setup task that needs administrator rights, run in ONE elevated process (one UAC prompt, asked right after the user clicks
/// Install). The host relaunches its own exe elevated with <see cref="Switch"/> (headless, no window), e.g.
/// <c>EzForensics.exe --elevated-setup enable-wsl --result &lt;file&gt;</c>. Today the only task is enabling WSL:
/// <c>wsl --install --no-distribution</c>, falling back to DISM (Microsoft-Windows-Subsystem-Linux + VirtualMachinePlatform) on
/// Windows builds whose wsl.exe does not support it. Never restarts Windows (the wizard asks once, at the end).
/// </summary>
public static class ElevatedSetupHelper
{
    public const string Switch = "--elevated-setup";
    public const string EnableWslTask = "enable-wsl";
    /// <summary>Only the classic WSL component (for the WSL 1 fallback); see <see cref="EnableWsl1"/>.</summary>
    public const string EnableWsl1Task = "enable-wsl1";
    /// <summary>ERROR_SUCCESS_REBOOT_REQUIRED: done, effective after a restart (DISM, msiexec, wsl --install).</summary>
    public const int RebootRequiredExit = 3010;
    public const string WslFeature = "Microsoft-Windows-Subsystem-Linux";
    public const string VmPlatformFeature = "VirtualMachinePlatform";

    /// <summary>Runs one command (hidden, output to <paramref name="log"/>) and returns its exit code; -1 when it could not start.</summary>
    public delegate int CommandRunner(string exe, IReadOnlyList<string> args, Action<string> log);

    /// <summary>Arguments for the elevated relaunch (after any host prefix such as the dll path for <c>dotnet</c>).</summary>
    /// <param name="logPath">Optional <c>--log</c> file: the helper appends every log line to it (flushed per line) so the wizard can tail it live.</param>
    public static IReadOnlyList<string> BuildArgs(IEnumerable<string> tasks, string resultPath, string? logPath = null)
    {
        var a = new List<string> { Switch };
        a.AddRange(tasks);
        a.Add("--result");
        a.Add(resultPath);
        if (logPath != null)
        {
            a.Add("--log");
            a.Add(logPath);
        }
        return a;
    }

    /// <summary>Entry point of the elevated process (args after the switch). Returns 0 when every task succeeded.</summary>
    public static int RunCommandLine(IReadOnlyList<string> args, Action<string>? log = null, CommandRunner? runner = null)
    {
        log ??= _ => { };
        runner ??= RunHidden;
        string? resultPath = null, logPath = null;
        var tasks = new List<string>();
        for (int i = 0; i < args.Count; i++)
        {
            if (args[i] == "--result" && i + 1 < args.Count) resultPath = args[++i];
            else if (args[i] == "--log" && i + 1 < args.Count) logPath = args[++i];
            else tasks.Add(args[i]);
        }
        using var file = logPath != null ? LineLogFile.TryOpen(logPath) : null;
        if (file != null)
        {
            var inner = log;
            log = line => { inner(line); file.WriteLine(line); };
        }
        var result = new ElevatedRunResult { Ok = true };
        foreach (var t in tasks)
        {
            if (t.Equals(EnableWslTask, StringComparison.OrdinalIgnoreCase) || t.Equals(EnableWsl1Task, StringComparison.OrdinalIgnoreCase))
            {
                var r = t.Equals(EnableWsl1Task, StringComparison.OrdinalIgnoreCase)
                    ? EnableWsl1(runner, SystemExe("dism.exe"), log)
                    : EnableWsl(runner, SystemExe("wsl.exe"), SystemExe("dism.exe"), log);
                result.Ok &= r.Ok;
                result.RestartRequired |= r.RestartRequired;
                result.Message = (result.Message + " " + r.Message).Trim();
                result.Commands.AddRange(r.Commands);
            }
            else
            {
                result.Ok = false;
                result.Message = (result.Message + $" Unknown task '{t}'.").Trim();
            }
        }
        if (resultPath != null)
        {
            try { WriteResult(resultPath, result); }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                log($"Could not write the result file {resultPath}: {ex.Message}");
                return 1;
            }
        }
        return result.Ok ? 0 : 1;
    }

    /// <summary><c>wsl --install --no-distribution</c>; when that fails, enable both Windows features with DISM.</summary>
    public static ElevatedRunResult EnableWsl(CommandRunner run, string wslExe, string dismExe, Action<string> log)
    {
        var r = new ElevatedRunResult();
        int Run(string exe, params string[] a)
        {
            log($"> {exe} {string.Join(" ", a)}");
            int code = run(exe, a, log);
            log($"exit code {code}");
            r.Commands.Add(new ElevatedCommandLog { Command = $"{Path.GetFileName(exe)} {string.Join(" ", a)}", ExitCode = code });
            return code;
        }

        int wsl = Run(wslExe, "--install", "--no-distribution");
        if (wsl is 0 or RebootRequiredExit)
        {
            r.Ok = true;
            r.RestartRequired = wsl == RebootRequiredExit;
            r.Message = "WSL was enabled (wsl --install --no-distribution).";
            // Modern 'wsl --install' enables only VirtualMachinePlatform. The classic WSL component is what WSL 1 needs, the fallback
            // when WSL 2 cannot run (no hardware virtualization, e.g. a VM without nested virtualization). Not fatal: WSL 2 works without it.
            int classic = Run(dismExe, "/online", "/enable-feature", "/featurename:" + WslFeature, "/all", "/norestart");
            if (classic == RebootRequiredExit) r.RestartRequired = true;
            else if (classic != 0) log($"Enabling {WslFeature} (needed only for WSL 1) failed with exit code {classic}; WSL 2 is not affected.");
            return r;
        }
        log("wsl --install --no-distribution did not succeed: enabling the Windows features with DISM instead.");
        bool restart = false, ok = true;
        foreach (var f in new[] { WslFeature, VmPlatformFeature })
        {
            int code = Run(dismExe, "/online", "/enable-feature", "/featurename:" + f, "/all", "/norestart");
            if (code == RebootRequiredExit) restart = true;
            else if (code != 0) ok = false;
        }
        r.Ok = ok;
        r.RestartRequired = restart;
        r.Message = ok ? "WSL was enabled (DISM: Microsoft-Windows-Subsystem-Linux and VirtualMachinePlatform)."
            : $"Enabling WSL failed (wsl --install exit code {wsl}; DISM exit codes {string.Join(", ", r.Commands.Skip(1).Select(c => c.ExitCode))}). Virtualization may be off in the BIOS/UEFI; see docs\\SETUP.md.";
        return r;
    }

    /// <summary>
    /// Enables only the classic WSL component (Microsoft-Windows-Subsystem-Linux) with DISM: what WSL 1 needs when WSL 2 cannot run
    /// and an earlier 'wsl --install' enabled only VirtualMachinePlatform. Effective after a restart.
    /// </summary>
    public static ElevatedRunResult EnableWsl1(CommandRunner run, string dismExe, Action<string> log)
    {
        var r = new ElevatedRunResult();
        var args = new[] { "/online", "/enable-feature", "/featurename:" + WslFeature, "/all", "/norestart" };
        log($"> {dismExe} {string.Join(" ", args)}");
        int code = run(dismExe, args, log);
        log($"exit code {code}");
        r.Commands.Add(new ElevatedCommandLog { Command = $"{Path.GetFileName(dismExe)} {string.Join(" ", args)}", ExitCode = code });
        r.Ok = code is 0 or RebootRequiredExit;
        r.RestartRequired = code == RebootRequiredExit;
        r.Message = r.Ok ? $"The Windows Subsystem for Linux component (WSL 1) was enabled." : $"Enabling {WslFeature} failed (DISM exit code {code}).";
        return r;
    }

    /// <summary>
    /// Maps the helper's result plus what the machine says now to the step outcome: WSL works without a restart -> Done (the
    /// distro, Zeek and RITA continue in the same run); otherwise -> waiting for the restart (asked once, at the end of the run).
    /// </summary>
    public static SetupStepResult Interpret(ElevatedRunResult r, bool wslUsableNow, bool rebootPending)
    {
        var log = string.Join("; ", r.Commands.Select(c => $"{c.Command} -> {c.ExitCode}"));
        if (!r.Ok) return SetupStepResult.Fail(r.Message + (log.Length > 0 ? $" ({log})" : ""));
        if (r.RestartRequired || rebootPending || !wslUsableNow)
            return SetupStepResult.RestartRequired("WSL was enabled. Windows needs one restart before the Linux environment, Zeek and RITA can be installed: " +
                                                   "setup asks for it at the end and continues by itself after you sign in again.");
        return SetupStepResult.Ok("WSL was enabled and works without a restart.");
    }

    /// <summary>
    /// Writes the result to a NEW file (never overwrites: the path is in a user-writable folder and this runs elevated, so an existing
    /// file or a planted link there is never truncated or followed into an existing file; the wizard deletes an earlier result first).
    /// </summary>
    public static void WriteResult(string path, ElevatedRunResult r)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(Path.GetFullPath(path))!);
        using var fs = new FileStream(path, FileMode.CreateNew, FileAccess.Write, FileShare.Read);
        using var w = new StreamWriter(fs, new System.Text.UTF8Encoding(false));
        w.Write(JsonSerializer.Serialize(r, new JsonSerializerOptions { WriteIndented = true }));
    }

    public static ElevatedRunResult? ReadResult(string path)
    {
        try { return File.Exists(path) ? JsonSerializer.Deserialize<ElevatedRunResult>(File.ReadAllText(path)) : null; }
        catch (JsonException) { return null; }
    }

    /// <summary>
    /// Windows has a restart pending from component servicing (set when Windows features are enabled). Readable without admin.
    /// </summary>
    public static bool IsRebootPending()
    {
        if (!OperatingSystem.IsWindows()) return false;
        try
        {
            using var k = Microsoft.Win32.Registry.LocalMachine.OpenSubKey(@"SOFTWARE\Microsoft\Windows\CurrentVersion\Component Based Servicing\RebootPending");
            return k != null;
        }
        catch { return false; }
    }

    private static string SystemExe(string name) => Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.System), name);

    /// <summary>Runs a console command with no window; output (UTF-8 for wsl.exe via WSL_UTF8) goes to the log.</summary>
    public static int RunHidden(string exe, IReadOnlyList<string> args, Action<string> log)
    {
        try
        {
            var psi = new ProcessStartInfo(exe)
            {
                UseShellExecute = false, CreateNoWindow = true, RedirectStandardOutput = true, RedirectStandardError = true,
            };
            foreach (var a in args) psi.ArgumentList.Add(a);
            foreach (var kv in WslRuntime.Utf8Env) psi.Environment[kv.Key] = kv.Value;
            using var p = Process.Start(psi);
            if (p == null) return -1;
            p.OutputDataReceived += (_, e) => { if (!string.IsNullOrWhiteSpace(e.Data)) log(e.Data.Replace("\0", "")); };
            p.ErrorDataReceived += (_, e) => { if (!string.IsNullOrWhiteSpace(e.Data)) log(e.Data.Replace("\0", "")); };
            p.BeginOutputReadLine();
            p.BeginErrorReadLine();
            if (!p.WaitForExit((int)TimeSpan.FromMinutes(30).TotalMilliseconds))
            {
                try { p.Kill(true); } catch { }
                log("Timed out after 30 minutes.");
                return -1;
            }
            p.WaitForExit();
            return p.ExitCode;
        }
        catch (Exception ex)
        {
            log($"Could not start {exe}: {ex.Message}");
            return -1;
        }
    }
}
