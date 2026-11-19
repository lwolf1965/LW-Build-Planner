using System;
using System.Collections.Generic;
using System.Linq;

namespace ClientPlugin.Policy;

// Pure logic: no game, Harmony or Avalonia types here (compiled into tests/).

/// <summary>One line of the build list as an order sees it: Count identical blocks, each needing Parts.</summary>
public sealed class BlockLine
{
    /// <summary>The list line's key, as text (the game layer maps it back to its line).</summary>
    public string Key;
    public string Name;
    public int Count;
    /// <summary>The blueprint this line belongs to, or null for a block added by hand.</summary>
    public string Project;
    /// <summary>Parts one block needs (part id -> pieces).</summary>
    public IReadOnlyDictionary<string, long> Parts = new Dictionary<string, long>();
    /// <summary>
    /// How hard one block is to make: the raw material (ore) it costs from scratch. Stock goes to the most complex
    /// blocks first ("big removal of blocks from the list ... maybe more complex blocks first", Lone 2026-11-18).
    /// </summary>
    public double Complexity;

    /// <summary>Pieces of all parts of one block: "smallest block first" goes by this.</summary>
    public long Size => Parts.Values.Sum();
}

/// <summary>A run of identical blocks in the hand-out order.</summary>
public sealed class BlockGroup
{
    public string Key;
    public string Name;
    public int Count;
    public IReadOnlyDictionary<string, long> Parts = new Dictionary<string, long>();
}

/// <summary>What an order does with the build list (BlockSets.Plan).</summary>
public sealed class BlockPlan
{
    /// <summary>Blocks whose whole part set is in storage already: they leave the list at once (key -> blocks).</summary>
    public readonly Dictionary<string, int> FromStock = new();
    /// <summary>Blocks to make, in hand-out order (smallest block first).</summary>
    public readonly List<BlockGroup> Craft = new();
    /// <summary>Blocks that do not fit the ore: they stay in the list (key -> blocks).</summary>
    public readonly Dictionary<string, int> Waiting = new();
    /// <summary>Ore the waiting blocks still lack (what the whole list would need on top), ore id -> amount.</summary>
    public readonly Dictionary<string, long> WaitShortage = new();
    /// <summary>Parts the crafters have to make for the Craft blocks.</summary>
    public readonly Dictionary<string, long> Make = new();
    /// <summary>
    /// Parts of the Craft blocks that storage already holds (a block covered only in part): they count as handed out
    /// from the start, so the first blocks of the hand-out order are done sooner.
    /// </summary>
    public readonly Dictionary<string, long> Credit = new();
    /// <summary>Parts taken from storage for the FromStock blocks.</summary>
    public readonly Dictionary<string, long> StockUsed = new();
    /// <summary>
    /// Blueprints that wait as a whole because their ore is short (none of their blocks is ordered, not even from
    /// storage): blueprint -> ore it still lacks on top of this order (ore id -> amount).
    /// </summary>
    public readonly Dictionary<string, Dictionary<string, long>> ProjectWaiting = new();

    public int CraftBlocks => Craft.Sum(g => g.Count);
    public int StockBlocks => FromStock.Values.Sum();
    public int WaitingBlocks => Waiting.Values.Sum();
}

/// <summary>
/// M3, "whole blocks" (agreed with Lone 2026-11-18/19, see the M3 design note):
///  - an order never starts part of a block's set: a block is either covered, made whole, or waits;
///  - "Order missing": storage first covers whole blocks, most complex first; those leave the list at once;
///  - the rest is made smallest block first; when the ore is short only the blocks that fit are made, the others wait;
///  - a block is done when all its parts have been handed to crafters (queued), in hand-out order.
/// </summary>
public static class BlockSets
{
    private static void AddTo(Dictionary<string, long> into, string key, long amount)
    {
        if (amount == 0) return;
        into[key] = (into.TryGetValue(key, out var have) ? have : 0) + amount;
    }

