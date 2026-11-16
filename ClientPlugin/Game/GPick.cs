using System;
using System.Collections;
using System.Collections.Generic;
using System.Linq;
using System.Reflection;
using System.Runtime.InteropServices;
using ClientPlugin.Policy;
using Keen.Game2.Simulation.WorldObjects.CubeBlocks;
using Keen.Game2.Simulation.WorldObjects.Items;

namespace ClientPlugin.Game;

/// <summary>
/// First usable planner, no screen of its own: keys while the terminal / G menu is open.
///   = / + (or Insert) add the selected block to the build list
///   -                 take it off again
///                     steps: armor 1, Shift 10, Ctrl 100 (at most 1000); other blocks 1, Shift 5, Ctrl 10 (at most 50)
///   Home              open the planner window (list, parts check, alerts, orders, Order button)
///   End               clear the build list
/// Runs on the UI thread, from the postfix of TerminalScreenViewModel.Update (see Patches).
/// Reads the game's view models through fields only (no property getters, so nothing is created or changed).
/// Selected block (Game2.Client, from the probe): GScreenViewModel.TileDetail is a BlockTileDetailViewModel whose
/// _blockTile (BlockTileModel) has Block (EntityCompositeDefinition); its Definitions hold the CubeBlockDefinition.
/// </summary>
internal static class GPick
{
    private const int VkInsert = 0x2D, VkHome = 0x24, VkEnd = 0x23, VkShift = 0x10, VkControl = 0x11;
    private const int VkNumPlus = 0x6B, VkPlus = 0xBB, VkNumMinus = 0x6D, VkMinus = 0xBD;

    [DllImport("user32.dll")] private static extern short GetAsyncKeyState(int vKey);
    [DllImport("user32.dll")] private static extern IntPtr GetForegroundWindow();
    [DllImport("user32.dll")] private static extern uint GetWindowThreadProcessId(IntPtr hWnd, out uint processId);

    private static bool insertLast, numPlusLast, plusLast, numMinusLast, minusLast, homeLast, endLast, keysUnavailable, alive;

    /// <summary>Called every frame while a terminal screen exists.</summary>
    public static void OnTerminalUpdate(object terminalViewModel)
    {
        try
        {
            if (!alive)
            {
                alive = true;
                PluginLog.Info("Terminal hook alive: = adds the selected block, - removes, Home opens the planner window, End clears the list.");
            }

            var ui = Reflect.Get(Reflect.Field(terminalViewModel.GetType(), "_inGameUI"), terminalViewModel);
            for (var shown = 0; shown < 3 && PlanStore.Messages.TryDequeue(out var message); shown++)
                HudNotify.Show(ui, message);

            if (keysUnavailable || !GameHasFocus()) return;
            // Every key is read each frame (no short-circuit), so a held key never fires twice.
            var insert = Edge(VkInsert, ref insertLast) | Edge(VkNumPlus, ref numPlusLast) | Edge(VkPlus, ref plusLast);
            var minus = Edge(VkNumMinus, ref numMinusLast) | Edge(VkMinus, ref minusLast);
            var home = Edge(VkHome, ref homeLast);
            var end = Edge(VkEnd, ref endLast);

            if (insert) AddSelected(terminalViewModel, 1);
            if (minus) AddSelected(terminalViewModel, -1);
            if (end)
            {
                PlanStore.Clear();
                PlanStore.Say("Build Planner: list cleared");
            }
            if (home) Screens.PlannerScreen.Open();
        }
        catch (DllNotFoundException)
        {
            keysUnavailable = true;
            PluginLog.Warn("Keys not available on this system (user32 missing).");
        }
        catch (Exception e)
        {
            PluginLog.Error("G menu pick failed: " + e);
        }
    }

    private static bool Down(int vk) => (GetAsyncKeyState(vk) & 0x8000) != 0;

    /// <summary>Shift and Ctrl right now, for the planner window's +/- buttons (same bigger steps as the G menu keys).</summary>
    internal static (bool Shift, bool Ctrl) Modifiers() => (Down(VkShift), Down(VkControl));

    private static bool Edge(int vk, ref bool last)
    {
        var down = Down(vk);
        var pressed = down && !last;
        last = down;
        return pressed;
    }

    private static bool GameHasFocus()
    {
        GetWindowThreadProcessId(GetForegroundWindow(), out var processId);
        return processId == (uint)Environment.ProcessId;
    }

