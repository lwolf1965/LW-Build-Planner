using System;
using System.Collections;
using System.Collections.Generic;
using System.Linq;
using System.Reflection;
using ClientPlugin.Policy;
using Keen.Game2.Simulation.WorldObjects.CubeBlocks;
using Keen.VRage.Library.Definitions;

namespace ClientPlugin.Game;

/// <summary>
/// Reads blueprints: for the add key in the terminal's Blueprints tab (the selected blueprint goes into the build list as
/// the blueprint project, PlanStore.SetProject; one blueprint at a time, it replaces the previous one) and for the Build
/// Planner box in the game's Blueprint Details screen (BlueprintPanel: read, checked, added only on its button).
/// How a blueprint is read (found with the blueprint probe, 2026-11-17):
///   - the Blueprints tab is TerminalScreenViewModel._selectedSubScreen._content (TerminalBlueprintsScreenViewModel);
///   - SelectedBlueprint is a BlueprintViewModel: BlueprintID is the name, Metadata.BlockCount the number of blocks;
///   - Model.TryLoadGridAsync(lifetime) with the screen's _toolbarInputHandler as lifetime (as the game's own
///     "Place blueprint") returns the game's own Task&lt;BlueprintEntityBundle&gt; (a struct): polled every frame,
///     IsCompleted, then TryGetResult;
///   - BlueprintEntityBundle.Bundle.Builders: EntityObjectBuilders { Definition, ObjectBuilders, DebugName }. A grid's
///     HierarchyComponentObjectBuilder.Children holds every block of that grid (armor included) as an EntityObjectBuilder
///     whose Definition is the block's EntityCompositeDefinition (its Definitions hold the CubeBlockDefinition). The
///     working blocks also appear as top-level builders; they are not counted twice.
/// Runs on the UI thread (terminal hook, the box's timer). Reads only: nothing is placed.
/// </summary>
internal static class BlueprintRead
{
    private const BindingFlags Flags = BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic;

    /// <summary>A started read: the game's load task, polled until it has finished.</summary>
    private sealed class Load
    {
        public object Task;
        public DateTime Since;
        public string Name;
        public int Expected;
        public bool FromKey;
        public Action<List<PlannedEntry>> Done;
        public Action Failed;
    }

    private static readonly List<Load> Loads = new();
    private static WeakReference<object> lastScreen = new(null);
    private static long lastScreenLook;

    /// <summary>
    /// The toolbar input handler of the Blueprints tab the terminal showed last (the lifetime the game's "Place" loads
    /// with), for the Blueprint Details screen when its own action handler has none. Null when there was none.
    /// </summary>
    public static object LastLifetime => lastScreen.TryGetTarget(out var screen) ? Value(screen, "_toolbarInputHandler") : null;

    /// <summary>The Blueprints tab's view model while that tab is the open one, else null.</summary>
    public static object OpenScreen(object terminal)
    {
        foreach (var member in new[] { "_selectedSubScreen", "_activeSubScreen" })
        {
            var content = Value(Value(terminal, member), "_content");
            if (content?.GetType().Name == "TerminalBlueprintsScreenViewModel") return content;
        }
        return null;
    }

    /// <summary>The blueprint's name (its id in the blueprint list), "?" when it has none.</summary>
    public static string NameOf(object blueprint)
    {
        var name = Convert.ToString(Value(blueprint, "BlueprintID"));
        if (string.IsNullOrWhiteSpace(name)) name = GPick.Text(Value(Value(blueprint, "Metadata"), "DisplayName"));
        return string.IsNullOrWhiteSpace(name) ? "?" : name;
    }

    /// <summary>
    /// The add key in the terminal. Returns true when the Blueprints tab is open: then the key belongs to the blueprint
    /// (it is read, or the player is told why not) and must not add a G menu block.
    /// </summary>
    public static bool TryStart(object terminal)
    {
        var screen = OpenScreen(terminal);
        if (screen == null) return false;
        GameLanguage.Refresh();
        bool busy;
        lock (Loads) busy = Loads.Any(l => l.FromKey);
        if (busy)
        {
            PlanStore.Say(Tr.T("hud.bpBusy"));
            return true;
        }
        var selected = Value(screen, "SelectedBlueprint") ?? Value(screen, "_selectedBlueprint");
        if (selected == null)
        {
            PlanStore.Say(Tr.T("hud.bpSelect"));
            return true;
        }
        var name = NameOf(selected);
        if (!Start(selected, Value(screen, "_toolbarInputHandler"), lines => AddProject(name, lines),
                () => PlanStore.Say(Tr.T("hud.bpFailed", name)), fromKey: true))
            PlanStore.Say(Tr.T("hud.bpFailed", name));
        return true;
    }

