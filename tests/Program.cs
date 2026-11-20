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

// K2: the ore the running orders still lack (Policy/OreShort)
var k2Running = new Dictionary<string, long> { ["AdvancedCompressor"] = 1 };
var k2None = OreShort.Of(k2Running, new Dictionary<string, long>(), recipes);
Check(k2None.Count == 5 && k2None["OreTitanium"] == 240 && k2None["OreNickel"] == 90, "ore short: all the ore of the stalled part when storage has none");
var k2Some = OreShort.Of(k2Running, new Dictionary<string, long> { ["OreTitanium"] = 240, ["OreNickel"] = 90, ["OreIron"] = 10, ["AdvancedCompressor"] = 99 }, recipes);
Check(k2Some.Count == 3 && k2Some["OreIron"] == 5 && k2Some["OreLead"] == 100 && k2Some["OreCopper"] == 40,
      "ore short: ore in storage is taken off; stock of the running part itself is ignored (it is made regardless)");
Check(OreShort.Of(k2Running, new Dictionary<string, long> { ["OreTitanium"] = 240, ["OreNickel"] = 90, ["OreIron"] = 15, ["OreLead"] = 100, ["OreCopper"] = 40 }, recipes).Count == 0,
      "ore short: enough ore = nothing short");
string K2Name(string id) => id.Replace("Ore", "");
Check(OreShort.Format(k2None, K2Name, (n, a) => $"{n} {a}") == "Titanium 240, Lead 100, Nickel 90 (+2)", "ore short text: the three largest shortages, then +N");
Check(OreShort.Format(new Dictionary<string, long> { ["OreIron"] = 12000 }, K2Name, (n, a) => $"{a} more {n}") == "12000 more Iron", "ore short text: one ore");
Check(OreShort.Format(new Dictionary<string, long>(), K2Name, (n, a) => n) == "" && OreShort.Format(new Dictionary<string, long> { ["OreIron"] = 0 }, K2Name, (n, a) => n) == "",
      "ore short text: nothing short = empty");

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

var broken = Good(); broken.NonFunctionalCrafters = 7;
broken.NonFunctionalNames.AddRange(new[] { "Assembler 5 m", "Assembler 5 m", "Smelter 2.5 m", "Refinery", "Fabricator", "Gearforge" });
var brokenAlert = PlanAlerts.Build(okTree, broken, Nm, Ore).FirstOrDefault(a => a.Text.Contains("Assembler 5 m (2)"));
Check(brokenAlert != null && brokenAlert.Level == AlertLevel.Warning && brokenAlert.Text.Contains("Fabricator, Gearforge, Refinery, +1") && !brokenAlert.Text.Contains("Smelter"),
    "crafters not working are named (same names counted, at most 4 shown)");
var countOnly = Good(); countOnly.NonFunctionalCrafters = 2;
Check(PlanAlerts.Build(okTree, countOnly, Nm, Ore).Any(a => a.Text.StartsWith("2 crafter(s) not working")), "crafters not working without names: the count");

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
Check(!ledger.NeedForNew(first, stockNow, false, new Dictionary<string, long> { ["SteelPlate"] = 1400 }).ContainsKey("SteelPlate"),
      "M3 order all: blocks ordered and not done are not made again (no ore counted twice)");
Check(ledger.NeedForNew(first, stockNow, false, new Dictionary<string, long> { ["SteelPlate"] = 400 })["SteelPlate"] == 1000,
      "M3 order all: only the blocks not ordered yet");
ledger.CrafterLost(1); ledger.CrafterLost(2); ledger.DropWaiting("SteelPlate");
Check(ledger.Missing("SteelPlate", 1400, 1400) == 0 && ledger.Missing("SteelPlate", 1500, 1400) == 100, "finished parts are stock: missing = wanted - stock");
ledger.MarkQueued(3, "SteelTube", 200);
var tubesWaiting = ledger.Waiting.GetValueOrDefault("SteelTube");
Check(ledger.CrafterLost(3).Single() is { Key: "SteelTube", Value: 200 } && ledger.QueuedOfCrafter(3) == 0
      && ledger.Waiting["SteelTube"] == tubesWaiting + 200, "K3: a crafter that is gone gives its queued parts back to waiting");
