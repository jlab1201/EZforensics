using System.Diagnostics;

namespace EzForensics.Core.Diagnostics;

/// <summary>
/// Safety net for shutdown: started at the very end of the exit path, it waits <c>grace</c> on a background thread.
/// A normal exit kills that thread with the process, so nothing happens. If the process is still alive when the
/// grace period ends, something is holding it open: it logs that (with the thread count) and forces the exit.
/// </summary>
public sealed class ExitWatchdog : IDisposable
{
    private readonly ManualResetEventSlim _cancel = new(false);

    private ExitWatchdog() { }

    public static ExitWatchdog Start(TimeSpan grace, Action<string> log, Action<int> exit, int exitCode)
    {
        var w = new ExitWatchdog();
        var t = new Thread(() =>
        {
            if (w._cancel.Wait(grace)) return;
            string threads;
            try { threads = Process.GetCurrentProcess().Threads.Count + " threads"; }
            catch { threads = "thread count unknown"; }
            try { log($"Process still alive {grace.TotalSeconds:0.#} s after exit ({threads}); forcing exit."); } catch { }
            exit(exitCode);
        })
        { IsBackground = true, Name = "ExitWatchdog" };
        t.Start();
        return w;
    }

    /// <summary>Stops the watchdog (tests only; the app lets the process end instead).</summary>
    public void Dispose() => _cancel.Set();
}
