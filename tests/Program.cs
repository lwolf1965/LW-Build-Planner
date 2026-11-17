using System.Linq;
using System;
using System.Collections.Generic;
using ClientPlugin.Policy;

var fails = 0;
void Check(bool ok, string name)
{
    Console.WriteLine((ok ? "ok   " : "FAIL ") + name);
    if (!ok) fails++;
}

var list = new List<PlannedBlock>();
BuildPlan.Add(list, "Thruster", 10);
BuildPlan.Add(list, "Thruster", 5);
BuildPlan.Add(list, "Tank", 4);
Check(list.Count == 2 && list[0].Count == 15, "add merges same block");
BuildPlan.Add(list, "Tank", -10);
Check(list.Count == 1, "removing more than present drops the line");

var costs = new Dictionary<string, IReadOnlyDictionary<string, int>>
{
    ["Thruster"] = new Dictionary<string, int> { ["SteelPlate"] = 20, ["Motor"] = 2 },
};
var need = BuildPlan.Need(list, costs);
Check(need["SteelPlate"] == 300 && need["Motor"] == 30, "need = cost x count");

var lines = BuildPlan.Compare(need, new Dictionary<string, long> { ["SteelPlate"] = 120 });
Check(lines.Find(l => l.ItemId == "SteelPlate").Missing == 180, "missing = need - have");
Check(lines.Find(l => l.ItemId == "Motor").Missing == 30, "no stock = all missing");

Check(OrderPlan.MayUse("Assembler 1") && OrderPlan.MayUse("") && OrderPlan.MayUse(null), "no tag needed: every crafter may be used");
Check(!OrderPlan.MayUse("Assembler [NoMake]") && !OrderPlan.MayUse("x [no  make] y"), "[NoMake] opts a crafter out");
Check(!OrderPlan.MayUse("Gearforge [Make Ammo]"), "[Make Ammo] belongs to Cargo Manager");
Check(OrderPlan.Batches(180, 20, 10) == 9, "batches cover the missing amount");
Check(OrderPlan.Batches(1000, 20, 10) == 10, "batch limit caps the order");
Check(OrderPlan.Batches(0, 20, 10) == 0 && OrderPlan.Batches(5, 0, 10) == 0, "nothing missing or bad recipe = 0");
Check(PlanKinds.RoleOf(PlanKinds.Assembler) == PlanRole.Producer && PlanKinds.RoleOf(null) == PlanRole.Consumer, "roles");

Check(PlanKinds.MachineOf(PlanKinds.Smelter) == "Smelter" && PlanKinds.MachineOf(PlanKinds.Gearforge) == "Gearforge"
      && PlanKinds.MachineOf(PlanKinds.CargoContainer) == null && PlanKinds.MachineOf(null) == null, "machine names");
Check(PlanKinds.RoleOf(PlanKinds.Refinery) == PlanRole.Producer && PlanKinds.RoleOf(PlanKinds.Locker) == PlanRole.Storage, "all crafters are producers");
Check(OrderPlan.Split(5, 2, 10).SequenceEqual(new[] { 3, 2 }), "split spreads crafts evenly");
Check(OrderPlan.Split(25, 2, 10).Sum() == 20, "split respects the per-crafter limit");
Check(OrderPlan.Split(3, 0, 10).Length == 0 && OrderPlan.Split(0, 2, 10).Sum() == 0, "split: no crafters or nothing to do");

// Recipe tree, with real SE2 recipes (data version 4.0.0.421): AdvancedCompressor needs Fabricator > Assembler > Smelter/Refinery > ore.
Recipe R(string item, string machine, params (string, int)[] inputs)
{
    var d = new Dictionary<string, int>();
    foreach (var (i, n) in inputs) d[i] = n;
    return new Recipe { Item = item, Machine = machine, Inputs = d };
}
var recipes = new Dictionary<string, Recipe>();
foreach (var r in new[]
{
    R("AdvancedCompressor", "Fabricator", ("Compressor", 1), ("PressurePipe", 2), ("TitaniumSheet", 8)),
    R("Compressor", "Assembler", ("Motor", 2), ("Wire", 2), ("SteelTube", 1)),
    R("PressurePipe", "Assembler", ("TurbineBlades", 2), ("LeadBar", 1), ("SteelTube", 1)),
    R("TitaniumSheet", "Refinery", ("OreTitanium", 30)),
    R("Motor", "Smelter", ("OreNickel", 25)),
    R("Wire", "Smelter", ("OreCopper", 20)),
    R("SteelTube", "Smelter", ("OreIron", 5)),
    R("TurbineBlades", "Smelter", ("OreNickel", 10)),
    R("LeadBar", "Smelter", ("OreLead", 50)),
})
    recipes[r.Item] = r;

