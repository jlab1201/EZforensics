using System.Diagnostics;
using System.Text;
using System.Text.RegularExpressions;

namespace EzForensics.Core.Execution;

public sealed record ProcessResult(int ExitCode, bool TimedOut, bool Cancelled);

public static class ProcessRunner
{
    private static readonly Regex Ansi = new(
        @"\x1B\[[0-?]*[ -/]*[@-~]|\x1B\][^\x07\x1B]*(\x07|\x1B\\)|\x1B[@-Z\\-_]",
        RegexOptions.Compiled);

    public static string StripAnsi(string s) => s.IndexOf('\x1B') < 0 ? s : Ansi.Replace(s, "");

    /// <summary>
    /// A captured line read as UTF-8 that may come from a UTF-16 writer (some Windows tools write UTF-16 with a BOM when their output is
    /// redirected): NUL bytes are dropped and a leading BOM (or its UTF-8 replacement characters) removed, so the text stays readable.
    /// </summary>
    public static string CleanLine(string s)
    {
        if (s.IndexOf('\0') >= 0) s = s.Replace("\0", "");
        int i = 0;
        while (i < s.Length && s[i] is '﻿' or '�') i++;
        return StripAnsi(i == 0 ? s : s[i..]);
    }

    /// <param name="onLine">(isStdErr, line)</param>
    /// <param name="stdoutPath">When set, raw stdout is copied to this file byte for byte (not passed to <paramref name="onLine"/>).</param>
    public static async Task<ProcessResult> RunAsync(
        string exePath, IReadOnlyList<string> args, string? workingDir,
        Action<bool, string> onLine, TimeSpan timeout, CancellationToken ct,
        IReadOnlyDictionary<string, string>? environment = null, string? stdoutPath = null)
    {
        // wsl.exe may write its own messages as UTF-16LE even with WSL_UTF8=1 (older builds): detect it per stream.
        var outEncoding = Runtimes.WslOutputEncoding.IsWslExe(exePath) ? Runtimes.WslOutputEncoding.Instance : Encoding.UTF8;
        var psi = new ProcessStartInfo(exePath)
        {
            UseShellExecute = false,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            RedirectStandardInput = true,
            StandardOutputEncoding = outEncoding,
            StandardErrorEncoding = outEncoding,
            CreateNoWindow = true,
        };
        if (!string.IsNullOrEmpty(workingDir)) psi.WorkingDirectory = workingDir;
        foreach (var a in args) psi.ArgumentList.Add(a);
        if (environment != null) foreach (var (k, v) in environment) psi.Environment[k] = v;

        using var proc = new Process { StartInfo = psi };
        var gate = new object();
        void Handle(bool err, string? line)
        {
            if (line == null) return;
            // NULs only appear when UTF-16 text was read as UTF-8 (a stream that switched encoding): drop them so the text stays readable.
            if (line.IndexOf('\0') >= 0) line = line.Replace("\0", "");
            lock (gate) onLine(err, CleanLine(line));
        }
        proc.OutputDataReceived += (_, e) => Handle(false, e.Data);
        proc.ErrorDataReceived += (_, e) => Handle(true, e.Data);

        // Raw stdout capture: the file is created before the process starts so a failed open never leaves a running process behind.
        await using var stdoutFile = stdoutPath != null
            ? new FileStream(stdoutPath, FileMode.Create, FileAccess.Write, FileShare.Read, 1 << 16, FileOptions.Asynchronous)
            : null;
        Task stdoutCopy = Task.CompletedTask;

        proc.Start();
        try { proc.StandardInput.Close(); } catch { }
        if (stdoutFile != null) stdoutCopy = proc.StandardOutput.BaseStream.CopyToAsync(stdoutFile);
        else proc.BeginOutputReadLine();
        proc.BeginErrorReadLine();

        using var timeoutCts = new CancellationTokenSource();
        if (timeout > TimeSpan.Zero && timeout != Timeout.InfiniteTimeSpan) timeoutCts.CancelAfter(timeout);
        using var linked = CancellationTokenSource.CreateLinkedTokenSource(ct, timeoutCts.Token);

        bool timedOut = false, cancelled = false;
        try
        {
            await proc.WaitForExitAsync(linked.Token).ConfigureAwait(false);
        }
        catch (OperationCanceledException)
        {
            timedOut = timeoutCts.IsCancellationRequested && !ct.IsCancellationRequested;
            cancelled = !timedOut;
            await KillTreeAsync(proc).ConfigureAwait(false);
            var exited = Task.Run(() => proc.WaitForExit());
            await Task.WhenAny(exited, Task.Delay(TimeSpan.FromSeconds(5))).ConfigureAwait(false);
        }

        // Flush remaining output (EOF); bounded in case an orphan holds the pipe open.
        if (!timedOut && !cancelled)
        {
            var flush = Task.Run(() => proc.WaitForExit());
            await Task.WhenAny(flush, Task.Delay(TimeSpan.FromSeconds(5))).ConfigureAwait(false);
        }

        if (stdoutFile != null)
        {
            // EOF arrives once the process (tree) is gone; bounded in case an orphan holds the pipe open.
            await Task.WhenAny(stdoutCopy, Task.Delay(TimeSpan.FromSeconds(5))).ConfigureAwait(false);
            try { if (stdoutCopy.IsCompleted) await stdoutCopy.ConfigureAwait(false); else _ = stdoutCopy.ContinueWith(t => _ = t.Exception, TaskScheduler.Default); }
            catch (Exception) { }
            try { await stdoutFile.FlushAsync().ConfigureAwait(false); } catch (Exception) { }
        }

        int code;
        try { code = proc.HasExited ? proc.ExitCode : -1; } catch { code = -1; }
        return new ProcessResult(code, timedOut, cancelled);
    }

    /// <summary>Kills the process tree, retrying: a kill issued right after Start can fail transiently and would leave an orphan holding the output pipes.</summary>
    private static async Task KillTreeAsync(Process proc)
    {
        for (int attempt = 0; attempt < 25; attempt++)
        {
            try { if (proc.HasExited) return; } catch { }
            try { proc.Kill(entireProcessTree: true); } catch { }
            try { if (proc.WaitForExit(200)) return; } catch { }
            try { proc.Kill(); } catch { }
            await Task.Delay(50).ConfigureAwait(false);
        }
    }
}
