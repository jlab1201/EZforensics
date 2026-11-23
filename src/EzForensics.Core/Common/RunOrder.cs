using System.Collections.ObjectModel;

namespace EzForensics.Core.Common;

/// <summary>Where an item stands in a run, for <see cref="RunOrder"/>.</summary>
public enum RunPhase
{
    /// <summary>Working now (top of the list).</summary>
    Running,
    /// <summary>Waiting to run (or ticked before a run), in run sequence.</summary>
    Queued,
    /// <summary>Done, failed, skipped, cancelled, already installed...: in the order they finished, the newest at the bottom.</summary>
    Finished,
    /// <summary>Not part of the run (unticked, greyed, unavailable): at the very bottom, in plan order.</summary>
    Inactive,
}

/// <summary>
/// The one live order of the Ingest planned jobs, the Jobs list and the setup wizard's install list: running, then queued (in run sequence),
/// then finished (in the order they finished), then inactive rows (in plan order). Pure; <see cref="Apply{T}"/> moves the items of an
/// <see cref="ObservableCollection{T}"/> into that order (never rebuilds it, so item state and selection survive).
/// </summary>
public static class RunOrder
{
    private static long _lastStamp;

    /// <summary>A strictly increasing stamp for "finished at" (time based, never equal to an earlier one).</summary>
    public static long NextStamp()
    {
        while (true)
        {
            var last = Interlocked.Read(ref _lastStamp);
            var next = Math.Max(last + 1, DateTime.UtcNow.Ticks);
            if (Interlocked.CompareExchange(ref _lastStamp, next, last) == last) return next;
        }
    }

    /// <summary>
    /// The target order of <paramref name="items"/>. <paramref name="sequence"/> is the run / plan sequence (queued and inactive items, and
    /// the tie-break); <paramref name="finishedAt"/> orders the finished items (null sorts first, by sequence). Stable for equal keys.
    /// </summary>
    public static List<T> Sort<T>(IEnumerable<T> items, Func<T, RunPhase> phase, Func<T, long> sequence, Func<T, long?> finishedAt)
    {
        return items.Select((item, index) => (item, index, phase: phase(item)))
            .OrderBy(t => (int)t.phase)
            .ThenBy(t => t.phase == RunPhase.Finished ? finishedAt(t.item) ?? long.MinValue : 0)
            .ThenBy(t => sequence(t.item))
            .ThenBy(t => t.index)
            .Select(t => t.item)
            .ToList();
    }

    /// <summary>Moves the items of <paramref name="target"/> into <paramref name="order"/> (same items) with the fewest Move calls; returns the number of moves.</summary>
    public static int Apply<T>(ObservableCollection<T> target, IReadOnlyList<T> order)
    {
        if (order.Count != target.Count) throw new ArgumentException("The order must hold exactly the items of the collection.", nameof(order));
        int moves = 0;
        for (int i = 0; i < order.Count; i++)
        {
            if (EqualityComparer<T>.Default.Equals(target[i], order[i])) continue;
            int from = -1;
            for (int j = i + 1; j < target.Count; j++)
                if (EqualityComparer<T>.Default.Equals(target[j], order[i])) { from = j; break; }
            if (from < 0) throw new ArgumentException("The order holds an item that is not in the collection.", nameof(order));
            target.Move(from, i);
            moves++;
        }
        return moves;
    }

    /// <summary><see cref="Sort{T}"/> then <see cref="Apply{T}"/> on <paramref name="target"/>.</summary>
    public static int Reorder<T>(ObservableCollection<T> target, Func<T, RunPhase> phase, Func<T, long> sequence, Func<T, long?> finishedAt) =>
        Apply(target, Sort(target, phase, sequence, finishedAt));
}
