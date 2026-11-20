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
    /// <summary>
    /// Parts held back because another part still on order is made from them, anywhere down its tree of parts ->
    /// sub-parts -> components -> ore (Lone 2026-11-20: Assemblers making Heavy-Duty Plate take the Steel Plate ordered
    /// for blocks from storage, and the game asks its Smelters only for what storage lacks, so the blocks end up short;
    /// a sub-part the game makes for an ordered part takes its inputs from storage the same way). A part is handed out
    /// only when no ordered part made from it is still waiting or in a crafter. Returns held part -> the ordered part it
    /// waits for (the first one, by id).
    /// inputsOf: per part, the parts its recipe takes (ordered or not, as deep as known). incoming: parts still
    /// waiting or queued.
    /// </summary>
    public static Dictionary<string, string> Held(IReadOnlyDictionary<string, IReadOnlyCollection<string>> inputsOf, IEnumerable<string> incoming)
    {
        var ordered = incoming.Distinct().OrderBy(i => i, StringComparer.Ordinal).ToList();
        var held = new Dictionary<string, string>();
        foreach (var user in ordered)
        {
            // Everything this part is made from, all levels down.
            var below = new HashSet<string>();
            var open = new Stack<string>(new[] { user });
            while (open.Count > 0)
                if (inputsOf.TryGetValue(open.Pop(), out var inputs))
                    foreach (var input in inputs)
                        if (input != user && below.Add(input)) open.Push(input);
            foreach (var part in ordered.Where(below.Contains))
                if (!held.ContainsKey(part)) held[part] = user;
        }
        return held;
    }

    /// <summary>
    /// Most crafts one crafter gets in one batch (settings: batch size, default 10). Small batches keep every crafter busy
    /// until the part is finished.
    /// </summary>
    public static int MaxBatchCrafts => PlannerSettings.Current.BatchCrafts;

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
