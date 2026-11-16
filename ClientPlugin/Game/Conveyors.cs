using System.Collections.Generic;
using Keen.Game2.Simulation.WorldObjects.CubeGrids.ResourceDistribution.Conveyors;
using Keen.Game2.Simulation.WorldObjects.Items;

namespace ClientPlugin.Game;

/// <summary>
/// Read-only conveyor reachability, the same game call LW Cargo Manager uses for every transfer
/// (ConveyorSystemComponent.IterateReachableInventories). Reachability is per item: a sorter can block one item.
/// The game returns its own struct enumerator, so it is copied with foreach.
/// </summary>
internal static class Conveyors
{
    public static HashSet<InventoryComponent> ReachableSet(InventoryComponent source, ItemDefinition item)
    {
        var set = new HashSet<InventoryComponent>();
        foreach (var reachable in ConveyorSystemComponent.IterateReachableInventories(source, item, followEdgeDirection: true))
            set.Add(reachable);
        return set;
    }
}
