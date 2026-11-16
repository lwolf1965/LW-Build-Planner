using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Linq;
using ClientPlugin.Policy;
using Keen.Game2.Simulation.WorldObjects.CubeGrids;
using Keen.Game2.Simulation.WorldObjects.Items;

namespace ClientPlugin.Game;

/// <summary>One line of the order list on the planner screen.</summary>
internal sealed class OrderLine
{
    public string Item;
    /// <summary>Parts not queued yet (no free crafter).</summary>
    public long Waiting;
    /// <summary>Parts queued in crafters, not finished yet.</summary>
    public long Queued;
    /// <summary>Crafter names working on it.</summary>
    public string Crafters;
    /// <summary>Why it waits, or "".</summary>
    public string Note;
}

/// <summary>
/// The running orders. Game thread only (called from PlannerEngine); the screen reads the Lines snapshot.
/// Every few seconds: parts that arrived in stock are taken off the order lines, and parts still waiting are queued
/// on the crafters that are free (not crafting) right now, spread evenly. Nothing is queued on a busy crafter: when
/// all crafters of a kind are busy the parts wait, and the player is told that more crafters would help.
/// A line is done when its parts have arrived, not when the crafter pauses: an Assembler waiting for sub-parts from
/// a Smelter is not crafting but far from done (seen in game 2026-11-16). A line with no arrivals and no crafter
/// working for a minute is marked "no progress" and stays until the player clears it.
/// Only the planner's own orders are tracked; what the player queues by hand is never touched.
/// Not saved yet: after a game restart the list is empty (the crafters keep their queues).
/// </summary>
internal static class OrderBook
{
    public const double TickSeconds = 5;

    private static readonly OrderLedger Ledger = new();
    private static readonly Dictionary<string, ItemDefinition> Items = new();
    private static readonly Dictionary<object, int> CrafterIds = new(ReferenceEqualityComparer.Instance);
    private static readonly Dictionary<int, string> CrafterNames = new();
    private static readonly Dictionary<string, long> LastStock = new();
    private static readonly Dictionary<string, double> LastProgress = new();
    public const double StuckSeconds = 60;
    private static readonly Dictionary<string, string> Notes = new();
    private static readonly HashSet<string> Told = new();
    private static readonly Stopwatch Clock = Stopwatch.StartNew();
    private static HashSet<object> unlinked = new();
    private static CubeGridComponent grid;
    private static double lastTick;
    private static bool wasActive;

    /// <summary>Snapshot for the screen; replaced as a whole, never changed in place.</summary>
    public static volatile IReadOnlyList<OrderLine> Lines = Array.Empty<OrderLine>();

    public static OrderLedger LedgerForCheck => Ledger;
    public static IReadOnlyDictionary<string, ItemDefinition> ItemsForCheck => Items;

    /// <summary>The grid the running orders belong to; a new order goes to the same grid while orders run.</summary>
    public static CubeGridComponent Grid => Ledger.Active ? grid : null;

    public static bool TickDue => Ledger.Active && Clock.Elapsed.TotalSeconds - lastTick >= TickSeconds;

    /// <summary>Takes a checked plan as an order and queues what can be queued now.</summary>
    public static void Commit(PlannerContext context, PlanReport report)
    {
        grid = report.Grid.Grid;
        unlinked = report.Unlinked;
        foreach (var pair in report.Items) Items[pair.Key] = pair.Value;
        Ledger.Commit(report.Need);
        Told.Clear();
        var now = Clock.Elapsed.TotalSeconds;
        foreach (var item in report.Need.Keys) LastProgress[item] = now;
        Process(report.Grid, StockScan.Run(context).Values);
    }

    public static void Tick(PlannerContext context)
    {
        lastTick = Clock.Elapsed.TotalSeconds;
        var grids = StockScan.Run(context);
        if (grid == null || !grids.TryGetValue(grid, out var scan))
        {
            if (Ledger.Active) PlanStore.Say("Build Planner: the grid with the orders is gone, orders dropped");
            Reset();
            return;
        }
        Process(scan, grids.Values);
    }

    /// <summary>Stops queueing; what is already in the crafters keeps running.</summary>
    public static void StopWaiting()
    {
        Ledger.StopWaiting();
        Notes.Clear();
        Publish();
        PlanStore.Say("Build Planner: waiting parts dropped (crafters finish what they have)");
    }

    /// <summary>Forgets all orders (the crafters keep what is in their queues).</summary>
    public static void Reset()
    {
        if (Ledger.Active) PluginLog.Info("Orders cleared.");
        Ledger.Clear();
        Notes.Clear();
        LastStock.Clear();
        LastProgress.Clear();
        Told.Clear();
        wasActive = false;
        Publish();
    }

    private static int IdOf(Maker maker)
    {
        if (!CrafterIds.TryGetValue(maker.Entity, out var id))
            CrafterIds[maker.Entity] = id = CrafterIds.Count + 1;
        CrafterNames[id] = maker.Name;
        return id;
    }