    private static void AddSelected(object terminalViewModel, int sign)
    {
        var gScreen = Reflect.Get(Reflect.Field(terminalViewModel.GetType(), "_ownedGScreen"), terminalViewModel)
                      ?? Reflect.Get(Reflect.Property(terminalViewModel.GetType(), "GScreen"), terminalViewModel);
        if (gScreen == null)
        {
            PlanStore.Say("Build Planner: open the G menu and select a block first");
            return;
        }

        var pick = SelectedBlock(gScreen);
        if (pick.Block == null)
        {
            // Fallback: search the screen's objects, and log what was found so the path can be fixed.
            var found = FindBlocks(gScreen);
            foreach (var hit in found.Take(12))
                PluginLog.Info($"Pick candidate: {NameOf(hit.Block)} at {hit.Path}");
            var first = found.FirstOrDefault()?.Block;
            if (first != null) pick = (first, first, NameOf(first));
        }
        if (pick.Block == null)
        {
            PlanStore.Say("Build Planner: select a block in the G menu first");
            return;
        }

        // Limits are on blocks, not on parts: armor by the hundred, working blocks by the handful.
        var armor = pick.Block.GetType().Name.Contains("Armor", StringComparison.OrdinalIgnoreCase);
        var step = sign * PlanLimits.Step(armor, Down(VkShift), Down(VkControl));
        var before = PlanStore.CountOf(pick.Key);
        var after = PlanLimits.Apply(before, step, armor);
        PlanStore.Add(pick.Key, pick.Name, pick.Block, after - before);
        var capped = sign > 0 && after - before < step ? $", limit {PlanLimits.Max(armor)}" : "";
        PlanStore.Say($"Build Planner: {pick.Name} {(after >= before ? "+" : "")}{after - before} (now {after}{capped})");
    }

    /// <summary>
    /// The block shown in the G menu's detail panel. Tiles come in three levels: group (the tile in the middle panel),
    /// kind (the row of variants in the detail panel, BlockKindTileModel) and block (one per size, BlockTileModel).
    /// The G menu keeps several detail view models (shown, last selected, last hovered); the last selected one is
    /// asked first, and in it the tile it is about (CurrentTile / Tile) before SelectedSubTile, which can stay on the
    /// group's first variant. Everything found is logged, so a wrong pick can be traced.
    /// </summary>
    private static (object Key, CubeBlockDefinition Block, string Name) SelectedBlock(object gScreen)
    {
        (object Key, CubeBlockDefinition Block, string Name) best = (null, null, null);
        foreach (var member in new[] { "LastSelectedTileDetail", "TileDetail", "LastHoveredTileDetail" })
        {
            var detail = Value(gScreen, member);
            if (detail == null) continue;

            var size = Value(detail, "SelectedSize");
            var sizeDefinition = Value(size, "Definition");
            var shown = Value(detail, "CurrentTile") ?? Value(detail, "Tile");
            var subTile = Value(detail, "SelectedSubTile");
            PluginLog.Info($"Pick look {member} ({detail.GetType().Name}): title '{Text(Value(detail, "TileName"))}', shown tile {Describe(shown)}, "
                           + $"sub tile {Describe(subTile)}, size '{Text(Value(size, "Name"))}', first tile {Describe(Value(detail, "_blockTile"))}");
            if (best.Block != null) continue; // keep logging the other details, the first hit stays

            // The tile the panel is about, if it is a variant or a block; else the selected variant.
            var kind = Value(shown, "Blocks") != null ? shown : Value(shown, "BlockKindTile") ?? subTile;
            object tile = Value(shown, "Block") != null && Value(shown, "Blocks") == null ? shown : null;
            if (Value(kind, "Blocks") is IEnumerable variantTiles)
            {
                object firstSize = null;
                foreach (var candidate in variantTiles)
                {
                    firstSize ??= candidate;
                    if (sizeDefinition != null && ReferenceEquals(Value(Value(candidate, "BlockSize"), "Definition"), sizeDefinition))
                    {
                        tile = candidate;
                        break;
                    }
                }
                tile ??= firstSize;
            }
            tile ??= Value(size, "Block") ?? Value(detail, "_blockTile");

            var composite = Value(tile, "Block");
            var block = CubeOf(composite);
            if (block == null) continue;
            PluginLog.Info($"Pick from {member}: tile {Describe(tile)}, definition {composite}, cost block {block}");
            if (!block.Items.IsDefaultOrEmpty)
                PluginLog.Info("Pick cost: " + string.Join(", ", block.Items.Select(a => $"{Fx.ToDouble(a.Amount)} x {OrderJob.NameOf(a.Item)}"))
                               + $"; total {Fx.ToDouble(block.TotalItemAmount)}, optional {Fx.ToDouble(block.OptionalItemAmount)}, functional at {block.MinFunctionalBuildProgress:0.##}");

            var name = Text(Value(kind, "Name"));
            if (string.IsNullOrWhiteSpace(name)) name = Text(Value(tile, "Name"));
            if (string.IsNullOrWhiteSpace(name)) name = NameOf(block);
            var sizeName = Text(Value(Value(tile, "BlockSize"), "Name"));
            if (string.IsNullOrWhiteSpace(sizeName)) sizeName = Text(Value(size, "Name"));
            if (!string.IsNullOrWhiteSpace(sizeName) && !name.Contains(sizeName)) name += " (" + sizeName + ")";
            best = (composite ?? block, block, name);
        }
        return best;
    }

    /// <summary>"TypeName 'display name'" of a tile model, for the log.</summary>
    private static string Describe(object tile)
        => tile == null ? "-" : $"{tile.GetType().Name} '{Text(Value(tile, "Name"))}'";

