using System;
using System.Collections.Concurrent;
using System.Diagnostics;
using System.Runtime.CompilerServices;
using Keen.Game2.Client.GameSystems.PlayerControl;
using Keen.Game2.Client.UI.InGame;
using Keen.Game2.Game.SessionComponents;
using Keen.Game2.Simulation.GameSystems.Ownership;
using Keen.Game2.Simulation.RuntimeSystems.CoreScenes;
using Keen.VRage.Core.Game.Systems;
using Keen.VRage.DCS.Components;

namespace ClientPlugin.Game;

/// <summary>What a queued action may use: the running world and the local player.</summary>
internal sealed class PlannerContext
{
    public Session Session;
    public IdentityId Player;
}

/// <summary>
/// Runs queued actions from the Session.Update hook, on the DCS thread, only on the in-process game server
/// (same guards as LW Cargo Manager's engine). The UI never touches the world directly: it calls Enqueue.
/// Nothing runs while the queue is empty, so the plugin costs nothing when idle.
/// </summary>
internal static class PlannerEngine
{
    /// <summary>Entity captured from InputGameComponent.Init, used to reach the scene.</summary>
    public static Entity Host;

    private static readonly ConcurrentQueue<Action<PlannerContext>> Pending = new();
    private static readonly ConditionalWeakTable<Session, object> Probed = new();

    public static void Enqueue(Action<PlannerContext> action) => Pending.Enqueue(action);

    /// <summary>When the older-save dialog is due (Stopwatch seconds), or below 0 when none is.</summary>
    private static double olderSaveAt = -1;
    private static readonly Stopwatch Clock = Stopwatch.StartNew();

    /// <summary>An older save was loaded (PlanSave): the dialog opens a few seconds after the world runs.</summary>
    public static void AskAboutOlderSave() => olderSaveAt = Clock.Elapsed.TotalSeconds + 5;

    /// <summary>Build list, check result and orders belong to one world: a new session starts them empty.</summary>
    private static void ForgetWorld(WorldSessionComponent world)
    {
        PlanSave.Detach(); // the old world's list is never written into the new world's file
        HudNotify.Forget(); // the old world's HUD is gone; the new one is remembered when a terminal opens
        OrderBook.ForgetWorld();
        GameLanguage.Refresh(); // messages of this world in the game's language
        PlanStore.Clear();
        PlanStore.Offered = null;
        PlanService.LastView = null;
        PluginLog.Info("New world session: build list, check and orders reset.");
        PlanSave.Attach(world); // this world's saved build list
    }

    public static void OnUpdate(Session session, bool updateLifetime)
    {
        if (!updateLifetime) return;
        try
        {
            var scene = Host?.Scene?.UserObject as GameCoreScene;
            var world = scene?.GameServer?.TryGet<WorldSessionComponent>();
            if (world == null || !ReferenceEquals(world.OwnedSession, session) || session.IsUnloading) return;

            var client = scene.GameClient?.TryGet<WorldSessionComponent>();
            if (client == null || world.Pause.IsPaused || client.Pause.IsPaused || world.IsSaving) return;

            // The older-save dialog: once the world has run a few seconds (not under the loading screen).
            if (olderSaveAt >= 0 && Clock.Elapsed.TotalSeconds >= olderSaveAt)
            {
                olderSaveAt = -1;
                Avalonia.Threading.Dispatcher.UIThread.Post(Screens.OlderSaveDialog.Open);
            }

            // Idle frames (nothing queued, session already seen) stop here, before the player lookup.
            var probed = Probed.TryGetValue(session, out _);
            if (probed && Pending.IsEmpty && !OrderBook.TickDue) return;

            var players = client.OwnedSession?.SessionComponents?.TryGet<ClientPlayersSessionComponent>();
            if (players == null || !players.LocalPlayerIdentity.IsValid()) return;

            if (!session.Scene.IsCurrentThreadDCS()) return;

            var context = new PlannerContext { Session = session, Player = players.LocalPlayerIdentity };

            if (!probed)
            {
                Probed.Add(session, new object()); // from now on idle frames stop early (see above)
                ForgetWorld(world); // a world was loaded: nothing of the previous one may stay
                // The HUD notifications go through the client session's in-game UI (the same object a terminal screen
                // gets as _inGameUI, it implements InGameUI). Taken here at world load, messages show from the start,
                // not only after the first terminal was opened (found 2026-11-19).
                var hud = client.OwnedSession?.SessionComponents?.TryGet<SessionInGameUISessionComponent>();
                HudNotify.Remember(hud);
                PluginLog.Info(hud != null
                    ? "HUD: in-game UI found at world load, notifications show without a terminal."
                    : "HUD: in-game UI not found at world load, notifications show after the first terminal.");
            }

            if (OrderBook.TickDue)
            {
                try { OrderBook.Tick(context); }
                catch (Exception e) { PluginLog.Error("Order tick failed: " + e); }
            }

            while (Pending.TryDequeue(out var action))
            {
                try { action(context); }
                catch (Exception e) { PluginLog.Error("Planner action failed: " + e); }
            }
        }
        catch (Exception e)
        {
            PluginLog.Error("Planner update failed: " + e);
        }
    }
}
