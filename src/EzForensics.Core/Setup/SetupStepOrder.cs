using System.Collections.ObjectModel;
using EzForensics.Core.Common;

namespace EzForensics.Core.Setup;

/// <summary>
/// Keeps the wizard's install list in its live order: the running step first, then the queued steps in run sequence, then the
/// finished ones (Done, Failed, Skipped, Cancelled, AlreadyInstalled, WaitingForRestart, ActionNeeded) in the order they finished,
/// the newest at the very bottom. Only <see cref="ObservableCollection{T}.Move"/> is used, so the list is never rebuilt.
/// Call <see cref="Reset"/> after the collection was filled with a new plan and <see cref="Update"/> on the UI thread after a
/// step's state changed. A thin adapter over the shared <see cref="RunOrder"/> helper (the same live order as Ingest and the Jobs list).
/// </summary>
public sealed class SetupStepOrder
{
    private readonly ObservableCollection<SetupStep> _steps;
    private readonly Dictionary<SetupStep, int> _sequence = new(ReferenceEqualityComparer.Instance);
    private readonly Dictionary<SetupStep, long> _finishedAt = new(ReferenceEqualityComparer.Instance);

    public SetupStepOrder(ObservableCollection<SetupStep> steps)
    {
        _steps = steps;
        Reset();
    }

    /// <summary>Running, queued or finished (RunOrder phase) of a step state.</summary>
    public static RunPhase Phase(SetupStepState s) => s switch
    {
        SetupStepState.Running => RunPhase.Running,
        SetupStepState.Queued => RunPhase.Queued,
        _ => RunPhase.Finished,
    };

    /// <summary>
    /// The collection holds a new plan, in run sequence: that order is remembered, and steps already finished (built in, already
    /// installed) count as finished first, in plan order. Nothing is moved.
    /// </summary>
    public void Reset()
    {
        _sequence.Clear();
        _finishedAt.Clear();
        for (int i = 0; i < _steps.Count; i++) _sequence[_steps[i]] = i;
        foreach (var s in _steps.Where(s => s.IsFinished)) _finishedAt[s] = RunOrder.NextStamp();
    }

    /// <summary>Records which steps finished since the last call (a retried step is queued again) and moves rows into the live order.</summary>
    public void Update()
    {
        foreach (var s in _steps.OrderBy(Seq))
        {
            if (!s.IsFinished) _finishedAt.Remove(s);
            else if (!_finishedAt.ContainsKey(s)) _finishedAt[s] = RunOrder.NextStamp();
        }
        RunOrder.Reorder(_steps, s => Phase(s.State), Seq, s => _finishedAt.TryGetValue(s, out var t) ? t : null);
    }

    private long Seq(SetupStep s) => _sequence.TryGetValue(s, out var i) ? i : long.MaxValue;
}
