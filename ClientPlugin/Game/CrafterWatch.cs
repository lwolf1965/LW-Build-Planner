using System;
using System.Collections;
using System.Collections.Generic;
using System.Diagnostics;
using System.Linq;
using System.Reflection;
using HarmonyLib;

namespace ClientPlugin.Game;

/// <summary>
/// Diagnostics for the save / load test (2026-11-19): do the crafters keep their queues across a save and a load?
/// Writes to the log only, changes nothing:
///  - "World save started / finished" when the game saves (WorldSessionComponent.IsSaving goes on / off),
///  - "Crafters: ..." every 15 s while a crafter works and during the first 5 minutes after a world load, only when
///    something changed: each crafter's name, crafting or idle, and its queue (part x crafts) when it can be read.
/// The queue lives in the crafter's DCS data (ConversionQueueData.ConversionQueue: ConversionQueueItem { Recipe, Times,
/// Requester }), and ItemConverterObjectBuilder saves it (ConversionQueue, CurrentRecipe), so the game probably keeps it.
/// Reading it goes through Component.Data; if that fails, the first failure logs the data API once and the rest of the
/// watch still works (crafting / idle).
/// </summary>
internal static class CrafterWatch
{
    private const BindingFlags Any = BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic;
    private static readonly Stopwatch Clock = Stopwatch.StartNew();
    private static double worldLoadedAt = -1;
    private static double lastLook = -1000;
    private static string lastLine;
    private static bool anyBusy;
    private static bool saving;
    private static bool queueApiLogged;
    private static Type queueDataType;

    /// <summary>A world was loaded: watch the crafters for the next 5 minutes (and while they work).</summary>
    public static void NewWorld()
    {
        worldLoadedAt = Clock.Elapsed.TotalSeconds;
        lastLook = -1000;
        lastLine = null;
        anyBusy = false;
        PluginLog.Info("Crafter watch: world loaded, crafters are logged when they change (every 15 s while they work).");
    }

    /// <summary>Time for a look (engine frame check, cheap).</summary>
    public static bool Due
    {
        get
        {
            if (worldLoadedAt < 0) return false;
            var now = Clock.Elapsed.TotalSeconds;
            return now - lastLook >= 15 && (anyBusy || now - worldLoadedAt < 300);
        }
    }

    /// <summary>Engine: the world's IsSaving flag, every frame (cheap: logs only when it changes).</summary>
    public static void Saving(bool isSaving)
    {
        if (isSaving == saving) return;
        saving = isSaving;
        PluginLog.Info(isSaving ? "World save started. Last crafter state: " + (lastLine ?? "not looked at yet") : "World save finished.");
    }

    /// <summary>Game thread: one look at the player's crafters.</summary>
    public static void Look(PlannerContext context)
    {
        lastLook = Clock.Elapsed.TotalSeconds;
        var makers = StockScan.Run(context).Values.SelectMany(g => g.Makers).ToList();
        var busy = false;
        var parts = new List<string>();
        foreach (var maker in makers.OrderBy(m => m.Name, StringComparer.OrdinalIgnoreCase))
        {
            var crafting = false;
            try { crafting = CrafterApi.IsCrafting(maker.Converter); }
            catch (Exception) { /* read below */ }
            var queue = Queue(maker.Converter);
            if (crafting || queue.Length > 0) busy = true;
            parts.Add($"{maker.Name} {(crafting ? "crafting" : "idle")}{(queue.Length > 0 ? " [" + queue + "]" : "")}");
        }
        anyBusy = busy;
        var line = makers.Count == 0 ? "none found" : string.Join("; ", parts);
        if (line == lastLine) return;
        lastLine = line;
        PluginLog.Info("Crafters: " + line);
    }

    /// <summary>"Steel Plate x 10, Motor x 3" from the crafter's conversion queue; empty when empty or unreadable.</summary>
    private static string Queue(object converter)
    {
        try
        {
            queueDataType ??= AccessTools.TypeByName("ConversionQueueData");
            if (queueDataType == null) return "";
            var data = Reflect.Get(Reflect.Member(converter.GetType(), "Data"), converter);
            if (data == null) return "";
            object queueData = null;
            foreach (var method in data.GetType().GetMethods(Any).Where(m => m.IsGenericMethodDefinition && m.GetGenericArguments().Length == 1))
            {
                var parameters = method.GetParameters();
                try
                {
                    var typed = method.MakeGenericMethod(queueDataType);
                    if (parameters.Length == 0 && !typed.ReturnType.IsByRef && typed.ReturnType != typeof(void))
                        queueData = typed.Invoke(data, null);
                    else if (parameters.Length == 1 && parameters[0].IsOut && typed.ReturnType == typeof(bool))
                    {
                        var args = new object[1];
                        if (typed.Invoke(data, args) is true) queueData = args[0];
                    }
                }
                catch (Exception) { /* not this one */ }
                if (queueData != null && queueDataType.IsInstanceOfType(queueData)) break;
                queueData = null;
            }
            if (queueData == null)
            {
                LogApiOnce(data.GetType());
                return "";
            }
            var list = queueDataType.GetField("ConversionQueue", Any)?.GetValue(queueData);
            if (list is not IEnumerable items) return "";
            var texts = new List<string>();
            foreach (var item in items)
            {
                var type = item.GetType();
                var recipeHolder = type.GetField("Recipe", Any)?.GetValue(item);
                var recipe = recipeHolder?.GetType().GetProperty("Value", Any)?.GetValue(recipeHolder) ?? recipeHolder;
                var times = type.GetField("Times", Any)?.GetValue(item);
                var outputs = recipe == null ? null : CrafterApi.Outputs(recipe);
                var name = outputs is { Count: > 0 } ? OrderJob.NameOf(outputs[0].Item) : recipe?.ToString() ?? "?";
                texts.Add($"{name} x {times}");
            }
            return string.Join(", ", texts);
        }
        catch (Exception e)
        {
            if (!queueApiLogged)
            {
                queueApiLogged = true;
                PluginLog.Warn("Crafter watch: queue not readable: " + e.Message);
            }
            return "";
        }
    }

    private static void LogApiOnce(Type dataType)
    {
        if (queueApiLogged) return;
        queueApiLogged = true;
        var methods = dataType.GetMethods(Any).Where(m => m.DeclaringType != typeof(object))
            .Select(m => $"{m.ReturnType.Name} {m.Name}{(m.IsGenericMethodDefinition ? "<T>" : "")}({string.Join(", ", m.GetParameters().Select(p => (p.IsOut ? "out " : "") + p.ParameterType.Name))})")
            .Distinct();
        PluginLog.Info($"Crafter watch: queue data not found through {dataType.FullName}; its methods: {string.Join("; ", methods)}");
    }
}
