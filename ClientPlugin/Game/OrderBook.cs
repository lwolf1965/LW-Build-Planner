using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Linq;
using ClientPlugin.Policy;
using Keen.Game2.Simulation.WorldObjects.CubeGrids;
using Keen.Game2.Simulation.WorldObjects.CubeBlocks.Production.ItemConverters;
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
    /// <summary>Held back by the planner: another ordered part is made from it and comes first ("next: after X").</summary>
    Next,
    /// <summary>No arrivals for a minute and no crafter working on it.</summary>
    NoProgress,
    /// <summary>The player stopped the waiting parts (or the crafter is gone): the rest will not be made.</summary>
    Stopped,
}

/// <summary>M3: blocks of one list line in the hand-out order, and how many of them are done (all parts handed out).</summary>
internal sealed class OrderGroup
{
    public BlockGroup Blocks;
    /// <summary>The build list line (PlannedEntry.Key) the blocks leave when done.</summary>
    public object EntryKey;
    public int Done;
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
/// Saved with the world (PlanSave, Lone 2026-11-20): after a load the orders go on by themselves; the game keeps the
/// crafters' queues, the planner finds its queued parts in them again (Restore, then the first Process).
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
    /// <summary>
    /// Batches queued per part since its last "Queued" log line: the log says it at the first batch, then at most once a
    /// minute and at the last batch (a 412-block order wrote 552 lines, one per batch, 2026-11-19).
    /// </summary>
    private static readonly Dictionary<string, (int Batches, long Crafts, HashSet<int> Crafters)> QueuedSince = new();
    private static readonly Dictionary<string, double> QueuedLogged = new();
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
    /// <summary>K2: the ore shortage is looked up at most this often while parts stall (it reads the recipes of the crafters).</summary>
    private const double OreShortEverySeconds = 10;
    private static volatile string oreShortText = "";
    private static string oreShortHud = "";
    private static double oreShortAt = -100;
    /// <summary>
    /// K2: the ore the stalled parts still lack, as the window words it ("Iron 12,000, Nickel 300 (+2)"), or "" when
    /// nothing is short (or no part stalls). The window shows it as a red line under the progress bar.
    /// </summary>
    public static string OreShortText => oreShortText;
    /// <summary>Crafters (ids) that were crafting at the last tick.</summary>
    private static HashSet<int> crafting = new();
    /// <summary>Crafters (ids) that hold a counted batch but do not craft: since when (seconds). Used to get a crafter back after a lost batch.</summary>
    private static readonly Dictionary<int, double> IdleSince = new();
    /// <summary>A crafter that holds a counted batch but has not crafted for this long gets a new batch anyway.</summary>
    public const double IdleReclaimSeconds = 30;
    private static double startedAt = -1;
    private static double finishedAt = -1;
    /// <summary>Orders read back from the world's save: the grid and the crafters with the queued parts are found at the next tick.</summary>
    private static bool restored;

    /// <summary>
    /// The button that ordered the blueprint project ("All" / "Missing"), null when no project block is on order. The
    /// window shows it next to the blueprint's name (Lone 2026-11-20). Safe to read from the UI thread.
    /// </summary>
    public static volatile string ProjectOrderedWith;

    /// <summary>The orders as the next world save writes them; null when nothing runs. Game thread writes, the save reads.</summary>
    public static volatile string SavedText;

    /// <summary>Snapshot for the screen; replaced as a whole, never changed in place.</summary>
    public static volatile IReadOnlyList<OrderLine> Lines = Array.Empty<OrderLine>();

    public static OrderLedger LedgerForCheck => Ledger;

    /// <summary>
    /// M3: the ordered blocks in hand-out order (smallest first). A block is done when all its parts are handed to
    /// crafters (Credit from storage + batches queued): it leaves the build list then and its parts are kept for it.
    /// </summary>
    private static readonly List<OrderGroup> Groups = new();
    /// <summary>Parts handed out for the groups so far: storage credit + every batch queued.</summary>
    private static readonly Dictionary<string, long> HandedOut = new();