ledger.DropWaiting("SteelTube");
Check(!ledger.Active, "nothing queued and nothing waiting: not active");
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
{
    // A crafter that finished its queue has made its whole line, even when the parts were taken from storage at once.
    var l = new OrderLedger();
    l.Commit(new Dictionary<string, long> { ["plate"] = 100 });
    l.MarkQueued(1, "plate", 50);
    l.MarkQueued(2, "plate", 50);
    l.Arrived("plate", 30, new[] { 1 });
    var qcFirst = l.CrafterFinished(1, "plate");
    var qcAgain = l.CrafterFinished(1, "plate");
    Check(qcFirst == 20 && qcAgain == 0 && l.Done("plate") == 50 && l.QueuedOf("plate") == 50 && l.Active,
        "queue check: a crafter with an empty queue makes the rest of its line done, once");
    var qcSecond = l.CrafterFinished(2, "plate");
    l.Arrived("plate", 40); // seen later: nothing is counted twice
    Check(qcSecond == 50 && l.Done("plate") == 100 && !l.Active && l.CrafterFinished(3, "plate") == 0,
        "queue check: all crafters done = order done, late arrivals are not counted twice");
}
Check(PlanLimits.IsArmor("CubeBlockDefinition", @"e7f8da13-3742-4d19-bb83-5e8900e819a8 : Vanilla\Armors\Cube\50\Light\ArmorCubeLight50_LightArmorCubeBlockDefinition.def")
      && PlanLimits.IsArmor("ArmorBlockDefinition", null)
      && !PlanLimits.IsArmor("CubeBlockDefinition", @"a5ca6893-65d2-46e6-b3ab-f30bf867ea72 : Vanilla\Blocks\Thrusters\Hydrogen\50\HydrogenThruster50_HydrogenThrustersPowerableBlockDefinition.def")
      && !PlanLimits.IsArmor(null, null), "armor is told by the definition (path / type), not by the shown name");
Check(PlanLimits.Apply(995, 100, true) == 1000 && PlanLimits.Apply(45, 10, false) == 50 && PlanLimits.Apply(3, -10, false) == 0, "counts stay between 0 and the limit");
// ---- settings (PlannerSettings): steps, limits, batch size and cargo warning come from the settings dialog
var defaults = PlannerSettings.Current;
PlannerSettings.Current = new PlannerSettings { StepShift = 3, StepCtrl = 7, StepAlt = 20, ArmorStepAlt = 250, MaxPerLine = 30, BatchCrafts = 4, CargoWarnPercent = 60 }.Checked();
Check(PlanLimits.Step(false, true, false) == 3 && PlanLimits.Step(false, false, true) == 7 && PlanLimits.Step(false, true, true, true) == 20 && PlanLimits.Step(true, false, false, true) == 250, "settings: step sizes for Shift, Ctrl and Alt (Alt beats Ctrl beats Shift)");
Check(PlanLimits.Apply(25, 10, false) == 30 && Dispatch.MaxBatchCrafts == 4 && Math.Abs(PlanAlerts.TightShare - 0.6) < 1e-9, "settings: line limit, batch size and cargo warning");
var wild = new PlannerSettings { StepShift = -5, MaxPerLine = 0, BatchCrafts = 0, CargoWarnPercent = 500, NoProgressSeconds = 1 }.Checked();
Check(wild.StepShift == 1 && wild.MaxPerLine == 1 && wild.BatchCrafts == 1 && wild.CargoWarnPercent == 100 && wild.NoProgressSeconds == 10, "settings: a broken config file is kept in range");
PlannerSettings.Current = defaults;
Check(PlanLimits.Step(true, false, true) == 100 && PlanLimits.Max(true) == 1000 && Dispatch.MaxBatchCrafts == 10 && Math.Abs(PlanAlerts.TightShare - 0.8) < 1e-9, "settings: defaults are the planner's old behaviour");

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

// ---- blueprint project (list file v2, line names)
var projectFile = PlanFile.Write(new[]
{
    new PlanLine { Key = "k-1", Block = "b-1", Count = 3, Name = "Hand Picked" },
    new PlanLine { Key = "k-2", Block = "b-2", Count = 361, Name = "Heavy Armor Cube", Project = true },
}, "Dart EX-2");
var projectBack = PlanFile.Read(projectFile, out var projectName);
Check(projectName == "Dart EX-2" && projectBack.Count == 2 && !projectBack[0].Project && projectBack[1].Project && projectBack[1].Count == 361, "list file: the blueprint project and its lines are kept");
Check(PlanFile.Read(PlanFile.HeaderV1 + "\nk\tb\t5\tOld", out var oldProject).Count == 1 && oldProject == null, "list file: a version 1 file (no project) is still read");
Check(PlanFile.Read(PlanFile.Write(new[] { new PlanLine { Key = "k", Block = "b", Count = 1, Name = "x" } }), out var noProject).Count == 1 && noProject == null, "list file: a list without a project has no project row");
Check(BlueprintNames.SizeOf(@"Vanilla\Blocks\Thrusters\Hydrogen\250\HydrogenThruster250_ServerComposition.def") == "2.5 m"
      && BlueprintNames.SizeOf(@"Vanilla\Blocks\GatlingTurret\600\GatlingTurret600_ServerComposition.def") == "6 m"
      && BlueprintNames.SizeOf(@"Vanilla\System\Grid\Grid_ServerComposite.def") == null, "blueprint: block size from the definition path");
