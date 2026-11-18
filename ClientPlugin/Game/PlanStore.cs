using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Linq;
using ClientPlugin.Policy;
using Keen.Game2.Simulation.WorldObjects.CubeBlocks;
using Keen.VRage.Library.Definitions;

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
    /// <summary>The line belongs to the blueprint project (key: ProjectKey; no line limit).</summary>
    public bool Project;

    public PlannedEntry With(int count) => new() { Key = Key, Name = Name, Block = Block, Count = count, Armor = Armor, Project = Project };
}

/// <summary>
/// Key of a blueprint project line: the block's composite definition, wrapped so a project line never merges with a
/// hand-picked line of the same block (the two stay separate lines).
/// </summary>
internal sealed class ProjectKey
{
    public readonly Definition Composite;
    public ProjectKey(Definition composite) => Composite = composite;
    public override string ToString() => "project " + Composite;
}

/// <summary>
/// The build list and the messages waiting to be shown on the HUD.
/// Filled from the UI thread (G menu keys, the window's +/- buttons), read on the game thread (order): guarded by a lock.
/// Lines are kept per picked variant (the G menu tile's definition), because several variants can share one cost.
/// A line that reaches 0 leaves the list. Entries are never changed in place: every change stores a new entry, so a
/// snapshot taken for the screen stays consistent.
/// Saved per world by PlanSave after every change (see there).
/// </summary>
internal static class PlanStore
{
    private static readonly object Gate = new();
    private static readonly Dictionary<object, PlannedEntry> Entries = new(ReferenceEqualityComparer.Instance);

    /// <summary>Name of the blueprint project in the list (one at a time), null when there is none.</summary>
    public static string ProjectName { get; private set; }

    /// <summary>
    /// A blueprint was read: its lines replace the project lines in the list (only one blueprint at a time); hand-picked
    /// lines stay. Returns the name of the project it replaced, or null.
    /// </summary>
    public static string SetProject(string name, IReadOnlyCollection<PlannedEntry> lines)
    {
        string previous;
        lock (Gate)
        {
            previous = ProjectName;
            foreach (var key in Entries.Where(p => p.Value.Project).Select(p => p.Key).ToList())
            {
                Entries.Remove(key);
                Ordered.Remove(key);
            }
            foreach (var line in lines) Entries[line.Key] = line;
            ProjectName = lines.Count > 0 ? name : null;
        }
        PlanSave.Save();
        return previous;
    }

    /// <summary>Takes the blueprint project and all its lines out of the list.</summary>
    public static void RemoveProject()
    {
        lock (Gate)
        {
            foreach (var key in Entries.Where(p => p.Value.Project).Select(p => p.Key).ToList()) Entries.Remove(key);
            ProjectName = null;
        }
        PlanSave.Save();
    }

    /// <summary>The project's name read back from the world's file (PlanSave).</summary>
    public static void RestoreProject(string name)
    {
        lock (Gate) ProjectName = Entries.Values.Any(e => e.Project) ? name : null;
    }

    /// <summary>Inside the lock: a project with no lines left is over.</summary>
    private static void ForgetEmptyProject()
    {
        if (ProjectName != null && !Entries.Values.Any(e => e.Project)) ProjectName = null;
    }

    /// <summary>The block that was selected in the G menu when Ctrl+N was pressed; the window offers it with Add buttons. Count is unused.</summary>
    public static volatile PlannedEntry Offered;

    /// <summary>Adds blocks of the offered block, kept within the line's limit. Returns how many were really added.</summary>
    public static int AddOffered(PlannedEntry offered, int count)
    {
        int added;
        lock (Gate)
        {
            var before = Entries.TryGetValue(offered.Key, out var entry) ? entry.Count : 0;
            var after = PlanLimits.Apply(before, count, offered.Armor);
            if (after != before)
                Entries[offered.Key] = new PlannedEntry { Key = offered.Key, Name = offered.Name, Block = offered.Block, Count = after, Armor = offered.Armor };
            added = after - before;
        }
        if (added != 0) PlanSave.Save();
        return added;
    }

    /// <summary>A line read back from the world's file (PlanSave); not written again.</summary>
    public static void Restore(PlannedEntry entry)
    {
        lock (Gate) Entries[entry.Key] = entry;
    }

    /// <summary>Messages for the player; shown by the UI thread as HUD notifications and always logged.</summary>
    public static readonly ConcurrentQueue<string> Messages = new();

    public static bool IsArmor(CubeBlockDefinition block) => block.GetType().Name.Contains("Armor", StringComparison.OrdinalIgnoreCase);

