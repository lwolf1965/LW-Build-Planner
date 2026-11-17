using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using ClientPlugin.Policy;
using Keen.Game2.Game.SessionComponents;
using Keen.Game2.Simulation.WorldObjects.CubeBlocks;
using Keen.VRage.Core;
using Keen.VRage.Library.Definitions;
using Keen.VRage.Library.Utils;

namespace ClientPlugin.Game;

/// <summary>
/// Keeps the build list of each world across save and restart: one small file per world in the plugin's own folder
/// under the game's user-data folder (Singleton&lt;VRageCore&gt;.Instance.AppDataPath\LwBuildPlanner\Worlds), named after
/// the world's save id (WorldSessionComponent.SaveContainerId). Written after every change of the list, read when a
/// world loads. A world that was never saved has no id yet: its list is kept from the first save on.
/// Orders are not kept: after a restart the crafters finish what they have, and "Order missing" orders the rest.
/// Blocks are found again by their definition Guid (DefinitionManager.TryGetDefinition); a block the game no longer
/// knows (mod removed) is dropped with a log line. Any file error is logged and never stops the game.
/// </summary>
internal static class PlanSave
{
    private static readonly object Gate = new();
    private static WeakReference<WorldSessionComponent> world;
    private static string lastId;

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
        try
        {
            var path = PathOf(loaded);
            if (path != null && File.Exists(path))
            {
                var manager = Singleton<DefinitionManager>.Instance;
                foreach (var line in PlanFile.Read(File.ReadAllText(path)))
                {
                    if (!Guid.TryParse(line.Block, out var blockId) || !manager.TryGetDefinition(blockId, out CubeBlockDefinition block))
                    {
                        dropped++;
                        PluginLog.Warn($"Build list: '{line.Name}' is not in the game any more (mod removed?), dropped.");
                        continue;
                    }
                    object key = block;
                    if (line.Key != line.Block && Guid.TryParse(line.Key, out var keyId) && manager.TryGetDefinition(keyId, out Definition picked)) key = picked;
                    var armor = PlanStore.IsArmor(block);
                    PlanStore.Restore(new PlannedEntry
                    {
                        Key = key, Block = block, Armor = armor,
                        Name = string.IsNullOrWhiteSpace(line.Name) ? GPick.NameOf(block) : line.Name,
                        Count = Math.Min(line.Count, PlanLimits.Max(armor)),
                    });
                    restored++;
                }
            }
            PluginLog.Info(path == null
                ? "Build list: this world has no save id yet; the list is kept from its first save on."
                : $"Build list: {restored} line(s) restored{(dropped > 0 ? $", {dropped} dropped" : "")} ({Path.GetFileName(path)}).");
        }
        catch (Exception e)
        {
            PluginLog.Warn("Build list: reading the saved list failed: " + e.Message);
        }
        lock (Gate) world = new WeakReference<WorldSessionComponent>(loaded);
    }

    /// <summary>After every change of the list (any thread): writes the list of the current world.</summary>
    public static void Save()
    {
        lock (Gate)
        {
            if (world == null || !world.TryGetTarget(out var current)) return;
            try
            {
                var path = PathOf(current);
                if (path == null) return; // not saved yet
                Directory.CreateDirectory(Path.GetDirectoryName(path)!);
                var lines = PlanStore.Snapshot().Select(e => new PlanLine
                {
                    Key = (e.Key as Definition)?.Guid.ToString() ?? e.Block.Guid.ToString(),
                    Block = e.Block.Guid.ToString(), Count = e.Count, Name = e.Name,
                });
                var temp = path + ".tmp";
                File.WriteAllText(temp, PlanFile.Write(lines));
                File.Move(temp, path, true); // replace in one step: a crash never leaves half a file
            }
            catch (Exception e)
            {
                PluginLog.Warn("Build list: saving failed: " + e.Message);
            }
        }
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