    /// <summary>
    /// Plans an order of the whole list, blueprints all or nothing (Lone 2026-11-19: half a ship built in space floats
    /// and turns): with "Order missing", a blueprint whose ore is short is not ordered at all, so its ore and storage
    /// go to the other lines; buildAnyway names the blueprints the player wants made as far as the ore goes.
    /// </summary>
    public static BlockPlan Plan(IReadOnlyList<BlockLine> lines, IReadOnlyDictionary<string, long> available,
        IReadOnlyDictionary<string, long> treeStock, IReadOnlyDictionary<string, Recipe> recipes, Func<string, bool> isOre,
        bool onlyMissing, IReadOnlyCollection<string> buildAnyway)
    {
        var held = new HashSet<string>();
        while (true)
        {
            var plan = PlanLines(lines.Where(l => l.Project == null || !held.Contains(l.Project)).ToList(),
                available, treeStock, recipes, isOre, onlyMissing);
            var shortNow = lines.Where(l => l.Project != null && !held.Contains(l.Project)
                                            && !(buildAnyway?.Contains(l.Project) ?? false) && plan.Waiting.ContainsKey(l.Key))
                .Select(l => l.Project).Distinct().ToList();
            if (shortNow.Count > 0)
            {
                // Held blueprints free their ore and storage: plan again without them (more for the rest, never less).
                held.UnionWith(shortNow);
                continue;
            }
            foreach (var project in held)
            {
                var mine = lines.Where(l => l.Project == project && l.Count > 0).ToList();
                foreach (var line in mine) plan.Waiting[line.Key] = line.Count;
                // What it lacks on top of this order: the whole blueprint made new after the chosen blocks.
                var ordered = plan.Craft.Select(g => (lines.First(l => l.Key == g.Key), g.Count));
                var tree = Resolve(ordered.Concat(mine.Select(l => (l, l.Count))), Leftover(available, plan), treeStock, recipes);
                plan.ProjectWaiting[project] = tree.RawShortage.Where(r => r.Value > 0 && isOre(r.Key)).ToDictionary(r => r.Key, r => r.Value);
            }
            return plan;
        }
    }

    /// <summary>Parts the order leaves in storage (available minus the parts covered blocks took).</summary>
    private static Dictionary<string, long> Leftover(IReadOnlyDictionary<string, long> available, BlockPlan plan)
    {
        var left = new Dictionary<string, long>();
        foreach (var pair in available)
        {
            var rest = pair.Value - (plan.StockUsed.TryGetValue(pair.Key, out var used) ? used : 0);
            if (rest > 0) left[pair.Key] = rest;
        }
        return left;
    }

    /// <summary>
    /// Plans an order of these lines (blueprints not treated as a whole).
    /// available: parts storage holds for the list (stock + on its way - kept for blocks handed out earlier).
    /// treeStock: stock the recipe tree may use for making parts (ingots, ore, sub-parts; the list's parts at 0).
    /// onlyMissing: "Order missing" (cover from storage, fit the ore); false: "Order all" (make every block new; the
    /// caller blocks it when the ore is short).
    /// </summary>
    private static BlockPlan PlanLines(IReadOnlyList<BlockLine> lines, IReadOnlyDictionary<string, long> available,
        IReadOnlyDictionary<string, long> treeStock, IReadOnlyDictionary<string, Recipe> recipes, Func<string, bool> isOre,
        bool onlyMissing)
    {
        var plan = new BlockPlan();
        var avail = new Dictionary<string, long>();
        if (onlyMissing)
            foreach (var pair in available)
                if (pair.Value > 0) avail[pair.Key] = pair.Value;
        var rest = lines.Where(l => l.Count > 0).ToDictionary(l => l.Key, l => l.Count);

        // 1. Storage covers whole blocks, most complex first.
        if (onlyMissing)
            foreach (var line in lines.Where(l => l.Count > 0 && l.Parts.Count > 0)
                         .OrderByDescending(l => l.Complexity).ThenByDescending(l => l.Size).ThenBy(l => l.Key, StringComparer.Ordinal))
            {
                var whole = line.Parts.Where(p => p.Value > 0)
                    .Select(p => (avail.TryGetValue(p.Key, out var a) ? a : 0) / p.Value).DefaultIfEmpty(0).Min();
                var covered = (int)Math.Min(rest[line.Key], whole);
                if (covered <= 0) continue;
                plan.FromStock[line.Key] = covered;
                rest[line.Key] -= covered;
                foreach (var part in line.Parts)
                {
                    avail[part.Key] -= part.Value * covered;
                    AddTo(plan.StockUsed, part.Key, part.Value * covered);
                }
            }

        // 2. The rest is made, smallest block first. With "Order missing" only the blocks the ore allows.
        var order = lines.Where(l => rest.TryGetValue(l.Key, out var n) && n > 0)
            .OrderBy(l => l.Size).ThenBy(l => l.Key, StringComparer.Ordinal).ToList();
        var chosen = new List<(BlockLine Line, int Count)>();
        foreach (var line in order)
        {
            var count = rest[line.Key];
            var fit = onlyMissing ? LargestFit(chosen, line, count, avail, treeStock, recipes, isOre) : count;
            if (fit > 0) chosen.Add((line, fit));
            if (fit < count) plan.Waiting[line.Key] = count - fit;
        }

        foreach (var (line, count) in chosen)
            plan.Craft.Add(new BlockGroup { Key = line.Key, Name = line.Name, Count = count, Parts = line.Parts });
        var parts = Sum(chosen);
        foreach (var part in parts)
        {
            var fromStore = Math.Min(part.Value, avail.TryGetValue(part.Key, out var a) ? a : 0);
            if (fromStore > 0) plan.Credit[part.Key] = fromStore;
            if (part.Value - fromStore > 0) plan.Make[part.Key] = part.Value - fromStore;
        }

        if (plan.Waiting.Count > 0)
        {
            // What mining would let every block be made: the shortage of the whole rest of the list.
            var all = order.Select(l => (l, rest[l.Key])).ToList();
            foreach (var raw in Resolve(all, avail, treeStock, recipes).RawShortage)
                if (raw.Value > 0 && isOre(raw.Key)) plan.WaitShortage[raw.Key] = raw.Value;
        }
        return plan;
    }

