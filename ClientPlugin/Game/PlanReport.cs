using System;
using System.Collections.Generic;
using System.Linq;
using ClientPlugin.Policy;
using Keen.Game2.Simulation.WorldObjects.Items;

namespace ClientPlugin.Game;

/// <summary>
/// The answer for one grid: what to craft (bottom-up), what is missing, and the alerts that say why a build is not
/// possible (ore deficit, no crafter of the needed kind, no conveyor link, ...). The planner screen shows the alerts;
/// until it exists they go to the game log.
/// </summary>
internal sealed class PlanReport
{
    public GridStock Grid;
    public TreeResult Tree;
    public List<PlanAlert> Alerts = new();
    public readonly Dictionary<string, ItemDefinition> Items = new();

    public bool CanBuild => PlanAlerts.CanBuild(Alerts);

    /// <summary>Everything the plan asks for, by item id.</summary>
    public Dictionary<string, long> Wanted = new();
    /// <summary>What an order would make: everything ("order all") or only the missing parts ("order missing").</summary>
    public Dictionary<string, long> Need = new();
    /// <summary>Stock the crafters can reach, by item id.</summary>
    public Dictionary<string, long> Stock = new();
    /// <summary>Blocks with no conveyor link (entities); the order book skips crafters in this set.</summary>
    public HashSet<object> Unlinked = new();

    public static string Id(ItemDefinition item) => item.Guid.ToString();

    public string NameOf(string id) => Items.TryGetValue(id, out var item) ? OrderJob.NameOf(item) : id;

    /// <summary>Ore by the game's item type (ItemDefinition.Type has the Ore flag), read by name so an enum change can't break the build.</summary>
    public bool IsOre(string id) => Items.TryGetValue(id, out var item)
                                    && (Reflect.Get(Reflect.Member(item.GetType(), "Type"), item)?.ToString() ?? "").Contains("Ore", StringComparison.OrdinalIgnoreCase);