    private static CubeBlockDefinition CubeOf(object composite)
    {
        try
        {
            if (Value(composite, "Definitions") is IEnumerable definitions)
                foreach (var definition in definitions)
                    if (definition is CubeBlockDefinition block) return block;
        }
        catch (Exception) { /* default array */ }
        return null;
    }

    /// <summary>Text of a LocalizableText or LocKey (EvaluateText), else ToString; "" for null.</summary>
    internal static string Text(object localizable)
    {
        if (localizable == null) return "";
        try
        {
            var methods = localizable.GetType().GetMethods().Where(m => m.Name == "EvaluateText").ToList();
            var plain = methods.FirstOrDefault(m => m.GetParameters().Length == 0);
            if (plain != null) return plain.Invoke(localizable, null) as string ?? "";
            var filtered = methods.FirstOrDefault(m => m.GetParameters().Length == 1 && m.GetParameters()[0].ParameterType == typeof(Func<string, string>));
            if (filtered != null) return filtered.Invoke(localizable, new object[] { null }) as string ?? "";
        }
        catch (Exception) { /* fall through */ }
        return localizable.ToString() ?? "";
    }

    private static object Value(object target, string member)
    {
        if (target == null) return null;
        try { return Reflect.Get(Reflect.Member(target.GetType(), member), target); }
        catch (Exception) { return null; }
    }

    /// <summary>Display name of a block: its UI name, else the definition's path.</summary>
    public static string NameOf(CubeBlockDefinition block)
    {
        try
        {
            var ui = block.UIData;
            var name = ui == null ? null : Reflect.Get(Reflect.Member(ui.GetType(), "Name"), ui);
            var evaluate = name?.GetType().GetMethods().FirstOrDefault(m => m.Name == "EvaluateText" && m.GetParameters().Length == 1
                                                                            && m.GetParameters()[0].ParameterType == typeof(Func<string, string>));
            var text = evaluate?.Invoke(name, new object[] { null }) as string;
            if (!string.IsNullOrWhiteSpace(text)) return text;
        }
        catch (Exception) { /* fall back to the path */ }
        var path = block.ToString() ?? "?";
        var cut = Math.Max(path.LastIndexOf('\\'), path.LastIndexOf('/'));
        return cut >= 0 ? path.Substring(cut + 1) : path;
    }

    private sealed class Hit
    {
        public CubeBlockDefinition Block;
        public string Path;
        public int Score;
    }

    /// <summary>
    /// Breadth-first walk over the fields of the G menu's view model (depth 5, at most 4000 objects), collecting every
    /// CubeBlockDefinition. Best first: paths through "selected" / "detail" members, then the shortest path.
    /// </summary>
    private static List<Hit> FindBlocks(object root)
    {
        var hits = new List<Hit>();
        var seen = new HashSet<object>(ReferenceEqualityComparer.Instance);
        var queue = new Queue<(object Node, string Path, int Depth)>();
        queue.Enqueue((root, root.GetType().Name, 0));

        while (queue.Count > 0 && seen.Count < 4000)
        {
            var (node, path, depth) = queue.Dequeue();
            if (node == null || !seen.Add(node)) continue;

            if (node is CubeBlockDefinition block)
            {
                var lower = path.ToLowerInvariant();
                hits.Add(new Hit
                {
                    Block = block, Path = path,
                    Score = (lower.Contains("selected") ? 100 : 0) + (lower.Contains("detail") ? 40 : 0)
                            + (lower.Contains("current") || lower.Contains("active") ? 20 : 0) - depth,
                });
                continue;
            }

            var type = node.GetType();
            if (depth >= 5 || type.IsPrimitive || type.IsEnum || node is string || node is Delegate || node is Type || node is MemberInfo) continue;
            var ns = type.Namespace ?? "";
            if (node is IEnumerable list && !ns.StartsWith("Keen.Game2.Client"))
            {
                var index = 0;
                try
                {
                    foreach (var item in list)
                    {
                        if (index >= 40) break;
                        if (item != null && !item.GetType().IsValueType) queue.Enqueue((item, $"{path}[{index}]", depth + 1));
                        index++;
                    }
                }
                catch (Exception) { /* collection changed or not enumerable now */ }
                continue;
            }
            // Only the game's own UI and definition objects are opened; Avalonia, system and engine internals are not.
            if (!ns.StartsWith("Keen.Game2") && type.Name != "EntityCompositeDefinition") continue;

            for (var t = type; t != null && t != typeof(object); t = t.BaseType)
                foreach (var field in t.GetFields(BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.DeclaredOnly))
                {
                    if (field.FieldType.IsPrimitive || field.FieldType.IsEnum || field.FieldType == typeof(string)) continue;
                    object value;
                    try { value = field.GetValue(node); }
                    catch (Exception) { continue; }
                    if (value == null || value.GetType().IsValueType && !(value is IEnumerable)) continue;
                    queue.Enqueue((value, path + "." + field.Name.Replace("<", "").Replace(">k__BackingField", ""), depth + 1));
                }
        }

        return hits.OrderByDescending(h => h.Score).ThenBy(h => h.Path.Length).ToList();
    }
}
