using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using ClientPlugin.Policy;
using Keen.Game2.Game.SessionComponents;
using Keen.Game2.Simulation.GameSystems.GameTimes;
using Keen.Game2.Simulation.WorldObjects.CubeBlocks;
using Keen.Game2.Simulation.WorldObjects.Items;
using Keen.VRage.Core;
using Keen.VRage.Library.Definitions;
using Keen.VRage.Library.Utils;

namespace ClientPlugin.Game;

/// <summary>
/// Keeps the build list of each world across save and restart: one small file per world in the plugin's own folder
/// under the game's user-data folder (Singleton&lt;VRageCore&gt;.Instance.AppDataPath\LwBuildPlanner\Worlds), named after
/// the world's save id (WorldSessionComponent.SaveContainerId). Written when the game saves the world (every kind of
/// save, see WorldSavePatch), so the file always belongs to the save the world goes back to: after "exit without saving"
/// or a crash the list is the one of the last save, not one with changes the world never saw. An empty list leaves no
/// file (an old one is removed). Read when a world loads. Save as / a new world's first save get their own file.
/// The running orders go to a second file next to it ("&lt;id&gt;.orders.txt", Policy/OrderFile) and go on after a load
/// (Lone 2026-11-20); no orders: no file.
/// Blocks are found again by their definition Guid (DefinitionManager.TryGetDefinition); a block the game no longer
/// knows (mod removed) is dropped with a log line. Any file error is logged and never stops the game.
/// </summary>
internal static class PlanSave
{
    private static readonly object Gate = new();
    private static WeakReference<WorldSessionComponent> world;
    private static string lastId;
    // What the file holds now (null: no file), so an autosave with an unchanged list writes nothing.
    private static string writtenPath;
    private static string writtenText;
    private static string writtenOrders;

    /// <summary>A list line's key as the files write it: the picked definition's Guid (a blueprint line's composite).</summary>
    public static string KeyText(object key) => ((key as ProjectKey)?.Composite ?? key as Definition)?.Guid.ToString();

    /// <summary>The world's game time (saved with the world, runs while playing), in ticks; 0 when not found.</summary>
    private static long GameTimeOf(WorldSessionComponent world)
    {
        try { return world?.OwnedSession?.SessionComponents?.TryGet<GameTimeSessionComponent>()?.CurrentGameTime.Ticks ?? 0; }
        catch { return 0; }
    }

    private static string OrdersPath(string listPath) => Path.ChangeExtension(listPath, ".orders.txt");

    private static string Folder => Path.Combine(Singleton<VRageCore>.Instance.AppDataPath, "LwBuildPlanner", "Worlds");

    /// <summary>A world is being left or loaded: stop writing until the new world's list is read.</summary>
    public static void Detach()
    {
        lock (Gate) world = null;
    }

    /// <summary>Game thread, right after a world loaded (the list is already empty): reads that world's list.</summary>
    public static void Attach(WorldSessionComponent loaded)
    {
        var restored = 0;
        var dropped = 0;
        string project = null;
        var started = false;
        try
        {
            var path = PathOf(loaded);
            var text = path != null && File.Exists(path) ? File.ReadAllText(path) : null;
            lock (Gate)
            {
                writtenPath = path;
                writtenText = text;
            }
            if (text != null)
            {
                var manager = Singleton<DefinitionManager>.Instance;
                var lines = PlanFile.Read(text, out project, out started, out var savedAt);
                // An older save of this world (autosave slot, backup, a reload after a disaster): the files are from the
                // newest save. Say so and what to do (Lone 2026-11-20: "it warns the player and shows ways").
                var now = GameTimeOf(loaded);
                if (savedAt > 0 && now > 0 && now + TimeSpan.TicksPerMinute < savedAt)
                {
                    PluginLog.Info($"Build list: this save is older than the list file ({TimeSpan.FromTicks(savedAt - now):%h}h {TimeSpan.FromTicks(savedAt - now):%m}m of game time); a dialog asks the player once the world is on screen.");
                    PlannerEngine.AskAboutOlderSave(); // a HUD line now is lost under the loading screen
                }
                foreach (var line in lines)
                {
                    if (!Guid.TryParse(line.Block, out var blockId) || !manager.TryGetDefinition(blockId, out CubeBlockDefinition block))
                    {
                        dropped++;
                        PluginLog.Warn($"Build list: '{line.Name}' is not in the game any more (mod removed?), dropped.");
                        continue;
                    }
                    Definition picked = block;
                    if (line.Key != line.Block && Guid.TryParse(line.Key, out var keyId) && manager.TryGetDefinition(keyId, out Definition keyDefinition)) picked = keyDefinition;
                    var armor = PlanStore.IsArmor(block);
                    PlanStore.Restore(new PlannedEntry
                    {
                        // Blueprint project lines get their own key and no line limit (see PlanStore.SetProject).
                        Key = line.Project ? new ProjectKey(picked) : picked, Block = block, Armor = armor, Project = line.Project,
                        Name = string.IsNullOrWhiteSpace(line.Name) ? GPick.NameOf(block) : line.Name,
                        Count = line.Project ? line.Count : Math.Min(line.Count, PlanLimits.Max(armor)),
                    });
                    restored++;
                }
            }
            PlanStore.RestoreProject(project);
            PlanStore.ProjectStarted = started && PlanStore.ProjectName != null;
            if (PlanStore.ProjectName != null) PluginLog.Info($"Build list: blueprint project '{PlanStore.ProjectName}' restored.");
            PluginLog.Info(path == null
                ? "Build list: this world has no save id yet; the list is written from its first save on."
                : $"Build list: {restored} line(s) restored{(dropped > 0 ? $", {dropped} dropped" : "")} ({Path.GetFileName(path)}).");
            RestoreOrders(path);
        }
        catch (Exception e)
        {
            PluginLog.Warn("Build list: reading the saved list failed: " + e.Message);
        }
        lock (Gate) world = new WeakReference<WorldSessionComponent>(loaded);
    }

