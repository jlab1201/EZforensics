using System.Diagnostics;
using System.Text;

namespace EzForensics.Core.Runtimes;

/// <summary>
/// File system / process access used by runtime discovery (Docker, WSL, native tools). Replaced by a fake in tests.
/// Shared by every engine that is not an EZ tool (Zeek now; Volatility and others later).
/// </summary>
public interface IRuntimeProbe
{
    bool FileExists(string path);
    bool DirectoryExists(string path);
    /// <summary>Full path of <paramref name="fileName"/> on PATH, or null.</summary>
    string? FindOnPath(string fileName);
    /// <summary>Runs a process; stdout+stderr text, or null when it could not start, failed (non-zero exit) or timed out.</summary>
    string? Run(string exe, IReadOnlyList<string> args, TimeSpan timeout, IReadOnlyDictionary<string, string>? environment = null);
}

/// <summary>Real probe: PATH lookup and short-lived processes (argv via ArgumentList, never a quoted string).</summary>
public sealed class SystemRuntimeProbe : IRuntimeProbe
{
    public static readonly SystemRuntimeProbe Instance = new();

    public bool FileExists(string path) => File.Exists(path);
    public bool DirectoryExists(string path) => Directory.Exists(path);

    public string? FindOnPath(string fileName)
    {
        var path = Environment.GetEnvironmentVariable("PATH") ?? "";
        foreach (var dir in path.Split(Path.PathSeparator, StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
        {
            try
            {
                var p = Path.Combine(dir.Trim('"'), fileName);
                if (File.Exists(p)) return p;
            }
            catch (ArgumentException) { }
        }
        return null;
    }

    public string? Run(string exe, IReadOnlyList<string> args, TimeSpan timeout, IReadOnlyDictionary<string, string>? environment = null)
    {
        try
        {
            var psi = new ProcessStartInfo(exe)
            {
                UseShellExecute = false,
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                RedirectStandardInput = true,
                StandardOutputEncoding = WslOutputEncoding.IsWslExe(exe) ? WslOutputEncoding.Instance : Encoding.UTF8,
                StandardErrorEncoding = WslOutputEncoding.IsWslExe(exe) ? WslOutputEncoding.Instance : Encoding.UTF8,
                CreateNoWindow = true,
            };
            foreach (var a in args) psi.ArgumentList.Add(a);
            if (environment != null) foreach (var (k, v) in environment) psi.Environment[k] = v;
            using var p = Process.Start(psi);
            if (p == null) return null;
            try { p.StandardInput.Close(); } catch { }
            var o = p.StandardOutput.ReadToEndAsync();
            var e = p.StandardError.ReadToEndAsync();
            if (!p.WaitForExit((int)timeout.TotalMilliseconds))
            {
                try { p.Kill(entireProcessTree: true); } catch { }
                return null;
            }
            // A child the process left running may still hold the output pipes open: never block on them past the wait.
            Task.WaitAll(new Task[] { o, e }, 2000);
            if (p.ExitCode != 0) return null;
            return (o.IsCompletedSuccessfully ? o.Result : "") + (e.IsCompletedSuccessfully ? e.Result : "");
        }
        catch (Exception) { return null; }
    }
}

/// <summary>Small helpers shared by the runtime engines.</summary>
public static class RuntimeText
{
    /// <summary>Command line for display / logs (the process itself always gets an argv list).</summary>
    public static string Display(string exe, IReadOnlyList<string> args)
    {
        static string Q(string s) => s.Length == 0 || s.Any(c => char.IsWhiteSpace(c) || c == '"') ? "\"" + s.Replace("\"", "\\\"") + "\"" : s;
        return string.Join(" ", new[] { Q(exe) }.Concat(args.Select(Q)));
    }

    /// <summary>Removes NULs (UTF-16 output read as UTF-8) and a BOM, normalises line ends.</summary>
    public static string Clean(string? s) =>
        string.IsNullOrEmpty(s) ? "" : s.Replace("\0", "").Replace("﻿", "").Replace("\r\n", "\n").Replace('\r', '\n');
}