    /// <param name="ledger">Running orders: the ore and sub-parts they still need are not available to this plan.</param>
    /// <param name="onlyMissing">False = make every part of the list; true = only what stock and running orders do not cover.</param>
    /// <param name="ledgerItems">Item definitions of the ledger's ids (to read their recipes).</param>
    /// <param name="otherGrids">The player's other grids: their storage counts when the crafters reach it through conveyors (connectors).</param>
    public static PlanReport Build(GridStock grid, IReadOnlyDictionary<ItemDefinition, long> wanted,
        OrderLedger ledger, IReadOnlyDictionary<string, ItemDefinition> ledgerItems, IReadOnlyCollection<GridStock> otherGrids,
        bool onlyMissing)
    {
        var report = new PlanReport { Grid = grid };
        foreach (var pair in ledgerItems) report.Items[pair.Key] = pair.Value;
        foreach (var item in wanted.Keys) report.Items[Id(item)] = item;
        foreach (var store in grid.Stores)
            foreach (var item in store.Items.Keys) report.Items[Id(item)] = item;

        var facts = new PlanFacts { NonFunctionalCrafters = grid.NonFunctionalCrafters };
        var unlinkedBlocks = CheckConveyors(grid, wanted, facts);

        // Stock that the crafters can really reach: everything except storage with no conveyor link.
        var usable = new Dictionary<string, long>();
        var stranded = new Dictionary<string, long>();
        foreach (var store in grid.Stores)
        {
            var target = unlinkedBlocks.Contains(store.BlockEntity) ? stranded : usable;
            foreach (var pair in store.Items)
            {
                var id = Id(pair.Key);
                target[id] = (target.TryGetValue(id, out var sum) ? sum : 0) + (long)Math.Floor(pair.Value);
            }
        }

        // The storages whose free capacity the new parts will use: linked storage here and linked storage on other grids.
        var spaceStores = grid.Stores.Where(s => s.IsStorage && !unlinkedBlocks.Contains(s.BlockEntity)).ToList();

        // Storage on other grids (docked ship, base module behind a connector) that the crafters reach by conveyor.
        var remoteStorages = 0;
        var reachProbe = wanted.Keys.FirstOrDefault() ?? grid.Stores.SelectMany(s => s.Items.Keys).FirstOrDefault();
        if (reachProbe != null && otherGrids.Count > 0)
        {
            var reach = new HashSet<InventoryComponent>();
            foreach (var maker in grid.Makers.Where(m => !unlinkedBlocks.Contains(m.Entity)))
            {
                foreach (var inventory in Conveyors.ReachableSet(maker.Input, reachProbe)) reach.Add(inventory);
                foreach (var inventory in Conveyors.ReachableSet(maker.Output, reachProbe)) reach.Add(inventory);
            }
            foreach (var store in otherGrids.SelectMany(g => g.Stores).Where(s => s.IsStorage && reach.Contains(s.Inventory)))
            {
                remoteStorages++;
                spaceStores.Add(store);
                foreach (var pair in store.Items)
                {
                    var id = Id(pair.Key);
                    report.Items[id] = pair.Key;
                    usable[id] = (usable.TryGetValue(id, out var sum) ? sum : 0) + (long)Math.Floor(pair.Value);
                }
            }
        }
        PluginLog.Info($"Stock: {grid.Stores.Count} inventories on the crafters' grid ({unlinkedBlocks.Count} blocks not linked), {remoteStorages} linked storage(s) on other grids, {grid.Makers.Count} crafter(s): "
                       + string.Join(", ", grid.Makers.Select(m => $"{m.Name} [{m.Machine}]")));

        var incoming = ledger.IncomingAll();
        var recipes = ReadRecipes(grid, wanted.Keys.Concat(incoming.Keys.Where(report.Items.ContainsKey).Select(id => report.Items[id])), report);
        var wantedIds = wanted.Where(w => w.Value > 0).ToDictionary(w => Id(w.Key), w => w.Value);

        // Earlier orders still running: the ore and sub-parts they will use are taken off the stock first.
        // Their own parts are made regardless of stock, so stock of those items is not offered to them.
        var forOld = usable.Where(u => !incoming.ContainsKey(u.Key)).ToDictionary(u => u.Key, u => u.Value);
        var oldTree = RecipeTree.Resolve(incoming, forOld, recipes);
        var left = new Dictionary<string, long>(usable);
        foreach (var used in oldTree.Consumed)
            left[used.Key] = Math.Max(0, (left.TryGetValue(used.Key, out var have) ? have : 0) - used.Value);

        // The parts the plan asks for: their stock is already handled by the order mode, so it must not be used twice.
        report.Wanted = wantedIds;
        report.Stock = usable;
        report.Unlinked = unlinkedBlocks;
        // What the player carries is not counted: only storage on the conveyor network (the vanilla backpack is small).
        report.Need = ledger.NeedForNew(wantedIds, usable, onlyMissing);
        foreach (var id in wantedIds.Keys) left[id] = 0;
        report.Tree = RecipeTree.Resolve(report.Need, left, recipes);

        foreach (var maker in grid.Makers.Where(m => m.Linked))
            facts.UsableCrafters[maker.Machine] = (facts.UsableCrafters.TryGetValue(maker.Machine, out var n) ? n : 0) + 1;
        facts.StorageCount = grid.Stores.Where(s => s.IsStorage && !unlinkedBlocks.Contains(s.BlockEntity)).Select(s => s.BlockEntity).Distinct().Count() + remoteStorages;
        foreach (var pair in stranded)
            if (report.Tree.Touched.Contains(pair.Key) && pair.Value > 0) facts.StockInUnlinked[pair.Key] = pair.Value;

        report.Alerts = PlanAlerts.Build(report.Tree, facts, report.NameOf, report.IsOre);

        var space = CargoSpace(report, spaceStores, incoming);
        if (space != null) report.Alerts = report.Alerts.Append(space).OrderByDescending(a => a.Level).ToList();
        return report;
    }

    /// <summary>
    /// Do the parts of this order fit into the storage the crafters reach? The game limits an inventory by mass
    /// (InventoryComponent.Mass = max, current). The item mass unit may differ from the inventory unit, so the scale is
    /// taken from the stored items themselves (current / sum of amount * MassPerUnit). Parts already on order count too.
    /// </summary>
    private static PlanAlert CargoSpace(PlanReport report, List<StoreStock> stores, IReadOnlyDictionary<string, long> incoming)
    {
        double MassPer(ItemDefinition item)
        {
            try { return Fx.ToDouble(item.MassPerUnit); }
            catch (Exception) { return 0; }
        }

        double PerPiece(string id) => report.Items.TryGetValue(id, out var item) ? MassPer(item) : 0;

        // The maths (scale, free capacity, thresholds) is pure code in Policy/CargoLimit.cs, covered by the tests.
        var masses = stores.Select(s => new StoreMass { Max = s.MassMax, Now = s.MassNow, Calc = s.MassCalc }).ToList();
        var check = CargoLimit.Check(masses, report.Need, incoming, PerPiece, report.NameOf);
        if (check == null) return null;

        PluginLog.Info($"Cargo space: {check.Storages} storage inventories, free {check.Free:N0}, stored {check.Stored:N0} (items add up to {check.Calc:N0}, scale {check.Scale:0.###}); "
                       + string.Join(", ", report.Need.Take(6).Select(n => $"{report.NameOf(n.Key)} {n.Value:N0} x {PerPiece(n.Key) * check.Scale:0.##}")));
        return check.Alert;
    }