var bpLines = BlueprintNames.Lines(new[]
{
    new BlueprintKind<string> { Key = "t50", Name = "Hydrogen Thruster", Path = @"Blocks\Hydrogen\50\a.def", Count = 4 },
    new BlueprintKind<string> { Key = "t250", Name = "Hydrogen Thruster", Path = @"Blocks\Hydrogen\250\b.def", Count = 13 },
    new BlueprintKind<string> { Key = "cube", Name = "Heavy Armor Cube", Path = @"Blocks\Armor\250\c.def", Count = 10 },
    new BlueprintKind<string> { Key = "cube", Name = "Heavy Armor Cube", Path = @"Blocks\Armor\250\c.def", Count = 2 },
});
Check(bpLines.Count == 3 && bpLines[0].Name == "Heavy Armor Cube" && bpLines[0].Count == 12, "blueprint: one line per block kind, counts added up, unique names stay plain");
Check(bpLines[1].Name == "Hydrogen Thruster 0.5 m" && bpLines[2].Name == "Hydrogen Thruster 2.5 m" && bpLines[2].Count == 13, "blueprint: same-name blocks get their size");

// ---- every text the plugin's code asks for exists (probe over the sources, so a typo in a key shows here, not in game)
string SourceRoot()
{
    foreach (var start in new[] { System.IO.Directory.GetCurrentDirectory(), AppContext.BaseDirectory })
        for (var dir = new System.IO.DirectoryInfo(start); dir != null; dir = dir.Parent)
            if (System.IO.File.Exists(System.IO.Path.Combine(dir.FullName, "ClientPlugin", "Config.cs"))) return System.IO.Path.Combine(dir.FullName, "ClientPlugin");
    return null;
}
var sourceRoot = SourceRoot();
Check(sourceRoot != null, "sources: ClientPlugin folder found for the text probes");
if (sourceRoot != null)
{
    var sources = System.IO.Directory.GetFiles(sourceRoot, "*.cs", System.IO.SearchOption.AllDirectories)
        .Where(f => !f.Contains(System.IO.Path.DirectorySeparatorChar + "obj" + System.IO.Path.DirectorySeparatorChar)
                    && !f.Contains(System.IO.Path.DirectorySeparatorChar + "bin" + System.IO.Path.DirectorySeparatorChar)
                    && !f.EndsWith("TrTable.cs") && !f.EndsWith("Probe.cs"))
        .ToDictionary(f => f, System.IO.File.ReadAllText);
    var keyUse = new System.Text.RegularExpressions.Regex(@"Tr\.(?:T|In\([^,()]+,)\s*\(?\s*""([A-Za-z0-9_.]+)""");
    var usedKeys = sources.SelectMany(f => keyUse.Matches(f.Value).Select(m => (Key: m.Groups[1].Value, File: System.IO.Path.GetFileName(f.Key)))).ToList();
    var unknownKeys = usedKeys.Where(u => Tr.Row(u.Key) == null).Select(u => u.Key + " in " + u.File).Distinct().ToList();
    Check(usedKeys.Count > 50 && unknownKeys.Count == 0, $"sources: all {usedKeys.Select(u => u.Key).Distinct().Count()} text keys used in the code exist" + (unknownKeys.Count > 0 ? " (unknown: " + string.Join(", ", unknownKeys.Take(5)) + ")" : ""));

    // Settings dialog: every label, description, section and the title of Config.cs, and every Tr.Setting("...") text,
    // has its "set:" row, and the English cell is that exact text.
    var config = sources.First(f => f.Key.EndsWith(System.IO.Path.DirectorySeparatorChar + "Config.cs")).Value;
    var settingTexts = new System.Text.RegularExpressions.Regex(@"(?:label|description): ""([^""]+)""|\[Separator\(""([^""]+)""\)\]|Title = ""([^""]+)""")
        .Matches(config).Select(m => m.Groups.Cast<System.Text.RegularExpressions.Group>().Skip(1).First(g => g.Success).Value)
        .Concat(sources.Values.SelectMany(t => new System.Text.RegularExpressions.Regex(@"Tr\.Setting\(""([^""]+)""\)").Matches(t).Select(m => m.Groups[1].Value)))
        .Distinct().ToList();
    var untranslated = settingTexts.Where(t => Tr.Row("set:" + t) == null || Tr.Row("set:" + t)[0] != t).ToList();
    Check(settingTexts.Count >= 30 && untranslated.Count == 0, $"settings: all {settingTexts.Count} dialog texts have their 13 languages" + (untranslated.Count > 0 ? " (missing: " + string.Join(" | ", untranslated.Take(3)) + ")" : ""));
}
Tr.Use("ru-RU");
Check(Tr.Setting("Batch size") == "Размер партии" && Tr.Setting("Orders") == "Заказы", "settings: labels and sections follow the game's language (Russian)");
Check(Tr.Setting("A text with no row") == "A text with no row" && Tr.Setting(null) == null && Tr.Setting("") == "", "settings: a text without a row stays as it is (English)");
Tr.Use("en-US");
Check(Tr.Setting("Batch size") == "Batch size", "settings: back to English");

