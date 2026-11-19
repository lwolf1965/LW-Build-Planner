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
/// Keys while the terminal / G menu is open (both can be changed in the settings, Config.AddKey / Config.OpenKey):
///   +       adds the selected block to the build list (Shift, Ctrl and Alt for bigger steps); + and numpad + both work.
///           In the terminal's Blueprints tab it reads the selected blueprint into the list instead (BlueprintRead).
///   Ctrl+N  opens the planner window and brings the selected block along (PlanStore.Offered)
/// Nothing else is a key: removing, clearing and ordering are buttons in the window.
/// Runs on the UI thread, from the postfix of TerminalScreenViewModel.Update (see Patches).
/// Reads the game's view models through fields only (no property getters, so nothing is created or changed).
/// Selected block (Game2.Client, from the probe): GScreenViewModel.TileDetail is a BlockTileDetailViewModel whose
/// _blockTile (BlockTileModel) has Block (EntityCompositeDefinition); its Definitions hold the CubeBlockDefinition.
/// </summary>
internal static class GPick
{
    private const int VkShift = 0x10, VkControl = 0x11, VkAlt = 0x12, VkPlus = 0xBB, VkNumPlus = 0x6B;

    [DllImport("user32.dll")] private static extern short GetAsyncKeyState(int vKey);
    [DllImport("user32.dll")] private static extern IntPtr GetForegroundWindow();
    [DllImport("user32.dll")] private static extern uint GetWindowThreadProcessId(IntPtr hWnd, out uint processId);

    private static bool openLast, addLast, numPlusLast, keysUnavailable, alive;

    // The HUD field is looked up once per view model type, not every frame.
    private static Type uiFieldOwner;
    private static FieldInfo uiField;

    /// <summary>Called every frame while a terminal screen exists.</summary>
    /// <summary>True while a terminal / G menu screen exists (its hook ran in the last 0.3 s).</summary>
    internal static bool TerminalOpen => Environment.TickCount64 - System.Threading.Volatile.Read(ref lastTerminalTick) < 300;
    private static long lastTerminalTick;

