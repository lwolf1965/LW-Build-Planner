using System;
using System.Collections.Concurrent;
using System.Diagnostics;
using System.Runtime.CompilerServices;
using Keen.Game2.Client.GameSystems.PlayerControl;
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

    /// <summary>Build list, check result and orders belong to one world: a new session starts them empty.</summary>
    private static void ForgetWorld(WorldSessionComponent world)
    {
        PlanSave.Detach(); // nothing of the old world is written into the new world's file
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
