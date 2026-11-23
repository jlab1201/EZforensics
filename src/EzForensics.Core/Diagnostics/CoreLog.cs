namespace EzForensics.Core.Diagnostics;

/// <summary>Where Core reports swallowed save / load failures. The App points <see cref="Sink"/> at its log; without a sink nothing is written.</summary>
public static class CoreLog
{
    public static Action<string>? Sink { get; set; }

    public static void Warn(string what, Exception ex)
    {
        try { Sink?.Invoke($"{what}: {ex.GetType().Name}: {ex.Message}"); }
        catch { /* logging must never break the caller */ }
    }
}
