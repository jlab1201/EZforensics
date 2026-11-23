using System.Text;
using System.Text.Json.Serialization;
using EzForensics.Core.Execution;

namespace EzForensics.Core.Explore;

/// <summary>Raw output of one static-analyzer run on one file (what <see cref="ResultCache"/> stores).</summary>
/// <param name="Tool">Cache key of the tool (<c>die</c>, <c>exif</c>).</param>
/// <param name="Raw">The tool's stdout (JSON for DIE and ExifTool), UTF-8.</param>
/// <param name="Error">Why the run failed (timeout, cancel, no output, start failure); null on success.</param>
public sealed record ExploreToolOutput(
    string Tool,
    string? ToolVersion,
    string Raw,
    int ExitCode,
    bool TimedOut,
    bool Cancelled,
    string? Error,
    string? Stderr,
    string CreatedUtc)
{
    [JsonIgnore] public bool Ok => Error == null && !TimedOut && !Cancelled;

    /// <summary>True when this came from the cache instead of a fresh run.</summary>
    [JsonIgnore] public bool FromCache { get; init; }
}

/// <summary>Shared process plumbing for the Explore runners: raw stdout capture, stderr collection, timeout and cancellation.</summary>
internal static class ExploreToolProcess
{
    private const int MaxStderrChars = 16 * 1024;

    public static async Task<ExploreToolOutput> RunAsync(string tool, string? version, string exe, IReadOnlyList<string> args,
        TimeSpan timeout, CancellationToken ct)
    {
        var now = DateTime.UtcNow.ToString("o");
        if (!File.Exists(exe)) return new(tool, version, "", -1, false, false, $"{Path.GetFileName(exe)} is not installed ({exe}).", null, now);
        var tmp = Path.Combine(Path.GetTempPath(), $"ezf-explore-{tool}-{Guid.NewGuid():N}.out");
        var err = new StringBuilder();
        try
        {
            ProcessResult r;
            try
            {
                r = await ProcessRunner.RunAsync(exe, args, Path.GetDirectoryName(exe),
                    (isErr, line) => { if (isErr && err.Length < MaxStderrChars) err.AppendLine(line); },
                    timeout, ct, stdoutPath: tmp).ConfigureAwait(false);
            }
            catch (Exception ex) when (ex is System.ComponentModel.Win32Exception or IOException or InvalidOperationException)
            {
                return new(tool, version, "", -1, false, false, $"Could not start {Path.GetFileName(exe)}: {ex.Message}", null, now);
            }
            string raw = "";
            try { if (File.Exists(tmp)) raw = DecodeUtf8(File.ReadAllBytes(tmp)); }
            catch (IOException) { }
            string? error = r.TimedOut ? $"Timed out after {timeout.TotalSeconds:0} s."
                : r.Cancelled ? "Cancelled."
                : string.IsNullOrWhiteSpace(raw) ? $"No output (exit code {r.ExitCode})."
                : null;
            var stderr = err.Length == 0 ? null : err.ToString().TrimEnd();
            return new(tool, version, raw, r.ExitCode, r.TimedOut, r.Cancelled, error, stderr, now);
        }
        finally
        {
            try { File.Delete(tmp); } catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { }
        }
    }

    private static string DecodeUtf8(byte[] b)
    {
        int start = b.Length >= 3 && b[0] == 0xEF && b[1] == 0xBB && b[2] == 0xBF ? 3 : 0;
        return Encoding.UTF8.GetString(b, start, b.Length - start);
    }

    /// <summary>Full path of the evidence file; refuses reparse points and folders (never follows links) and missing files.</summary>
    public static string CheckedEvidencePath(string file)
    {
        var full = Path.GetFullPath(file);
        var attr = File.GetAttributes(full);
        if ((attr & FileAttributes.ReparsePoint) != 0) throw new IOException($"{full} is a reparse point and is not followed.");
        if ((attr & FileAttributes.Directory) != 0) throw new IOException($"{full} is a folder.");
        return full;
    }
}
