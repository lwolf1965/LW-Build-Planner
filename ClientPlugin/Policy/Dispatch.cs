using System;
using System.Collections.Generic;
using System.Linq;

namespace ClientPlugin.Policy;

// Pure logic: no game, Harmony or Avalonia types here (compiled into tests/).

/// <summary>A part still waiting for a crafter.</summary>
public sealed class DispatchWant
{
    public string Item;
    /// <summary>Pieces not queued yet.</summary>
    public long Waiting;
    /// <summary>Pieces one craft makes (below 1 counts as 1).</summary>
    public double PerCraft = 1;
    /// <summary>Crafters (ids) that can make it.</summary>
    public IReadOnlyCollection<int> Able = Array.Empty<int>();
}

/// <summary>One batch for one crafter.</summary>
public sealed class DispatchJob
{
    public int Crafter;
    public string Item;
    public int Crafts;
}

/// <summary>
/// Hands out the waiting parts in batches, part by part in the order they were ordered. More crafters of a kind
/// make a big build faster: every free crafter gets its own batch, and as soon as a crafter is free again it gets
/// the next batch of the oldest part still waiting.
/// </summary>
public static class Dispatch
{
    /// <summary>Most crafts one crafter gets in one batch. Small batches keep every crafter busy until the part is finished.</summary>
    public const int MaxBatchCrafts = 10;

    /// <summary>
    /// May a crafter get a new batch? Not while it crafts. Not while the planner still counts a batch in it (it may wait for
    /// sub-parts), unless it has been idle for reclaimSeconds: then the batch was lost or finished unnoticed.
    /// </summary>
    public static bool IsFree(bool crafting, long queuedHere, double idleSeconds, double reclaimSeconds)
        => !crafting && (queuedHere <= 0 || idleSeconds >= reclaimSeconds);

    /// <summary>
    /// wants: oldest order first. free: the crafters that may get a batch now. Each want takes free crafters that can make it:
    /// the crafts are spread evenly over them, no crafter above maxBatch crafts; crafters that got a batch are not free for
    /// the next want. Parts left over wait for the next pass.
    /// </summary>
    public static List<DispatchJob> Plan(IReadOnlyList<DispatchWant> wants, IReadOnlyList<int> free, int maxBatch)
    {
        var jobs = new List<DispatchJob>();
        var remaining = free.Distinct().OrderBy(id => id).ToList();
        if (maxBatch <= 0) return jobs;
        foreach (var want in wants)
        {
            if (remaining.Count == 0) break;
            if (want.Waiting <= 0) continue;
            var perCraft = want.PerCraft < 1 ? 1 : want.PerCraft;
            var crafts = (int)Math.Min(int.MaxValue, Math.Ceiling(want.Waiting / perCraft));
            var able = remaining.Where(id => want.Able.Contains(id)).ToList();
            if (able.Count == 0 || crafts <= 0) continue;

            int[] share;
            if (crafts >= (long)able.Count * maxBatch)
                share = Enumerable.Repeat(maxBatch, able.Count).ToArray();
            else
            {
                var used = Math.Min(able.Count, crafts);
                share = new int[used];
                for (var i = 0; i < used; i++) share[i] = crafts / used + (i < crafts % used ? 1 : 0);
            }
            for (var i = 0; i < share.Length; i++)
            {
                jobs.Add(new DispatchJob { Crafter = able[i], Item = want.Item, Crafts = share[i] });
                remaining.Remove(able[i]);
            }
        }
        return jobs;
    }
}
