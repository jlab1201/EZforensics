using System.Diagnostics;
using System.Globalization;
using EzForensics.Core.Sysinternals;

namespace EzForensics.Core.Execution;

/// <summary>How much a tool job writes to its log (Options > Installation > Tool logging).</summary>
public enum ToolLogLevel { Normal, Debug }

/// <summary>Facts about the tool executable for the debug header (version, size, time, signature).</summary>
public sealed record ToolFileFacts(string Path, bool Exists, string? FileVersion, long Size, DateTime? LastWriteUtc, string Signature);

/// <summary>
/// The extra job log lines written when tool logging is <see cref="ToolLogLevel.Debug"/>: every line starts with
/// <see cref="Prefix"/>. The queue writes <see cref="StartLines"/> before the process starts and <see cref="EndLines"/> after it
/// exits. Pure formatting, so it is tested without running anything.
/// </summary>
public static class ToolDebugLog
{
    public const string Prefix = "# Debug: ";
    public const int DefaultTail = 20;

    /// <summary>A Windows command line argument quoted the way CreateProcess / ProcessStartInfo.ArgumentList pass it.</summary>
    public static string Quote(string s)
    {
        if (s.Length > 0 && !s.Any(c => char.IsWhiteSpace(c) || c == '"')) return s;
        var sb = new System.Text.StringBuilder("\"");
        int backslashes = 0;
        foreach (var c in s)
        {
            if (c == '\\') { backslashes++; continue; }
            if (c == '"') sb.Append('\\', backslashes * 2 + 1).Append('"');
            else sb.Append('\\', backslashes).Append(c);
            backslashes = 0;
        }
        sb.Append('\\', backslashes * 2).Append('"');
        return sb.ToString();
    }

    public static string CommandLine(string exe, IReadOnlyList<string> args) => string.Join(" ", new[] { Quote(exe) }.Concat(args.Select(Quote)));

    public static IEnumerable<string> StartLines(string exe, IReadOnlyList<string> args, string? workingDir, bool elevated, ToolFileFacts tool,
        IReadOnlyDictionary<string, string>? environment = null, string? stdoutPath = null)
    {
        yield return Prefix + "command line: " + CommandLine(exe, args);
        yield return Prefix + "working directory: " + (string.IsNullOrEmpty(workingDir) ? "(inherited) " + Environment.CurrentDirectory : workingDir);
        yield return Prefix + "elevated (Administrator): " + (elevated ? "yes" : "no");
        yield return Prefix + "tool: " + FormatTool(tool);
        if (environment is { Count: > 0 })
            yield return Prefix + "environment: " + string.Join(", ", environment.Keys.OrderBy(k => k, StringComparer.OrdinalIgnoreCase).Select(k => k + "=" + environment[k]));
        if (stdoutPath != null) yield return Prefix + "stdout is written to " + stdoutPath;
    }

    public static string FormatTool(ToolFileFacts t)
    {
        if (!t.Exists) return t.Path + " (not found)";
        return $"{t.Path}, version {t.FileVersion ?? "-"}, {t.Size.ToString("N0", CultureInfo.InvariantCulture)} bytes, " +
               $"modified {(t.LastWriteUtc is { } w ? w.ToString("yyyy-MM-dd HH:mm:ss", CultureInfo.InvariantCulture) + " UTC" : "-")}, signature: {t.Signature}";
    }

    public static IEnumerable<string> EndLines(int exitCode, bool timedOut, bool cancelled, TimeSpan duration,
        IReadOnlyList<string> stdout, IReadOnlyList<string> stderr, int tail = DefaultTail)
    {
        yield return Prefix + $"exit code {exitCode} (0x{exitCode:X8}){(timedOut ? ", timed out" : cancelled ? ", cancelled" : "")}, duration {FormatDuration(duration)}";
        foreach (var l in Tail("stdout", stdout, tail)) yield return l;
        foreach (var l in Tail("stderr", stderr, tail)) yield return l;
    }

    private static IEnumerable<string> Tail(string name, IReadOnlyList<string> lines, int tail)
    {
        if (lines.Count == 0) { yield return Prefix + name + ": (empty)"; yield break; }
        var from = Math.Max(0, lines.Count - tail);
        yield return Prefix + $"{name}: last {lines.Count - from} of {lines.Count} line(s)";
        for (int i = from; i < lines.Count; i++) yield return Prefix + name + "| " + lines[i];
    }

    public static string FormatDuration(TimeSpan d) =>
        d.TotalHours >= 1 ? $"{(int)d.TotalHours}h {d.Minutes:00}m {d.Seconds:00}s"
        : d.TotalMinutes >= 1 ? $"{(int)d.TotalMinutes}m {d.Seconds:00}.{d.Milliseconds / 100}s"
        : d.TotalSeconds.ToString("0.000", CultureInfo.InvariantCulture) + " s";

    /// <summary>Version, size, time and Authenticode signature of the tool (never throws).</summary>
    public static ToolFileFacts Inspect(string path, ISignatureVerifier? verifier = null)
    {
        try
        {
            var fi = new FileInfo(path);
            if (!fi.Exists) return new ToolFileFacts(path, false, null, 0, null, "-");
            string? version = null;
            try { var v = FileVersionInfo.GetVersionInfo(path); version = v.FileVersion ?? v.ProductVersion; } catch (Exception) { }
            string sig;
            try
            {
                var s = (verifier ?? SignatureVerifiers.Default).Verify(path);
                sig = s.Valid ? "valid, " + (s.Signer ?? "unknown signer") : "not valid (" + s.Detail + ")";
            }
            catch (Exception ex) { sig = "not checked (" + ex.Message + ")"; }
            return new ToolFileFacts(path, true, string.IsNullOrWhiteSpace(version) ? null : version.Trim(), fi.Length, fi.LastWriteTimeUtc, sig);
        }
        catch (Exception ex) { return new ToolFileFacts(path, false, null, 0, null, "not checked (" + ex.Message + ")"); }
    }

}