// Texts that name a window button use that button's own text in every language (so the player finds the button).
bool Names(string key, string button) => Tr.Languages.Select((_, i) => i).All(i => Tr.Row(key)[i].Contains(Tr.Row(button)[i]));
Check(Names("problems.missingOk", "btn.orderMissing") && Names("problems.missingNone", "btn.orderMissing") && Names("problems.missingBlocked", "btn.orderMissing"), "texts: the 'Order missing' notes name the real button in every language");
Check(Names("dlg.olderSave", "btn.orderMissing") && Names("hud.listReset", "btn.orderMissing"), "texts: the older-save dialog names the real 'Order missing' button in every language");
Check(Names("bp.allShort", "btn.orderAll"), "texts: the blueprint box's 'Order all' line names the real button in every language");
Check(Names("problems.missingSome", "btn.orderMissing") && Tr.Languages.Select((_, i) => i).All(i => Tr.Row("hud.bpWaits")[i].Contains(Tr.Row("btn.orderFits")[i].Replace(" ({0})", "").Replace("（{0}）", ""))), "texts: M3 notes name the real buttons (Order missing, Order what fits) in every language");
var confirmTip = "set:Empty list and Stop orders need a second click.";
Check(Tr.Row(confirmTip) != null && Names(confirmTip, "btn.clearList") && Names(confirmTip, "btn.stopOrders"), "settings: the 'Confirm clearing' tip names the real buttons in every language");

// M3: whole blocks (Policy/BlockSets.cs). Recipes: Plate 10 iron, Motor 5 iron + 2 nickel, Pipe 3 iron.
var m3Recipes = new Dictionary<string, Recipe>
{
    ["Plate"] = new Recipe { Item = "Plate", Machine = "Assembler", Inputs = new Dictionary<string, int> { ["OreIron"] = 10 } },
    ["Motor"] = new Recipe { Item = "Motor", Machine = "Assembler", Inputs = new Dictionary<string, int> { ["OreIron"] = 5, ["OreNickel"] = 2 } },
    ["Pipe"] = new Recipe { Item = "Pipe", Machine = "Assembler", Inputs = new Dictionary<string, int> { ["OreIron"] = 3 } },
};
BlockLine BL(string key, int count, double complexity, params (string, long)[] parts)
    => new() { Key = key, Name = key, Count = count, Complexity = complexity, Parts = parts.ToDictionary(p => p.Item1, p => p.Item2) };
var m3Lines = new List<BlockLine>
{
    BL("Thruster", 3, 100, ("Plate", 20), ("Motor", 2)),
    BL("Tank", 2, 50, ("Plate", 10), ("Pipe", 4)),
    BL("Cube", 10, 1, ("Plate", 1)),
};
var m3Avail = new Dictionary<string, long> { ["Plate"] = 50, ["Motor"] = 4 };
var m3Ore = new Dictionary<string, long> { ["OreIron"] = 400, ["OreNickel"] = 10 };
var m3 = BlockSets.Plan(m3Lines, m3Avail, m3Ore, m3Recipes, Ore, onlyMissing: true, null);
Check(m3.FromStock.GetValueOrDefault("Thruster") == 2 && m3.FromStock.GetValueOrDefault("Cube") == 10 && !m3.FromStock.ContainsKey("Tank"),
    "M3 storage: covers whole blocks, most complex first (2 Thrusters take 40 Plate + 4 Motor, the last 10 Plate cover 10 Cubes)");
