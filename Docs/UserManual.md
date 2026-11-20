# LW Build Planner: user manual

English | [Русский](UserManual.ru.md)

Version 0.1.1.0 (test build). Single player. Tested with Space Engineers 2 (Game2) 4.0.0.3169; a game update can change what the planner reads, so after one, check the log for "not available" warnings.

## 1. What it is for
You want to build something: ten hydrogen thrusters, or a whole ship from a blueprint. The planner tells you which parts that needs, what is already in your storage, and orders the rest from your crafters, whole blocks at a time.

## 2. Install
Install [Pulsar](https://github.com/SpaceGT/Pulsar) (the plugin loader for Space Engineers 2). In Pulsar's plugin list (LW Build Planner is in the PluginHub) enable **LW Build Planner** and restart the game. Nothing has to be set up; it works in every world you load.

## 3. Quick start
1. In the G menu select a block and press **+** (or select a blueprint in the terminal's Blueprints tab and press **+**).
2. Press **Ctrl+N**: the planner window shows the parts, what is missing, and any problem.
3. Press **Order missing**. A blueprint short of ore waits: press **Order what fits (N)** on its heading.
4. Blocks leave the list as their parts go to the crafters. A green notification says when everything has arrived.
5. Short of ore? Mine, then press **Order what fits** (or **Order missing**) again for the next blocks.

## 4. Picking blocks and blueprints

### Blocks
1. Open the G menu: click the block group, the variant in the row on the right, then the size (small squares).
2. Press **+** (the `+`/`=` key or numpad `+`). Shift, Ctrl and Alt give bigger steps (change them in the settings):

| Block | + | Shift + | Ctrl + | Alt + | Most per line |
|---|---|---|---|---|---|
| Armor | 1 | 10 | 100 | 500 | 1000 |
| Everything else | 1 | 5 | 10 | 25 | 50 |

A notification confirms each press, for example "Build: Mk3 200 +1 (now 3)". In the G menu only `+` and Ctrl+N are keys; removing, clearing and ordering are buttons in the window, so a stray key press never takes anything off the list or starts crafters.

Note: the game's own "Components" list in the G menu shows the cost of a group's first variant for every variant. The planner uses the real cost of the variant and size you selected.

### Blueprints
1. Open the terminal (G), go to the **Blueprints** tab and click a blueprint.
2. Press **+**. Every block of every grid, armor included, goes into the list as one **blueprint**: a heading with its name, blocks and kinds, and one line per block kind ("Build: blueprint Dart EX-2 added: 361 blocks, 50 kinds").

One blueprint at a time: a new one replaces it (blocks you picked by hand stay). Blueprint lines have no line limit and no -, + or X: the blueprint changes only as a whole. The **X** on its heading removes it; once part of it is ordered or made, that needs a second click. Blocks with the same name in different sizes get the size after the name ("Hydrogen Thruster 2.5 m").

**The Build Planner box.** The game's **Blueprint Details** screen gets a box in its left column (the game's own buttons stay as they are). It shows:
- blocks and kinds;
- the **ore the whole blueprint costs** from scratch;
- whether **Order missing** could build it from your stock now (green), or the missing ore (red), or the first problem;
- the ore **Order all** would lack (orange), when only that is short;
- **Add to Build Planner** (replacing another blueprint asks for a second click; the same blueprint is updated).

The box only reads; nothing changes until you press its button.

## 5. The planner window
**Ctrl+N** anywhere in a world. **Settings** (top left) opens the settings. The window scrolls and is made of boxes:

**Build list.** Lines you added by hand have `[-]` count `[+]` `[X]` (Shift, Ctrl, Alt for bigger steps; a line at 0 leaves the list). A blueprint shows its heading and its lines; while its blocks are on order the heading says "(in progress: Order all / Order missing)"; when short of ore it says in red what ore is missing, with **Order what fits (N)**, and its lines show "N wait for ore".

**Parts**
| Column | Meaning |
|---|---|
| need | parts for the whole list |
| in storage | in containers on the crafters' conveyor network (docked grids too) |
| on order | ordered by the planner, not arrived yet |
| missing | need − in storage − on order |

What you carry is not counted. With nothing missing it says "Everything is in storage", or "Nothing missing: storage and running orders cover every part" while some is still on order.

**Problems for 'Order all'.** Why an order is blocked (section 8). Order all stays grey while there is one; the box also says whether **Order missing** would still work (green) or is blocked too (red). **Notes** lists warnings that do not block (for example crafters that are not working).

**Orders.** Progress: parts finished, pieces made of the total, a bar and a clock. One line per part, oldest first:

| Status | Meaning |
|---|---|
| crafting | a crafter works on it now |
| queued, waiting | a crafter has it but is not making it yet (the line under it says why) |
| next | held back: another ordered part is made from it and comes first ("after Heavy-Duty Plate") |
| waiting for a free crafter | all crafters of its kind are busy |
| done | everything ordered of it has arrived |
| no progress | nothing for a minute and no crafter works on it |
| stopped, rest not made | no crafter could take it (a crafter that is gone gives its parts back, see section 6) |

When a part shows "no progress" because ore ran out, the HUD says once "Build: running orders need 12,000 more Iron." and a red line under the progress bar says "Ore missing: Iron 12,000" while it lasts (the three largest shortages, "+N" for more). It counts what the stalled parts still need beyond the storage the crafters reach.

Under a line, each crafter and why it waits: "crafting", "waits for ore: Iron" (Smelters, Refineries), "waits for Steel Plate" (Assemblers, Fabricators), "output full: free cargo" (its output inventory is full, the conveyors have nowhere to put the parts) or "earlier jobs first". **Stop orders** drops the parts not yet given to a crafter (crafters finish their queues); when all is done the button reads **Remove orders**.

**Buttons**
| Button | What it does |
|---|---|
| Check | counts storage, ore and conveyor links again (off while the list is empty) |
| Order all | makes every part of the list new, whatever you have |
| Order missing | makes only what storage and running orders do not cover (section 6) |
| Empty list | takes every line out; running orders go on |

After an order click both order buttons wait a moment for the answer, so a quick second click does nothing.

## 6. How ordering works
1. **Check.** Every block is broken down into parts, every part into sub-parts, down to ore, with your own crafters' recipes (mods included). Storage counts as it is.
2. **Storage first** (Order missing). Whole blocks that storage covers leave the list at once, the most complex first.
3. **Whole blocks only.** The rest is made smallest block first, as far as the ore goes; a block whose part set the ore cannot give is not started ("N wait for ore", red). A half-made block is no use: welding goes part by part and stops at the first missing part, leaving a half-welded block.
4. **A blueprint is all or nothing.** Short of ore it is not ordered at all (half a ship would float away); **Order what fits (N)** orders the N whole blocks that fit, the rest waits. Its **next rounds are made from ore only**: the parts in storage belong to its earlier blocks, not welded yet. Mine, press again, until it is done. **Hand-picked blocks come first.** Blocks you added by hand are planned before a blueprint that is short of ore: the blueprint waits as a whole, so the ore and the storage go to your blocks first. To raise the build's speed (crafters, more storage), add those blocks by hand and press **Order missing**; weld them, then press **Order what fits (N)** on the blueprint. The number on **Order what fits (N)** can change from one click to the next while crafters work (the ore moves into the crafters, and the click counts again); it never orders more than the ore allows.
5. **Complex parts first.** A part that another ordered part is made from (at any depth) waits until that part is done, shown as "next": Steel Plate for blocks waits for the Heavy-Duty Plate, Gold Thread and Electronic Parts for the Motherboards, Turbine Blades and Steel Tube for the Combustion Chambers. Otherwise the Assemblers would take them from storage and the blocks would end up short. The sub-parts of ordered parts (the Steel Plate inside a Heavy-Duty Plate) are requested by the game's crafters themselves.
6. **Hand-out.** Every 5 seconds every free crafter gets a batch (up to 10 crafts) of the first part still waiting. More crafters of a kind make a big build faster; the planner uses all of them except those named `[NoMake]` (a Gearforge named `[Make Ammo]` belongs to LW Cargo Manager). A crafter that holds a batch and waits for sub-parts gets no more. All crafters of a kind busy: you are told once.
7. **A block leaves the list** as soon as all its own parts are handed to crafters. Parts already handed out to earlier blocks are never counted twice.
8. **A part is done** when it arrives in storage, or when no crafter has it queued any more (checked twice, 5 seconds apart, so parts taken for welding at once still count). When everything is done: "Build: all ordered parts are done" (green, with the game's sound).

9. **A crafter that is gone** (removed, destroyed, its grid lost) with parts of the planner in its queue gives them back: they wait again and the other crafters make them. The log says "Crafter 'X' is gone, its parts wait for the other crafters again". If the crafter comes back with its queue, those parts may be made twice (more, never fewer). A crafter that is only switched off keeps its queue: the line shows "no progress" and goes on when you switch it on.

You can add blocks and press **Order missing** while orders run: what is already ordered is never ordered twice. Parts you take out for welding show up as missing at the next check.

**Keeping crafters for yourself.** Put `[NoMake]` anywhere in a crafter's name (in its terminal) and the planner never orders on it, so you keep it for what you queue by hand. Crafters without the tag are all used.

## 7. Saves and loading
- The list and the running orders are saved **with the world**, at every save (Save, Save as, autosave, leaving), in small files per save (`AppData\LwBuildPlanner\Worlds` in the game's data folder). An empty list leaves no file; **Save as** gets its own files.
- After **exit without saving** or a crash you get the list and orders of the last save, matching the world.
- After a load the orders go on by themselves: the crafters finish what the game kept in their queues ("queued before the load"), and parts the queues lost are made again. The parts queued before a load stay one line, the planner cannot tell how many each crafter has: if a crafter that holds some of them is removed afterwards, its share is counted as made when the others finish. Then press **Order missing** again.
- The files belong to the **newest** save. Load an older one (an autosave slot, a reload after a disaster) and a dialog asks, a few seconds after the world is on screen: **Keep list**, or **Stop orders and empty list**; then add the blueprint again and press **Order missing**: that counts what the older world really has.
- A block short of a part in the end (for example you welded it onto another grid): add the block or the blueprint again and press **Order missing**; only what is not in storage is made.

## 8. Problems and notes
| Message | Meaning | What to do |
|---|---|---|
| Ore deficit: X, N more needed | not enough ore (ore that running orders still need is already taken off) | mine or buy, then Check |
| No Assembler to use on this grid | a crafter kind the parts need is missing, not working, or named `[NoMake]` | build or repair one |
| No Assembler can reach storage | the crafter has no conveyor link to the main network | connect it |
| No storage on the conveyor network | nothing to take materials from | add a container |
| X: no conveyor link, not used / not counted | warning: a crafter or container is cut off | connect it, or ignore |
| Not enough cargo space | the new parts weigh more than the free capacity of the reachable storage | add or empty containers, or order fewer blocks |
| Cargo space gets tight | the new parts need more than 80 % of the free capacity (warning) | — |
| A crafter says "output full: free cargo" | the storage it reaches is full, the crafter stopped | empty or add containers; it goes on by itself |
| X is not progressing | nothing arrived for a minute, no crafter works on it (also when its crafters are switched off) | check ore and sub-parts, switch the crafters on; **Stop orders** if it stays |
| Running orders need 12,000 more Iron / Ore missing: Iron 12,000 | the stalled parts need more ore than the storage holds | mine or buy it; the orders go on by themselves |
| Not working, not used: Assembler 5 m (2), … | note: unfinished, damaged or unpowered crafters are left out (at most four names, "+N") | finish, repair or power them |

The cargo check adds up the free capacity of the storage the crafters reach, minus parts already on order, and compares it with the mass of the new parts. When the game does not report a capacity or a mass, the planner gives no verdict and never blocks on a guess.

## 9. Settings
Pulsar's config button, or **Settings** in the window. Saved when the dialog closes, applied at once, in the game's language.

| Setting | Default | What it does |
|---|---|---|
| Keep list when done | off | ordered blocks stay in the list, so the same build can be ordered again |
| Confirm clearing | off | **Empty list** and **Stop orders** need a second click within 4 seconds |
| Batch size | 10 | crafts a free crafter gets at once |
| Cargo warning % | 80 | warn above this share of the free cargo space |
| No progress after (s) | 60 | when a part shows "no progress" |
| Add block | + | the add key (Shift, Ctrl, Alt give the steps below) |
| Open planner | Ctrl+N | the key that opens the window |
| Steps: blocks | 5 / 10 / 25, most 50 | Shift / Ctrl / Alt steps and line limit for working blocks |
| Steps: armor | 10 / 100 / 500, most 1000 | the same for armor |

## 10. Languages
Window, notifications, dialogs and problem texts follow the game's language: English, Czech, German, Spanish, French, Italian, Polish, Portuguese (Brazil), Russian, Slovak, Turkish, Ukrainian, Simplified Chinese. Block, part and crafter names come from the game. A language the game adds later shows English. The translations were not reviewed by native speakers: if a text reads wrong, please open an issue on GitHub.

## 11. Limits of this version
- Orders go to the crafters of one grid (the one with the most crafters); storage on other grids counts when those crafters reach it by conveyor.
- Parts already inside a crafter cannot be cancelled from the planner; remove them in the crafter's screen.
- A mod that makes crafters output more than the recipe says can make lines finish too early.
- **Order all** with orders still running is refused (a second Order all would make blocks twice); use **Order missing**.
- If an idle crafter still holds a part you queued by hand and the planner queues the same part there, the game adds both into one entry; nothing is lost.
- A started blueprint's rounds ignore storage parts on purpose; spare unrelated parts in storage are then not used for it (a few extra are made).
- A huge order is handed out in batches as crafters free up (a crafter's queue is limited): the rest waits in the planner and nothing is lost.
- Storage that is not linked to the crafters by conveyor is not counted: ore in a loose container shows as missing.
- Parts credited from storage at the first order can be welded away before their block is built: add the blueprint again and press **Order missing**.
- Tested in single player only; multiplayer and dedicated servers are not tested.
- The add key in the G menu needs Windows; Ctrl+N uses the game's own input.