    private static void Process(GridStock scan, IEnumerable<GridStock> allGrids)
    {
        var makers = scan.Makers.Where(m => !unlinked.Contains(m.Entity)).ToList();
        var byId = makers.ToDictionary(IdOf, m => m);
        var now = Clock.Elapsed.TotalSeconds;

        // 1. Arrivals: any rise of an ordered part's stock (all storage and crafter outputs on the player's grids, so a
        //    move by LW Cargo Manager changes nothing) is taken off the order lines. Parts taken out are ignored.
        var tracked = Ledger.QueuedLines.Select(q => q.Item).Distinct().ToList();
        foreach (var itemId in tracked)
        {
            if (!Items.TryGetValue(itemId, out var item)) continue;
            long stock = 0;
            foreach (var store in allGrids.SelectMany(g => g.Stores))
                if (store.Items.TryGetValue(item, out var amount)) stock += (long)Math.Floor(amount);
            if (LastStock.TryGetValue(itemId, out var before) && stock != before)
            {
                if (stock > before)
                {
                    Ledger.Arrived(itemId, stock - before);
                    LastProgress[itemId] = now;
                    Told.Remove("stuck:" + itemId);
                }
                PluginLog.Info($"Order progress: {NameOf(itemId)} stock {before} -> {stock}, {Ledger.QueuedOf(itemId)} still in crafters");
            }
            LastStock[itemId] = stock;
        }
        foreach (var id in Ledger.CraftersWithOrders.Where(c => !byId.ContainsKey(c)).ToList())
            PluginLog.Info($"Crafter '{(CrafterNames.TryGetValue(id, out var gone) ? gone : "?")}' is gone, its orders are closed: {string.Join(", ", Ledger.CrafterDone(id).Select(NameOf))}");

        // 2. No arrivals for a minute and none of the item's crafters working: tell the player, keep the line.
        Notes.Clear();
        foreach (var itemId in Ledger.QueuedLines.Select(q => q.Item).Distinct().ToList())
        {
            var working = Ledger.QueuedLines.Where(q => q.Item == itemId).Any(q => byId.TryGetValue(q.Crafter, out var m) && CrafterApi.IsCrafting(m.Converter));
            if (working) LastProgress[itemId] = now;
            if (now - (LastProgress.TryGetValue(itemId, out var last) ? last : now) < StuckSeconds) continue;
            Notes[itemId] = "no progress (ore or sub-parts missing?)";
            if (Told.Add("stuck:" + itemId))
                PlanStore.Say($"Build Planner: {NameOf(itemId)} is not progressing. Check ore and sub-parts, or clear the orders.");
        }

        // 3. Waiting parts go to the crafters that are free now.
        foreach (var itemId in Ledger.Waiting.Keys.ToList())
        {
            if (!Items.TryGetValue(itemId, out var item)) { Ledger.DropWaiting(itemId); continue; }
            var able = new List<(Maker Maker, object Recipe)>();
            foreach (var maker in makers)
            {
                var recipe = CrafterApi.CanProduce(maker.Converter, item);
                if (recipe != null) able.Add((maker, recipe));
            }
            if (able.Count == 0)
            {
                Ledger.DropWaiting(itemId);
                PlanStore.Say($"Build Planner: no crafter here can make {NameOf(itemId)}, dropped");
                continue;
            }

            var machine = able[0].Maker.Machine;
            var free = able.Where(a => !CrafterApi.IsCrafting(a.Maker.Converter)).ToList();
            var waiting = Ledger.Waiting[itemId];
            if (free.Count == 0)
            {
                Notes[itemId] = $"all {able.Count} {machine}(s) busy";
                LastProgress[itemId] = now;
                if (Told.Add("busy:" + machine))
                    PlanStore.Say($"Build Planner: all {able.Count} {machine}(s) are busy, {waiting} {NameOf(itemId)} waiting. More {machine}s would finish sooner.");
                continue;
            }
            Told.Remove("busy:" + machine);

            var perCraft = Math.Max(1, CrafterApi.Outputs(free[0].Recipe).Where(o => o.Item == item).Sum(o => o.Amount));
            var crafts = (int)Math.Min(int.MaxValue, Math.Ceiling(waiting / perCraft));
            var split = OrderPlan.Split(crafts, free.Count, int.MaxValue);
            for (var i = 0; i < free.Count; i++)
            {
                if (split[i] <= 0) continue;
                var (maker, recipe) = free[i];
                var ok = OrderJob.DryRun || CrafterApi.Enqueue(maker.Converter, recipe, split[i]);
                PluginLog.Info($"{(OrderJob.DryRun ? "DRY RUN: would queue" : "Queued")} {split[i]} x {NameOf(itemId)} in '{maker.Name}' ({(ok ? "ok" : "refused")})");
                if (ok)
                {
                    Ledger.MarkQueued(IdOf(maker), itemId, (long)Math.Ceiling(split[i] * perCraft));
                    LastProgress[itemId] = now;
                    if (!LastStock.ContainsKey(itemId))
                    {
                        long stock = 0;
                        foreach (var store in allGrids.SelectMany(g => g.Stores))
                            if (store.Items.TryGetValue(item, out var amount)) stock += (long)Math.Floor(amount);
                        LastStock[itemId] = stock;
                    }
                }
                else Notes[itemId] = $"'{maker.Name}' refused the order (queue full?)";
            }
        }

        if (wasActive && !Ledger.Active) PlanStore.SayDone("Build Planner: all ordered parts are done");
        wasActive = Ledger.Active;
        Publish();
    }

    private static string NameOf(string itemId) => Items.TryGetValue(itemId, out var item) ? OrderJob.NameOf(item) : itemId;

    private static void Publish()
    {
        var lines = new List<OrderLine>();
        var queuedLines = Ledger.QueuedLines.ToList();
        foreach (var itemId in Ledger.Waiting.Keys.Concat(queuedLines.Select(q => q.Item)).Distinct())
            lines.Add(new OrderLine
            {
                Item = NameOf(itemId),
                Waiting = Ledger.Waiting.TryGetValue(itemId, out var waiting) ? waiting : 0,
                Queued = Ledger.QueuedOf(itemId),
                Crafters = string.Join(", ", queuedLines.Where(q => q.Item == itemId).Select(q => CrafterNames.TryGetValue(q.Crafter, out var n) ? n : "?").Distinct()),
                Note = Notes.TryGetValue(itemId, out var note) ? note : "",
            });
        Lines = lines.OrderBy(l => l.Item, StringComparer.OrdinalIgnoreCase).ToList();
    }
}
