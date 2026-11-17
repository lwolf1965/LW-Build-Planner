using System;
using System.Collections.Generic;

namespace ClientPlugin.Policy;

/// <summary>
/// Which blocks of the build list the running orders cover. When all orders are done those blocks leave the list, so a
/// finished build cannot be ordered again by mistake; blocks added after the last order stay.
/// Pure code (keys are the list's line keys), tested in tests/.
/// </summary>
public static class OrderedBlocks
{
    /// <summary>
    /// An order was placed for the list as it is now: each line is covered up to its count. A second order on the same
    /// list (Order missing while orders run) does not count the line twice: the larger count is kept.
    /// </summary>
    public static void Mark<TKey>(Dictionary<TKey, int> ordered, IEnumerable<KeyValuePair<TKey, int>> list)
    {
        foreach (var (key, count) in list)
            if (count > 0) ordered[key] = Math.Max(ordered.TryGetValue(key, out var before) ? before : 0, count);
    }

    /// <summary>The list after all orders are done: every line minus what was ordered of it; lines at 0 are left out.</summary>
    public static Dictionary<TKey, int> Left<TKey>(IReadOnlyDictionary<TKey, int> list, IReadOnlyDictionary<TKey, int> ordered,
        IEqualityComparer<TKey> comparer = null)
    {
        var left = new Dictionary<TKey, int>(comparer);
        foreach (var (key, count) in list)
        {
            var now = count - (ordered.TryGetValue(key, out var made) ? made : 0);
            if (now > 0) left[key] = now;
        }
        return left;
    }
}