Check(RecipeTree.Depth("OreIron", recipes) == 0 && RecipeTree.Depth("SteelTube", recipes) == 1
      && RecipeTree.Depth("Compressor", recipes) == 2 && RecipeTree.Depth("AdvancedCompressor", recipes) == 3, "depth: ore 0, smelter 1, assembler 2, fabricator 3");

var tree = RecipeTree.Resolve(new Dictionary<string, long> { ["AdvancedCompressor"] = 1 }, new Dictionary<string, long>(), recipes);
long Crafts(TreeResult t, string item) => t.Orders.Find(o => o.Item == item)?.Crafts ?? 0;
Check(Crafts(tree, "AdvancedCompressor") == 1 && Crafts(tree, "Compressor") == 1 && Crafts(tree, "PressurePipe") == 2, "top parts crafted");
Check(Crafts(tree, "SteelTube") == 3 && Crafts(tree, "Motor") == 2 && Crafts(tree, "TurbineBlades") == 4, "shared part (SteelTube) counted once across the tree");
Check(tree.RawShortage["OreNickel"] == 90 && tree.RawShortage["OreIron"] == 15 && tree.RawShortage["OreTitanium"] == 240
      && tree.RawShortage["OreLead"] == 100 && tree.RawShortage["OreCopper"] == 40, "raw ore totals");
Check(tree.Orders[0].Depth == 1 && tree.Orders[^1].Item == "AdvancedCompressor", "orders go bottom-up");
Check(tree.Orders.Find(o => o.Item == "Compressor").Machine == "Assembler", "machine kept per order");

var withStock = RecipeTree.Resolve(new Dictionary<string, long> { ["AdvancedCompressor"] = 1 },
    new Dictionary<string, long> { ["SteelTube"] = 2, ["Compressor"] = 1, ["OreTitanium"] = 240 }, recipes);
Check(Crafts(withStock, "Compressor") == 0 && Crafts(withStock, "Motor") == 0, "stocked part is not crafted again, nor its inputs");
Check(Crafts(withStock, "SteelTube") == 0 && !withStock.RawShortage.ContainsKey("OreIron"), "stocked SteelTube covers the pipes, no iron needed");
Check(!withStock.RawShortage.ContainsKey("OreTitanium"), "enough ore in stock = no shortage");

var sharedStock = RecipeTree.Resolve(new Dictionary<string, long> { ["AdvancedCompressor"] = 1 },
    new Dictionary<string, long> { ["SteelTube"] = 2 }, recipes);
Check(Crafts(sharedStock, "SteelTube") == 1 && sharedStock.RawShortage["OreIron"] == 5, "stock of a shared part is used once (3 needed, 2 in stock)");

Check(tree.Orders.Count(o => o.TopLevel) == 1 && tree.Orders.Single(o => o.TopLevel).Item == "AdvancedCompressor", "only the asked part is top level (the game cascades the rest)");

// Alerts
string Nm(string id) => id;
bool Ore(string id) => id.StartsWith("Ore");
PlanFacts Good()
{
    var f = new PlanFacts { StorageCount = 2 };
    foreach (var m in new[] { "Smelter", "Refinery", "Assembler", "Fabricator" }) f.UsableCrafters[m] = 1;
    return f;
}
var okTree = RecipeTree.Resolve(new Dictionary<string, long> { ["AdvancedCompressor"] = 1 },
    new Dictionary<string, long> { ["OreIron"] = 100, ["OreTitanium"] = 500, ["OreNickel"] = 200, ["OreCopper"] = 100, ["OreLead"] = 200 }, recipes);
Check(PlanAlerts.CanBuild(PlanAlerts.Build(okTree, Good(), Nm, Ore)), "enough ore + all crafters = can build");

var oreAlerts = PlanAlerts.Build(tree, Good(), Nm, Ore);
Check(!PlanAlerts.CanBuild(oreAlerts) && oreAlerts.Any(a => a.Level == AlertLevel.Error && a.Text.Contains("Ore deficit: OreTitanium, 240")), "ore deficit is an error with the amount");

var noFab = Good(); noFab.UsableCrafters.Remove("Fabricator");
var noFabAlerts = PlanAlerts.Build(okTree, noFab, Nm, Ore);
Check(!PlanAlerts.CanBuild(noFabAlerts) && noFabAlerts.Any(a => a.Text.StartsWith("No Fabricator") && a.Text.Contains("AdvancedCompressor")), "missing crafter kind blocks the build");