Check(m3.StockUsed["Plate"] == 50 && m3.StockUsed["Motor"] == 4, "M3 storage: the parts it used");
Check(m3.Craft.Count == 1 && m3.Craft[0].Key == "Tank" && m3.Craft[0].Count == 2,
    "M3 ore: 2 Tanks fit 400 iron (224), the third Thruster would need 434: smallest block first, only what fits");
Check(m3.Waiting.GetValueOrDefault("Thruster") == 1 && m3.WaitShortage.GetValueOrDefault("OreIron") == 34 && !m3.WaitShortage.ContainsKey("OreNickel"),
    "M3 ore: the Thruster waits; 34 more iron would make every block");
Check(m3.Make["Plate"] == 20 && m3.Make["Pipe"] == 8 && !m3.Make.ContainsKey("Motor") && m3.Credit.Count == 0, "M3 make: the parts of the 2 Tanks");
Check(m3.StockBlocks == 12 && m3.CraftBlocks == 2 && m3.WaitingBlocks == 1, "M3: 12 from storage + 2 made + 1 waiting = the 15 blocks");

var m3All = BlockSets.Plan(m3Lines, m3Avail, m3Ore, m3Recipes, Ore, onlyMissing: false, null);
Check(m3All.FromStock.Count == 0 && m3All.Waiting.Count == 0 && m3All.Craft.Select(g => g.Key).SequenceEqual(new[] { "Cube", "Tank", "Thruster" }),
    "M3 Order all: every block made new, smallest first (Cube 1 part, Tank 14, Thruster 22)");
Check(m3All.Make["Plate"] == 90 && m3All.Make["Motor"] == 6 && m3All.Make["Pipe"] == 8, "M3 Order all: all parts of all blocks");

var m3Partial = BlockSets.Plan(new List<BlockLine> { BL("Tank", 3, 50, ("Plate", 10), ("Pipe", 4)) },
    new Dictionary<string, long> { ["Plate"] = 15, ["Pipe"] = 2 }, new Dictionary<string, long> { ["OreIron"] = 10000 }, m3Recipes, Ore, true, null);
Check(m3Partial.FromStock.Count == 0 && m3Partial.Credit["Plate"] == 15 && m3Partial.Credit["Pipe"] == 2
      && m3Partial.Make["Plate"] == 15 && m3Partial.Make["Pipe"] == 10, "M3 storage that covers no whole block is used for the made blocks (credit), the rest is made");

var m3Armor = BlockSets.Plan(new List<BlockLine> { BL("Cube", 1000, 1, ("Plate", 1)) },
    new Dictionary<string, long>(), new Dictionary<string, long> { ["OreIron"] = 255 }, m3Recipes, Ore, true, null);
Check(m3Armor.CraftBlocks == 25 && m3Armor.Waiting["Cube"] == 975 && m3Armor.WaitShortage["OreIron"] == 9745, "M3 ore: 1,000 cubes, iron for 25: 25 made, 975 wait (binary search)");

var m3Nickel = BlockSets.Plan(new List<BlockLine> { BL("Motorblock", 2, 10, ("Motor", 5)), BL("Plateblock", 2, 10, ("Plate", 6)) },
    new Dictionary<string, long>(), new Dictionary<string, long> { ["OreIron"] = 1000, ["OreNickel"] = 0 }, m3Recipes, Ore, true, null);
Check(m3Nickel.Waiting["Motorblock"] == 2 && m3Nickel.Craft.Single().Key == "Plateblock" && m3Nickel.Craft[0].Count == 2,
    "M3 ore: a block that lacks one ore waits, a later block with other ore is still made");

var m3Groups = new List<BlockGroup>
{
    new() { Key = "Tank", Count = 2, Parts = new Dictionary<string, long> { ["Plate"] = 10, ["Pipe"] = 4 } },
    new() { Key = "Thruster", Count = 1, Parts = new Dictionary<string, long> { ["Plate"] = 20, ["Motor"] = 2 } },
};
Check(BlockSets.Completed(m3Groups, new Dictionary<string, long> { ["Plate"] = 15, ["Pipe"] = 8 }).SequenceEqual(new[] { 1, 0 }),
    "M3 done: 15 Plate + 8 Pipe handed out = the first Tank only");
Check(BlockSets.Completed(m3Groups, new Dictionary<string, long> { ["Plate"] = 40, ["Pipe"] = 8 }).SequenceEqual(new[] { 2, 0 }),
    "M3 done: the Thruster waits for its Motors although its Plate is out");