    /// <summary>
    /// Same idea as LW Cargo Manager's [Alerts] check: blocks are grouped by conveyor reach and every block outside the
    /// biggest group is "not linked". Nodes are the storage blocks and the usable crafters. Marks Maker.Linked and fills
    /// the unlinked lists in <paramref name="facts"/>; returns the unlinked block entities.
    /// </summary>
    private static HashSet<object> CheckConveyors(GridStock grid, IReadOnlyDictionary<ItemDefinition, long> wanted, PlanFacts facts)
    {
        var unlinked = new HashSet<object>();
        var probe = grid.Stores.SelectMany(s => s.Items.Keys).FirstOrDefault() ?? wanted.Keys.FirstOrDefault();
        if (probe == null) return unlinked; // nothing to probe with: cannot judge, assume linked

        // One node per block: storages by their first inventory, crafters by their output inventory.
        var nodes = new List<(int Id, string Name)>();
        var source = new List<Keen.Game2.Simulation.WorldObjects.Items.InventoryComponent>();
        var entityOf = new List<object>();
        var machineOf = new List<string>();
        var nodeOfInventory = new Dictionary<InventoryComponent, int>();
        var seen = new HashSet<object>();

        void AddNode(object entity, string name, InventoryComponent from, string machine, params InventoryComponent[] all)
        {
            if (!seen.Add(entity)) return;
            var id = nodes.Count;
            nodes.Add((id, name + "\u001f" + id));
            source.Add(from);
            entityOf.Add(entity);
            machineOf.Add(machine);
            foreach (var inventory in all) nodeOfInventory[inventory] = id;
        }

        foreach (var store in grid.Stores.Where(s => s.IsStorage))
            AddNode(store.BlockEntity, store.Name, store.Inventory, null, grid.Stores.Where(s => s.BlockEntity == store.BlockEntity).Select(s => s.Inventory).ToArray());
        foreach (var maker in grid.Makers)
            AddNode(maker.Entity, maker.Name, maker.Output, maker.Machine, maker.Input, maker.Output);

        var names = ConveyorCheck.Unlinked(nodes, id =>
        {
            var reached = new HashSet<int>();
            foreach (var inventory in Conveyors.ReachableSet(source[id], probe))
                if (nodeOfInventory.TryGetValue(inventory, out var other)) reached.Add(other);
            return reached;
        });

        foreach (var tagged in names)
        {
            var id = int.Parse(tagged.Substring(tagged.LastIndexOf('\u001f') + 1));
            var name = tagged.Substring(0, tagged.LastIndexOf('\u001f'));
            unlinked.Add(entityOf[id]);
            if (machineOf[id] == null) facts.UnlinkedStorages.Add(name);
            else facts.UnlinkedCrafters.Add((name, machineOf[id]));
            PluginLog.Warn($"Conveyor check: '{name}' is not linked to the main conveyor network (probe item {OrderJob.NameOf(probe)})");
        }
        foreach (var maker in grid.Makers)
            maker.Linked = !unlinked.Contains(maker.Entity);
        return unlinked;
    }

    /// <summary>
    /// Recipes from the game itself: for every item the plan touches, ask the crafters on the grid
    /// (CanProduceItem) and read the recipe's inputs and outputs. Items no crafter here can make have no recipe: they
    /// count as raw (ore) or as "crafter missing" in the alerts.
    /// </summary>
    private static Dictionary<string, Recipe> ReadRecipes(GridStock grid, IEnumerable<ItemDefinition> wanted, PlanReport report)
    {
        var recipes = new Dictionary<string, Recipe>();
        var queue = new Queue<ItemDefinition>(wanted);
        var seen = new HashSet<string>();
        while (queue.Count > 0 && seen.Count < 300)
        {
            var item = queue.Dequeue();
            var id = Id(item);
            if (!seen.Add(id)) continue;
            foreach (var maker in grid.Makers)
            {
                var recipe = CrafterApi.CanProduce(maker.Converter, item);
                if (recipe == null) continue;
                var produced = CrafterApi.Outputs(recipe).Where(o => o.Item == item).Sum(o => o.Amount);
                var inputs = new Dictionary<string, int>();
                foreach (var input in CrafterApi.Inputs(recipe))
                {
                    var inputId = Id(input.Item);
                    report.Items[inputId] = input.Item;
                    inputs[inputId] = (inputs.TryGetValue(inputId, out var have) ? have : 0) + (int)Math.Ceiling(input.Amount);
                    queue.Enqueue(input.Item);
                }
                recipes[id] = new Recipe { Item = id, OutAmount = (int)Math.Max(1, Math.Round(produced)), Machine = maker.Machine, Inputs = inputs };
                break;
            }
        }
        return recipes;
    }
}
