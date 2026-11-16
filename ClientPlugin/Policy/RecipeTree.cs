using System;
using System.Collections.Generic;
using System.Linq;

namespace ClientPlugin.Policy;

// Pure logic: no game, Harmony or Avalonia types here (compiled into tests/).

/// <summary>One recipe: the item it makes, how many per craft, the kind of crafter and the inputs per craft.
/// Machine is a plain name (Smelter, Refinery, Assembler, Fabricator ...); the game layer maps crafter blocks to it.</summary>
public sealed class Recipe
{
    public string Item;
    public int OutAmount = 1;
    public string Machine;
    public IReadOnlyDictionary<string, int> Inputs;
}

/// <summary>Crafts to queue for one item, in the machine that makes it.</summary>
public sealed class CraftOrder
{
    public string Item;
    public string Machine;
    public int Crafts;
    /// <summary>Items this order produces (Crafts x OutAmount; may exceed the need by up to one craft).</summary>
    public long Amount;
    /// <summary>0 = made straight from raw materials; higher = needs parts made at lower depths.</summary>
    public int Depth;
    /// <summary>
    /// True for an item the player asked for (not just an input of one). Only these are queued: the game itself asks
    /// smaller crafters for the sub-parts (seen in game 2026-11-16: one Mainframe order made the whole chain run).
    /// </summary>
    public bool TopLevel;
}

public sealed class TreeResult
{
    /// <summary>Bottom-up: low depth first, so inputs are queued before the parts that use them.</summary>
    public readonly List<CraftOrder> Orders = new();
    /// <summary>Items nobody can craft (ore) that are still missing after stock: the player has to mine these.</summary>
    public readonly Dictionary<string, long> RawShortage = new();
    /// <summary>Every item the plan touches (wanted items and all their inputs), also those fully covered by stock.</summary>
    public readonly HashSet<string> Touched = new();
    /// <summary>Stock this plan uses up, per item (parts and raw). Lets a second plan see what the first one leaves.</summary>
    public readonly Dictionary<string, long> Consumed = new();
}

public static class RecipeTree
{
    /// <summary>0 for an item without recipe (raw), else 1 + the deepest input. Cycles count as raw.</summary>
    public static int Depth(string item, IReadOnlyDictionary<string, Recipe> recipes)
        => Depth(item, recipes, new Dictionary<string, int>(), new HashSet<string>());

    private static int Depth(string item, IReadOnlyDictionary<string, Recipe> recipes, Dictionary<string, int> memo, HashSet<string> visiting)
    {
        if (memo.TryGetValue(item, out var known)) return known;
        if (!recipes.TryGetValue(item, out var recipe) || !visiting.Add(item)) return 0;
        var depth = 1 + recipe.Inputs.Keys.Select(i => Depth(i, recipes, memo, visiting)).DefaultIfEmpty(0).Max();
        visiting.Remove(item);
        memo[item] = depth;
        return depth;
    }

    /// <summary>
    /// Walks the recipes from the wanted items down to raw materials. Highest depth first, so each item's total demand
    /// is known (all the parts above it have added theirs) before stock is subtracted: stock of an intermediate part
    /// is used once, then the rest is crafted. Raw items still missing are reported, never ordered.
    /// </summary>
    public static TreeResult Resolve(
        IReadOnlyDictionary<string, long> wanted, IReadOnlyDictionary<string, long> stock, IReadOnlyDictionary<string, Recipe> recipes)
    {
        var result = new TreeResult();
        var demand = new Dictionary<string, long>();
        foreach (var kv in wanted)
            if (kv.Value > 0) demand[kv.Key] = kv.Value;
        var stockLeft = new Dictionary<string, long>();
        foreach (var kv in stock) stockLeft[kv.Key] = kv.Value;
        var memo = new Dictionary<string, int>();
        var done = new HashSet<string>();

        while (true)
        {
            string next = null;
            var best = -1;
            foreach (var item in demand.Keys)
            {
                if (done.Contains(item)) continue;
                var depth = Depth(item, recipes, memo, new HashSet<string>());
                if (depth > best) { best = depth; next = item; }
            }
            if (next == null) break;
            done.Add(next);

            var missing = demand[next];
            if (stockLeft.TryGetValue(next, out var have))
            {
                var used = Math.Min(have, missing);
                stockLeft[next] = have - used;
                missing -= used;
                if (used > 0) result.Consumed[next] = used;
            }
            if (missing <= 0) continue;

            if (!recipes.TryGetValue(next, out var recipe) || best == 0)
            {
                result.RawShortage[next] = missing;
                continue;
            }

            var crafts = (int)Math.Ceiling(missing / (double)Math.Max(1, recipe.OutAmount));
            result.Orders.Add(new CraftOrder
            {
                Item = next, Machine = recipe.Machine, Crafts = crafts, Amount = (long)crafts * recipe.OutAmount, Depth = best,
                TopLevel = wanted.TryGetValue(next, out var asked) && asked > 0,
            });
            foreach (var input in recipe.Inputs)
                demand[input.Key] = (demand.TryGetValue(input.Key, out var d) ? d : 0) + (long)input.Value * crafts;
        }

        foreach (var item in demand.Keys) result.Touched.Add(item);
        result.Orders.Sort((a, b) => a.Depth != b.Depth ? a.Depth.CompareTo(b.Depth) : string.CompareOrdinal(a.Item, b.Item));
        return result;
    }
}
