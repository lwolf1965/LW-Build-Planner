using System.Collections.Generic;
using System.Linq;
using ClientPlugin.Policy;
using Keen.Game2.Simulation.GameSystems.Ownership;
using Keen.Game2.Simulation.Utils;
using Keen.Game2.Simulation.WorldObjects.CubeBlocks;
using Keen.Game2.Simulation.WorldObjects.CubeGrids;
using Keen.Game2.Simulation.WorldObjects.CubeBlocks.Production.ItemConverters;
using Keen.Game2.Simulation.WorldObjects.Items;
using Keen.VRage.Core.Game.Systems;
using Keen.VRage.DCS.Components;
using Keen.VRage.Library.Localization;

namespace ClientPlugin.Game;

/// <summary>One inventory that counts as stock: a storage block, or the output side of a crafter (finished parts waiting there).</summary>
internal sealed class StoreStock
{
    public Entity BlockEntity;
    public InventoryComponent Inventory;
    public string Name;
    public bool IsStorage;
    public readonly Dictionary<ItemDefinition, double> Items = new();
    /// <summary>Inventory capacity (InventoryComponent.Mass: max, current) in the game's own unit; NaN when unreadable.</summary>
    public double MassMax = double.NaN;
    public double MassNow = double.NaN;
    /// <summary>Sum of amount * ItemDefinition.MassPerUnit over Items; compared with MassNow to find the unit scale.</summary>
    public double MassCalc;
}

/// <summary>A crafter the planner may use (working, not [NoMake], not [Make Ammo]).</summary>
internal sealed class Maker
{
    public Entity Entity;
    public string Name;
    /// <summary>Smelter, Refinery, Assembler, Fabricator or Gearforge, the same names the recipes use.</summary>
    public string Machine;
    public ItemConverterComponent Converter;
    public InventoryComponent Input;
    public InventoryComponent Output;
    /// <summary>Set by the conveyor check; true until proven otherwise.</summary>
    public bool Linked = true;
}

/// <summary>Everything the planner knows about one owned grid.</summary>
internal sealed class GridStock
{
    public CubeGridComponent Grid;
    public readonly List<StoreStock> Stores = new();
    public readonly List<Maker> Makers = new();
    /// <summary>Crafters that exist but are not working (unfinished, damaged, unpowered ...); counted for the alerts.</summary>
    public int NonFunctionalCrafters;
    /// <summary>Their names as the player sees them (custom names included), for the note in the window.</summary>
    public readonly List<string> NonFunctionalNames = new();
}

/// <summary>
/// Read-only scan of the blocks the local player owns: stock per inventory and the crafters, per grid.
/// Same ownership rule as LW Cargo Manager for now (owned by the local player).
/// Rule for inventories: storage blocks count fully; for crafters only the output side
/// (a single inventory, or InventoryIndex >= 1), never the input queue.
/// Whether the stock is actually reachable by the crafters is decided later (PlanReport), through the conveyors.
/// </summary>
internal static class StockScan
{
    public static Dictionary<CubeGridComponent, GridStock> Run(PlannerContext context)
    {
        var grids = new Dictionary<CubeGridComponent, GridStock>();

        foreach (var entity in context.Session.GetEntitiesOfType<FunctionalBlockComponent>())
        {
            var block = entity.TryGet<FunctionalBlockComponent>();
            var grid = block?.Grid;
            if (grid == null) continue;

            var owner = entity.TryGetOwner() ?? grid.Entity.TryGetOwner();
            if (!owner.HasValue || !owner.Value.Equals(context.Player)) continue;

            var kind = block.Definition.BlockKind?.Guid;
            var role = PlanKinds.RoleOf(kind);
            if (role == PlanRole.Consumer) continue;
            var machine = PlanKinds.MachineOf(kind);

            if (!grids.TryGetValue(grid, out var info))
                grids[grid] = info = new GridStock { Grid = grid };

            if (!block.Functional)
            {
                if (machine != null)
                {
                    info.NonFunctionalCrafters++;
                    info.NonFunctionalNames.Add(block.DisplayName.EvaluateText() ?? machine);
                }
                continue;
            }

            var name = block.DisplayName.EvaluateText() ?? "";
            var inventories = new List<InventoryComponent>();
            foreach (var component in entity.Components)
                if (component is InventoryComponent inventory && inventory.ConveyorSystem != null)
                    inventories.Add(inventory);

            foreach (var inventory in inventories)
            {
                var isOutput = role == PlanRole.Storage || inventories.Count == 1 || inventory.InventoryIndex >= 1;
                if (!isOutput) continue;
                var store = new StoreStock { BlockEntity = entity, Inventory = inventory, Name = name, IsStorage = role == PlanRole.Storage };
                var items = inventory.Items;
                for (var i = 0; i < items.Length; i++)
                {
                    store.Items.TryGetValue(items[i].Definition, out var sum);
                    store.Items[items[i].Definition] = sum + Fx.ToDouble(items[i].Amount);
                }
                try
                {
                    var mass = inventory.Mass;
                    store.MassMax = Fx.ToDouble(mass.Item1);
                    store.MassNow = Fx.ToDouble(mass.Item2);
                    foreach (var pair in store.Items) store.MassCalc += pair.Value * Fx.ToDouble(pair.Key.MassPerUnit);
                }
                catch (System.Exception) { store.MassMax = store.MassNow = double.NaN; }
                info.Stores.Add(store);
            }

            if (machine != null && OrderPlan.MayUse(name) && inventories.Count > 0)
            {
                ItemConverterComponent converter = null;
                foreach (var component in entity.Components)
                    if (component.GetType() == typeof(ItemConverterComponent)) { converter = (ItemConverterComponent)(object)component; break; }
                if (converter != null)
                    info.Makers.Add(new Maker
                    {
                        Entity = entity, Name = name, Machine = machine, Converter = converter,
                        Input = inventories.FirstOrDefault(i => i.InventoryIndex == 0) ?? inventories[0],
                        Output = inventories.FirstOrDefault(i => i.InventoryIndex >= 1) ?? inventories[0],
                    });
            }
        }

        return grids;
    }
}
