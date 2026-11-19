using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Linq;
using ClientPlugin.Policy;
using Keen.Game2.Simulation.WorldObjects.CubeGrids;
using Keen.Game2.Simulation.WorldObjects.Items;

namespace ClientPlugin.Game;

/// <summary>Where one ordered part stands.</summary>
internal enum OrderState
{
    /// <summary>Everything ordered of it has arrived.</summary>
    Done,
    /// <summary>A crafter is working on it.</summary>
    Crafting,
    /// <summary>Queued in a crafter that is not crafting yet: it waits for sub-parts from smaller crafters or for earlier jobs.</summary>
    Queued,
    /// <summary>No crafter of the right kind is free yet.</summary>
    WaitingForCrafter,
    /// <summary>No arrivals for a minute and no crafter working on it.</summary>
    NoProgress,
    /// <summary>The player stopped the waiting parts (or the crafter is gone): the rest will not be made.</summary>
    Stopped,
}

/// <summary>One line of the order list on the planner screen, in the order the parts were ordered (oldest first).</summary>
internal sealed class OrderLine
{
    public int Position;
    public string Item;
    /// <summary>Everything ordered of this part in the batch.</summary>
    public long Ordered;
    /// <summary>Parts that arrived so far.</summary>
    public long Done;
    /// <summary>Parts not queued yet (no free crafter).</summary>
    public long Waiting;
    /// <summary>Parts queued in crafters, not finished yet.</summary>
    public long Queued;
    public OrderState State;
    /// <summary>Per crafter: how many parts it still has and whether it is crafting.</summary>
    public string Detail;
    /// <summary>Extra reason (busy crafters, no progress ...), or "".</summary>
    public string Note;
}