    private static Dictionary<string, long> Sum(IEnumerable<(BlockLine Line, int Count)> blocks)
    {
        var sum = new Dictionary<string, long>();
        foreach (var (line, count) in blocks)
            foreach (var part in line.Parts) AddTo(sum, part.Key, part.Value * count);
        return sum;
    }

    private static TreeResult Resolve(IEnumerable<(BlockLine Line, int Count)> blocks, IReadOnlyDictionary<string, long> avail,
        IReadOnlyDictionary<string, long> treeStock, IReadOnlyDictionary<string, Recipe> recipes)
    {
        var make = new Dictionary<string, long>();
        foreach (var part in Sum(blocks))
        {
            var left = part.Value - (avail.TryGetValue(part.Key, out var a) ? a : 0);
            if (left > 0) make[part.Key] = left;
        }
        return RecipeTree.Resolve(make, treeStock, recipes);
    }

    /// <summary>
    /// The most blocks of this line (up to count) that the ore allows on top of the blocks already chosen. More blocks
    /// never need less ore, so a binary search finds it with a few recipe walks even for 1,000 armor blocks.
    /// Only ore counts here: a part no crafter can make blocks the whole order elsewhere (alerts).
    /// </summary>
    private static int LargestFit(List<(BlockLine Line, int Count)> chosen, BlockLine line, int count,
        IReadOnlyDictionary<string, long> avail, IReadOnlyDictionary<string, long> treeStock,
        IReadOnlyDictionary<string, Recipe> recipes, Func<string, bool> isOre)
    {
        bool Fits(int n)
        {
            if (n <= 0) return true;
            var tree = Resolve(chosen.Append((line, n)), avail, treeStock, recipes);
            return !tree.RawShortage.Any(r => r.Value > 0 && isOre(r.Key));
        }
        if (Fits(count)) return count;
        int low = 0, high = count - 1; // low fits, count does not
        while (low < high)
        {
            var mid = low + (high - low + 1) / 2;
            if (Fits(mid)) low = mid;
            else high = mid - 1;
        }
        return low;
    }

    /// <summary>
    /// How many blocks of each group are done, going through the blocks in hand-out order: a block is done when the
    /// parts handed out (queued in crafters, plus the Credit from storage) cover it and every block before it. Returns
    /// one count per group, in the groups' order.
    /// </summary>
    public static int[] Completed(IReadOnlyList<BlockGroup> groups, IReadOnlyDictionary<string, long> handedOut)
    {
        var done = new int[groups.Count];
        var used = new Dictionary<string, long>();
        for (var g = 0; g < groups.Count; g++)
        {
            var group = groups[g];
            long whole = group.Count;
            foreach (var part in group.Parts.Where(p => p.Value > 0))
            {
                var left = (handedOut.TryGetValue(part.Key, out var h) ? h : 0) - (used.TryGetValue(part.Key, out var u) ? u : 0);
                whole = Math.Min(whole, Math.Max(0, left) / part.Value);
            }
            done[g] = (int)whole;
            foreach (var part in group.Parts) AddTo(used, part.Key, part.Value * whole);
            if (whole < group.Count) break; // the next blocks wait for this one
        }
        return done;
    }

    /// <summary>
    /// The parts still to hand out, in the order the blocks need them: a part comes before another when the first block
    /// it is still missing for comes earlier in the hand-out order. The crafters get the parts in this order, so the
    /// first blocks are done first. Parts already handed out in full are left out.
    /// </summary>
    public static List<string> PartOrder(IReadOnlyList<BlockGroup> groups, IReadOnlyDictionary<string, long> handedOut)
    {
        var firstShort = new Dictionary<string, long>();
        var cumulative = new Dictionary<string, long>();
        long position = 0;
        foreach (var group in groups)
        {
            foreach (var part in group.Parts.Where(p => p.Value > 0))
            {
                if (firstShort.ContainsKey(part.Key)) continue;
                var before = cumulative.TryGetValue(part.Key, out var c) ? c : 0;
                var have = handedOut.TryGetValue(part.Key, out var h) ? h : 0;
                // The first block of this group that still lacks the part: (have - before) / per blocks are covered.
                var coveredHere = Math.Max(0, have - before) / part.Value;
                if (coveredHere < group.Count) firstShort[part.Key] = position + coveredHere;
            }
            foreach (var part in group.Parts) AddTo(cumulative, part.Key, part.Value * group.Count);
            position += group.Count;
        }
        return firstShort.OrderBy(p => p.Value).ThenBy(p => p.Key, StringComparer.Ordinal).Select(p => p.Key).ToList();
    }
}