var unlinked = Good(); unlinked.UsableCrafters.Remove("Assembler"); unlinked.UnlinkedCrafters.Add(("Assembler 2", "Assembler"));
var unlinkedAlerts = PlanAlerts.Build(okTree, unlinked, Nm, Ore);
Check(unlinkedAlerts.Any(a => a.Level == AlertLevel.Error && a.Text.Contains("Assembler 2") && a.Text.Contains("reach storage")), "crafter with no conveyor link is named");
Check(unlinkedAlerts.Any(a => a.Level == AlertLevel.Warning && a.Text.Contains("no conveyor link")), "unlinked crafter also warns");

var noStorage = Good(); noStorage.StorageCount = 0;
Check(!PlanAlerts.CanBuild(PlanAlerts.Build(okTree, noStorage, Nm, Ore)), "no storage blocks the build");

var stranded = Good(); stranded.StockInUnlinked["OreIron"] = 500;
var strandedAlerts = PlanAlerts.Build(okTree, stranded, Nm, Ore);
Check(PlanAlerts.CanBuild(strandedAlerts) && strandedAlerts.Any(a => a.Level == AlertLevel.Warning && a.Text.Contains("OreIron: 500")), "stock behind no link is only a warning");
Check(PlanAlerts.Build(new TreeResult(), Good(), Nm, Ore).Count == 0, "nothing to build = no alerts");
Check(oreAlerts[0].Level == AlertLevel.Error, "errors sorted first");
Check(okTree.Touched.Contains("OreIron") && okTree.Touched.Contains("AdvancedCompressor"), "touched includes inputs and wanted");

// Order ledger: "order all" always builds, "order missing" counts stock and running orders
var ledger = new OrderLedger();
var stockNow = new Dictionary<string, long> { ["SteelPlate"] = 144 };
var first = new Dictionary<string, long> { ["SteelPlate"] = 1400, ["SteelTube"] = 200 };
Check(ledger.NeedForNew(first, stockNow, false)["SteelPlate"] == 1400, "order all: the full amount, whatever is in stock");
var needFirst = ledger.NeedForNew(first, stockNow, true);
Check(needFirst["SteelPlate"] == 1256 && needFirst["SteelTube"] == 200, "order missing: wanted - stock");
ledger.Commit(needFirst);
Check(ledger.Active && ledger.Incoming("SteelPlate") == 1256, "ordered parts wait for a crafter");
ledger.MarkQueued(1, "SteelPlate", 628);
ledger.MarkQueued(2, "SteelPlate", 628);
Check(ledger.Waiting.ContainsKey("SteelPlate") == false && ledger.QueuedOf("SteelPlate") == 1256, "queued parts leave waiting");
Check(ledger.NeedForNew(first, stockNow, true).ContainsKey("SteelPlate") == false, "order missing again: running orders count, nothing ordered twice");
Check(ledger.NeedForNew(first, stockNow, false)["SteelPlate"] == 1400, "order all again: a second full batch");
ledger.CrafterDone(1); ledger.CrafterDone(2);
Check(ledger.Missing("SteelPlate", 1400, 1400) == 0 && ledger.Missing("SteelPlate", 1500, 1400) == 100, "finished parts are stock: missing = wanted - stock");
ledger.MarkQueued(3, "SteelTube", 200);
Check(ledger.CrafterDone(3).Single() == "SteelTube" && !ledger.Active, "a finished crafter closes its lines");
ledger.MarkQueued(1, "Wire", 100);
ledger.MarkQueued(2, "Wire", 50);
Check(ledger.Arrived("Wire", 120) == 0 && ledger.QueuedOf("Wire") == 30 && ledger.CraftersWithOrders.Single() == 2, "arrived parts close the queued lines in order");
Check(ledger.Arrived("Wire", 45) == 15 && !ledger.Active, "more arrived than ordered: lines closed, the rest is reported");
ledger.Commit(needFirst);
ledger.Clear();
Check(!ledger.Active, "clear forgets the orders");

