using System;
using System.Collections.Generic;
using System.Linq;
using System.Reflection;
using Keen.Game2.Simulation.WorldObjects.CubeBlocks.Production.ItemConverters;
using Keen.Game2.Simulation.WorldObjects.Items;

namespace ClientPlugin.Game;

/// <summary>
/// The crafter calls (Game2.Simulation 4.0.0.3169), typed where the game makes them public:
///  - ItemConverterComponent.Crafting (public bool) is true while it works,
///  - ItemConverterComponent.TryEnqueueRecipe(recipe, int batches) is public and returns bool,
///  - ItemRecipeDefinition.Inputs / Outputs: ImmutableArray of ItemAmount (Item, Amount).
/// Two calls are not public, so they stay reflection (typed in and out), looked up once:
///  - CanProduceItem(ItemDefinition) returns the ItemRecipeDefinition or null,
///  - IsProducingRecipeFor(recipe, requester) returns a nullable value, set only while the recipe is queued or running.
/// </summary>
internal static class CrafterApi
{
    private const BindingFlags Any = BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic;

    private static MethodInfo canProduce;
    private static bool canProduceWarned;

    public static ItemRecipeDefinition CanProduce(ItemConverterComponent converter, ItemDefinition item)
    {
        try
        {
            canProduce ??= typeof(ItemConverterComponent).GetMethods(Any)
                .FirstOrDefault(m => m.Name == "CanProduceItem" && m.GetParameters().Length == 1 && m.GetParameters()[0].ParameterType == typeof(ItemDefinition))
                ?? throw new MissingMethodException("ItemConverterComponent.CanProduceItem not found");
            return canProduce.Invoke(converter, new object[] { item }) as ItemRecipeDefinition;
        }
        catch (Exception e)
        {
            if (!canProduceWarned)
            {
                canProduceWarned = true;
                PluginLog.Warn("Crafter recipe check not available: " + (e.InnerException ?? e).Message);
            }
            return null;
        }
    }

    public static bool IsCrafting(ItemConverterComponent converter) => converter.Crafting;

    private static MethodInfo producing;
    private static bool producingWarned;

    /// <summary>
    /// Whether the crafter still has this recipe (with no requester, as the planner and the player queue it) in its
    /// current job or its queue: ItemConverterComponent.IsProducingRecipeFor(recipe, requester), the game's own check,
    /// which returns a value only while it is there. Null when the check is not available (then storage arrivals count).
    /// </summary>
    public static bool? IsProducing(ItemConverterComponent converter, ItemRecipeDefinition recipe)
    {
        try
        {
            producing ??= typeof(ItemConverterComponent).GetMethods(Any).FirstOrDefault(m => m.Name == "IsProducingRecipeFor" && m.GetParameters().Length == 2)
                ?? throw new MissingMethodException("ItemConverterComponent.IsProducingRecipeFor not found");
            return producing.Invoke(converter, new object[] { recipe, null }) != null;
        }
        catch (Exception e)
        {
            if (!producingWarned)
            {
                producingWarned = true;
                PluginLog.Warn("Crafter queue check not available, progress counts storage arrivals only: " + (e.InnerException ?? e).Message);
            }
            return null;
        }
    }

    public static bool Enqueue(ItemConverterComponent converter, ItemRecipeDefinition recipe, int batches) => converter.TryEnqueueRecipe(recipe, batches);

    public static List<(ItemDefinition Item, double Amount)> Inputs(ItemRecipeDefinition recipe) => Amounts(recipe.Inputs);

    public static List<(ItemDefinition Item, double Amount)> Outputs(ItemRecipeDefinition recipe) => Amounts(recipe.Outputs);

    private static bool outputFullWarned;

    /// <summary>
    /// Whether the crafter stopped because its output inventory is full (cargo full, nowhere to put the parts):
    /// the game's own failure flag, ItemConverterComponent.RecipeFailureReasonData.FailureReason has InventoryFull,
    /// read from the block entity's data like the game's ItemCraftingBlockedParameter does. False when not readable.
    /// </summary>
    public static bool OutputFull(Maker maker)
    {
        try
        {
            var data = maker.Entity.Data;
            var reason = data.Get<ItemConverterComponent.RecipeFailureReasonData>().FailureReason;
            return (reason & ItemConverterComponent.FailureReasons.InventoryFull) != 0;
        }
        catch (Exception e)
        {
            if (!outputFullWarned)
            {
                outputFullWarned = true;
                PluginLog.Warn("Crafter output check not available, a full output shows as earlier jobs: " + e.Message);
            }
            return false;
        }
    }

    private static List<(ItemDefinition, double)> Amounts(IEnumerable<ItemAmount> amounts)
    {
        var result = new List<(ItemDefinition, double)>();
        try
        {
            foreach (var amount in amounts)
                if (amount.Item != null) result.Add((amount.Item, Fx.ToDouble(amount.Amount)));
        }
        catch (Exception) { /* default ImmutableArray throws when enumerated */ }
        return result;
    }
}
