using System;
using ClientPlugin.Game;
using HarmonyLib;
using Keen.Game2.Client.Input;
using Keen.Game2.Game.SessionComponents;
using Keen.VRage.Core.Game.Systems;

namespace ClientPlugin.Patches;

// Both hooks are shared with LW Cargo Manager (same game methods). Harmony runs the postfixes of every plugin,
// each under its own Harmony id, so the two plugins don't conflict.

/// <summary>Captures an entity whose Scene leads to GameCoreScene.</summary>
[HarmonyPatch(typeof(InputGameComponent), "Init", new Type[0])]
internal static class InputInitPatch
{
    private static void Postfix(InputGameComponent __instance)
    {
        PlannerEngine.Host = __instance.Entity;
        PluginLog.Info("Input host captured.");
    }
}

/// <summary>Runs queued planner actions after each Session.Update(bool updateLifetime).</summary>
[HarmonyPatch(typeof(Session), nameof(Session.Update), new[] { typeof(bool) })]
internal static class SessionUpdatePatch
{
    // __0 = updateLifetime (positional, so a renamed parameter doesn't break the patch)
    private static void Postfix(Session __instance, bool __0)
    {
        PlannerEngine.OnUpdate(__instance, __0);
        Hotkeys.OnSessionUpdate(__instance); // Ctrl+N outside the G menu (client session only)
        HudNotify.PostDrain(); // messages show on time also without an open terminal (cheap when none wait)
    }
}

/// <summary>
/// UI-thread tick while a terminal screen (which also hosts the G menu) exists: reads the planner keys and shows the
/// planner's HUD messages. Target found by name, so a renamed game class only disables this hook (logged at start).
/// </summary>
[HarmonyPatch]
internal static class TerminalUpdatePatch
{
    private static System.Reflection.MethodBase TargetMethod()
        => AccessTools.Method(AccessTools.TypeByName("Keen.Game2.Client.UI.TerminalScreen.TerminalScreenViewModel"), "Update", new Type[0]);

    private static void Postfix(object __instance) => GPick.OnTerminalUpdate(__instance);
}