    /// <summary>
    /// Starts reading a blueprint (a BlueprintViewModel). lifetime: the game's toolbar input handler, as for the game's
    /// own "Place". done gets the counted block lines (never empty), failed is called when it could not be read.
    /// Both are called from Poll. False when the read could not even start (logged).
    /// </summary>
    public static bool Start(object blueprint, object lifetime, Action<List<PlannedEntry>> done, Action failed, bool fromKey = false)
    {
        var name = NameOf(blueprint);
        var expected = 0;
        try { expected = Convert.ToInt32(Value(Value(blueprint, "Metadata"), "BlockCount") ?? 0); }
        catch (Exception) { /* not a number */ }

        try
        {
            if (lifetime == null) throw new InvalidOperationException("no toolbar input handler to load with");
            var model = Value(blueprint, "Model");
            var load = model?.GetType().GetMethods(Flags).FirstOrDefault(m => m.Name == "TryLoadGridAsync");
            if (load == null) throw new MissingMethodException("Blueprint.TryLoadGridAsync not found");
            var args = load.GetParameters().Select(p =>
                p.ParameterType.IsInstanceOfType(lifetime) || p.ParameterType == typeof(object) ? lifetime
                : p.HasDefaultValue ? p.DefaultValue
                : p.ParameterType.IsValueType ? Activator.CreateInstance(p.ParameterType) : null).ToArray();
            var task = load.Invoke(model, args);
            if (task == null) throw new InvalidOperationException("the load returned nothing");
            lock (Loads)
                Loads.Add(new Load
                {
                    Task = task, Since = DateTime.Now, Name = name, Expected = expected, FromKey = fromKey,
                    Done = done, Failed = failed,
                });
            PluginLog.Info($"Blueprint '{name}': reading ({expected} blocks){(fromKey ? "" : " for the details screen")}.");
            return true;
        }
        catch (Exception e)
        {
            PluginLog.Error($"Blueprint '{name}' could not be read: {e}");
            return false;
        }
    }

    /// <summary>Terminal hook and the box's timer: finished reads are counted and handed to their callers.</summary>
    public static void Poll(object terminal = null)
    {
        if (terminal != null && Environment.TickCount64 - lastScreenLook > 500)
        {
            lastScreenLook = Environment.TickCount64;
            var screen = OpenScreen(terminal);
            if (screen != null) lastScreen = new WeakReference<object>(screen);
        }
        List<Load> finished;
        lock (Loads)
        {
            if (Loads.Count == 0) return;
            finished = Loads.Where(l => Finished(l) || DateTime.Now - l.Since > TimeSpan.FromSeconds(30)).ToList();
            foreach (var load in finished) Loads.Remove(load);
        }
        foreach (var load in finished) Finish(load);
    }

    private static bool Finished(Load load)
    {
        try { return load.Task.GetType().GetProperty("IsCompleted")?.GetValue(load.Task) as bool? ?? true; }
        catch (Exception) { return true; }
    }

    private static void Finish(Load load)
    {
        List<PlannedEntry> lines = null;
        try
        {
            var task = load.Task;
            var type = task.GetType();
            var done = type.GetProperty("IsCompleted")?.GetValue(task) as bool? ?? true;
            if (!done) throw new TimeoutException("not read within 30 s, given up");

            object bundle = null;
            var tryGet = type.GetMethods().FirstOrDefault(m => m.Name == "TryGetResult" && m.GetParameters().Length == 1);
            if (tryGet != null)
            {
                var outArgs = new object[1];
                if (tryGet.Invoke(task, outArgs) is true) bundle = outArgs[0];
            }
            if (bundle == null)
            {
                var awaiter = type.GetMethod("GetAwaiter", Type.EmptyTypes)?.Invoke(task, null);
                bundle = awaiter?.GetType().GetMethod("GetResult", Type.EmptyTypes)?.Invoke(awaiter, null);
            }
            if (bundle == null) throw new InvalidOperationException("the load gave no result");
            lines = Count(load.Name, bundle, load.Expected);
            if (lines.Count == 0) throw new InvalidOperationException("no blocks found");
        }
        catch (Exception e)
        {
            PluginLog.Error($"Blueprint '{load.Name}' could not be read: {e}");
            lines = null;
        }

        try
        {
            if (lines == null) load.Failed?.Invoke();
            else load.Done?.Invoke(lines);
        }
        catch (Exception e) { PluginLog.Error($"Blueprint '{load.Name}': the reader's caller failed: {e}"); }
    }