    /// <summary>
    /// Blocks ordered but not done yet, per list line (game thread). A new check leaves them out (they are ordered) and
    /// keeps their parts for them, so "Order missing" while orders run never orders or covers them twice.
    /// </summary>
    public static Dictionary<object, int> PendingBlocks()
    {
        var pending = new Dictionary<object, int>(ReferenceEqualityComparer.Instance);
        foreach (var group in Groups.Where(g => g.Blocks.Count > g.Done))
            pending[group.EntryKey] = (pending.TryGetValue(group.EntryKey, out var n) ? n : 0) + group.Blocks.Count - group.Done;
        return pending;
    }

    /// <summary>The parts of the pending blocks (see PendingBlocks).</summary>
    public static Dictionary<string, long> PendingParts()
    {
        var parts = new Dictionary<string, long>();
        foreach (var group in Groups.Where(g => g.Blocks.Count > g.Done))
            foreach (var part in group.Blocks.Parts)
                parts[part.Key] = (parts.TryGetValue(part.Key, out var n) ? n : 0) + part.Value * (group.Blocks.Count - group.Done);
        return parts;
    }
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

    /// <summary>
    /// Takes a checked block plan as an order (M3): blocks storage covers leave the list at once (their parts are kept
    /// for them), the blocks to make join the hand-out order, their parts wait for crafters; then queues what can be
    /// queued now. lines: the plan's line keys to the list lines and their parts per block.
    /// </summary>
    public static void Commit(PlannerContext context, PlanReport report, BlockPlan blocks,
        IReadOnlyDictionary<string, (PlannedEntry Entry, BlockLine Line)> lines, OrderMode mode)
    {
        grid = report.Grid.Grid;
        unlinked = report.Unlinked;
        foreach (var pair in report.Items) Items[pair.Key] = pair.Value;
        var now = Clock.Elapsed.TotalSeconds;
        if (!Ledger.Active) { startedAt = now; finishedAt = -1; } // a new batch starts

        var keep = PlannerSettings.Current.KeepListWhenDone;
        foreach (var (key, count) in blocks.FromStock)
            if (!keep && lines.TryGetValue(key, out var line)) TakeOff(line.Entry.Key, line.Entry.Name, line.Line.Parts, count, "in storage");
        foreach (var group in blocks.Craft)
            Groups.Add(new OrderGroup { Blocks = group, EntryKey = lines[group.Key].Entry.Key });
        foreach (var credit in blocks.Credit)
            HandedOut[credit.Key] = (HandedOut.TryGetValue(credit.Key, out var h) ? h : 0) + credit.Value;
        if (blocks.Craft.Any(g => lines[g.Key].Entry.Project) || blocks.FromStock.Keys.Any(k => lines[k].Entry.Project))
            PlanStore.ProjectStarted = true;
        if (blocks.Craft.Any(g => lines[g.Key].Entry.Project)) ProjectOrderedWith = mode.ToString();

        Ledger.Commit(blocks.Make);
        Told.Clear();
        InputsOf.Clear(); // a new order reads the recipes again (crafters may have changed)
        foreach (var item in blocks.Make.Keys) LastProgress[item] = now;
        Process(report.Grid, StockScan.Run(context).Values);
    }

    /// <summary>
    /// Blocks leave their list line (M3; not with "Keep list when done"). Their parts are not set aside: storage is what
    /// the next check counts (Lone 2026-11-20: parts used for welding later simply show up as missing again). Says so
    /// when this finished the blueprint. Returns how many left.
    /// </summary>
    private static int TakeOff(object entryKey, string name, IReadOnlyDictionary<string, long> parts, int count, string why)
    {
        var (taken, finished) = PlanStore.TakeBlocks(entryKey, count);
        if (taken <= 0) return 0;
        PluginLog.Info($"Build list: {taken} x '{name}' left the list ({why}).");
        if (finished != null)
        {
            PlanStore.SayDone(Tr.T("hud.bpDone", finished));
            PluginLog.Info($"Blueprint '{finished}': all its blocks are made or ordered.");
        }
        return taken;
    }

