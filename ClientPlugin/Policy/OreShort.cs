using System;
using System.Collections.Generic;
using System.Linq;

namespace ClientPlugin.Policy;

// Pure logic: no game, Harmony or Avalonia types here (compiled into tests/).

/// <summary>
/// K2: the ore the running orders still lack. When parts of an order stall, the player is told which ore is short and
/// by how much (Lone 2026-11-20: "Running orders need X more Iron"), not only that nothing moves.
/// </summary>
public static class OreShort
{
    /// <summary>At most this many items are named; the rest is counted as "+N".</summary>
    public const int MaxShown = 3;

    /// <summary>
    /// The raw items (ore) that the given running parts still need beyond what storage holds: the recipe tree is
    /// walked down to the raw items. Stock of the running parts themselves is left out (they are made regardless of
    /// stock, the same rule as the ore deficit of a new order, see PlanReport.Build). Empty when nothing is short.
    /// </summary>
    public static Dictionary<string, long> Of(
        IReadOnlyDictionary<string, long> running, IReadOnlyDictionary<string, long> stock, IReadOnlyDictionary<string, Recipe> recipes)
    {
        var usable = stock.Where(s => !running.ContainsKey(s.Key)).ToDictionary(s => s.Key, s => s.Value);
        return RecipeTree.Resolve(running, usable, recipes).RawShortage
            .Where(s => s.Value > 0).ToDictionary(s => s.Key, s => s.Value);
    }

    /// <summary>
    /// The largest shortages first, at most MaxShown, then " (+N)" for the rest. entry makes the text of one item
    /// from its name and amount (the HUD and the window word it differently). "" when nothing is short.
    /// </summary>
    public static string Format(IReadOnlyDictionary<string, long> shortage, Func<string, string> nameOf, Func<string, long, string> entry)
    {
        var list = shortage.Where(s => s.Value > 0)
            .OrderByDescending(s => s.Value).ThenBy(s => nameOf(s.Key), StringComparer.OrdinalIgnoreCase).ToList();
        if (list.Count == 0) return "";
        var text = string.Join(", ", list.Take(MaxShown).Select(s => entry(nameOf(s.Key), s.Value)));
        return list.Count > MaxShown ? $"{text} (+{list.Count - MaxShown})" : text;
    }
}
