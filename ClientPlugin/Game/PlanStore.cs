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
/// Saved per world by PlanSave when the game saves the world (see there).
/// </summary>
internal static class PlanStore
{
    private static readonly object Gate = new();
    private static readonly Dictionary<object, PlannedEntry> Entries = new(ReferenceEqualityComparer.Instance);

    /// <summary>Name of the blueprint project in the list (one at a time), null when there is none.</summary>
    public static string ProjectName { get; private set; }

    /// <summary>
    /// Part of the blueprint was ordered or made already: removing it then needs a second click (Lone 2026-11-19), even
    /// with "Confirm clearing" off; its next rounds are made from ore only, storage parts belong to the earlier rounds
    /// (Lone 2026-11-20). Saved with the list.
    /// </summary>
    public static volatile bool ProjectStarted;

    /// <summary>Inside the lock: a new blueprint, or none: its choices start over.</summary>
    private static void NewProject()
    {
        ProjectStarted = false;
    }

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
            foreach (var key in Entries.Where(p => p.Value.Project).Select(p => p.Key).ToList()) Entries.Remove(key);
            foreach (var line in lines) Entries[line.Key] = line;
            ProjectName = lines.Count > 0 ? name : null;
            NewProject();
        }
        return previous;
    }

    /// <summary>Takes the blueprint project and all its lines out of the list.</summary>
    public static void RemoveProject()
    {
        lock (Gate)
        {
            foreach (var key in Entries.Where(p => p.Value.Project).Select(p => p.Key).ToList()) Entries.Remove(key);
            ProjectName = null;
            NewProject();
        }
    }

    /// <summary>The project's name read back from the world's file (PlanSave).</summary>
    public static void RestoreProject(string name)
    {
        lock (Gate) ProjectName = Entries.Values.Any(e => e.Project) ? name : null;
    }

    /// <summary>Inside the lock: a project with no lines left is over.</summary>
    private static void ForgetEmptyProject()
    {
        if (ProjectName != null && !Entries.Values.Any(e => e.Project))
        {
            ProjectName = null;
            NewProject();
        }
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
        return added;
    }

    /// <summary>A line read back from the world's file (PlanSave); not written again.</summary>
    public static void Restore(PlannedEntry entry)
    {
        lock (Gate) Entries[entry.Key] = entry;
    }

    /// <summary>Messages for the player; shown by the UI thread as HUD notifications and always logged.</summary>
    public static readonly ConcurrentQueue<string> Messages = new();

    public static bool IsArmor(CubeBlockDefinition block) => block != null && PlanLimits.IsArmor(block.GetType().Name, block.ToString());

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
    }

    public static int CountOf(object variantKey)
    {
        lock (Gate) return Entries.TryGetValue(variantKey, out var entry) ? entry.Count : 0;
    }

    /// <summary>Empties the list (Clear list button, or a world change). The file follows at the next world save.</summary>
    public static void Clear()
    {
        lock (Gate)
        {
            Entries.Clear();
            ProjectName = null;
            NewProject();
        }
    }

    /// <summary>
    /// M3: blocks whose parts are all handed out (or were in storage already) leave their line. Returns how many were
    /// taken (fewer when the player lowered the line meanwhile) and, when this took the blueprint's last block, its name.
    /// </summary>
    public static (int Taken, string FinishedProject) TakeBlocks(object key, int count)
    {
        lock (Gate)
        {
            if (count <= 0 || !Entries.TryGetValue(key, out var entry)) return (0, null);
            var take = Math.Min(count, entry.Count);
            if (take == entry.Count) Entries.Remove(key);
            else Entries[key] = entry.With(entry.Count - take);
            var project = ProjectName;
            if (entry.Project) ProjectStarted = true;
            ForgetEmptyProject();
            return (take, entry.Project && project != null && ProjectName == null ? project : null);
        }
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
