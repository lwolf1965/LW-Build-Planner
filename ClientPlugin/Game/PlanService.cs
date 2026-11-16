using System;
using System.Collections.Generic;
using System.Linq;
using ClientPlugin.Policy;
using Keen.Game2.Simulation.WorldObjects.Items;

namespace ClientPlugin.Game;

/// <summary>One part of the checked plan, for the planner screen.</summary>
internal sealed class PartLine
{
    public string Item;
    public long Need;
    /// <summary>In reachable storage now.</summary>
    public long Stock;
    /// <summary>Already ordered and on its way.</summary>
    public long Incoming;
    /// <summary>Need - storage - on order, never below 0. What "order missing" would make.</summary>
    public long Missing;
}

/// <summary>Result of the last check; replaced as a whole.</summary>
internal sealed class PlanView
{
    public string Grid = "";
    public bool CanBuild;
    public List<PartLine> Parts = new();
    public List<PlanAlert> Alerts = new();
    public DateTime Time = DateTime.Now;
}

/// <summary>What a check should do after counting.</summary>
internal enum OrderMode
{
    /// <summary>Only count and show.</summary>
    None,
    /// <summary>Make every part of the build list, whatever is already there.</summary>
    All,
    /// <summary>Make only what storage and running orders do not cover.</summary>
    Missing,
}

/// <summary>
/// Check and order, on the game thread (always through PlannerEngine.Enqueue).
/// One grid only: the grid the running orders are on, else the grid with the most usable crafters.
/// </summary>
internal static class PlanService
{
    /// <summary>Last check, for the screen. Null before the first check.</summary>
    public static volatile PlanView LastView;

    public static void Check(PlannerContext context, OrderMode mode)
    {
        var plan = PlanStore.Snapshot();
        var wanted = new Dictionary<ItemDefinition, long>();
        foreach (var entry in plan)
            foreach (var amount in entry.Block.Items)
            {
                wanted.TryGetValue(amount.Item, out var have);
                wanted[amount.Item] = have + (long)Math.Ceiling(Fx.ToDouble(amount.Amount)) * entry.Count;
            }

        var grids = StockScan.Run(context);
        GridStock grid = null;
        if (OrderBook.Grid != null) grids.TryGetValue(OrderBook.Grid, out grid);
        grid ??= grids.Values.Where(g => g.Makers.Count > 0).OrderByDescending(g => g.Makers.Count).FirstOrDefault();
        if (grid == null)
        {
            LastView = new PlanView { Alerts = { new PlanAlert { Level = AlertLevel.Error, Text = "No crafter found on any grid you own." } } };
            if (mode != OrderMode.None) PlanStore.Say("Build Planner: no crafter found on any grid you own");
            return;
        }

        // The window always shows the check for "order all" (the bigger order); "order missing" is checked when pressed.
        var ledger = OrderBook.LedgerForCheck;
        var report = PlanReport.Build(grid, wanted, ledger, OrderBook.ItemsForCheck,
            grids.Values.Where(g => !ReferenceEquals(g, grid)).ToList(), mode == OrderMode.Missing);
        var view = new PlanView { CanBuild = report.CanBuild, Alerts = report.Alerts };
        foreach (var want in report.Wanted.OrderBy(w => report.NameOf(w.Key), StringComparer.OrdinalIgnoreCase))
        {
            var stock = report.Stock.TryGetValue(want.Key, out var stored) ? stored : 0;
            view.Parts.Add(new PartLine
            {
                Item = report.NameOf(want.Key), Need = want.Value, Stock = stock,
                Incoming = ledger.Incoming(want.Key),
                Missing = ledger.Missing(want.Key, want.Value, stock),
            });
        }
        LastView = view;

        PluginLog.Info($"Plan check ({mode}): {plan.Count} list line(s), can build = {report.CanBuild}, "
                       + string.Join(", ", view.Parts.Select(p => $"{p.Item} need {p.Need} storage {p.Stock} on order {p.Incoming} missing {p.Missing}")));
        foreach (var alert in report.Alerts)
            PluginLog.Info($"Plan alert ({alert.Level}): {alert.Text}");
        if (mode == OrderMode.None) return;

        if (plan.Count == 0)
        {
            PlanStore.Say("Build Planner: the build list is empty");
            return;
        }
        if (report.Need.Count == 0)
        {
            // Only possible with "order missing": tell the player instead of silently doing nothing.
            PlanStore.Say("Build Planner: you already have every part for this list, nothing ordered. 'Order all' makes them anyway.");
            return;
        }
        if (!report.CanBuild)
        {
            var errors = report.Alerts.Where(a => a.Level == AlertLevel.Error).ToList();
            PlanStore.Say($"Build Planner: cannot order, {errors.Count} problem(s)");
            foreach (var error in errors.Take(2)) PlanStore.Say(error.Text);
            return;
        }

        var made = string.Join(", ", report.Need.OrderBy(n => report.NameOf(n.Key)).Take(4).Select(n => $"{n.Value:N0} {report.NameOf(n.Key)}"));
        OrderBook.Commit(context, report);
        PlanStore.Clear(); // the list is now an order; the screen shows it under Orders
        PlanStore.Say($"Build Planner: ordered {made}{(report.Need.Count > 4 ? ", ..." : "")}");
        Check(context, OrderMode.None); // refresh the view
    }
}