var batch = new OrderLedger();
batch.Commit(new Dictionary<string, long> { ["A"] = 100, ["B"] = 10 });
batch.Commit(new Dictionary<string, long> { ["A"] = 50, ["C"] = 4 });
Check(batch.Ordered("A") == 150 && batch.Sequence.SequenceEqual(new[] { "A", "B", "C" }), "batch: ordered amounts add up and keep the order they were ordered in");
Check(batch.WaitingOrder().SequenceEqual(new[] { "A", "B", "C" }), "batch: waiting parts go to the crafters oldest first");
batch.MarkQueued(1, "A", 150);
batch.MarkQueued(2, "B", 10);
Check(batch.WaitingOrder().SequenceEqual(new[] { "C" }), "batch: queued parts leave the waiting order");
batch.Arrived("A", 60);
Check(batch.Done("A") == 60 && batch.Ordered("A") == 150 && batch.Active, "batch: arrived parts count as done");
batch.Arrived("A", 500);
batch.Arrived("B", 10);
Check(batch.Done("A") == 150 && batch.Done("B") == 10, "batch: done never exceeds ordered, even when a mod makes more");
batch.DropWaiting("C");
Check(!batch.Active && batch.Sequence.Count == 3 && batch.Done("A") == 150, "batch: finished lines keep their numbers until the next order");
batch.StopWaiting();
batch.Commit(new Dictionary<string, long> { ["D"] = 7 });
Check(batch.Sequence.SequenceEqual(new[] { "D" }) && batch.Ordered("A") == 0 && batch.Done("A") == 0, "batch: a new order after everything finished starts a fresh history");
batch.Commit(new Dictionary<string, long> { ["E"] = 1 });
Check(batch.Sequence.SequenceEqual(new[] { "D", "E" }), "batch: an order while another runs joins the same history");
batch.Clear();
Check(batch.Sequence.Count == 0 && batch.Ordered("D") == 0, "batch: clear forgets the history too");

var usedTree = RecipeTree.Resolve(new Dictionary<string, long> { ["SteelTube"] = 3 }, new Dictionary<string, long> { ["OreIron"] = 100, ["SteelTube"] = 1 }, recipes);
Check(usedTree.Consumed["SteelTube"] == 1 && usedTree.Consumed["OreIron"] == 10, "consumed stock is reported (1 tube, ore for 2 more)");

Check(PlanLimits.Step(true, false, true) == 100 && PlanLimits.Step(false, false, true) == 10 && PlanLimits.Step(false, true, false) == 5, "armor by the hundred, other blocks by the handful");
Check(PlanLimits.Apply(995, 100, true) == 1000 && PlanLimits.Apply(45, 10, false) == 50 && PlanLimits.Apply(3, -10, false) == 0, "counts stay between 0 and the limit");

var spaceNeed = new Dictionary<string, long> { ["Plate"] = 100, ["Tube"] = 50 };
double SpaceMass(string id) => id == "Plate" ? 5 : id == "Tube" ? 2 : 0;
string SpaceName(string id) => id;
var spaceOk = PlanAlerts.Space(spaceNeed, SpaceMass, 1000, SpaceName);
Check(spaceOk == null, "cargo space: plenty of capacity, no alert (need 600 of 1000)");
var spaceTight = PlanAlerts.Space(spaceNeed, SpaceMass, 700, SpaceName);
Check(spaceTight != null && spaceTight.Level == AlertLevel.Warning, "cargo space: above 80 % of the free capacity is a warning");
var spaceFull = PlanAlerts.Space(spaceNeed, SpaceMass, 500, SpaceName);
Check(spaceFull != null && spaceFull.Level == AlertLevel.Error && spaceFull.Text.Contains("600") && spaceFull.Text.Contains("Plate"), "cargo space: parts that do not fit are an error that names the biggest item");
Check(PlanAlerts.Space(spaceNeed, SpaceMass, double.NaN, SpaceName) == null && PlanAlerts.Space(spaceNeed, SpaceMass, -1, SpaceName) == null, "cargo space: unknown free capacity gives no alert");
Check(PlanAlerts.Space(new Dictionary<string, long> { ["Mystery"] = 10 }, SpaceMass, 0, SpaceName) == null, "cargo space: parts with unknown mass are not counted");
Check(!PlanAlerts.CanBuild(new[] { spaceFull }) && PlanAlerts.CanBuild(new[] { spaceTight }), "cargo space: only the error blocks the order");

// ---- storage limiter (CargoLimit): how the check behaves before the fill limit and when there is not enough storage
StoreMass Store(double max, double now, double calc) => new StoreMass { Max = max, Now = now, Calc = calc };
var noOrder = new Dictionary<string, long>();
Dictionary<string, long> Plates(long n) => new() { ["Plate"] = n };
double PlateMass(string id) => id == "Plate" ? 1 : 0;
string PlateName(string id) => id;
// two containers: 1000 each, the first holds 200 -> free 1800, scale 1
var twoBoxes = new List<StoreMass> { Store(1000, 200, 200), Store(1000, 0, 0) };

