using System;
using System.Collections;
using System.Collections.Generic;
using System.Linq;
using System.Reflection;
using Keen.Game2.Simulation.WorldObjects.Items;
using Keen.VRage.Library.Mathematics;

namespace ClientPlugin.Game;

/// <summary>
/// The crafter calls, all through reflection like LW Cargo Manager's AmmoJob (Game2.Simulation 4.0.0.3169):
///  - ItemConverterComponent.CanProduceItem(ItemDefinition) returns the ItemRecipeDefinition or null,
///  - ItemConverterComponent.Crafting (bool) is true while it works,
///  - ItemConverterComponent.TryEnqueueRecipe(recipe, int batches) returns bool,
///  - ItemRecipeDefinition.Inputs / Outputs: ImmutableArray of ItemAmount (fields Item, Amount).
/// TODO: check in game that CanProduceItem works for components in an Assembler like it does for ammo in the Gearforge.
/// </summary>
internal static class CrafterApi
{
    private const BindingFlags Any = BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic;
    private static readonly HashSet<string> Warned = new();

    public static object CanProduce(object converter, ItemDefinition item) => Call(converter.GetType(), "CanProduceItem", converter, item);

    public static bool IsCrafting(object converter) => Reflect.Get(Reflect.Member(converter.GetType(), "Crafting"), converter) is true;

    private static MethodInfo producingMethod;
    private static Type producingOwner;
    private static bool producingWarned;

    /// <summary>
    /// Whether the crafter still has this recipe (with no requester, as the planner and the player queue it) in its
    /// current job or its queue: ItemConverterComponent.IsProducingRecipeFor(recipe, requester), the game's own check,
    /// which returns a value only while it is there. Null when the check is not available (then storage arrivals count).
    /// </summary>
    public static bool? IsProducing(object converter, object recipe)
    {
        try
        {
            var type = converter.GetType();
            if (producingOwner != type)
            {
                producingOwner = type;
                producingMethod = type.GetMethods(Any).FirstOrDefault(m => m.Name == "IsProducingRecipeFor" && m.GetParameters().Length == 2);
            }
            if (producingMethod == null) throw new MissingMethodException("ItemConverterComponent.IsProducingRecipeFor not found");
            return producingMethod.Invoke(converter, new[] { recipe, null }) != null;
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

    public static bool Enqueue(object converter, object recipe, int batches) => Call(converter.GetType(), "TryEnqueueRecipe", converter, recipe, batches) is true;

    public static List<(ItemDefinition Item, double Amount)> Inputs(object recipe) => Amounts(Reflect.Get(Reflect.Member(recipe.GetType(), "Inputs"), recipe));

    public static List<(ItemDefinition Item, double Amount)> Outputs(object recipe) => Amounts(Reflect.Get(Reflect.Member(recipe.GetType(), "Outputs"), recipe));

    private static object Call(Type type, string name, object target, params object[] args)
    {
        var method = type.GetMethods(Any).FirstOrDefault(m => m.Name == name && m.GetParameters().Length == args.Length
            && m.GetParameters().Select((p, i) => args[i] == null || p.ParameterType.IsInstanceOfType(args[i]) || p.ParameterType == args[i].GetType()).All(x => x));
        if (method == null)
        {
            if (Warned.Add(type.Name + "." + name)) PluginLog.Warn($"{type.Name}.{name} not found");
            return null;
        }
        return method.Invoke(target, args);
    }

    private static List<(ItemDefinition, double)> Amounts(object immutableArray)
    {
        var result = new List<(ItemDefinition, double)>();
        try
        {
            if (immutableArray is IEnumerable list)
                foreach (var amount in list)
                {
                    var t = amount.GetType();
                    if (Reflect.Get(Reflect.Member(t, "Item"), amount) is ItemDefinition item
                        && Reflect.Get(Reflect.Member(t, "Amount"), amount) is FixedPoint fp)
                        result.Add((item, Fx.ToDouble(fp)));
                }
        }
        catch (Exception) { /* default ImmutableArray throws when enumerated */ }
        return result;
    }
}
