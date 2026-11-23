using System.Diagnostics;
using System.Runtime.CompilerServices;
using System.Text;
using EzForensics.Core.Runtimes;

namespace EzForensics.Core.Assistant;

/// <summary>A long-lived child process with an OPEN stdin (unlike ProcessRunner, which closes it). Line framing, UTF-8 without BOM.</summary>
public sealed class StdioProcessHost : IAsyncDisposable
{
    private readonly Process _p;
    private readonly IReadOnlyList<string>? _cancel;
    private readonly SemaphoreSlim _writeLock = new(1, 1);

    private StdioProcessHost(Process p, IReadOnlyList<string>? cancel) { _p = p; _cancel = cancel; }

    public bool HasExited { get { try { return _p.HasExited; } catch (InvalidOperationException) { return true; } } }

    public static StdioProcessHost Start(string exe, IReadOnlyList<string> args, IReadOnlyDictionary<string, string>? env,
        IReadOnlyList<string>? cancelCommand, Action<string>? stderrLine)
    {
        var psi = new ProcessStartInfo(exe)
        {
            UseShellExecute = false, CreateNoWindow = true,
            RedirectStandardInput = true, RedirectStandardOutput = true, RedirectStandardError = true,
            StandardInputEncoding = new UTF8Encoding(false),
            StandardOutputEncoding = new UTF8Encoding(false),
            StandardErrorEncoding = WslOutputEncoding.IsWslExe(exe) ? WslOutputEncoding.Instance : new UTF8Encoding(false),
        };
        foreach (var a in args) psi.ArgumentList.Add(a);
        foreach (var kv in WslRuntime.Utf8Env) psi.Environment[kv.Key] = kv.Value;
        if (env != null) foreach (var kv in env) psi.Environment[kv.Key] = kv.Value;
        var p = Process.Start(psi) ?? throw new InvalidOperationException($"Could not start {exe}");
        p.StandardInput.AutoFlush = true;
        p.StandardInput.NewLine = "\n";
        p.ErrorDataReceived += (_, e) => { if (e.Data != null) stderrLine?.Invoke(RuntimeText.Clean(e.Data)); };
        p.BeginErrorReadLine();
        return new StdioProcessHost(p, cancelCommand);
    }

    /// <summary>Writes one line; calls are serialised so concurrent writers never interleave.</summary>
    public async Task WriteLineAsync(string json, CancellationToken ct)
    {
        await _writeLock.WaitAsync(ct).ConfigureAwait(false);
        try { await _p.StandardInput.WriteLineAsync(json.AsMemory(), ct).ConfigureAwait(false); }
        finally { _writeLock.Release(); }
    }

    public async IAsyncEnumerable<string> ReadLinesAsync([EnumeratorCancellation] CancellationToken ct)
    {
        while (!ct.IsCancellationRequested)
        {
            string? line;
            try { line = await _p.StandardOutput.ReadLineAsync(ct).ConfigureAwait(false); }
            catch (ObjectDisposedException) { yield break; }
            catch (InvalidOperationException) { yield break; }
            if (line == null) yield break;
            if (line.Length > 0) yield return line;
        }
    }

    public async ValueTask DisposeAsync()
    {
        try { _p.StandardInput.Close(); } catch (Exception ex) when (ex is IOException or InvalidOperationException) { }
        using var wait = new CancellationTokenSource(TimeSpan.FromSeconds(3));
        try { await _p.WaitForExitAsync(wait.Token).ConfigureAwait(false); }
        catch (OperationCanceledException)
        {
            try { _p.Kill(entireProcessTree: true); await _p.WaitForExitAsync().ConfigureAwait(false); }
            catch (Exception ex) when (ex is InvalidOperationException or System.ComponentModel.Win32Exception) { }
        }
        if (_cancel is { Count: > 0 }) // killing wsl.exe may leave the Linux process alive
        {
            try
            {
                var psi = new ProcessStartInfo(_cancel[0]) { UseShellExecute = false, CreateNoWindow = true };
                foreach (var a in _cancel.Skip(1)) psi.ArgumentList.Add(a);
                using var k = Process.Start(psi);
                k?.WaitForExit(10_000);
            }
            catch (Exception ex) when (ex is System.ComponentModel.Win32Exception or InvalidOperationException) { }
        }
        _p.Dispose();
    }
}
