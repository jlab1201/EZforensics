namespace EzForensics.Core.Setup;

/// <summary>
/// One EZ Forensics window per user session: the first process owns a named mutex and waits on a named event; a later process
/// signals that event (so the first one brings its window to the front) and exits. An abandoned mutex (previous owner crashed)
/// counts as owned. The names are parameters so tests can use unique ones.
/// An instance running as Administrator owns objects a normal launch may not open (integrity "no write-up"): such a launch is told
/// another instance runs (<see cref="TryBecomeFirst()"/> false, <see cref="OwnedByElevatedInstance"/> true) instead of crashing.
/// </summary>
public sealed class SingleInstanceGuard : IDisposable
{
    public const string DefaultMutexName = @"Local\EzForensics.App";
    public const string DefaultActivateEventName = @"Local\EzForensics.App.Activate";

    private readonly string _eventName;
    private Mutex? _mutex;
    private EventWaitHandle? _activate;
    private Thread? _listener;
    private volatile bool _stop;

    private readonly Func<string, EventWaitHandle> _openEvent;

    public SingleInstanceGuard(string mutexName = DefaultMutexName, string activateEventName = DefaultActivateEventName)
        : this(mutexName, activateEventName, n => new Mutex(false, n), EventWaitHandle.OpenExisting) { }

    /// <summary>Tests: replace how the mutex is created and the activate event opened (e.g. to simulate access denied).</summary>
    public SingleInstanceGuard(string mutexName, string activateEventName, Func<string, Mutex> createMutex, Func<string, EventWaitHandle> openEvent)
    {
        _eventName = activateEventName;
        _openEvent = openEvent;
        try { _mutex = createMutex(mutexName); }
        catch (UnauthorizedAccessException) { OwnedByElevatedInstance = true; }
    }

    /// <summary>The mutex exists but this process may not open it: an instance running as Administrator owns it.</summary>
    public bool OwnedByElevatedInstance { get; private set; }

    /// <summary>True when this process is now the running instance.</summary>
    public bool IsFirst { get; private set; }

    /// <summary>Takes the mutex. False means another instance owns it.</summary>
    public bool TryBecomeFirst() => TryBecomeFirst(TimeSpan.Zero);

    /// <summary>
    /// Takes the mutex, waiting up to <paramref name="wait"/> for the owner to exit (a relaunch of this app, e.g. Restart as Administrator: the
    /// old process still holds the mutex while it shuts down). False means another instance still owns it.
    /// </summary>
    public bool TryBecomeFirst(TimeSpan wait)
    {
        if (IsFirst) return true;
        if (_mutex == null) return false;
        try { IsFirst = _mutex.WaitOne(wait); }
        catch (AbandonedMutexException) { IsFirst = true; }
        return IsFirst;
    }

    /// <summary>Asks the running instance to show its window. Returns false when it is not listening (yet).</summary>
    [System.Diagnostics.CodeAnalysis.SuppressMessage("Interoperability", "CA1416", Justification = "Windows-only app")]
    public bool SignalExisting()
    {
        try
        {
            using var ev = _openEvent(_eventName);
            return ev.Set();
        }
        catch (WaitHandleCannotBeOpenedException) { return false; }
        catch (UnauthorizedAccessException) { OwnedByElevatedInstance = true; return false; }
    }

    /// <summary>Starts the background wait; <paramref name="onActivate"/> runs on the listener thread for each signal.</summary>
    public void StartListening(Action onActivate)
    {
        if (!IsFirst || _listener != null) return;
        var activate = _activate = new EventWaitHandle(false, EventResetMode.AutoReset, _eventName);
        _listener = new Thread(() =>
        {
            while (!_stop)
            {
                try
                {
                    if (!activate.WaitOne(250)) continue;
                    if (_stop) break;
                    onActivate();
                }
                catch (ObjectDisposedException) { break; }
                catch { /* never let the activation callback end the listener */ }
            }
        }) { IsBackground = true, Name = "EzForensics.SingleInstance" };
        _listener.Start();
    }

    public void Dispose()
    {
        _stop = true;
        _listener?.Join(1000);
        _activate?.Dispose();
        if (_mutex != null)
        {
            if (IsFirst) try { _mutex.ReleaseMutex(); } catch (ApplicationException) { }
            _mutex.Dispose();
            _mutex = null;
        }
        IsFirst = false;
    }
}