    public static void Tick(PlannerContext context)
    {
        lastTick = Clock.Elapsed.TotalSeconds;
        var grids = StockScan.Run(context);
        // Orders read back from a save: they go to the grid with the most crafters (the one they were ordered on).
        if (grid == null && restored)
            grid = grids.Values.Where(g => g.Makers.Count > 0).OrderByDescending(g => g.Makers.Count).FirstOrDefault()?.Grid;
        if (grid == null || !grids.TryGetValue(grid, out var scan))
        {
            if (Ledger.Active) PlanStore.Say(Tr.T("hud.gridGone"));
            Reset();
            return;
        }
        if (Process(scan, grids.Values)) PlanService.Check(context, OrderMode.None, quiet: true); // the list changed: refresh the view
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
        // Blocks not done yet stay in the list (their parts were not all handed out); blocks that already left stay off it.
        Groups.Clear();
        HandedOut.Clear();
        Ledger.Clear();
        Notes.Clear();
        oreShortHud = "";
        oreShortText = "";
        LastStock.Clear();
        LastProgress.Clear();
        ProgressLogged.Clear();
        QueuedSince.Clear();
        QueuedLogged.Clear();
        Told.Clear();
        InputsOf.Clear();
        NotProducingSeen.Clear();
        HeldNow = new Dictionary<string, string>();
        WaitReasons.Clear();
        wasActive = false;
        restored = false;
        ProjectOrderedWith = null;
        SavedText = null;
        startedAt = finishedAt = -1;
        crafting = new HashSet<int>();
        IdleSince.Clear();
        Publish();
    }

    /// <summary>The orders as text for the world save (PlanSave): pending blocks, parts handed out, the ledger.</summary>
    private static void Remember()
    {
        if (!Ledger.Active && Groups.All(g => g.Done >= g.Blocks.Count))
        {
            SavedText = null;
            return;
        }
        var saved = new SavedOrders
        {
            Mode = ProjectOrderedWith ?? "Missing",
            Elapsed = startedAt >= 0 ? Clock.Elapsed.TotalSeconds - startedAt : 0,
            HandedOut = new Dictionary<string, long>(HandedOut),
            Parts = Ledger.Save(),
        };
        // Finished groups are not saved (their blocks left the list): the parts they used leave the handed-out count
        // too, or after a load those parts would finish other blocks a second time (stream test 2026-11-20: 45 blocks
        // left the list at once after loading).
        foreach (var group in Groups.Where(g => g.Done >= g.Blocks.Count))
            foreach (var part in group.Blocks.Parts)
                if (saved.HandedOut.TryGetValue(part.Key, out var handed))
                {
                    var left = handed - part.Value * group.Blocks.Count;
                    if (left > 0) saved.HandedOut[part.Key] = left;
                    else saved.HandedOut.Remove(part.Key);
                }
        foreach (var group in Groups.Where(g => g.Done < g.Blocks.Count))
            saved.Groups.Add(new SavedGroup
            {
                Key = PlanSave.KeyText(group.EntryKey), Project = group.EntryKey is ProjectKey, Count = group.Blocks.Count,
                Done = group.Done, Parts = new Dictionary<string, long>(group.Blocks.Parts),
            });
        SavedText = OrderFile.Write(saved);
    }