    /// <summary>Makes the lines the blueprint project of the build list (it replaces another one) and says so.</summary>
    public static void AddProject(string name, List<PlannedEntry> lines)
    {
        var total = lines.Sum(l => l.Count);
        var previous = PlanStore.SetProject(name, lines);
        PlanStore.Say(previous == null || previous == name
            ? Tr.T("hud.bpAdded", name, total, lines.Count)
            : Tr.T("hud.bpReplaced", name, previous, total, lines.Count));
        PlannerEngine.Enqueue(c => PlanService.Check(c, OrderMode.None)); // the parts table follows the new list
    }

    /// <summary>Counts the blocks of every grid in the bundle, as build list lines of the blueprint project.</summary>
    private static List<PlannedEntry> Count(string name, object bundle, int expected)
    {
        var builders = Items(Value(Value(bundle, "Bundle") ?? bundle, "Builders") as IEnumerable);
        var kinds = new List<BlueprintKind<object>>();
        var blocks = new Dictionary<object, CubeBlockDefinition>(ReferenceEqualityComparer.Instance);
        var seen = new HashSet<object>(ReferenceEqualityComparer.Instance);
        var grids = 0;
        var unknown = 0;

        // Every top-level builder that is not a block is a grid; its hierarchy holds its blocks (and maybe subgrids).
        foreach (var builder in builders)
            if (Cube(Value(builder, "Definition") as Definition) == null) Walk(builder);

        void Walk(object entity)
        {
            if (entity == null || !seen.Add(entity)) return;
            grids++;
            foreach (var component in Items(Value(entity, "ObjectBuilders") as IEnumerable))
            {
                if (component?.GetType().Name != "HierarchyComponentObjectBuilder") continue;
                foreach (var child in Items(Value(component, "Children") as IEnumerable))
                {
                    var definition = Value(child, "Definition") as Definition;
                    var cube = Cube(definition);
                    if (cube == null)
                    {
                        if (definition == null) unknown++;
                        else Walk(child); // a grid inside (subgrid)
                        continue;
                    }
                    blocks[definition] = cube;
                    kinds.Add(new BlueprintKind<object> { Key = definition, Name = GPick.NameOf(cube), Path = definition.ToString(), Count = 1 });
                    // A block can carry a subgrid in its own hierarchy (rotor, piston, hinge tops).
                    foreach (var sub in Items(Value(child, "ObjectBuilders") as IEnumerable))
                        if (sub?.GetType().Name == "HierarchyComponentObjectBuilder")
                            foreach (var inner in Items(Value(sub, "Children") as IEnumerable))
                                if (Cube(Value(inner, "Definition") as Definition) == null && Value(inner, "Definition") is Definition) Walk(inner);
                }
            }
        }

        var lines = BlueprintNames.Lines(kinds).Select(k => new PlannedEntry
        {
            Key = new ProjectKey((Definition)k.Key), Name = k.Name, Block = blocks[k.Key], Count = k.Count,
            Armor = PlanStore.IsArmor(blocks[k.Key]), Project = true,
        }).ToList();
        var total = lines.Sum(l => l.Count);
        PluginLog.Info($"Blueprint '{name}': {total} blocks in {lines.Count} kinds on {grids} grid(s)"
                       + (unknown > 0 ? $", {unknown} entries without a definition skipped" : "")
                       + (expected > 0 && expected != total ? $" (the blueprint says {expected} blocks)" : "")
                       + ": " + string.Join(", ", lines.OrderByDescending(l => l.Count).Take(12).Select(l => $"{l.Count} x {l.Name}")));
        return lines;
    }

    private static CubeBlockDefinition Cube(Definition definition)
    {
        if (definition == null) return null;
        if (definition is CubeBlockDefinition block) return block;
        try
        {
            if (Value(definition, "Definitions") is IEnumerable parts)
                foreach (var part in parts)
                    if (part is CubeBlockDefinition cube) return cube;
        }
        catch (Exception) { /* not a composite */ }
        return null;
    }

    /// <summary>Items of a collection; KeyValuePair items give their Value. Never throws (some game readers do).</summary>
    private static List<object> Items(IEnumerable items)
    {
        var result = new List<object>();
        if (items == null) return result;
        try
        {
            foreach (var item in items)
            {
                if (item != null && item.GetType().Name.StartsWith("KeyValuePair"))
                    result.Add(item.GetType().GetProperty("Value")?.GetValue(item));
                else result.Add(item);
            }
        }
        catch (Exception e) { PluginLog.Warn("Blueprint: a list could not be read to the end: " + e.GetType().Name); }
        return result;
    }

    private static object Value(object target, string member)
    {
        if (target == null) return null;
        try { return Reflect.Get(Reflect.Member(target.GetType(), member), target); }
        catch (Exception) { return null; }
    }
}
