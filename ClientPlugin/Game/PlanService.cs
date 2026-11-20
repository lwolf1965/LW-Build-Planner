using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
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
    /// <summary>M3: list lines (PlannedEntry.Key) with blocks that "Order missing" leaves waiting for ore, and how many.</summary>
    public Dictionary<object, int> WaitingLines = new(ReferenceEqualityComparer.Instance);
    /// <summary>M3: the blueprint waits as a whole for this ore ("Iron 64,209, Nickel 120"); null when it does not.</summary>
    public string ProjectShort;
    /// <summary>M3: blocks of that blueprint "Order what fits" would make now (0: none fits the ore yet).</summary>
    public int ProjectFits;
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

    private static int refreshQueued;

    /// <summary>
    /// The list changed (any thread): the window's check follows it. Quick changes (clicks on [-] / [+]) give one check,
    /// not one each: while a re-check waits in the queue no other is added, and it reads the list when it runs.
    /// Logged in one line (the full check is logged for the Check button, the window opening and every order).
    /// </summary>
    public static void Refresh()
    {
        if (Interlocked.Exchange(ref refreshQueued, 1) == 1) return;
        PlannerEngine.Enqueue(c =>
        {
            Interlocked.Exchange(ref refreshQueued, 0);
            Check(c, OrderMode.None, quiet: true);
        });
    }

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

    /// <summary>"Iron 64,209, Nickel 120": ore amounts, biggest first.</summary>
    private static string OreText(PlanReport report, IReadOnlyDictionary<string, long> ore)
        => string.Join(", ", ore.Where(o => o.Value > 0).OrderByDescending(o => o.Value).Select(o => $"{report.NameOf(o.Key)} {o.Value:N0}"));

    /// <summary>
    /// M3: what an order would do with the list, block by block (Policy/BlockSets): blocks already ordered and not done
    /// are left out and their parts kept for them (they are on order); everything else in storage counts.
    /// Returns the plan and its line keys ("L0", "L1" ...) to the list lines.
    /// </summary>
    private static (BlockPlan Plan, Dictionary<string, (PlannedEntry Entry, BlockLine Line)> Lines) BlockPlanFor(
        IReadOnlyList<PlannedEntry> plan, PlanReport report, OrderLedger ledger, bool onlyMissing, bool orderWhatFits)
    {
        var pending = OrderBook.PendingBlocks();
        var pendingParts = OrderBook.PendingParts();
        var lines = new Dictionary<string, (PlannedEntry Entry, BlockLine Line)>();
        foreach (var entry in plan)
        {
            var count = entry.Count - (pending.TryGetValue(entry.Key, out var ordered) ? ordered : 0);
            if (count <= 0) continue;
            var parts = new Dictionary<string, long>();
            foreach (var amount in entry.Block.Items)
            {
                var id = PlanReport.Id(amount.Item);
                parts[id] = (parts.TryGetValue(id, out var n) ? n : 0) + (long)Math.Ceiling(Fx.ToDouble(amount.Amount));
            }
            var key = "L" + lines.Count;
            lines[key] = (entry, new BlockLine
            {
                Key = key, Name = entry.Name, Count = count, Project = entry.Project ? "bp" : null, Parts = parts,
                // A started blueprint's next rounds: from ore only, storage parts are its earlier rounds' (Lone 2026-11-20).
                NoStock = entry.Project && PlanStore.ProjectStarted,
                // Complex = costs more ore from scratch: storage covers those blocks first.
                Complexity = RecipeTree.Resolve(parts, new Dictionary<string, long>(), report.Recipes).RawShortage.Values.Sum(),
            });
        }
        var available = new Dictionary<string, long>();
        foreach (var id in lines.Values.SelectMany(l => l.Line.Parts.Keys).Distinct())
        {
            // Everything on its way belongs to blocks already ordered (still in the list or already off it), so a
            // new block gets only storage, less what the ordered blocks still in the list need beyond what is on its
            // way (Lone 2026-11-20: "Order what fits (27)" with no new iron; the parts of blocks that had left the
            // list were counted as free).
            var onWay = ledger.Incoming(id);
            var free = ledger.Available(id, report.Stock.TryGetValue(id, out var stock) ? stock : 0)
                       - Math.Max(onWay, pendingParts.TryGetValue(id, out var kept) ? kept : 0);
            if (free > 0) available[id] = free;
        }
        var blocks = BlockSets.Plan(lines.Values.Select(l => l.Line).ToList(), available, report.TreeStock, report.Recipes,
            report.IsOre, onlyMissing, orderWhatFits ? new[] { "bp" } : null);
        return (blocks, lines);
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
        PlanReport.LogDetails = false; // the summary line below says what matters
        PlanReport report;
        try
        {
            report = PlanReport.Build(grid, wanted, OrderBook.LedgerForCheck, OrderBook.ItemsForCheck,
                grids.Values.Where(g => !ReferenceEquals(g, grid)).ToList(), onlyMissing: true);
        }
        finally { PlanReport.LogDetails = true; }
        result.CanBuild = report.CanBuild;
        var full = RecipeTree.Resolve(report.Wanted, new Dictionary<string, long>(), report.Recipes);
        try
        {
            var none = new Dictionary<string, long>();
            report.LogResources("blueprint from scratch", full, none);
            report.LogResources("blueprint with storage, 'Order missing'", report.Tree, report.TreeStock);
        }
        catch (Exception e) { PluginLog.Warn("Blueprint check: resource log failed: " + e.Message); }
        foreach (var raw in full.RawShortage.Where(r => r.Value > 0).OrderByDescending(r => r.Value))
            (report.IsOre(raw.Key) ? result.OreCost : result.NotMade).Add((report.NameOf(raw.Key), raw.Value));
        foreach (var raw in report.Tree.RawShortage.Where(r => r.Value > 0 && report.IsOre(r.Key)).OrderByDescending(r => r.Value))
            result.MissingOre.Add((report.NameOf(raw.Key), raw.Value));
        try
        {
            // "Order all": every part made new, storage used only as raw material (the window's "Problems for 'Order all'").
            PlanReport.LogDetails = false;
            PlanReport all;
            try
            {
                all = PlanReport.Build(grid, wanted, OrderBook.LedgerForCheck, OrderBook.ItemsForCheck,
                    grids.Values.Where(g => !ReferenceEquals(g, grid)).ToList(), onlyMissing: false);
            }
            finally { PlanReport.LogDetails = true; }
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
        Dictionary<ItemDefinition, long> wanted, OrderLedger ledger, bool quiet, BlockPlan blocks)
    {
        var details = PlanReport.LogDetails;
        PlanReport.LogDetails = false; // same stock as the main check, already logged
        try
        {
            var missing = PlanReport.Build(grid, wanted, ledger, OrderBook.ItemsForCheck,
                grids.Values.Where(g => !ReferenceEquals(g, grid)).ToList(), true);
            var firstError = missing.Alerts.FirstOrDefault(a => a.Level == AlertLevel.Error)?.Text;
            // A started blueprint's next rounds are made from ore only (BlockLine.NoStock): its ore shortage comes from
            // the block plan, not from the parts check that counts storage (Lone 2026-11-20: two different iron numbers).
            if (PlanStore.ProjectStarted && blocks != null && blocks.ProjectWaiting.TryGetValue("bp", out var bpOre) && bpOre.Count > 0)
            {
                var top = bpOre.OrderByDescending(o => o.Value).First();
                firstError = Tr.T("alert.ore", missing.NameOf(top.Key), top.Value.ToString("N0"));
            }
            // M3: short of ore only, "Order missing" still makes the whole blocks the ore allows.
            var oreOnly = !missing.CanBuild && missing.Alerts.Where(a => a.Level == AlertLevel.Error).All(a => a.Ore);
            var some = blocks != null && blocks.CraftBlocks + blocks.StockBlocks > 0;
            view.MissingOk = missing.Need.Count == 0 || missing.CanBuild || (oreOnly && some);
            view.MissingNote = missing.Need.Count == 0 ? Tr.T("problems.missingNone")
                : missing.CanBuild ? Tr.T("problems.missingOk", missing.Need.Count)
                : oreOnly && some ? Tr.T("problems.missingSome", blocks.CraftBlocks + blocks.StockBlocks, blocks.StockBlocks, blocks.WaitingBlocks)
                : Tr.T("problems.missingBlocked", firstError ?? "?");
            if (PlanStore.ProjectStarted && blocks != null && blocks.ProjectWaiting.ContainsKey("bp"))
            {
                // Started blueprint short of ore: storage parts do not count for it, so the parts check above does not
                // decide; the block plan does.
                view.MissingOk = some;
                view.MissingNote = some
                    ? Tr.T("problems.missingSome", blocks.CraftBlocks + blocks.StockBlocks, blocks.StockBlocks, blocks.WaitingBlocks)
                    : Tr.T("problems.missingBlocked", firstError ?? "?");
            }
            if (!quiet) PluginLog.Info("Plan check (Missing, for the window): " + view.MissingNote);
        }
        catch (Exception e) { PluginLog.Warn("Plan check: the 'Order missing' note failed: " + e.Message); }
        finally { PlanReport.LogDetails = details; }
    }

    /// <summary>
    /// Counts, shows and (mode All / Missing) orders. Quiet (re-check after a list change, see Refresh): the log gets one
    /// line instead of the stock, cargo, part and alert lines.
    /// </summary>
    /// orderWhatFits: the blueprint's "Order what fits" button: with "Order missing", a blueprint short of ore is made as
    /// far as the ore goes (whole blocks) instead of waiting as a whole; the rest of it stays in the list.
    public static void Check(PlannerContext context, OrderMode mode, bool quiet = false, bool orderWhatFits = false)
    {
        PlanReport.LogDetails = !quiet;
        try { CheckLogged(context, mode, quiet, orderWhatFits); }
        finally { PlanReport.LogDetails = true; }
    }

    private static void CheckLogged(PlannerContext context, OrderMode mode, bool quiet, bool orderWhatFits)
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
        // Blocks ordered and not done are not made again: without this the window's check counted their ore twice
        // (once for the running orders, once for the list) and showed a false ore deficit while they were crafted.
        var report = PlanReport.Build(grid, wanted, ledger, OrderBook.ItemsForCheck,
            grids.Values.Where(g => !ReferenceEquals(g, grid)).ToList(), mode == OrderMode.Missing, OrderBook.PendingParts());
        var view = new PlanView { CanBuild = report.CanBuild, Alerts = report.Alerts };
        BlockPlan blocks = null;
        Dictionary<string, (PlannedEntry Entry, BlockLine Line)> blockLines = null;
        if (plan.Count > 0)
            try
            {
                (blocks, blockLines) = BlockPlanFor(plan, report, ledger, mode != OrderMode.All, orderWhatFits);
                foreach (var (key, count) in blocks.Waiting) view.WaitingLines[blockLines[key].Entry.Key] = count;
                if (blocks.ProjectWaiting.TryGetValue("bp", out var projectOre))
                {
                    view.ProjectShort = OreText(report, projectOre);
                    // What its "Order what fits" button would make now.
                    var (fits, fitLines) = BlockPlanFor(plan, report, ledger, true, true);
                    view.ProjectFits = fits.Craft.Where(g => fitLines[g.Key].Entry.Project).Sum(g => g.Count)
                                       + fits.FromStock.Where(f => fitLines[f.Key].Entry.Project).Sum(f => f.Value);
                    // Its lines show only what that button would leave waiting (Lone 2026-11-20: not all red).
                    foreach (var key in view.WaitingLines.Keys.Where(k => plan.Any(e => e.Project && ReferenceEquals(e.Key, k))).ToList())
                        view.WaitingLines.Remove(key);
                    foreach (var (key, count) in fits.Waiting)
                        if (fitLines[key].Entry.Project) view.WaitingLines[fitLines[key].Entry.Key] = count;
                }
            }
            catch (Exception e) { PluginLog.Warn("Plan check: the block plan failed: " + e); }
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
        if (mode == OrderMode.None && !report.CanBuild && plan.Count > 0) NoteMissing(view, grid, grids, wanted, ledger, quiet, blocks);
        LastView = view;

        if (quiet)
            PluginLog.Info($"Plan check (list changed): {plan.Count} list line(s), {plan.Sum(p => p.Count)} block(s), can build = {report.CanBuild}, "
                           + $"{view.Parts.Count} part(s), {report.Alerts.Count(a => a.Level == AlertLevel.Error)} error(s), {report.Alerts.Count(a => a.Level == AlertLevel.Warning)} warning(s)");
        else
        {
            PluginLog.Info($"Plan check ({mode}): {plan.Count} list line(s), can build = {report.CanBuild}, "
                           + string.Join(", ", view.Parts.Select(p => $"{p.Item} need {p.Need} storage {p.Stock} on order {p.Incoming} missing {p.Missing}")));
            foreach (var alert in report.Alerts)
                PluginLog.Info($"Plan alert ({alert.Level}): {alert.Text}");
        }
        if (mode == OrderMode.None) return;

        if (plan.Count == 0)
        {
            PlanStore.Say(Tr.T("hud.emptyList"));
            return;
        }
        // "Order missing" short of ore only: it makes the whole blocks the ore allows (M3); every other error blocks.
        var errors = report.Alerts.Where(a => a.Level == AlertLevel.Error && !(mode == OrderMode.Missing && a.Ore)).ToList();
        if (errors.Count > 0 || blocks == null)
        {
            if (errors.Count == 0) errors = report.Alerts.Where(a => a.Level == AlertLevel.Error).ToList();
            PlanStore.Say(Tr.T("hud.cannotOrder", Math.Max(1, errors.Count)));
            foreach (var error in errors.Take(2)) PlanStore.Say(error.Text);
            return;
        }
        var shortOre = blocks.ProjectWaiting.TryGetValue("bp", out var projectShort) ? projectShort : blocks.WaitShortage;
        if (blocks.CraftBlocks + blocks.StockBlocks == 0)
        {
            // Nothing to make now: all ordered already, in storage, or waiting for ore.
            PlanStore.Say(blocks.WaitingBlocks > 0 ? Tr.T("hud.nothingFits", OreText(report, shortOre)) : Tr.T("hud.haveAll"));
            return;
        }

        // Biggest first, so the HUD shows what the order really is (2026-11-20: "6 Compressor, 61 Electronic Parts..."
        // hid 2,742 Steel Plate).
        var made = string.Join(", ", blocks.Make.OrderByDescending(n => n.Value).ThenBy(n => report.NameOf(n.Key)).Take(4).Select(n => $"{n.Value:N0} {report.NameOf(n.Key)}"));
        OrderBook.Commit(context, report, blocks, blockLines, mode);
        // M3: blocks covered by storage left the list at once; the others leave as their parts are handed to crafters
        // (OrderBook.Process). Blocks waiting for ore stay in the list for the next "Order missing".
        if (blocks.WaitingBlocks > 0)
            PlanStore.Say(Tr.T("hud.orderedSome", blocks.CraftBlocks + blocks.StockBlocks,
                blocks.CraftBlocks + blocks.StockBlocks + blocks.WaitingBlocks, blocks.WaitingBlocks, OreText(report, shortOre)));
        else if (blocks.CraftBlocks > 0)
            PlanStore.Say(Tr.T("hud.ordered", made + (blocks.Make.Count > 4 ? ", ..." : "")));
        if (blocks.StockBlocks > 0) PlanStore.Say(Tr.T("hud.fromStock", blocks.StockBlocks));
        if (blocks.ProjectWaiting.ContainsKey("bp"))
            PlanStore.Say(Tr.T("hud.bpWaits", PlanStore.ProjectName ?? "?", OreText(report, blocks.ProjectWaiting["bp"])));
        try { report.LogResources($"order {mode}, parts to make", RecipeTree.Resolve(blocks.Make, report.TreeStock, report.Recipes), report.TreeStock); }
        catch (Exception e) { PluginLog.Warn("Order: resource log failed: " + e.Message); }
        PluginLog.Info($"Order {mode}{(orderWhatFits ? " (blueprint: what fits)" : "")}: {blocks.CraftBlocks} block(s) to make, {blocks.StockBlocks} from storage, {blocks.WaitingBlocks} waiting"
                       + (blocks.WaitingBlocks > 0 ? $" (short: {OreText(report, shortOre)})" : "") + $"; parts to make: {made}");
        Check(context, OrderMode.None); // refresh the view
    }
}
