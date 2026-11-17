using System;
using System.Collections.Generic;
using System.Linq;

namespace ClientPlugin.Policy;

// Pure logic: no game, Harmony or Avalonia types here (compiled into tests/).

/// <summary>One storage inventory: capacity and fill by mass, NaN when the game would not tell.</summary>
public sealed class StoreMass
{
    /// <summary>Most the inventory holds (game mass unit).</summary>
    public double Max = double.NaN;
    /// <summary>What it holds now (game mass unit).</summary>
    public double Now = double.NaN;
    /// <summary>Sum of amount x MassPerUnit of its items (item mass unit); used to find the scale between the two units.</summary>
    public double Calc;
}

/// <summary>The numbers behind a cargo-space verdict, for the log and the tests.</summary>
public sealed class CargoCheck
{
    /// <summary>Storage inventories that gave usable numbers.</summary>
    public int Storages;
    /// <summary>Free capacity after the parts already on order are subtracted (never below 0).</summary>
    public double Free;
    public double Stored;
    public double Calc;
    /// <summary>Game mass unit per item mass unit (stored / sum of items); 1 when nothing is stored.</summary>
    public double Scale = 1;
    /// <summary>Error: does not fit. Warning: tight. null: fine, or not judged.</summary>
    public PlanAlert Alert;
}

/// <summary>
/// The storage limiter: do the new parts fit into the storage the crafters reach?
///  1. Only inventories that report both their maximum and their current mass are used.
///  2. Scale: the item mass unit may differ from the inventory unit, so it is measured from what is stored
///     (current mass / sum of amount x MassPerUnit). Nothing stored = scale 1 (measured 1 in game).
///  3. Free = sum of (max - current) over the inventories, minus the mass of parts already ordered but not arrived.
///  4. Need = amount x MassPerUnit x scale for every part of the order.
///  5. Need above free: Error (the order is blocked). Need above 80 % of free: Warning. Otherwise no alert.
/// No usable inventory, an empty order, or parts of unknown mass: no verdict (null / no alert), the order is never blocked on a guess.
/// </summary>
public static class CargoLimit
{
    /// <param name="stores">One entry per storage inventory.</param>
    /// <param name="toMake">Item id to the amount the new order makes.</param>
    /// <param name="incoming">Item id to the amount ordered earlier and not arrived yet (takes room too).</param>
    /// <param name="massPerUnit">Mass of one piece in the item unit (0 or less = unknown).</param>
    public static CargoCheck Check(IReadOnlyList<StoreMass> stores, IReadOnlyDictionary<string, long> toMake,
        IReadOnlyDictionary<string, long> incoming, Func<string, double> massPerUnit, Func<string, string> nameOf)
    {
        var usable = stores.Where(s => !double.IsNaN(s.Max) && !double.IsNaN(s.Now)).ToList();
        if (usable.Count == 0 || toMake.Count == 0) return null;

        var calc = usable.Sum(s => double.IsNaN(s.Calc) ? 0 : s.Calc);
        var now = usable.Sum(s => s.Now);
        var scale = calc > 0 && now > 0 ? now / calc : 1.0;
        var free = usable.Sum(s => Math.Max(0, s.Max - s.Now));

        double Mass(string id) => Math.Max(0, massPerUnit(id)) * scale;
        foreach (var pair in incoming) free -= Mass(pair.Key) * pair.Value;
        free = Math.Max(0, free);

        return new CargoCheck
        {
            Storages = usable.Count,
            Free = free,
            Stored = now,
            Calc = calc,
            Scale = scale,
            Alert = PlanAlerts.Space(toMake, Mass, free, nameOf),
        };
    }
}