Check(BlockSets.Completed(m3Groups, new Dictionary<string, long> { ["Plate"] = 40, ["Pipe"] = 8, ["Motor"] = 2 }).SequenceEqual(new[] { 2, 1 }),
    "M3 done: all parts out = all blocks done");
Check(BlockSets.Completed(m3Groups, new Dictionary<string, long> { ["Plate"] = 100, ["Motor"] = 2 }).SequenceEqual(new[] { 0, 1 }),
    "M3 done: the Thruster is done before the Tanks when the Tanks' parts are kept for them (Plate 100 = 20 kept + 20)");
Check(BlockSets.Completed(m3Groups, new Dictionary<string, long> { ["Plate"] = 30, ["Motor"] = 2 }).SequenceEqual(new[] { 0, 0 }),
    "M3 done: a later block never takes parts an earlier block waits for (Plate 30: 20 kept for the Tanks, 10 < 20)");
Check(BlockSets.PartOrder(m3Groups, new Dictionary<string, long> { ["Plate"] = 15, ["Pipe"] = 8 }).SequenceEqual(new[] { "Plate", "Motor" }),
    "M3 hand-out: Plate first (the 2nd Tank lacks it), then Motor (the Thruster); Pipe is all out");
Check(BlockSets.PartOrder(m3Groups, new Dictionary<string, long>()).SequenceEqual(new[] { "Pipe", "Plate", "Motor" }),
    "M3 hand-out: nothing out yet: the first block's parts first");

// M3 blueprints: all or nothing (Lone 2026-11-19), unless the player says "build anyway".
BlockLine M3PL(string key, string project, int count, params (string, long)[] parts)
{
    var line = BL(key, count, 10, parts);
    line.Project = project;
    return line;
}
var m3BpLines = new List<BlockLine>
{
    BL("Cube", 5, 1, ("Plate", 1)),
    M3PL("ShipThruster", "Ship", 2, ("Plate", 20), ("Motor", 2)),
    M3PL("ShipTank", "Ship", 1, ("Plate", 10), ("Pipe", 4)),
};
var m3BpOre = new Dictionary<string, long> { ["OreIron"] = 300, ["OreNickel"] = 100 };
var m3BpHeld = BlockSets.Plan(m3BpLines, new Dictionary<string, long>(), m3BpOre, m3Recipes, Ore, true, null);
Check(m3BpHeld.Craft.Count == 1 && m3BpHeld.Craft[0].Key == "Cube" && m3BpHeld.Craft[0].Count == 5,
    "M3 blueprint short of ore: none of its blocks is made (its Tank would fit), the hand-added Cubes are");
Check(m3BpHeld.Waiting["ShipThruster"] == 2 && m3BpHeld.Waiting["ShipTank"] == 1 && m3BpHeld.ProjectWaiting["Ship"]["OreIron"] == 282
      && !m3BpHeld.ProjectWaiting["Ship"].ContainsKey("OreNickel"), "M3 blueprint short of ore: it waits whole and lacks 282 iron (582 for Cubes + Ship, 300 there)");
var m3BpForced = BlockSets.Plan(m3BpLines, new Dictionary<string, long>(), m3BpOre, m3Recipes, Ore, true, new[] { "Ship" });
Check(m3BpForced.Craft.Select(g => g.Key).SequenceEqual(new[] { "Cube", "ShipTank" }) && m3BpForced.Waiting["ShipThruster"] == 2 && m3BpForced.ProjectWaiting.Count == 0,
    "M3 build anyway: the blueprint's blocks that fit are made, the rest waits in the list");
var m3BpRich = BlockSets.Plan(m3BpLines, new Dictionary<string, long>(), new Dictionary<string, long> { ["OreIron"] = 1000, ["OreNickel"] = 100 }, m3Recipes, Ore, true, null);
Check(m3BpRich.CraftBlocks == 8 && m3BpRich.Waiting.Count == 0 && m3BpRich.ProjectWaiting.Count == 0, "M3 blueprint with enough ore: all made");
var m3BpStock = BlockSets.Plan(m3BpLines, new Dictionary<string, long> { ["Plate"] = 10, ["Pipe"] = 4 }, m3BpOre, m3Recipes, Ore, true, null);
Check(!m3BpStock.FromStock.ContainsKey("ShipTank") && m3BpStock.Waiting["ShipTank"] == 1,
    "M3 blueprint short of ore: storage does not take its blocks off the list either (it stays whole)");