    public static void OnTerminalUpdate(object terminalViewModel)
    {
        System.Threading.Volatile.Write(ref lastTerminalTick, Environment.TickCount64);
        try
        {
            if (!alive)
            {
                alive = true;
                PluginLog.Info($"Terminal hook alive: {KeyName(Config.Current.AddKey)} adds the selected block (Shift / Ctrl / Alt bigger steps) or, in the Blueprints tab, the selected blueprint; {KeyName(Config.Current.OpenKey)} opens the planner window.");
            }

            var viewModelType = terminalViewModel.GetType();
            if (uiFieldOwner != viewModelType) { uiFieldOwner = viewModelType; uiField = Reflect.Field(viewModelType, "_inGameUI"); }
            var ui = Reflect.Get(uiField, terminalViewModel);
            HudNotify.Remember(ui); // also used while no terminal is open (HudNotify.PostDrain)
            HudNotify.Drain(ui);
            BlueprintRead.Poll(terminalViewModel); // a blueprint being read is picked up when it is ready; remembers the Blueprints tab

            if (keysUnavailable || !GameHasFocus()) return;
            // Every key is read each frame (no short-circuit), so a held key fires once.
            var config = Config.Current;
            var openKey = config.OpenKey;
            var addKey = config.AddKey;
            var open = openKey.IsBound && Edge(openKey.Vk, ref openLast);
            var add = addKey.IsBound && Edge(addKey.Vk, ref addLast);
            add |= addKey.Vk == VkPlus && Edge(VkNumPlus, ref numPlusLast);
            // The open key needs exactly its own modifiers (Ctrl+Shift+N is not Ctrl+N); the add key takes any, they are the steps.
            if (open && Down(VkControl) == openKey.Ctrl && Down(VkShift) == openKey.Shift && Down(VkAlt) == openKey.Alt)
                OpenWithSelected(terminalViewModel);
            else if (add && !(openKey.Vk == addKey.Vk && open))
            {
                // In the Blueprints tab the add key reads the selected blueprint; anywhere else it adds the G menu block.
                if (!BlueprintRead.TryStart(terminalViewModel)) AddSelected(terminalViewModel);
            }
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

    /// <summary>
    /// A key binding as text for the log ("Ctrl+N", "+", "Insert"). The binding's own ToString uses the game's key glyphs,
    /// which come out empty or as a lone "+" in a log line.
    /// </summary>
    internal static string KeyName(Settings.Tools.Binding binding)
    {
        if (!binding.IsBound) return "(none)";
        var vk = binding.Vk;
        var key = vk switch
        {
            >= 0x30 and <= 0x39 or >= 0x41 and <= 0x5A => ((char)vk).ToString(),
            >= 0x70 and <= 0x87 => "F" + (vk - 0x6F),
            >= 0x60 and <= 0x69 => "Num" + (vk - 0x60),
            0xBB => "+", 0xBD => "-", 0x6B => "Num+", 0x6D => "Num-", 0x2D => "Insert", 0x2E => "Delete", 0x24 => "Home", 0x23 => "End",
            0x21 => "PageUp", 0x22 => "PageDown", 0x20 => "Space", 0x0D => "Enter", 0x09 => "Tab",
            _ => $"VK 0x{vk:X2}",
        };
        return (binding.Ctrl ? "Ctrl+" : "") + (binding.Alt ? "Alt+" : "") + (binding.Shift ? "Shift+" : "") + key;
    }

    /// <summary>Shift, Ctrl and Alt right now, for the planner window's +/- buttons (same bigger steps as the G menu keys).</summary>
    internal static (bool Shift, bool Ctrl, bool Alt) Modifiers()
    {
        try { return (Down(VkShift), Down(VkControl), Down(VkAlt)); }
        catch (DllNotFoundException) { return (false, false, false); } // no user32 (not Windows): the buttons still work, with the small step
    }

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

    /// <summary>The block selected in the G menu, or null. It also becomes the window's offered block.</summary>
    private static PlannedEntry PickSelected(object terminalViewModel)
    {
        var gScreen = Reflect.Get(Reflect.Field(terminalViewModel.GetType(), "_ownedGScreen"), terminalViewModel)
                      ?? Reflect.Get(Reflect.Property(terminalViewModel.GetType(), "GScreen"), terminalViewModel);
        if (gScreen == null) return null;

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
        if (pick.Block == null) return null;
        var offered = new PlannedEntry { Key = pick.Key, Name = pick.Name, Block = pick.Block, Armor = PlanStore.IsArmor(pick.Block) };
        PlanStore.Offered = offered;
        return offered;
    }

    /// <summary>+: adds the selected block. Shift, Ctrl and Alt give bigger steps (settings), the line's limit is kept.</summary>
    private static void AddSelected(object terminalViewModel)
    {
        GameLanguage.Refresh(); // the player may have switched the language
        var pick = PickSelected(terminalViewModel);
        if (pick == null)
        {
            PlanStore.Say(Tr.T("hud.selectFirst"));
            return;
        }
        var step = PlanLimits.Step(pick.Armor, Down(VkShift), Down(VkControl), Down(VkAlt));
        var added = PlanStore.AddOffered(pick, step);
        var now = PlanStore.CountOf(pick.Key);
        PlanStore.Say(added < step
            ? Tr.T("hud.addedLimit", pick.Name, added, now, PlanLimits.Max(pick.Armor))
            : Tr.T("hud.added", pick.Name, added, now));
    }

    /// <summary>Ctrl+N: remembers the block selected in the G menu (if any) and opens the planner window.</summary>
    private static void OpenWithSelected(object terminalViewModel)
    {
        PickSelected(terminalViewModel);
        Screens.PlannerScreen.Open();
    }

    /// <summary>
    /// The block shown in the G menu's detail panel. Tiles come in three levels: group (the tile in the middle panel),
    /// kind (the row of variants in the detail panel, BlockKindTileModel) and block (one per size, BlockTileModel).
    /// The G menu keeps several detail view models (shown, last selected, last hovered); the last selected one is
    /// asked first, and in it the tile it is about (CurrentTile / Tile) before SelectedSubTile, which can stay on the
    /// group's first variant. A good pick logs one line; when nothing is found, everything looked at is logged.
    /// </summary>
    private static (object Key, CubeBlockDefinition Block, string Name) SelectedBlock(object gScreen)
    {
        (object Key, CubeBlockDefinition Block, string Name) best = (null, null, null);
        // What was looked at is logged only when no block was found (a wrong or failed pick can still be traced);
        // a good pick writes one line.
        var looks = new List<string>();
        foreach (var member in new[] { "LastSelectedTileDetail", "TileDetail", "LastHoveredTileDetail" })
        {
            var detail = Value(gScreen, member);
            if (detail == null) continue;

            var size = Value(detail, "SelectedSize");
            var sizeDefinition = Value(size, "Definition");
            var shown = Value(detail, "CurrentTile") ?? Value(detail, "Tile");
            var subTile = Value(detail, "SelectedSubTile");
            looks.Add($"Pick look {member} ({detail.GetType().Name}): title '{Text(Value(detail, "TileName"))}', shown tile {Describe(shown)}, "
                      + $"sub tile {Describe(subTile)}, size '{Text(Value(size, "Name"))}', first tile {Describe(Value(detail, "_blockTile"))}");
            if (best.Block != null) break; // the first hit stays

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
            PluginLog.Info($"Pick from {member}: {Describe(tile)}, {composite}; cost "
                           + (block.Items.IsDefaultOrEmpty ? "none" : string.Join(", ", block.Items.Select(a => $"{Fx.ToDouble(a.Amount)} x {OrderJob.NameOf(a.Item)}"))));

            var name = Text(Value(kind, "Name"));
            if (string.IsNullOrWhiteSpace(name)) name = Text(Value(tile, "Name"));
            if (string.IsNullOrWhiteSpace(name)) name = NameOf(block);
            // The size ("Block size 1") is not added to the name: it made the names long and the HUD message was cut.
            best = (composite ?? block, block, name);
        }
        if (best.Block == null)
            foreach (var look in looks) PluginLog.Info(look);
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
