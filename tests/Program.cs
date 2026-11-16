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

Console.WriteLine(fails == 0 ? "ALL PASSED" : fails + " FAILED");
return fails == 0 ? 0 : 1;