// Several recipes for one part (2026-11-20: titanium never counted): the game spreads requests evenly over all
// crafters that can make it, each with its own recipe; the tree splits the same way.
var altBlades = new Recipe { Item = "Blades", Machine = "Smelter", Crafters = 6, Inputs = new Dictionary<string, int> { ["OreNickel"] = 10 } };
altBlades.Alternatives.Add(new Recipe { Item = "Blades", Machine = "Fabricator", Crafters = 2, Inputs = new Dictionary<string, int> { ["OreTitanium"] = 5 } });
var altRecipes = new Dictionary<string, Recipe>
{
    ["Blades"] = altBlades,
    ["Chamber"] = new Recipe { Item = "Chamber", Machine = "Assembler", Inputs = new Dictionary<string, int> { ["Blades"] = 2 } },
};
var altTree = RecipeTree.Resolve(new Dictionary<string, long> { ["Chamber"] = 40 }, new Dictionary<string, long> { ["OreNickel"] = 100000 }, altRecipes);
Check(altTree.RawShortage.GetValueOrDefault("OreTitanium") == 100 && !altTree.RawShortage.ContainsKey("OreNickel"),
    "recipes: 80 Blades split 6:2 over Smelter (nickel) and Fabricator (titanium): 20 need 100 titanium, short when there is none");
Check(altTree.Orders.Where(o => o.Item == "Blades").Sum(o => o.Amount) == 80 && altTree.Orders.Count(o => o.Item == "Blades") == 2,
    "recipes: both crafter kinds get their share, 60 + 20 = 80 Blades");

var m3Ledger = new OrderLedger();
m3Ledger.Commit(new Dictionary<string, long> { ["Plate"] = 20 });
Check(m3Ledger.Missing("Plate", 60, 30) == 10 && m3Ledger.Missing("Plate", 10, 30) == 0 && m3Ledger.Missing("Plate", 5, 0) == 0,
    "missing = wanted - storage - on order (storage counts as it is, no parts are kept back)");
m3Ledger.Clear();
Check(m3Ledger.Missing("Plate", 60, 30) == 30, "missing: with the orders stopped only storage counts");

// A started blueprint's next rounds are made from ore only (Lone 2026-11-20: storage holds the earlier rounds' parts).
var nextRound = BL("Wall", 5, 10, ("Plate", 2));
nextRound.NoStock = true;
var roundPlan = BlockSets.Plan(new List<BlockLine> { nextRound }, new Dictionary<string, long> { ["Plate"] = 100 },
    new Dictionary<string, long> { ["OreIron"] = 40 }, m3Recipes, Ore, true, null);
Check(roundPlan.FromStock.Count == 0 && roundPlan.Craft.Single().Count == 2 && roundPlan.Make["Plate"] == 4 && roundPlan.Credit.Count == 0,
    "next round: 100 Plate in storage are not used, 40 iron make 2 of 5 walls (4 Plate)");
var listText = PlanFile.Write(new[] { new PlanLine { Key = "k", Block = "b", Count = 1, Name = "n", Project = true } }, "Dart", true);
PlanFile.Read(listText, out var startedName, out var startedFlag);
Check(startedName == "Dart" && startedFlag, "list file: a started blueprint stays started after a load");
var stampText = PlanFile.Write(new[] { new PlanLine { Key = "k", Block = "b", Count = 1, Name = "n" } }, null, false, 123456789);
var stampLines = PlanFile.Read(stampText, out _, out _, out var stampAt);
Check(stampAt == 123456789 && stampLines.Count == 1, "list file: the save's game time is kept (an older save is told by it), the line still reads");
var lostLedger = new OrderLedger();
lostLedger.Restore(new SavedPart { Item = "Plate", Ordered = 50, Queued = 30, Waiting = 5 });
Check(lostLedger.Requeue("Plate") == 30 && lostLedger.Waiting["Plate"] == 35 && lostLedger.UnassignedItems().Count == 0,
    "orders load: parts no crafter has any more wait for a crafter again (5 + 30)");

// Low parts wait for the ordered parts made from them (Lone 2026-11-20: HDP crafters ate the blocks' Steel Plate).
var heldInputs = new Dictionary<string, IReadOnlyCollection<string>>
{
    ["HDP"] = new[] { "SteelPlate" }, ["Chamber"] = new[] { "Blades", "Tube" }, ["SteelPlate"] = Array.Empty<string>(),
};
var heldNow = Dispatch.Held(heldInputs, new[] { "HDP", "SteelPlate", "Chamber", "Blades", "Tube" });
Check(heldNow["SteelPlate"] == "HDP" && heldNow["Blades"] == "Chamber" && heldNow["Tube"] == "Chamber" && !heldNow.ContainsKey("HDP"),
    "hold: Steel Plate waits for Heavy-Duty Plate, Blades and Tube for the Combustion Chamber");
