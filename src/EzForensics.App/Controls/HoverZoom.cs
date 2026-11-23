using System.Diagnostics;
using System.Windows;
using System.Windows.Media;
using EzForensics.App.Services;
using EzForensics.Core.Settings;

namespace EzForensics.App.Controls;

/// <summary>
/// Smooth hover zoom for OnRender controls: one 0..1 progress per item, moved toward 1 (hovered) or 0 over <see cref="DurationMs"/> and
/// shown through an ease-out curve. Runs a CompositionTarget.Rendering hook only while something is still moving; with animations off
/// (<see cref="Motion.Enabled"/>) the final state is set at once.
/// </summary>
internal sealed class HoverZoom
{
    public const double DurationMs = 150;

    private readonly FrameworkElement _owner;
    private readonly Dictionary<int, double> _progress = new();
    private int _target = -1;
    private readonly Stopwatch _clock = new();
    private long _last;
    private bool _subscribed;

    public HoverZoom(FrameworkElement owner)
    {
        _owner = owner;
        owner.Loaded += (_, _) => ThemeManager.Changed += OnThemeChanged;
        owner.Unloaded += (_, _) => { Stop(); ThemeManager.Changed -= OnThemeChanged; };
    }

    /// <summary>OnRender controls resolve brushes while drawing, so they must redraw when the theme swaps.</summary>
    private void OnThemeChanged(AppTheme _) => _owner.InvalidateVisual();

    // ---- pure helpers (tests)

    /// <summary>Cubic ease-out of a 0..1 progress.</summary>
    internal static double Ease(double p) { p = Math.Clamp(p, 0, 1); double q = 1 - p; return 1 - q * q * q; }

    /// <summary>Eased scale from 1 to <paramref name="max"/> after <paramref name="elapsedMs"/> of the zoom-in (or, run backwards, the zoom-out).</summary>
    internal static double ScaleAt(double elapsedMs, double max = 1.35) => 1 + (max - 1) * Ease(elapsedMs / DurationMs);

    /// <summary>Moves <paramref name="progress"/> toward the target (1 or 0) by <paramref name="dtMs"/>.</summary>
    internal static double Step(double progress, bool hovered, double dtMs)
    {
        double d = dtMs / DurationMs;
        return hovered ? Math.Min(1, progress + d) : Math.Max(0, progress - d);
    }

    // ---- state

    /// <summary>Eased 0..1 zoom of item <paramref name="i"/>.</summary>
    public double Of(int i) => _progress.TryGetValue(i, out var p) ? Ease(p) : 0;

    public void SetHover(int index)
    {
        if (index == _target) return;
        _target = index;
        if (!Motion.Enabled)
        {
            Stop();
            _progress.Clear();
            if (index >= 0) _progress[index] = 1;
            _owner.InvalidateVisual();
            return;
        }
        Start();
    }

    /// <summary>Snapshot hook: item <paramref name="index"/> at full zoom (others at rest), no animation.</summary>
    public void SetFull(int index)
    {
        Stop();
        _target = index;
        _progress.Clear();
        if (index >= 0) _progress[index] = 1;
        _owner.InvalidateVisual();
    }

    private bool Moving => _progress.Any(kv => kv.Key == _target ? kv.Value < 1 : kv.Value > 0) || (_target >= 0 && !_progress.ContainsKey(_target));

    private void Start()
    {
        if (_subscribed || !_owner.IsLoaded) { if (!_owner.IsLoaded) { SetFull(_target); } return; }
        _clock.Restart(); _last = 0; _subscribed = true;
        CompositionTarget.Rendering += OnFrame;
    }

    private void Stop()
    {
        if (!_subscribed) return;
        CompositionTarget.Rendering -= OnFrame;
        _subscribed = false; _clock.Stop();
    }

    private void OnFrame(object? sender, EventArgs e)
    {
        long now = _clock.ElapsedMilliseconds;
        double dt = Math.Min(50, now - _last);
        _last = now;
        if (_target >= 0 && !_progress.ContainsKey(_target)) _progress[_target] = 0;
        foreach (var k in _progress.Keys.ToList())
        {
            double p = Step(_progress[k], k == _target, dt);
            if (p <= 0 && k != _target) _progress.Remove(k); else _progress[k] = p;
        }
        _owner.InvalidateVisual();
        if (!Moving) Stop();
    }
}