/// <summary>
/// The running orders. Game thread only (called from PlannerEngine); the screen reads the Lines snapshot.
/// Every few seconds: parts that arrived in stock are taken off the order lines, and parts still waiting are queued
/// on the crafters that are free (not crafting) right now, in batches (Dispatch.MaxBatchCrafts crafts per crafter), part
/// by part in the order they were ordered. Every free crafter gets its own batch, so more crafters finish a big build
/// sooner; a crafter that becomes free gets the next batch of the oldest part still waiting. Nothing is queued on a busy
/// crafter: when all crafters of a kind are busy the parts wait, and the player is told that more crafters would help.
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
    /// <summary>When "Order progress" was last logged per part: at most every 30 s, plus the last arrival.</summary>
    private static readonly Dictionary<string, double> ProgressLogged = new();
    /// <summary>No arrivals and no crafter working on a part for this long: "no progress" (settings, default 60 s).</summary>
    public static double StuckSeconds => PlannerSettings.Current.NoProgressSeconds;
    private static readonly Dictionary<string, string> Notes = new();
    private static readonly HashSet<string> Told = new();
    private static readonly Stopwatch Clock = Stopwatch.StartNew();
    private static HashSet<object> unlinked = new();
    private static CubeGridComponent grid;
    private static double lastTick;
    private static bool wasActive;
    private const string NoProgressNote = "no progress (ore or sub-parts missing?)";
    /// <summary>Crafters (ids) that were crafting at the last tick.</summary>
    private static HashSet<int> crafting = new();
    /// <summary>Crafters (ids) that hold a counted batch but do not craft: since when (seconds). Used to get a crafter back after a lost batch.</summary>
    private static readonly Dictionary<int, double> IdleSince = new();
    /// <summary>A crafter that holds a counted batch but has not crafted for this long gets a new batch anyway.</summary>
    public const double IdleReclaimSeconds = 30;
    private static double startedAt = -1;
    private static double finishedAt = -1;

    /// <summary>Snapshot for the screen; replaced as a whole, never changed in place.</summary>
    public static volatile IReadOnlyList<OrderLine> Lines = Array.Empty<OrderLine>();

    public static OrderLedger LedgerForCheck => Ledger;
    public static IReadOnlyDictionary<string, ItemDefinition> ItemsForCheck => Items;

    /// <summary>The grid the running orders belong to; a new order goes to the same grid while orders run.</summary>
    public static CubeGridComponent Grid => Ledger.Active ? grid : null;

    /// <summary>"running 1:12" or "finished in 0:41" for the batch on the screen; "" when there is none. Safe to call from the UI thread.</summary>
    public static string ElapsedLabel()
    {
        var start = startedAt;
        if (start < 0) return "";
        var end = finishedAt;
        var span = TimeSpan.FromSeconds(Math.Max(0, (end >= 0 ? end : Clock.Elapsed.TotalSeconds) - start));
        var text = span.TotalHours >= 1 ? span.ToString(@"h\:mm\:ss") : span.ToString(@"m\:ss");
        return Tr.T(end >= 0 ? "elapsed.finished" : "elapsed.running", text);
    }

    public static bool TickDue => Ledger.Active && Clock.Elapsed.TotalSeconds - lastTick >= TickSeconds;

    /// <summary>Takes a checked plan as an order and queues what can be queued now.</summary>
    public static void Commit(PlannerContext context, PlanReport report)
    {
        grid = report.Grid.Grid;
        unlinked = report.Unlinked;
        foreach (var pair in report.Items) Items[pair.Key] = pair.Value;
        var now = Clock.Elapsed.TotalSeconds;
        if (!Ledger.Active) { startedAt = now; finishedAt = -1; } // a new batch starts
        Ledger.Commit(report.Need);
        Told.Clear();
        foreach (var item in report.Need.Keys) LastProgress[item] = now;
        Process(report.Grid, StockScan.Run(context).Values);
    }

    public static void Tick(PlannerContext context)
    {
        lastTick = Clock.Elapsed.TotalSeconds;
        var grids = StockScan.Run(context);
        if (grid == null || !grids.TryGetValue(grid, out var scan))
        {
            if (Ledger.Active) PlanStore.Say(Tr.T("hud.gridGone"));
            Reset();
            return;
        }
        if (Process(scan, grids.Values)) PlanService.Check(context, OrderMode.None); // the list changed: refresh the view
    }

    /// <summary>Stops queueing; what is already in the crafters keeps running.</summary>
    public static void StopWaiting()
    {
        Ledger.StopWaiting();
        Notes.Clear();
        Publish();
        PlanStore.Say(Tr.T("hud.waitingDropped"));
    }

    /// <summary>Forgets all orders (the crafters keep what is in their queues).</summary>
    public static void Reset()
    {
        if (Ledger.Active) PluginLog.Info("Orders cleared.");
        PlanStore.ForgetOrdered(); // stopped or removed before they were done: the list stays
        Ledger.Clear();
        Notes.Clear();
        LastStock.Clear();
        LastProgress.Clear();
        ProgressLogged.Clear();
        Told.Clear();
        wasActive = false;
        startedAt = finishedAt = -1;
        crafting = new HashSet<int>();
        IdleSince.Clear();
        Publish();
    }

    /// <summary>
    /// A new world was loaded: drop everything that points into the old one (orders, crafter and item lookups, the grid),
    /// so nothing of it stays in memory and no order of the old world is tracked in the new one.
    /// </summary>
    public static void ForgetWorld()
    {
        Reset();
        Items.Clear();
        CrafterIds.Clear();
        CrafterNames.Clear();
        grid = null;
        unlinked = new HashSet<object>();
    }

    private static int IdOf(Maker maker)
    {
        if (!CrafterIds.TryGetValue(maker.Entity, out var id))
            CrafterIds[maker.Entity] = id = CrafterIds.Count + 1;
        CrafterNames[id] = maker.Name;
        return id;
    }

    /// <summary>One pass over the crafters. Returns true when the last orders just finished (list and orders were cleared).</summary>
    private static bool Process(GridStock scan, IEnumerable<GridStock> allGrids)
    {
        GameLanguage.Refresh(); // messages of this tick in the language the player uses now
        var makers = scan.Makers.Where(m => !unlinked.Contains(m.Entity)).ToList();
        var byId = makers.ToDictionary(IdOf, m => m);
        crafting = byId.Where(p => CrafterApi.IsCrafting(p.Value.Converter)).Select(p => p.Key).ToHashSet();
        var now = Clock.Elapsed.TotalSeconds;
        var idle = byId.Keys.Where(id => !crafting.Contains(id)).ToList();
        foreach (var id in byId.Keys)
        {
            if (crafting.Contains(id) || Ledger.QueuedOfCrafter(id) <= 0) IdleSince.Remove(id);
            else if (!IdleSince.ContainsKey(id)) IdleSince[id] = now;
        }

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
                    Ledger.Arrived(itemId, stock - before, idle);
                    LastProgress[itemId] = now;
                    Told.Remove("stuck:" + itemId);
                }
                var left = Ledger.QueuedOf(itemId);
                if (left == 0 || !ProgressLogged.TryGetValue(itemId, out var logged) || now - logged >= 30)
                {
                    ProgressLogged[itemId] = now;
                    PluginLog.Info($"Order progress: {NameOf(itemId)} stock {before} -> {stock}, {left} still in crafters");
                }
            }
            LastStock[itemId] = stock;
        }
        foreach (var id in Ledger.CraftersWithOrders.Where(c => !byId.ContainsKey(c)).ToList())
            PluginLog.Info($"Crafter '{(CrafterNames.TryGetValue(id, out var gone) ? gone : "?")}' is gone, its orders are closed: {string.Join(", ", Ledger.CrafterDone(id).Select(NameOf))}");

        // 1b. The crafters' queues: a crafter that no longer has the recipe of its line in its queue has made all of it,
        //     even when the parts went out of storage at once (welding while crafting: stream test 2026-11-19, 1,800
        //     Steel Plate made, ~200 never seen arriving, a false "not progressing"). Storage rise stays the progress bar.
        foreach (var (crafterId, itemId, _) in Ledger.QueuedLines.ToList())
        {
            if (!byId.TryGetValue(crafterId, out var maker) || !Items.TryGetValue(itemId, out var item)) continue;
            var recipe = CrafterApi.CanProduce(maker.Converter, item);
            if (recipe == null || CrafterApi.IsProducing(maker.Converter, recipe) != false) continue;
            var credited = Ledger.CrafterFinished(crafterId, itemId);
            LastProgress[itemId] = now;
            Told.Remove("stuck:" + itemId);
            if (credited > 0)
                PluginLog.Info($"Order progress: '{maker.Name}' has no {NameOf(itemId)} left in its queue, its last {credited} count as made (taken from storage before they were seen)");
        }

        // 2. No arrivals for a minute and none of the item's crafters working: tell the player, keep the line.
        Notes.Clear();
        foreach (var itemId in Ledger.QueuedLines.Select(q => q.Item).Distinct().ToList())
        {
            var working = Ledger.QueuedLines.Where(q => q.Item == itemId).Any(q => byId.TryGetValue(q.Crafter, out var m) && CrafterApi.IsCrafting(m.Converter));
            if (working) LastProgress[itemId] = now;
            if (now - (LastProgress.TryGetValue(itemId, out var last) ? last : now) < StuckSeconds) continue;
            Notes[itemId] = NoProgressNote;
            if (Told.Add("stuck:" + itemId))
                PlanStore.Say(Tr.T("hud.noProgress", NameOf(itemId)));
        }

        // 3. Waiting parts go to the free crafters in batches, oldest order first (see Policy/Dispatch.cs).
        var free = byId.Keys.Where(id => Dispatch.IsFree(crafting.Contains(id), Ledger.QueuedOfCrafter(id),
            IdleSince.TryGetValue(id, out var since) ? now - since : 0, IdleReclaimSeconds)).ToList();
        var wants = new List<DispatchWant>();
        var recipes = new Dictionary<(int Crafter, string Item), object>();
        var ableOf = new Dictionary<string, (int Count, string Machine)>();
        foreach (var itemId in Ledger.WaitingOrder().ToList()) // oldest order first
        {
            if (!Items.TryGetValue(itemId, out var item)) { Ledger.DropWaiting(itemId); continue; }
            var able = new List<(int Id, Maker Maker, object Recipe)>();
            foreach (var pair in byId)
            {
                var recipe = CrafterApi.CanProduce(pair.Value.Converter, item);
                if (recipe != null) { able.Add((pair.Key, pair.Value, recipe)); recipes[(pair.Key, itemId)] = recipe; }
            }
            if (able.Count == 0)
            {
                Ledger.DropWaiting(itemId);
                PlanStore.Say(Tr.T("hud.noCrafter", NameOf(itemId)));
                continue;
            }
            ableOf[itemId] = (able.Count, able[0].Maker.Machine);
            wants.Add(new DispatchWant
            {
                Item = itemId,
                Waiting = Ledger.Waiting[itemId],
                PerCraft = Math.Max(1, CrafterApi.Outputs(able[0].Recipe).Where(o => o.Item == item).Sum(o => o.Amount)),
                Able = able.Select(a => a.Id).ToList(),
            });
        }

        var jobs = Dispatch.Plan(wants, free, Dispatch.MaxBatchCrafts);
        foreach (var job in jobs)
        {
            var item = Items[job.Item];
            var maker = byId[job.Crafter];
            var want = wants.First(w => w.Item == job.Item);
            var ok = OrderJob.DryRun || CrafterApi.Enqueue(maker.Converter, recipes[(job.Crafter, job.Item)], job.Crafts);
            PluginLog.Info($"{(OrderJob.DryRun ? "DRY RUN: would queue" : "Queued")} batch of {job.Crafts} x {NameOf(job.Item)} in '{maker.Name}' ({(ok ? "ok" : "refused")}), {Ledger.Waiting.GetValueOrDefault(job.Item)} waiting");
            if (!ok) { Notes[job.Item] = Tr.T("note.refused", maker.Name); continue; }
            Ledger.MarkQueued(job.Crafter, job.Item, (long)Math.Ceiling(job.Crafts * want.PerCraft));
            IdleSince.Remove(job.Crafter);
            LastProgress[job.Item] = now;
            // "All N x crafter are busy" is said once per order (Told is cleared by the next order click), not after
            // every batch: on long orders it came every ~35 s (stream 2026-11-18). The window's line note stays live.
            if (!LastStock.ContainsKey(job.Item))
            {
                long stock = 0;
                foreach (var store in allGrids.SelectMany(g => g.Stores))
                    if (store.Items.TryGetValue(item, out var amount)) stock += (long)Math.Floor(amount);
                LastStock[job.Item] = stock;
            }
        }

        // Parts that got no crafter this pass: say why (once), and keep them from looking stuck.
        foreach (var want in wants.Where(w => Ledger.Waiting.ContainsKey(w.Item) && jobs.All(j => j.Item != w.Item)))
        {
            var (count, machine) = ableOf[want.Item];
            Notes[want.Item] = free.Any(id => want.Able.Contains(id)) ? "" : Tr.T("note.busy", count, machine);
            LastProgress[want.Item] = now;
            if (!free.Any(id => want.Able.Contains(id)) && Told.Add("busy:" + machine))
                PlanStore.Say(Tr.T("hud.busy", count, machine, Ledger.Waiting[want.Item], NameOf(want.Item)));
        }

        if (wasActive && !Ledger.Active)
        {
            // All orders are done, so the build is ready: the ordered blocks leave the build list and the orders are
            // removed, so a finished build is not ordered twice by mistake. Stopped orders never get here (Reset clears
            // wasActive), and their list stays.
            // Settings: "Keep list when done" leaves the list as it is, so the same build can be ordered again.
            var keep = PlannerSettings.Current.KeepListWhenDone;
            var taken = keep ? 0 : PlanStore.TakeOrdered();
            PlanStore.SayDone(Tr.T(keep ? "hud.doneKept" : "hud.done"));
            PluginLog.Info($"All orders done: {taken} block(s) taken off the build list, orders removed.");
            Reset();
            return true;
        }
        wasActive = Ledger.Active;
        Publish();
        return false;
    }

    private static string NameOf(string itemId) => Items.TryGetValue(itemId, out var item) ? OrderJob.NameOf(item) : itemId;

    private static void Publish()
    {
        if (!Ledger.Active && startedAt >= 0 && finishedAt < 0) finishedAt = Clock.Elapsed.TotalSeconds;
        var lines = new List<OrderLine>();
        var queuedLines = Ledger.QueuedLines.ToList();
        foreach (var itemId in Ledger.Sequence)
        {
            var waiting = Ledger.Waiting.TryGetValue(itemId, out var w) ? w : 0;
            var queued = Ledger.QueuedOf(itemId);
            var mine = queuedLines.Where(q => q.Item == itemId).OrderBy(q => q.Crafter).ToList();
            Notes.TryGetValue(itemId, out var note);
            note ??= "";

            OrderState state;
            if (waiting == 0 && queued == 0) state = Ledger.Done(itemId) >= Ledger.Ordered(itemId) ? OrderState.Done : OrderState.Stopped;
            else if (note == NoProgressNote) state = OrderState.NoProgress;
            else if (queued > 0) state = mine.Any(q => crafting.Contains(q.Crafter)) ? OrderState.Crafting : OrderState.Queued;
            else state = OrderState.WaitingForCrafter;

            lines.Add(new OrderLine
            {
                Position = lines.Count + 1,
                Item = NameOf(itemId),
                Ordered = Ledger.Ordered(itemId),
                Done = Ledger.Done(itemId),
                Waiting = waiting,
                Queued = queued,
                State = state,
                Detail = string.Join("; ", mine.Select(q => Tr.T("detail.line",
                    CrafterNames.TryGetValue(q.Crafter, out var name) ? name : "?", q.Amount.ToString("N0"),
                    Tr.T(crafting.Contains(q.Crafter) ? "detail.crafting" : "detail.waiting")))),
                Note = note == NoProgressNote ? "" : note,
            });
        }
        Lines = lines;
    }
}
