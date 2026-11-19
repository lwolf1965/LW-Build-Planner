using System;
using System.Collections.Generic;
using System.Linq;
using ClientPlugin.Policy;
using Keen.Game2.Simulation.WorldObjects.CubeGrids;
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
    /// <summary>When "Order all" is blocked: what "Order missing" would do (null: not checked).</summary>
    public string MissingNote;
    public bool MissingOk;
    public DateTime Time = DateTime.Now;
}

/// <summary>A blueprint checked for the Blueprint Details screen (PlanService.Preview).</summary>
internal sealed class BlueprintCheck
{
    public bool CanBuild;
    /// <summary>Ore the whole blueprint needs from scratch, biggest first.</summary>
    public List<(string Name, long Amount)> OreCost = new();
    /// <summary>Raw materials the crafters here cannot make (e.g. ingots with no refinery), for the whole blueprint.</summary>
    public List<(string Name, long Amount)> NotMade = new();
    /// <summary>Ore that storage and running orders do not cover, biggest first.</summary>
    public List<(string Name, long Amount)> MissingOre = new();
    /// <summary>Ore missing for "Order all" (every part made new), biggest first.</summary>
    public List<(string Name, long Amount)> AllShort = new();
    public List<string> Errors = new();
    public List<string> Warnings = new();
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

    /// <summary>The parts the given lines need (block cost x count).</summary>
    private static Dictionary<ItemDefinition, long> Wanted(IEnumerable<PlannedEntry> lines)
    {
        var wanted = new Dictionary<ItemDefinition, long>();
        foreach (var entry in lines)
            foreach (var amount in entry.Block.Items)
            {
                wanted.TryGetValue(amount.Item, out var have);
                wanted[amount.Item] = have + (long)Math.Ceiling(Fx.ToDouble(amount.Amount)) * entry.Count;
            }
        return wanted;
    }

    /// <summary>The grid the orders go to: the one with running orders, else the one with the most crafters.</summary>
    private static GridStock OrderGrid(IReadOnlyDictionary<CubeGridComponent, GridStock> grids)
    {
        GridStock grid = null;
        if (OrderBook.Grid != null) grids.TryGetValue(OrderBook.Grid, out grid);
        return grid ?? grids.Values.Where(g => g.Makers.Count > 0).OrderByDescending(g => g.Makers.Count).FirstOrDefault();
    }

    /// <summary>
    /// Checks a blueprint that is not (or not yet) in the list, for the Build Planner box in the game's Blueprint
    /// Details screen: the ore the whole blueprint costs from scratch, and whether it can be built now ("order missing":
    /// what storage and running orders do not cover). The build list and the orders are not touched. Game thread.
    /// </summary>
    public static BlueprintCheck Preview(PlannerContext context, IReadOnlyCollection<PlannedEntry> lines)
    {
        var result = new BlueprintCheck();
        var wanted = Wanted(lines);
        var grids = StockScan.Run(context);
        var grid = OrderGrid(grids);
        if (grid == null)
        {
            result.Errors.Add(Tr.T("hud.noCrafterAny"));
            return result;
        }
        var report = PlanReport.Build(grid, wanted, OrderBook.LedgerForCheck, OrderBook.ItemsForCheck,
            grids.Values.Where(g => !ReferenceEquals(g, grid)).ToList(), onlyMissing: true);
        result.CanBuild = report.CanBuild;
        var full = RecipeTree.Resolve(report.Wanted, new Dictionary<string, long>(), report.Recipes);
        foreach (var raw in full.RawShortage.Where(r => r.Value > 0).OrderByDescending(r => r.Value))
            (report.IsOre(raw.Key) ? result.OreCost : result.NotMade).Add((report.NameOf(raw.Key), raw.Value));
        foreach (var raw in report.Tree.RawShortage.Where(r => r.Value > 0 && report.IsOre(r.Key)).OrderByDescending(r => r.Value))
            result.MissingOre.Add((report.NameOf(raw.Key), raw.Value));
        try
        {
            // "Order all": every part made new, storage used only as raw material (the window's "Problems for 'Order all'").
            var all = PlanReport.Build(grid, wanted, OrderBook.LedgerForCheck, OrderBook.ItemsForCheck,
                grids.Values.Where(g => !ReferenceEquals(g, grid)).ToList(), onlyMissing: false);
            foreach (var raw in all.Tree.RawShortage.Where(r => r.Value > 0 && all.IsOre(r.Key)).OrderByDescending(r => r.Value))
                result.AllShort.Add((all.NameOf(raw.Key), raw.Value));
        }
        catch (Exception e) { PluginLog.Warn("Blueprint check: the 'Order all' variant failed: " + e.Message); }
        result.Errors.AddRange(report.Alerts.Where(a => a.Level == AlertLevel.Error).Select(a => a.Text));
        result.Warnings.AddRange(report.Alerts.Where(a => a.Level == AlertLevel.Warning).Select(a => a.Text));
        PluginLog.Info($"Blueprint check: {lines.Sum(l => l.Count)} blocks, can build = {result.CanBuild}; ore for all: "
                       + string.Join(", ", result.OreCost.Select(o => $"{o.Name} {o.Amount:N0}")) + "; missing: "
                       + string.Join(", ", result.MissingOre.Select(o => $"{o.Name} {o.Amount:N0}")) + "; order all short: "
                       + string.Join(", ", result.AllShort.Select(o => $"{o.Name} {o.Amount:N0}")) + "; " + string.Join(" | ", result.Errors));
        return result;
    }