Check(Dispatch.Held(heldInputs, new[] { "SteelPlate", "Blades" }).Count == 0, "hold: the users are made = nothing held");
var deepInputs = new Dictionary<string, IReadOnlyCollection<string>>
{
    ["Thruster"] = new[] { "Chamber" }, ["Chamber"] = new[] { "Blades", "Grid" }, ["Blades"] = new[] { "Nickel" }, ["Grid"] = new[] { "Cobalt" },
};
var deepHeld = Dispatch.Held(deepInputs, new[] { "Thruster", "Blades" });
Check(deepHeld.Count == 1 && deepHeld["Blades"] == "Thruster",
    "hold: down the tree: ordered Blades wait for an ordered Thruster whose Chamber (made by the game) uses them");

// After a load the finished groups are gone: their parts leave the handed-out count, the others stay as they were.
var loadGroups = new List<BlockGroup>
{
    new() { Key = "Tank", Count = 2, Parts = new Dictionary<string, long> { ["Plate"] = 10, ["Pipe"] = 4 } },
    new() { Key = "Wall", Count = 3, Parts = new Dictionary<string, long> { ["Plate"] = 5 } },
    new() { Key = "Cube", Count = 4, Parts = new Dictionary<string, long> { ["Plate"] = 5 } },
};
var before = BlockSets.Completed(loadGroups, new Dictionary<string, long> { ["Plate"] = 50 });
var after = BlockSets.Completed(new List<BlockGroup> { loadGroups[0], loadGroups[2] }, new Dictionary<string, long> { ["Plate"] = 50 - 15 });
Check(before.SequenceEqual(new[] { 0, 3, 3 }) && after.SequenceEqual(new[] { 0, 3 }),
    "orders load: a finished group's parts leave the count, no block is finished twice (Cube stays at 3)");

// Orders saved with the world (Lone 2026-11-20: after a load the order goes on).
var saveLedger = new OrderLedger();
saveLedger.Commit(new Dictionary<string, long> { ["Plate"] = 100, ["Motor"] = 10 });
saveLedger.MarkQueued(3, "Plate", 40);
saveLedger.MarkQueued(4, "Plate", 20);
saveLedger.Arrived("Plate", 15);
var savedOrders = new SavedOrders
{
    Mode = "All", Elapsed = 72.5, HandedOut = new Dictionary<string, long> { ["Plate"] = 60 }, Parts = saveLedger.Save(),
    Groups = { new SavedGroup { Key = "abc", Project = true, Count = 5, Done = 2, Parts = new Dictionary<string, long> { ["Plate"] = 20, ["Motor"] = 2 } } },
};
var orderText = OrderFile.Write(savedOrders);
var ordersBack = OrderFile.Read(orderText);
Check(ordersBack != null && ordersBack.Mode == "All" && Math.Abs(ordersBack.Elapsed - 72.5) < 0.01 && ordersBack.HandedOut["Plate"] == 60
      && ordersBack.Groups.Single() is { Key: "abc", Project: true, Count: 5, Done: 2 } g && g.Parts["Motor"] == 2,
    "orders file: mode, time, groups and parts handed out read back");
var plateBack = ordersBack.Parts.First(p => p.Item == "Plate");
Check(ordersBack.Parts[0].Item == "Plate" && plateBack.Ordered == 100 && plateBack.Done == 15 && plateBack.Waiting == 40 && plateBack.Queued == 45,
    "orders file: per part ordered 100, done 15, waiting 40, queued 45 (oldest order first)");
Check(OrderFile.Read("LW Build Planner list 2\nx\ty\t1\n") == null && OrderFile.Read("") == null, "orders file: another file or none = no orders");

var loadLedger = new OrderLedger();
foreach (var part in ordersBack.Parts) loadLedger.Restore(part);
Check(loadLedger.Active && loadLedger.Waiting["Plate"] == 40 && loadLedger.UnassignedItems().SequenceEqual(new[] { "Plate" }),
    "orders load: waiting parts go on, queued parts wait for their crafters to be found");
Check(loadLedger.Requeue("Plate") == 45 && loadLedger.Waiting["Plate"] == 85 && loadLedger.UnassignedItems().Count == 0,
    "orders load: queued parts that no crafter has any more wait for a crafter again (40 + 45)");

Console.WriteLine(fails == 0 ? "ALL PASSED" : fails + " FAILED");
return fails == 0 ? 0 : 1;
