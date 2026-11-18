using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;

namespace ClientPlugin.Policy;

// Pure logic: no game, Harmony or Avalonia types here (compiled into tests/).

public enum AlertLevel { Info, Warning, Error }

public sealed class PlanAlert
{
    public AlertLevel Level;
    public string Text;
}

/// <summary>What the game scan found about one grid, in plain numbers and names.</summary>
public sealed class PlanFacts
{
    /// <summary>Storage blocks on the main conveyor network.</summary>
    public int StorageCount;
    /// <summary>Machine name to the number of crafters that can be used: working, allowed (no [NoMake]) and linked.</summary>
    public readonly Dictionary<string, int> UsableCrafters = new();
    public readonly List<(string Name, string Machine)> UnlinkedCrafters = new();
    public readonly List<string> UnlinkedStorages = new();
    public int NonFunctionalCrafters;
    /// <summary>Item id to the amount lying in storage that has no conveyor link (only items the plan touches).</summary>
    public readonly Dictionary<string, long> StockInUnlinked = new();
}

public static class PlanAlerts
{
    /// <summary>Share of the free capacity above which the cargo check warns (settings: cargo warning, default 80 %).</summary>
    public static double TightShare => PlannerSettings.Current.CargoWarnPercent / 100.0;

    private static string N(long n) => n.ToString("N0", CultureInfo.InvariantCulture);

    /// <summary>The problems that stop or weaken a build, errors first. No Error means the build can go ahead.</summary>
    public static List<PlanAlert> Build(TreeResult tree, PlanFacts facts, Func<string, string> nameOf, Func<string, bool> isOre)
    {
        var alerts = new List<PlanAlert>();
        void Add(AlertLevel level, string text) => alerts.Add(new PlanAlert { Level = level, Text = text });
        if (tree.Orders.Count == 0 && tree.RawShortage.Count == 0) return alerts; // nothing to build

        if (facts.StorageCount == 0)
            Add(AlertLevel.Error, Tr.T("alert.noStorage"));

        foreach (var machine in tree.Orders.Select(o => o.Machine).Distinct().OrderBy(m => m, StringComparer.Ordinal))
        {
            if (facts.UsableCrafters.TryGetValue(machine, out var usable) && usable > 0) continue;
            var items = tree.Orders.Where(o => o.Machine == machine).Select(o => nameOf(o.Item)).ToList();
            var list = string.Join(", ", items.Take(4)) + (items.Count > 4 ? ", ..." : "");
            var unlinked = facts.UnlinkedCrafters.Where(c => c.Machine == machine).Select(c => c.Name).ToList();
            Add(AlertLevel.Error, unlinked.Count > 0
                ? Tr.T("alert.noReach", machine, string.Join(", ", unlinked), list)
                : Tr.T("alert.noMachine", machine, list));
        }

        foreach (var kv in tree.RawShortage.OrderBy(k => nameOf(k.Key), StringComparer.OrdinalIgnoreCase))
            Add(AlertLevel.Error, isOre(kv.Key)
                ? Tr.T("alert.ore", nameOf(kv.Key), N(kv.Value))
                : Tr.T("alert.short", nameOf(kv.Key), N(kv.Value)));

        foreach (var crafter in facts.UnlinkedCrafters)
            Add(AlertLevel.Warning, Tr.T("alert.crafterUnlinked", crafter.Name));
        foreach (var storage in facts.UnlinkedStorages)
            Add(AlertLevel.Warning, Tr.T("alert.storageUnlinked", storage));
        foreach (var kv in facts.StockInUnlinked.OrderBy(k => nameOf(k.Key), StringComparer.OrdinalIgnoreCase))
            Add(AlertLevel.Warning, Tr.T("alert.stranded", nameOf(kv.Key), N(kv.Value)));
        if (facts.NonFunctionalCrafters > 0)
            Add(AlertLevel.Warning, Tr.T("alert.broken", facts.NonFunctionalCrafters));

        return alerts.OrderByDescending(a => a.Level).ToList();
    }

    /// <summary>
    /// Do the new parts fit into the storage? toMake: item id to the amount an order makes. massOf: mass of one piece
    /// (0 or less = unknown, not counted). free: free capacity of the storage the crafters reach, in the same unit as
    /// massOf (below 0 or NaN = not known: no alert). The game limits inventories by mass, not by volume.
    /// Error when the parts do not fit, Warning when they use more than 80 % of the free capacity.
    /// </summary>
    public static PlanAlert Space(IReadOnlyDictionary<string, long> toMake, Func<string, double> massOf, double free, Func<string, string> nameOf)
    {
        if (double.IsNaN(free) || free < 0 || toMake.Count == 0) return null;
        var need = 0.0;
        string biggest = null;
        var biggestMass = 0.0;
        foreach (var kv in toMake)
        {
            var mass = Math.Max(0, massOf(kv.Key)) * kv.Value;
            need += mass;
            if (mass > biggestMass) { biggestMass = mass; biggest = kv.Key; }
        }
        if (need <= 0) return null;
        var take = Math.Ceiling(need).ToString("N0", CultureInfo.InvariantCulture);
        var room = Math.Floor(free).ToString("N0", CultureInfo.InvariantCulture);
        var most = biggest == null ? "" : Tr.T("space.heaviest", nameOf(biggest));
        if (need > free)
            return new PlanAlert { Level = AlertLevel.Error, Text = Tr.T("space.full", take, room, most) };
        if (need > TightShare * free)
            return new PlanAlert { Level = AlertLevel.Warning, Text = Tr.T("space.tight", take, room, most) };
        return null;
    }

    public static bool CanBuild(IEnumerable<PlanAlert> alerts) => alerts.All(a => a.Level != AlertLevel.Error);
}