    /// <summary>
    /// "Order all" is blocked: says in the window whether "Order missing" (storage used first) would still work, as the
    /// Build Planner box in the Blueprint Details screen does.
    /// </summary>
    private static void NoteMissing(PlanView view, GridStock grid, Dictionary<CubeGridComponent, GridStock> grids,
        Dictionary<ItemDefinition, long> wanted, OrderLedger ledger)
    {
        try
        {
            var missing = PlanReport.Build(grid, wanted, ledger, OrderBook.ItemsForCheck,
                grids.Values.Where(g => !ReferenceEquals(g, grid)).ToList(), true);
            var firstError = missing.Alerts.FirstOrDefault(a => a.Level == AlertLevel.Error)?.Text;
            view.MissingOk = missing.Need.Count == 0 || missing.CanBuild;
            view.MissingNote = missing.Need.Count == 0 ? Tr.T("problems.missingNone")
                : missing.CanBuild ? Tr.T("problems.missingOk", missing.Need.Count)
                : Tr.T("problems.missingBlocked", firstError ?? "?");
            PluginLog.Info("Plan check (Missing, for the window): " + view.MissingNote);
        }
        catch (Exception e) { PluginLog.Warn("Plan check: the 'Order missing' note failed: " + e.Message); }
    }

    public static void Check(PlannerContext context, OrderMode mode)
    {
        var plan = PlanStore.Snapshot();
        var wanted = Wanted(plan);

        var grids = StockScan.Run(context);
        var grid = OrderGrid(grids);
        if (grid == null)
        {
            LastView = new PlanView { Alerts = { new PlanAlert { Level = AlertLevel.Error, Text = "No crafter found on any grid you own." } } };
            if (mode != OrderMode.None) PlanStore.Say(Tr.T("hud.noCrafterAny"));
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
        if (mode == OrderMode.None && !report.CanBuild && plan.Count > 0) NoteMissing(view, grid, grids, wanted, ledger);
        LastView = view;

        PluginLog.Info($"Plan check ({mode}): {plan.Count} list line(s), can build = {report.CanBuild}, "
                       + string.Join(", ", view.Parts.Select(p => $"{p.Item} need {p.Need} storage {p.Stock} on order {p.Incoming} missing {p.Missing}")));
        foreach (var alert in report.Alerts)
            PluginLog.Info($"Plan alert ({alert.Level}): {alert.Text}");
        if (mode == OrderMode.None) return;

        if (plan.Count == 0)
        {
            PlanStore.Say(Tr.T("hud.emptyList"));
            return;
        }
        if (report.Need.Count == 0)
        {
            // Only possible with "order missing": tell the player instead of silently doing nothing.
            PlanStore.Say(Tr.T("hud.haveAll"));
            return;
        }
        if (!report.CanBuild)
        {
            var errors = report.Alerts.Where(a => a.Level == AlertLevel.Error).ToList();
            PlanStore.Say(Tr.T("hud.cannotOrder", errors.Count));
            foreach (var error in errors.Take(2)) PlanStore.Say(error.Text);
            return;
        }

        var made = string.Join(", ", report.Need.OrderBy(n => report.NameOf(n.Key)).Take(4).Select(n => $"{n.Value:N0} {report.NameOf(n.Key)}"));
        OrderBook.Commit(context, report);
        // The list stays while the orders run (and is saved with the world): their parts count as "on order", after a
        // restart "Order missing" compares the list with storage again. When all orders are done the ordered blocks
        // leave the list (OrderBook.Process).
        PlanStore.MarkOrdered(plan);
        PlanStore.Say(Tr.T("hud.ordered", made + (report.Need.Count > 4 ? ", ..." : "")));
        Check(context, OrderMode.None); // refresh the view
    }
}
