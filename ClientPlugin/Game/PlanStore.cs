using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Linq;
using ClientPlugin.Policy;
using Keen.Game2.Simulation.WorldObjects.CubeBlocks;

namespace ClientPlugin.Game;

/// <summary>One line of the build list: a block variant as picked in the G menu.</summary>
internal sealed class PlannedEntry
{
    /// <summary>The G menu tile this line came from; the key the window's +/- buttons change it by.</summary>
    public object Key;
    /// <summary>Name as the G menu shows it (variant, e.g. "Mk3 200").</summary>
    public string Name;
    /// <summary>The cost comes from here (CubeBlockDefinition.Items).</summary>
    public CubeBlockDefinition Block;
    public int Count;
    /// <summary>Armor goes by the hundred (limit 1000 per line), working blocks by the handful (limit 50).</summary>
    public bool Armor;
}

/// <summary>
/// The build list and the messages waiting to be shown on the HUD.
/// Filled from the UI thread (G menu keys, the window's +/- buttons), read on the game thread (order): guarded by a lock.
/// Lines are kept per picked variant (the G menu tile's definition), because several variants can share one cost.
/// A line that reaches 0 leaves the list. Entries are never changed in place: every change stores a new entry, so a
/// snapshot taken for the screen stays consistent.
/// Not saved yet: the list is empty after a game restart.
/// </summary>
internal static class PlanStore
{
    private static readonly object Gate = new();
    private static readonly Dictionary<object, PlannedEntry> Entries = new(ReferenceEqualityComparer.Instance);

    /// <summary>The block that was selected in the G menu when Ctrl+N was pressed; the window offers it with Add buttons. Count is unused.</summary>
    public static volatile PlannedEntry Offered;

    /// <summary>Adds blocks of the offered block, kept within the line's limit. Returns how many were really added.</summary>
    public static int AddOffered(PlannedEntry offered, int count)
    {
        lock (Gate)
        {
            var before = Entries.TryGetValue(offered.Key, out var entry) ? entry.Count : 0;
            var after = PlanLimits.Apply(before, count, offered.Armor);
            if (after != before)
                Entries[offered.Key] = new PlannedEntry { Key = offered.Key, Name = offered.Name, Block = offered.Block, Count = after, Armor = offered.Armor };
            return after - before;
        }
    }

    /// <summary>Messages for the player; shown by the UI thread as HUD notifications and always logged.</summary>
    public static readonly ConcurrentQueue<string> Messages = new();

    public static bool IsArmor(CubeBlockDefinition block) => block.GetType().Name.Contains("Armor", StringComparison.OrdinalIgnoreCase);

    /// <summary>Adds (or with a negative count removes) blocks; returns the new count of that line.</summary>
    public static int Add(object variantKey, string name, CubeBlockDefinition block, int count)
    {
        lock (Gate)
        {
            Entries.TryGetValue(variantKey, out var entry);
            var now = Math.Max(0, (entry?.Count ?? 0) + count);
            if (now == 0) Entries.Remove(variantKey);
            else Entries[variantKey] = new PlannedEntry { Key = variantKey, Name = name, Block = block, Count = now, Armor = IsArmor(block) };
            return now;
        }
    }

    /// <summary>
    /// The window's +/- buttons: changes a line by delta blocks, kept between 0 and the line's limit; at 0 the line is
    /// removed. Returns the new count (0 for a line that is not in the list any more).
    /// </summary>
    public static int Change(object variantKey, int delta)
    {
        lock (Gate)
        {
            if (!Entries.TryGetValue(variantKey, out var entry)) return 0;
            var now = PlanLimits.Apply(entry.Count, delta, entry.Armor);
            if (now == 0) Entries.Remove(variantKey);
            else Entries[variantKey] = new PlannedEntry { Key = variantKey, Name = entry.Name, Block = entry.Block, Count = now, Armor = entry.Armor };
            return now;
        }
    }

    /// <summary>Takes a whole line out of the list.</summary>
    public static void Remove(object variantKey)
    {
        lock (Gate) Entries.Remove(variantKey);
    }

    public static int CountOf(object variantKey)
    {
        lock (Gate) return Entries.TryGetValue(variantKey, out var entry) ? entry.Count : 0;
    }

    public static void Clear()
    {
        lock (Gate) Entries.Clear();
    }

    public static List<PlannedEntry> Snapshot()
    {
        lock (Gate) return Entries.Values.ToList();
    }

    public static void Say(string message)
    {
        PluginLog.Info("HUD: " + message);
        Messages.Enqueue(message);
    }

    /// <summary>First character of a queued message that asks for the game's "success" look and high priority (see HudNotify).</summary>
    public const char ImportantMark = '\u0001';

    /// <summary>A result the player is waiting for (orders done): shown as a success notification with high priority.</summary>
    public static void SayDone(string message)
    {
        PluginLog.Info("HUD (done): " + message);
        Messages.Enqueue(ImportantMark + message);
    }
}