var cl = CargoLimit.Check(twoBoxes, Plates(100), noOrder, PlateMass, PlateName);
Check(cl != null && cl.Free == 1800 && cl.Storages == 2 && cl.Scale == 1 && cl.Alert == null, "limiter: plenty of room (100 of 1800 free) gives no alert");
Check(CargoLimit.Check(twoBoxes, Plates(1440), noOrder, PlateMass, PlateName).Alert == null, "limiter: exactly 80 % of the free capacity is still fine");
var clTight = CargoLimit.Check(twoBoxes, Plates(1441), noOrder, PlateMass, PlateName).Alert;
Check(clTight != null && clTight.Level == AlertLevel.Warning, "limiter: just above 80 % warns (cargo space gets tight)");
var clExact = CargoLimit.Check(twoBoxes, Plates(1800), noOrder, PlateMass, PlateName).Alert;
Check(clExact != null && clExact.Level == AlertLevel.Warning && PlanAlerts.CanBuild(new[] { clExact }), "limiter: filling the storage exactly is only a warning, the order is allowed");
var clFull = CargoLimit.Check(twoBoxes, Plates(1801), noOrder, PlateMass, PlateName).Alert;
Check(clFull != null && clFull.Level == AlertLevel.Error && clFull.Text.Contains("1,801") && clFull.Text.Contains("1,800") && !PlanAlerts.CanBuild(new[] { clFull }), "limiter: one piece too many is an error that blocks the order and names both numbers");

var onOrder = new Dictionary<string, long> { ["Plate"] = 600 };
var clIncoming = CargoLimit.Check(twoBoxes, Plates(1000), onOrder, PlateMass, PlateName);
Check(clIncoming.Free == 1200 && clIncoming.Alert != null && clIncoming.Alert.Level == AlertLevel.Warning, "limiter: parts already on order take room too (1800 - 600 = 1200 free)");
Check(CargoLimit.Check(twoBoxes, Plates(1000), noOrder, PlateMass, PlateName).Alert == null, "limiter: the same order is fine when nothing else is on order");
var clOverbooked = CargoLimit.Check(twoBoxes, Plates(1), new Dictionary<string, long> { ["Plate"] = 5000 }, PlateMass, PlateName);
Check(clOverbooked.Free == 0 && clOverbooked.Alert != null && clOverbooked.Alert.Level == AlertLevel.Error, "limiter: orders that already need more than the free room leave 0 free, never negative");
var clFullStore = CargoLimit.Check(new List<StoreMass> { Store(1000, 1000, 1000) }, Plates(1), noOrder, PlateMass, PlateName);
Check(clFullStore.Free == 0 && clFullStore.Alert.Level == AlertLevel.Error, "limiter: a completely full storage blocks any order");

// the inventory unit may differ from the item unit: the scale is measured from what is stored (400 stored, items add up to 200)
var scaled = new List<StoreMass> { Store(1000, 400, 200) };
var clScale = CargoLimit.Check(scaled, Plates(300), noOrder, PlateMass, PlateName);
Check(clScale.Scale == 2 && clScale.Free == 600 && clScale.Alert != null && clScale.Alert.Level == AlertLevel.Warning, "limiter: scale 2 is measured from the stored items (300 plates weigh 600, 600 free)");
Check(CargoLimit.Check(scaled, Plates(301), noOrder, PlateMass, PlateName).Alert.Level == AlertLevel.Error, "limiter: with scale 2, 301 plates no longer fit");
Check(CargoLimit.Check(new List<StoreMass> { Store(1000, 0, 0) }, Plates(10), noOrder, PlateMass, PlateName).Scale == 1, "limiter: nothing stored means scale 1");

// no verdict instead of a guess
Check(CargoLimit.Check(new List<StoreMass> { Store(double.NaN, 0, 0), Store(1000, double.NaN, 0) }, Plates(10), noOrder, PlateMass, PlateName) == null, "limiter: storage that does not report its mass gives no verdict");
var halfKnown = CargoLimit.Check(new List<StoreMass> { Store(double.NaN, 0, 0), Store(1000, 0, 0) }, Plates(10), noOrder, PlateMass, PlateName);
Check(halfKnown != null && halfKnown.Storages == 1 && halfKnown.Free == 1000, "limiter: unreadable inventories are skipped, the rest count");
Check(CargoLimit.Check(twoBoxes, noOrder, noOrder, PlateMass, PlateName) == null, "limiter: an empty order gives no verdict");
Check(CargoLimit.Check(twoBoxes, new Dictionary<string, long> { ["Mystery"] = 99999 }, noOrder, PlateMass, PlateName).Alert == null, "limiter: parts of unknown mass are not counted");