    /// <summary>
    /// The game is saving this world (any thread; called by WorldSavePatch before the world's state is captured): the
    /// list goes to the file of the save being written; an empty list removes the file. Unchanged: nothing is written.
    /// Another session's save (not the attached world) is ignored. Never throws: a file error is logged only.
    /// </summary>
    public static void WorldSaving(WorldSessionComponent saving)
    {
        lock (Gate)
        {
            if (world == null || !world.TryGetTarget(out var current) || !ReferenceEquals(current, saving)) return;
            try
            {
                var path = PathOf(current);
                if (path == null) return; // no save id: nothing to name the file by
                try { WriteOrders(path); }
                catch (Exception e) { PluginLog.Warn("Orders: saving failed: " + e.Message); }
                var entries = PlanStore.Snapshot();
                if (entries.Count == 0)
                {
                    if (File.Exists(path))
                    {
                        File.Delete(path);
                        PluginLog.Info($"Build list: empty at the world save, {Path.GetFileName(path)} removed.");
                    }
                    writtenPath = path;
                    writtenText = null;
                    return;
                }
                var lines = entries.Select(e => new PlanLine
                {
                    Key = KeyText(e.Key) ?? e.Block.Guid.ToString(),
                    Block = e.Block.Guid.ToString(), Count = e.Count, Name = e.Name, Project = e.Project,
                });
                var text = PlanFile.Write(lines, PlanStore.ProjectName, PlanStore.ProjectStarted, GameTimeOf(current));
                if (path == writtenPath && text == writtenText && File.Exists(path)) return; // e.g. autosave, list unchanged
                Directory.CreateDirectory(Path.GetDirectoryName(path)!);
                var temp = path + ".tmp";
                File.WriteAllText(temp, text);
                File.Move(temp, path, true); // replace in one step: a crash never leaves half a file
                writtenPath = path;
                writtenText = text;
                PluginLog.Info($"Build list: {entries.Count} line(s) written with the world save ({Path.GetFileName(path)}).");
            }
            catch (Exception e)
            {
                PluginLog.Warn("Build list: saving failed: " + e.Message);
            }
        }
    }

    /// <summary>Game thread, after the list was read: the orders saved with the world go on (OrderBook.Restore).</summary>
    private static void RestoreOrders(string listPath)
    {
        var path = listPath == null ? null : OrdersPath(listPath);
        var text = path != null && File.Exists(path) ? File.ReadAllText(path) : null;
        lock (Gate) writtenOrders = text;
        var saved = OrderFile.Read(text);
        if (saved == null) return;
        var manager = Singleton<DefinitionManager>.Instance;
        var items = new Dictionary<string, ItemDefinition>();
        foreach (var id in saved.Parts.Select(p => p.Item).Concat(saved.HandedOut.Keys).Concat(saved.Groups.SelectMany(g => g.Parts.Keys)).Distinct())
            if (Guid.TryParse(id, out var itemId) && manager.TryGetDefinition(itemId, out ItemDefinition item)) items[id] = item;
            else PluginLog.Warn($"Orders: part '{id}' is not in the game any more (mod removed?), dropped.");
        OrderBook.Restore(saved, PlanStore.Snapshot(), items);
    }

    /// <summary>Inside the lock: the orders file of this save (OrderBook.SavedText); no orders removes it.</summary>
    private static void WriteOrders(string listPath)
    {
        var path = OrdersPath(listPath);
        var text = OrderBook.SavedText;
        if (text == writtenOrders && File.Exists(path) == (text != null)) return;
        if (text == null)
        {
            if (File.Exists(path)) File.Delete(path);
        }
        else
        {
            Directory.CreateDirectory(Path.GetDirectoryName(path)!);
            File.WriteAllText(path + ".tmp", text);
            File.Move(path + ".tmp", path, true);
            PluginLog.Info($"Orders: written with the world save ({Path.GetFileName(path)}).");
        }
        writtenOrders = text;
    }

    private static string PathOf(WorldSessionComponent loaded)
    {
        var id = Convert.ToString(loaded?.SaveContainerId);
        var name = PlanFile.FileName(id);
        if (id != lastId)
        {
            lastId = id;
            PluginLog.Info($"Build list: world save id '{id}'" + (name == null ? " (none yet)" : ""));
        }
        return name == null ? null : Path.Combine(Folder, name);
    }
}
