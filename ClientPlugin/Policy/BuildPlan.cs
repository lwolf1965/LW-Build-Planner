using System;
using System.Collections.Generic;

namespace ClientPlugin.Policy;

// Pure logic: no game, Harmony or Avalonia types here (compiled into tests/).

/// <summary>One line of the build list: a block (by definition id) and how many of it.</summary>
public sealed class PlannedBlock
{
    public string BlockId;
    public int Count;
}

public sealed class ComponentLine
{
    public string ItemId;
    public long Need;
    public long Have;
    public long Missing => Math.Max(0, Need - Have);
}

public static class BuildPlan
{
    /// <summary>Adds count blocks to the list (merging same ids). Count may be negative to remove; never goes below zero.</summary>
    public static void Add(List<PlannedBlock> list, string blockId, int count)
    {
        var line = list.Find(b => b.BlockId == blockId);
        if (line == null)
        {
            if (count > 0) list.Add(new PlannedBlock { BlockId = blockId, Count = count });
            return;
        }
        line.Count = Math.Max(0, line.Count + count);
        if (line.Count == 0) list.Remove(line);
    }

    /// <summary>Total components for the list. costs: blockId to (itemId to amount per block).</summary>
    public static Dictionary<string, long> Need(
        IEnumerable<PlannedBlock> list, IReadOnlyDictionary<string, IReadOnlyDictionary<string, int>> costs)
    {
        var need = new Dictionary<string, long>();
        foreach (var b in list)
        {
            if (!costs.TryGetValue(b.BlockId, out var cost)) continue;
            foreach (var kv in cost)
                need[kv.Key] = (need.TryGetValue(kv.Key, out var n) ? n : 0) + (long)kv.Value * b.Count;
        }
        return need;
    }

    /// <summary>Have/need/missing per component. stock: itemId to amount in storage.</summary>
    public static List<ComponentLine> Compare(
        IReadOnlyDictionary<string, long> need, IReadOnlyDictionary<string, long> stock)
    {
        var lines = new List<ComponentLine>();
        foreach (var kv in need)
            lines.Add(new ComponentLine
            {
                ItemId = kv.Key,
                Need = kv.Value,
                Have = stock.TryGetValue(kv.Key, out var h) ? h : 0,
            });
        lines.Sort((a, b) => string.CompareOrdinal(a.ItemId, b.ItemId));
        return lines;
    }
}