// ---- batch dispatch: part by part from the order, every free crafter gets a batch
DispatchWant Want(string item, long waiting, double per, params int[] able) => new() { Item = item, Waiting = waiting, PerCraft = per, Able = able };
int CraftsFor(List<DispatchJob> jobs, string item) => jobs.Where(j => j.Item == item).Sum(j => j.Crafts);

var big = Dispatch.Plan(new[] { Want("Plate", 1000, 1, 1, 2, 3, 4) }, new[] { 1, 2, 3, 4 }, Dispatch.MaxBatchCrafts);
Check(big.Count == 4 && big.All(j => j.Crafts == Dispatch.MaxBatchCrafts), "dispatch: a big order gives every free crafter one full batch, the rest waits for the next pass");
var small = Dispatch.Plan(new[] { Want("Plate", 10, 1, 1, 2, 3, 4) }, new[] { 1, 2, 3, 4 }, Dispatch.MaxBatchCrafts);
Check(small.Count == 4 && CraftsFor(small, "Plate") == 10 && small.Max(j => j.Crafts) == 3 && small.Min(j => j.Crafts) == 2, "dispatch: a small order is spread evenly (10 crafts over 4 crafters = 3, 3, 2, 2)");
var tiny = Dispatch.Plan(new[] { Want("Plate", 2, 1, 1, 2, 3, 4) }, new[] { 1, 2, 3, 4 }, Dispatch.MaxBatchCrafts);
Check(tiny.Count == 2 && tiny.All(j => j.Crafts == 1), "dispatch: 2 crafts use 2 crafters, the other two stay free for the next part");
var perCraftTwo = Dispatch.Plan(new[] { Want("Gear", 10, 2, 1, 2) }, new[] { 1, 2 }, Dispatch.MaxBatchCrafts);
Check(CraftsFor(perCraftTwo, "Gear") == 5, "dispatch: a craft that makes 2 pieces needs half as many crafts");
var odd = Dispatch.Plan(new[] { Want("Gear", 5, 2, 1) }, new[] { 1 }, Dispatch.MaxBatchCrafts);
Check(CraftsFor(odd, "Gear") == 3, "dispatch: crafts round up (5 pieces at 2 per craft = 3 crafts)");

var fifo = Dispatch.Plan(new[] { Want("First", 3, 1, 1, 2, 3, 4), Want("Second", 100, 1, 1, 2, 3, 4) }, new[] { 1, 2, 3, 4 }, Dispatch.MaxBatchCrafts);
Check(CraftsFor(fifo, "First") == 3 && fifo.Count(j => j.Item == "First") == 3 && fifo.Count(j => j.Item == "Second") == 1, "dispatch: the oldest part is served first, the crafter it did not need goes to the next part");
var busyFirst = Dispatch.Plan(new[] { Want("First", 100, 1, 1, 2), Want("Second", 100, 1, 1, 2) }, new[] { 1, 2 }, Dispatch.MaxBatchCrafts);
Check(busyFirst.All(j => j.Item == "First"), "dispatch: when the oldest part takes every free crafter, the next part waits");
var kinds = Dispatch.Plan(new[] { Want("Plate", 50, 1, 1, 2), Want("Motor", 50, 1, 3) }, new[] { 1, 2, 3 }, Dispatch.MaxBatchCrafts);
Check(kinds.Count(j => j.Item == "Plate") == 2 && kinds.Count(j => j.Item == "Motor") == 1, "dispatch: parts of different crafter kinds are made at the same time");
Check(Dispatch.Plan(new[] { Want("Plate", 50, 1, 1, 2) }, new[] { 3 }, Dispatch.MaxBatchCrafts).Count == 0, "dispatch: no free crafter that can make the part, nothing is queued");
Check(Dispatch.Plan(new[] { Want("Plate", 50, 1, 1, 2) }, Array.Empty<int>(), Dispatch.MaxBatchCrafts).Count == 0, "dispatch: all crafters busy, nothing is queued");
Check(Dispatch.Plan(new[] { Want("Plate", 0, 1, 1) }, new[] { 1 }, Dispatch.MaxBatchCrafts).Count == 0, "dispatch: nothing waiting, nothing queued");