    /// <summary>Adds (or with a negative count removes) blocks; returns the new count of that line.</summary>
    public static int Add(object variantKey, string name, CubeBlockDefinition block, int count)
    {
        int now;
        lock (Gate)
        {
            Entries.TryGetValue(variantKey, out var entry);
            now = Math.Max(0, (entry?.Count ?? 0) + count);
            if (now == 0) Entries.Remove(variantKey);
            else Entries[variantKey] = new PlannedEntry { Key = variantKey, Name = name, Block = block, Count = now, Armor = IsArmor(block) };
        }
        PlanSave.Save();
        return now;
    }

    /// <summary>
    /// The window's +/- buttons: changes a line by delta blocks, kept between 0 and the line's limit; at 0 the line is
    /// removed. Returns the new count (0 for a line that is not in the list any more).
    /// </summary>
    public static int Change(object variantKey, int delta)
    {
        int now;
        lock (Gate)
        {
            if (!Entries.TryGetValue(variantKey, out var entry)) return 0;
            // Project lines come from the blueprint: no line limit (the blueprint may need 2000 armor blocks).
            now = entry.Project ? Math.Max(0, entry.Count + delta) : PlanLimits.Apply(entry.Count, delta, entry.Armor);
            if (now == 0) Entries.Remove(variantKey);
            else Entries[variantKey] = entry.With(now);
            ForgetEmptyProject();
        }
        PlanSave.Save();
        return now;
    }

    /// <summary>Takes a whole line out of the list.</summary>
    public static void Remove(object variantKey)
    {
        lock (Gate)
        {
            Entries.Remove(variantKey);
            ForgetEmptyProject();
        }
        PlanSave.Save();
    }

    public static int CountOf(object variantKey)
    {
        lock (Gate) return Entries.TryGetValue(variantKey, out var entry) ? entry.Count : 0;
    }

    /// <summary>Empties the list (Clear list button: saved; a world change: PlanSave is detached first, nothing written).</summary>
    public static void Clear()
    {
        lock (Gate)
        {
            Entries.Clear();
            Ordered.Clear();
            ProjectName = null;
        }
        PlanSave.Save();
    }

    /// <summary>Blocks per line that the running orders cover (see OrderedBlocks). Not saved: orders are not saved either.</summary>
    private static readonly Dictionary<object, int> Ordered = new(ReferenceEqualityComparer.Instance);

    /// <summary>An order was placed for these lines (the list as it was checked for the order).</summary>
    public static void MarkOrdered(IEnumerable<PlannedEntry> lines)
    {
        lock (Gate) OrderedBlocks.Mark(Ordered, lines.Select(e => new KeyValuePair<object, int>(e.Key, e.Count)));
    }

    /// <summary>The orders were stopped or removed before they were done: the list stays as it is.</summary>
    public static void ForgetOrdered()
    {
        lock (Gate) Ordered.Clear();
    }

    /// <summary>
    /// All orders are done: the ordered blocks leave the build list (lines added or raised since the last order keep the
    /// rest). Returns how many blocks were taken off.
    /// </summary>
    public static int TakeOrdered()
    {
        var taken = 0;
        lock (Gate)
        {
            if (Ordered.Count == 0) return 0;
            var counts = Entries.ToDictionary(p => p.Key, p => p.Value.Count, ReferenceEqualityComparer.Instance);
            var left = OrderedBlocks.Left(counts, Ordered, ReferenceEqualityComparer.Instance);
            foreach (var (key, entry) in Entries.ToList())
            {
                var now = left.TryGetValue(key, out var n) ? n : 0;
                if (now == entry.Count) continue;
                taken += entry.Count - now;
                if (now == 0) Entries.Remove(key);
                else Entries[key] = entry.With(now);
            }
            Ordered.Clear();
            ForgetEmptyProject();
        }
        if (taken > 0) PlanSave.Save();
        return taken;
    }

    public static List<PlannedEntry> Snapshot()
    {
        lock (Gate) return Entries.Values.ToList();
    }

    public static void Say(string message)
    {
        // The same message again within a few seconds (a key held or pressed quickly, 2026-11-18 stream: 11 x "select
        // a block first" in 8 s) is shown and logged once.
        lock (SayGate)
        {
            var now = DateTime.Now;
            if (message == lastSaid && now - lastSaidAt < TimeSpan.FromSeconds(4))
            {
                lastSaidAt = now;
                return;
            }
            lastSaid = message;
            lastSaidAt = now;
        }
        PluginLog.Info("HUD: " + message);
        Messages.Enqueue(message);
    }

    private static readonly object SayGate = new();
    private static string lastSaid;
    private static DateTime lastSaidAt;

    /// <summary>First character of a queued message that asks for the game's "success" look and high priority (see HudNotify).</summary>
    public const char ImportantMark = '\u0001';

    /// <summary>A result the player is waiting for (orders done): shown as a success notification with high priority.</summary>
    public static void SayDone(string message)
    {
        PluginLog.Info("HUD (done): " + message);
        Messages.Enqueue(ImportantMark + message);
    }
}