    /// <summary>
    /// Game thread, right after the world's build list was read (PlanSave.Attach): the orders saved with the world go
    /// on. Groups find their list lines again by key; a line the list no longer has drops its group. Blocks the list
    /// no longer has (taken off just before the save) count as done. Items the game no longer knows are dropped.
    /// </summary>
    public static void Restore(SavedOrders saved, IReadOnlyList<PlannedEntry> entries, IReadOnlyDictionary<string, ItemDefinition> items)
    {
        Reset();
        foreach (var pair in items) Items[pair.Key] = pair.Value;
        foreach (var part in saved.Parts.Where(p => Items.ContainsKey(p.Item))) Ledger.Restore(part);
        foreach (var pair in saved.HandedOut) HandedOut[pair.Key] = pair.Value;
        var dropped = 0;
        foreach (var group in saved.Groups)
        {
            var entry = entries.FirstOrDefault(e => e.Project == group.Project && PlanSave.KeyText(e.Key) == group.Key);
            if (entry == null || group.Parts.Keys.Any(id => !Items.ContainsKey(id))) { dropped++; continue; }
            var done = Math.Max(group.Done, group.Count - entry.Count);
            Groups.Add(new OrderGroup
            {
                Blocks = new BlockGroup { Key = "R" + Groups.Count, Name = entry.Name, Count = group.Count, Parts = new Dictionary<string, long>(group.Parts) },
                EntryKey = entry.Key, Done = Math.Max(0, done),
            });
        }
        restored = true;
        wasActive = Ledger.Active;
        startedAt = Clock.Elapsed.TotalSeconds - Math.Max(0, saved.Elapsed);
        foreach (var item in Ledger.Waiting.Keys.Concat(Ledger.UnassignedItems())) LastProgress[item] = Clock.Elapsed.TotalSeconds;
        if (Groups.Any(g => g.EntryKey is ProjectKey && g.Done < g.Blocks.Count))
        {
            ProjectOrderedWith = saved.Mode == "All" ? "All" : "Missing";
            PlanStore.ProjectStarted = true;
        }
        Remember();
        Publish();
        PluginLog.Info($"Orders restored from the save: {Groups.Sum(g => g.Blocks.Count - g.Done)} block(s) on order in {Groups.Count} line(s)"
                       + (dropped > 0 ? $" ({dropped} line(s) no longer in the list, dropped)" : "")
                       + $", waiting: {string.Join(", ", Ledger.Waiting.Select(w => $"{w.Value} {NameOf(w.Key)}"))}"
                       + $"; queued in crafters: {string.Join(", ", Ledger.UnassignedItems().Select(NameOf))}.");
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

    /// <summary>One pass over the crafters. Returns true when the build list changed (blocks done, or all orders finished).</summary>
    private static bool Process(GridStock scan, IEnumerable<GridStock> allGrids)
    {
        GameLanguage.Refresh(); // messages of this tick in the language the player uses now
        var makers = scan.Makers.Where(m => !unlinked.Contains(m.Entity)).ToList();
        var byId = makers.ToDictionary(IdOf, m => m);
        crafting = byId.Where(p => CrafterApi.IsCrafting(p.Value.Converter)).Select(p => p.Key).ToHashSet();
        var now = Clock.Elapsed.TotalSeconds;
        if (restored) FindQueuedAgain(byId);
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
        foreach (var id in Ledger.CraftersWithOrders.Where(c => c != OrderLedger.Unassigned && !byId.ContainsKey(c)).ToList())
            PluginLog.Info($"Crafter '{(CrafterNames.TryGetValue(id, out var gone) ? gone : "?")}' is gone, its orders are closed: {string.Join(", ", Ledger.CrafterDone(id).Select(NameOf))}");

        // 1b. The crafters' queues: a crafter that no longer has the recipe of its line in its queue has made all of it,
        //     even when the parts went out of storage at once (welding while crafting: stream test 2026-11-19, 1,800
        //     Steel Plate made, ~200 never seen arriving, a false "not progressing"). Storage rise stays the progress bar.
        var finished = new Dictionary<string, (long Credited, List<string> Crafters)>(); // one log line per part
        foreach (var (crafterId, itemId, _) in Ledger.QueuedLines.ToList())
        {
            Maker maker = null;
            if (crafterId == OrderLedger.Unassigned)
            {
                // Queued before the load: open while any crafter still has the part in its queue.
                if (HoldersOf(byId, itemId).Count > 0) { NotProducingSeen.Remove((crafterId, itemId)); continue; }
            }
            else
            {
                if (!byId.TryGetValue(crafterId, out maker) || !Items.TryGetValue(itemId, out var item)) continue;
                var recipe = CrafterApi.CanProduce(maker.Converter, item);
                if (recipe == null || CrafterApi.IsProducing(maker.Converter, recipe) != false) { NotProducingSeen.Remove((crafterId, itemId)); continue; }
            }
            // Finished only when the queue check says so twice in a row (5 s apart): one read said "not in the queue"
            // while the Assembler went on making 306 Heavy-Duty Plate (2026-11-20), so the Steel Plate held for them
            // was let out too early and eaten.
            NotProducingSeen[(crafterId, itemId)] = NotProducingSeen.GetValueOrDefault((crafterId, itemId)) + 1;
            if (NotProducingSeen[(crafterId, itemId)] < 2) continue;
            NotProducingSeen.Remove((crafterId, itemId));
            var credited = Ledger.CrafterFinished(crafterId, itemId);
            LastProgress[itemId] = now;
            Told.Remove("stuck:" + itemId);
            if (credited <= 0) continue;
            if (!finished.TryGetValue(itemId, out var sum)) sum = (0, new List<string>());
            sum.Crafters.Add(maker?.Name ?? Tr.T("detail.fromSave"));
            finished[itemId] = (sum.Credited + credited, sum.Crafters);
        }
        foreach (var (itemId, (credited, crafters)) in finished)
            PluginLog.Info($"Order progress: {NameOf(itemId)} finished in "
                           + string.Join(", ", crafters.GroupBy(n => n).Select(g => g.Count() > 1 ? $"'{g.Key}' x{g.Count()}" : $"'{g.Key}'"))
                           + $" (no more in their queues): their last {credited} count as made (taken from storage before they were seen)");

        // 2. No arrivals for a minute and none of the item's crafters working: tell the player, keep the line.
        Notes.Clear();
        var newlyStuck = new List<string>();
        foreach (var itemId in Ledger.QueuedLines.Select(q => q.Item).Distinct().ToList())
        {
            var working = Ledger.QueuedLines.Where(q => q.Item == itemId).Any(q => q.Crafter == OrderLedger.Unassigned
                ? HoldersOf(byId, itemId).Any(h => crafting.Contains(h))
                : byId.TryGetValue(q.Crafter, out var m) && CrafterApi.IsCrafting(m.Converter));
            if (working) LastProgress[itemId] = now;
            if (now - (LastProgress.TryGetValue(itemId, out var last) ? last : now) < StuckSeconds) continue;
            Notes[itemId] = NoProgressNote;
            if (Told.Add("stuck:" + itemId)) newlyStuck.Add(itemId);
        }

        // K2: when ore is what stalled them, the HUD says which ore and how much (once per shortage), else the old text.
        UpdateOreShort(scan, allGrids, now, newlyStuck.Count > 0);
        foreach (var itemId in newlyStuck)
        {
            if (oreShortHud.Length == 0) PlanStore.Say(Tr.T("hud.noProgress", NameOf(itemId)));
            else if (Told.Add("ore:" + oreShortHud)) PlanStore.Say(Tr.T("hud.oreShort", oreShortHud));
        }

        // 3. Waiting parts go to the free crafters in batches, oldest order first (see Policy/Dispatch.cs).
        var free = byId.Keys.Where(id => Dispatch.IsFree(crafting.Contains(id), Ledger.QueuedOfCrafter(id),
            IdleSince.TryGetValue(id, out var since) ? now - since : 0, IdleReclaimSeconds)).ToList();
        var wants = new List<DispatchWant>();
        var recipes = new Dictionary<(int Crafter, string Item), ItemRecipeDefinition>();
        var ableOf = new Dictionary<string, (int Count, string Machine)>();
        // M3: in the order the blocks need them (the first block's parts first, smallest block first); parts of no
        // block group (none today) after them, oldest order first.
        var groups = Groups.Select(g => g.Blocks).ToList();
        var byBlock = BlockSets.PartOrder(groups, HandedOut).Where(Ledger.Waiting.ContainsKey).ToList();
        var held = HeldParts(byId);
        HeldNow = held;
        foreach (var itemId in byBlock.Concat(Ledger.WaitingOrder().Where(i => !byBlock.Contains(i))).ToList())
        {
            if (!Items.TryGetValue(itemId, out var item)) { Ledger.DropWaiting(itemId); continue; }
            if (held.ContainsKey(itemId))
            {
                // Made later: the crafters making the part that uses it would take it from storage. Shown as "next".
                LastProgress[itemId] = now;
                continue;
            }
            var able = new List<(int Id, Maker Maker, ItemRecipeDefinition Recipe)>();
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
            if (!ok)
            {
                PluginLog.Info($"Batch of {job.Crafts} x {NameOf(job.Item)} refused by '{maker.Name}', {Ledger.Waiting.GetValueOrDefault(job.Item)} waiting");
                Notes[job.Item] = Tr.T("note.refused", maker.Name);
                continue;
            }
            if (!QueuedSince.TryGetValue(job.Item, out var since)) since = (0, 0, new HashSet<int>());
            since.Crafters.Add(job.Crafter);
            QueuedSince[job.Item] = (since.Batches + 1, since.Crafts + job.Crafts, since.Crafters);
            var handed = (long)Math.Ceiling(job.Crafts * want.PerCraft);
            Ledger.MarkQueued(job.Crafter, job.Item, handed);
            HandedOut[job.Item] = (HandedOut.TryGetValue(job.Item, out var handedBefore) ? handedBefore : 0) + handed;
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

        // The "Queued" log line per part: the first batch, then at most once a minute, and the last batch.
        foreach (var itemId in QueuedSince.Keys.ToList())
        {
            var first = !QueuedLogged.TryGetValue(itemId, out var loggedAt);
            var waitingNow = Ledger.Waiting.GetValueOrDefault(itemId);
            if (!first && waitingNow > 0 && now - loggedAt < 60) continue;
            var (batches, crafts, crafters) = QueuedSince[itemId];
            PluginLog.Info($"{(OrderJob.DryRun ? "DRY RUN: would queue" : "Queued")} {batches} batch(es), {crafts} craft(s) of {NameOf(itemId)} in "
                           + string.Join(", ", crafters.Select(id => CrafterNames.TryGetValue(id, out var n) ? n : "?").GroupBy(n => n).OrderBy(g => g.Key)
                               .Select(g => g.Count() > 1 ? $"'{g.Key}' x{g.Count()}" : $"'{g.Key}'")) + $", {waitingNow} waiting");
            QueuedLogged[itemId] = now;
            QueuedSince.Remove(itemId);
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

        // M3: blocks whose parts are all handed out are done: they leave the list now, not when the crafters finish
        // (Lone 2026-11-19: "if plugin ordered all parts to producers we can say that part is completed").
        // Settings: "Keep list when done" leaves the list as it is, so the same build can be ordered again.
        var keepList = PlannerSettings.Current.KeepListWhenDone;
        var listChanged = false;
        var done = BlockSets.Completed(groups, HandedOut);
        for (var g = 0; g < Groups.Count; g++)
        {
            var group = Groups[g];
            if (done[g] <= group.Done) continue;
            var newlyDone = done[g] - group.Done;
            group.Done = done[g];
            if (!keepList && TakeOff(group.EntryKey, group.Blocks.Name, group.Blocks.Parts, newlyDone, "all parts handed to crafters") > 0) listChanged = true;
        }
        if (!Groups.Any(g => g.EntryKey is ProjectKey && g.Done < g.Blocks.Count)) ProjectOrderedWith = null;

        if (wasActive && !Ledger.Active)
        {
            // All orders are done: the crafters made every part. The blocks left the list as their parts were handed
            // out; the orders are removed. Stopped orders never get here (Reset clears wasActive).
            PlanStore.SayDone(Tr.T(keepList ? "hud.doneKept" : "hud.done"));
            PluginLog.Info($"All orders done: {Groups.Sum(gr => gr.Done)} block(s) made, orders removed.");
            Reset();
            return true;
        }
        wasActive = Ledger.Active;
        ExplainWaits(byId, allGrids);
        Remember();
        Publish();
        return listChanged;
    }

    /// <summary>
    /// Why each crafter that holds a planner line is not crafting it (Lone 2026-11-20: a Smelter "waiting for
    /// sub-parts" read wrong; Smelters and Refineries wait for ore, only Assemblers and Fabricators for parts): its
    /// recipe's inputs that storage lacks, ore named as ore; nothing lacking: earlier jobs in its queue come first.
    /// Its output full (cargo full, K4) comes before both: nothing else matters until there is room.
    /// </summary>
    private static void ExplainWaits(Dictionary<int, Maker> byId, IEnumerable<GridStock> allGrids)
    {
        WaitReasons.Clear();
        var lines = Ledger.QueuedLines.Where(q => !crafting.Contains(q.Crafter)).ToList();
        if (lines.Count == 0) return;
        var stock = new Dictionary<ItemDefinition, double>();
        foreach (var store in allGrids.SelectMany(g => g.Stores))
            foreach (var (item, amount) in store.Items)
                stock[item] = stock.GetValueOrDefault(item) + amount;
        foreach (var (crafterId, itemId, _) in lines)
        {
            if (!Items.TryGetValue(itemId, out var item)) continue;
            // Queued before the load: the crafters are not known; any crafter that makes it has the same recipe.
            var maker = crafterId == OrderLedger.Unassigned
                ? byId.Values.FirstOrDefault(m => CrafterApi.CanProduce(m.Converter, item) != null)
                : byId.GetValueOrDefault(crafterId);
            if (maker == null) continue;
            if (crafterId == OrderLedger.Unassigned && HoldersOf(byId, itemId).Any(crafting.Contains))
            {
                WaitReasons[(crafterId, itemId)] = Tr.T("detail.crafting");
                continue;
            }
            if (crafterId != OrderLedger.Unassigned && CrafterApi.OutputFull(maker))
            {
                WaitReasons[(crafterId, itemId)] = Tr.T("detail.outputFull");
                continue;
            }
            var recipe = CrafterApi.CanProduce(maker.Converter, item);
            if (recipe == null) continue;
            var lacking = CrafterApi.Inputs(recipe).Where(i => stock.GetValueOrDefault(i.Item) < i.Amount).Select(i => i.Item).ToList();
            var ore = lacking.Where(i => byId.Values.All(m => CrafterApi.CanProduce(m.Converter, i) == null)).ToList();
            WaitReasons[(crafterId, itemId)] = ore.Count > 0 ? Tr.T("detail.waitOre", string.Join(", ", ore.Select(OrderJob.NameOf)))
                : lacking.Count > 0 ? Tr.T("detail.waitPart", string.Join(", ", lacking.Select(OrderJob.NameOf)))
                : Tr.T("detail.earlierJobs");
        }
    }

    /// <summary>Storage the crafters reach, by item id (linked storage on the crafters' grid and on the other grids).</summary>
    private static Dictionary<string, long> StockById(IEnumerable<GridStock> allGrids)
    {
        var stock = new Dictionary<string, long>();
        foreach (var store in allGrids.SelectMany(g => g.Stores).Where(s => !unlinked.Contains(s.BlockEntity)))
            foreach (var (item, amount) in store.Items)
            {
                var id = PlanReport.Id(item);
                stock[id] = stock.GetValueOrDefault(id) + (long)Math.Floor(amount);
            }
        return stock;
    }

    /// <summary>
    /// K2: while parts show "no progress", looks up the ore they still lack (their recipe trees down to ore, minus
    /// what storage holds; Policy/OreShort) for the HUD and the window. Not every tick: it reads the crafters' recipes.
    /// A failed lookup leaves the old text of "no progress" (never blocks anything).
    /// </summary>
    private static void UpdateOreShort(GridStock scan, IEnumerable<GridStock> allGrids, double now, bool force)
    {
        var stuck = Notes.Where(n => n.Value == NoProgressNote).Select(n => n.Key).Where(Items.ContainsKey).ToList();
        if (stuck.Count == 0)
        {
            if (oreShortText.Length > 0) PluginLog.Info("Ore short for running orders: no longer.");
            oreShortHud = "";
            oreShortText = "";
            Told.RemoveWhere(k => k.StartsWith("ore:"));
            return;
        }
        if (!force && now - oreShortAt < OreShortEverySeconds) return;
        oreShortAt = now;
        try
        {
            var running = Ledger.IncomingAll().Where(i => stuck.Contains(i.Key)).ToDictionary(i => i.Key, i => i.Value);
            var probe = new PlanReport();
            var recipes = PlanReport.ReadRecipes(scan, stuck.Select(id => Items[id]), probe);
            foreach (var pair in probe.Items) Items[pair.Key] = pair.Value; // names of the ore
            var shortage = OreShort.Of(running, StockById(allGrids), recipes);
            var hud = OreShort.Format(shortage, NameOf, (name, amount) => Tr.T("ore.more", amount.ToString("N0"), name));
            var window = OreShort.Format(shortage, NameOf, (name, amount) => $"{name} {amount:N0}");
            if (window != oreShortText && window.Length > 0) PluginLog.Info("Ore short for running orders: " + window);
            oreShortHud = hud;
            oreShortText = window;
        }
        catch (Exception e)
        {
            PluginLog.Warn("Ore shortage check failed, the plain no-progress text stays: " + e.Message);
            oreShortHud = "";
            oreShortText = "";
        }
    }

    /// <summary>
    /// Ordered parts that another ordered part still waiting or in a crafter is made from, at any depth of its recipe
    /// tree (Policy/Dispatch.Held): they are handed out after it. The recipes are the ones the crafters here would use.
    /// </summary>
    /// <summary>Parts held back this tick -> the ordered part they come after (for the window's "next").</summary>
    private static Dictionary<string, string> HeldNow = new();

    /// <summary>Why a crafter with a planner line is not crafting it (window detail), per crafter and part.</summary>
    private static readonly Dictionary<(int Crafter, string Item), string> WaitReasons = new();

    /// <summary>Queue reads that said a crafter no longer has its line's recipe, per crafter and part (see 1b in Process).</summary>
    private static readonly Dictionary<(int Crafter, string Item), int> NotProducingSeen = new();

    /// <summary>Recipe inputs per part, read once per order (reflection on every crafter is not done every tick).</summary>
    private static readonly Dictionary<string, IReadOnlyCollection<string>> InputsOf = new();

    private static Dictionary<string, string> HeldParts(Dictionary<int, Maker> byId)
    {
        var incoming = Ledger.IncomingAll().Keys.ToList();
        // An ordered part a crafter still has in its queue counts too, whatever the ledger says (a part counted as made
        // too early must not let its inputs out).
        foreach (var itemId in Ledger.Sequence.Where(i => !incoming.Contains(i) && Items.ContainsKey(i)))
            if (byId.Values.Any(m => CrafterApi.CanProduce(m.Converter, Items[itemId]) is { } recipe && CrafterApi.IsProducing(m.Converter, recipe) == true))
                incoming.Add(itemId);
        var inputsOf = InputsOf;
        var open = new Queue<ItemDefinition>(incoming.Where(Items.ContainsKey).Select(id => Items[id]));
        while (open.Count > 0)
        {
            var item = open.Dequeue();
            var id = PlanReport.Id(item);
            if (inputsOf.ContainsKey(id)) continue;
            var inputs = new HashSet<string>();
            foreach (var maker in byId.Values)
            {
                var recipe = CrafterApi.CanProduce(maker.Converter, item);
                if (recipe == null) continue;
                foreach (var input in CrafterApi.Inputs(recipe))
                    if (inputs.Add(PlanReport.Id(input.Item))) open.Enqueue(input.Item);
            }
            inputsOf[id] = inputs;
        }
        return Dispatch.Held(inputsOf, incoming);
    }

    /// <summary>
    /// First pass after a load: the parts saved as queued stay one line per part ("queued before the load"), not split
    /// over crafters: the planner cannot tell how many each crafter has, and an even split counted parts as made while
    /// another crafter still made them (Lone 2026-11-20: "restart eats some parts?"). The line closes by arrivals, or
    /// when no crafter has the part in its queue any more (two reads in a row). None has it right after the load: the
    /// queue was lost, the parts are made again.
    /// </summary>
    private static void FindQueuedAgain(Dictionary<int, Maker> byId)
    {
        restored = false;
        CrafterNames[OrderLedger.Unassigned] = Tr.T("detail.fromSave");
        foreach (var itemId in Ledger.UnassignedItems())
        {
            var amount = Ledger.QueuedLines.Where(q => q.Crafter == OrderLedger.Unassigned && q.Item == itemId).Sum(q => q.Amount);
            var holders = HoldersOf(byId, itemId);
            if (holders.Count == 0)
            {
                // Lost with the queue (Lone 2026-11-20: "not crafted parts, try make it"): they wait for a crafter
                // again, and leave the handed-out count until they are handed out again.
                var again = Ledger.Requeue(itemId);
                if (HandedOut.TryGetValue(itemId, out var handed)) HandedOut[itemId] = Math.Max(0, handed - again);
            }
            PluginLog.Info(holders.Count > 0
                ? $"Orders after load: {amount} {NameOf(itemId)} still in the queues of {holders.Count} crafter(s)."
                : $"Orders after load: {amount} {NameOf(itemId)} in no crafter's queue any more, ordered again.");
        }
    }

    /// <summary>Crafters (ids) that have this part's recipe in their current job or queue.</summary>
    private static List<int> HoldersOf(Dictionary<int, Maker> byId, string itemId)
    {
        var holders = new List<int>();
        if (!Items.TryGetValue(itemId, out var item)) return holders;
        foreach (var pair in byId)
        {
            var recipe = CrafterApi.CanProduce(pair.Value.Converter, item);
            if (recipe != null && CrafterApi.IsProducing(pair.Value.Converter, recipe) == true) holders.Add(pair.Key);
        }
        return holders;
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
            else if (HeldNow.TryGetValue(itemId, out var after))
            {
                state = OrderState.Next;
                note = Tr.T("note.after", NameOf(after));
            }
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
                    crafting.Contains(q.Crafter) ? Tr.T("detail.crafting")
                    : WaitReasons.TryGetValue((q.Crafter, q.Item), out var why) ? why : Tr.T("detail.waiting")))),
                Note = note == NoProgressNote ? "" : note,
            });
        }
        Lines = lines;
    }
}