// a freed crafter gets the next batch of the oldest waiting part: simulate passes until the order is done
long left = 95; var passes = 0; var passJobs = 0;
while (left > 0 && passes < 50)
{
    var plan = Dispatch.Plan(new[] { Want("Plate", left, 1, 1, 2, 3) }, new[] { 1, 2, 3 }, Dispatch.MaxBatchCrafts);
    if (plan.Count == 0) break;
    left -= plan.Sum(j => j.Crafts);
    passJobs += plan.Count;
    passes++;
}
Check(left == 0 && passes == 4 && passJobs == 3 * 3 + 3, "dispatch: 95 plates on 3 crafters take 4 passes of batches (30, 30, 30, 5) and nothing is lost or queued twice");
var moreCrafters = 0; long left2 = 95;
while (left2 > 0) { left2 -= Dispatch.Plan(new[] { Want("Plate", left2, 1, 1, 2, 3, 4, 5, 6) }, new[] { 1, 2, 3, 4, 5, 6 }, Dispatch.MaxBatchCrafts).Sum(j => j.Crafts); moreCrafters++; }
Check(moreCrafters < passes, "dispatch: more crafters need fewer passes, so a big build gets faster");

Check(Dispatch.Plan(new[] { Want("Plate", 50, 1, 1) }, new[] { 1 }, 0).Count == 0, "dispatch: a batch limit of 0 queues nothing");
Check(Dispatch.IsFree(false, 0, 0, 30), "crafter: idle and nothing counted in it is free");
Check(!Dispatch.IsFree(true, 0, 0, 30), "crafter: a crafting crafter is not free");
Check(!Dispatch.IsFree(false, 10, 5, 30), "crafter: idle with a counted batch (waiting for sub-parts) is not free yet");
Check(Dispatch.IsFree(false, 10, 30, 30), "crafter: idle with a counted batch for 30 s is free again (the batch was lost or finished unnoticed)");

// arrivals are credited to the crafters that stopped working first
var credit = new OrderLedger();
credit.Commit(new Dictionary<string, long> { ["A"] = 20 });
credit.MarkQueued(1, "A", 10);
credit.MarkQueued(2, "A", 10);
credit.Arrived("A", 10, new[] { 2 });
Check(credit.QueuedOfCrafter(2) == 0 && credit.QueuedOfCrafter(1) == 10, "ledger: arrived parts are credited to the idle crafter first, not the lowest id");
credit.Arrived("A", 10);
Check(credit.QueuedOfCrafter(1) == 0 && !credit.Active, "ledger: without a hint the lowest id is credited as before");


// ---- texts in the game's 13 languages (Policy/Tr)
var placeholder = new System.Text.RegularExpressions.Regex(@"\{(\d+)\}");
Check(Tr.Languages.Length == 13 && Tr.Languages[0] == "en", "texts: 13 languages, English first (the fallback)");
var badRows = Tr.Keys.Where(k => Tr.Row(k) == null || Tr.Row(k).Length != Tr.Languages.Length || Tr.Row(k).Any(string.IsNullOrWhiteSpace)).ToList();
Check(badRows.Count == 0, "texts: every text exists in every language" + (badRows.Count > 0 ? " (missing: " + string.Join(", ", badRows.Take(5)) + ")" : ""));
string Slots(string text) => string.Join(",", placeholder.Matches(text).Select(m => m.Groups[1].Value).Distinct().OrderBy(v => v));
var badSlots = Tr.Keys.SelectMany(k => Tr.Languages.Select((code, i) => (k, code, i))).Where(x => Slots(Tr.Row(x.k)[x.i]) != Slots(Tr.Row(x.k)[0])).Select(x => x.k + "/" + x.code).ToList();
Check(badSlots.Count == 0, "texts: every translation keeps the placeholders of the English text" + (badSlots.Count > 0 ? " (" + string.Join(", ", badSlots.Take(5)) + ")" : ""));
var strayBraces = Tr.Keys.SelectMany(k => Tr.Row(k)).Where(t => placeholder.Replace(t, "").IndexOfAny(new[] { '{', '}' }) >= 0).ToList();
Check(strayBraces.Count == 0, "texts: no braces besides the placeholders (the game would read them as filters)");
Check(Tr.Normalize("ru-RU") == "ru" && Tr.Normalize("cz-CZ") == "cs" && Tr.Normalize("pt-BR") == "pt" && Tr.Normalize("zh-CN") == "zh" && Tr.Normalize("uk-UA") == "uk", "texts: the game's culture names map to the languages (cz-CZ is Czech)");
Check(Tr.Normalize("ja-JP") == "en" && Tr.Normalize(null) == "en" && Tr.Normalize("") == "en", "texts: an unknown language falls back to English");
Check(Tr.In("en", "hud.added", "Mk1", 5, 16) == "Build: Mk1 +5 (now 16)", "texts: placeholders are filled in");
Check(Tr.In("de", "btn.check") == "Prüfen" && Tr.In("xx", "btn.check") == "Check", "texts: a language code gives its text, an unknown code English");
Check(Tr.In("en", "no.such.key") == "no.such.key", "texts: an unknown key shows as itself");
Tr.Use("ru-RU");
var ruAlerts = PlanAlerts.Build(tree, Good(), Nm, Ore);
Check(Tr.Language == "ru" && ruAlerts.Any(a => a.Text.Contains("Не хватает руды: OreTitanium, нужно ещё 240")), "texts: alerts follow the game's language (Russian)");
Tr.Use("en-US");
Check(Tr.Language == "en" && PlanAlerts.Build(tree, Good(), Nm, Ore).Any(a => a.Text.Contains("Ore deficit: OreTitanium, 240")), "texts: back to English");


// ---- finished orders take their blocks off the build list (OrderedBlocks)
var ordered = new Dictionary<string, int>();
OrderedBlocks.Mark(ordered, new Dictionary<string, int> { ["tank"] = 2, ["door"] = 2 });
Check(OrderedBlocks.Left(new Dictionary<string, int> { ["tank"] = 2, ["door"] = 2 }, ordered).Count == 0, "done: ordered lines leave the list");
OrderedBlocks.Mark(ordered, new Dictionary<string, int> { ["tank"] = 2, ["door"] = 3 });
Check(ordered["tank"] == 2 && ordered["door"] == 3, "done: a second order on the same list does not count a line twice");
var afterDone = OrderedBlocks.Left(new Dictionary<string, int> { ["tank"] = 5, ["door"] = 3, ["light"] = 4 }, ordered);
Check(afterDone.Count == 2 && afterDone["tank"] == 3 && afterDone["light"] == 4, "done: blocks added after the order stay");
Check(OrderedBlocks.Left(new Dictionary<string, int> { ["tank"] = 1 }, ordered).Count == 0, "done: a line lowered after the order goes, never below 0");

// ---- build list file (PlanFile): kept per world across save and restart
var savedLines = new[]
{
    new PlanLine { Key = "k-1", Block = "b-1", Count = 35, Name = "Heavy Armor Block" },
    new PlanLine { Key = "k-2", Block = "b-2", Count = 2, Name = "Name\twith\ttabs\nand a newline" },
    new PlanLine { Key = null, Block = "b-3", Count = 1, Name = "No key" },
    new PlanLine { Key = "k-4", Block = "b-4", Count = 0, Name = "zero is not written" },
};
var fileText = PlanFile.Write(savedLines);
var readBack = PlanFile.Read(fileText);
Check(readBack.Count == 3 && readBack[0].Key == "k-1" && readBack[0].Block == "b-1" && readBack[0].Count == 35 && readBack[0].Name == "Heavy Armor Block", "list file: a line is read back as written");
Check(readBack[1].Name == "Name with tabs and a newline" && readBack[1].Count == 2, "list file: tabs and line breaks in a name cannot break the file");
Check(readBack[2].Key == "b-3", "list file: a line without its own key uses the block as key");
Check(PlanFile.Read("something else\nk\tb\t5\tx").Count == 0, "list file: a file with an unknown header is ignored");
Check(PlanFile.Read(PlanFile.Header + "\r\nk\tb\t7\tx\r\nbroken line\nk\tb\tnot-a-number\tx\nk\t\t3\tx\n").Count == 1, "list file: Windows line ends work, broken lines are skipped");
Check(PlanFile.Read("").Count == 0 && PlanFile.Read(null).Count == 0, "list file: an empty file is an empty list");
Check(PlanFile.FileName("3f2504e0-4f89-11d3-9a0c-0305e82c3301") == "3f2504e0-4f89-11d3-9a0c-0305e82c3301.txt", "list file: the world's save id is the file name");
Check(PlanFile.FileName("../../evil\\path:name") is string safeName && safeName.EndsWith(".txt") && safeName.Substring(0, safeName.Length - 4).IndexOfAny(new[] { '/', '\\', ':', '.' }) < 0, "list file: a save id can never leave the plugin's folder");
Check(PlanFile.FileName(null) == null && PlanFile.FileName("") == null && PlanFile.FileName("00000000-0000-0000-0000-000000000000") == null, "list file: a world without a save id gets no file");

Console.WriteLine(fails == 0 ? "ALL PASSED" : fails + " FAILED");
return fails == 0 ? 0 : 1;
