# LW Build Planner: user manual

English | [Русский](UserManual.ru.md)

Version 0.1.1.0 (test build). Single player.

## What it is for
You want to build something, for example ten hydrogen thrusters. The planner tells you which parts that needs, what is already in your storage, and orders the rest from your crafters.

## Install
Install [Pulsar](https://github.com/SpaceGT/Pulsar) (the plugin loader for Space Engineers 2). Open the plugin list in Pulsar (LW Build Planner is listed in the PluginHub), enable **LW Build Planner** and restart the game. Nothing has to be set up; the plugin works in every world you load.

## Quick start
1. In the G menu select a block and press **+**. Do this for every block you want to build.
2. Press **Ctrl+N**: the planner window shows the parts the list needs and what is missing.
3. Fix what the window lists as a problem (ore, crafter, conveyor, cargo space), then press **Order missing**.
4. The Orders box shows the progress. When everything has arrived you get a green notification, the blocks leave the list, and you can build.

## 1. Pick blocks
1. Open the G menu.
2. Click the block group, then the variant in the row on the right, then the size (the small squares).
3. Press **+** (the `+`/`=` key or numpad `+`) to add it to the build list. Shift, Ctrl and Alt give bigger steps (defaults below, change them in the settings):

| Block | + alone | Shift + | Ctrl + | Alt + | Most per line |
|---|---|---|---|---|---|
| Armor | 1 | 10 | 100 | 500 | 1000 |
| Everything else | 1 | 5 | 10 | 25 | 50 |

A notification confirms each press, for example "Build: Mk3 200 +1 (now 3)".

4. **Ctrl+N** opens the planner window, in the G menu and anywhere else in a world (flying, walking, in a cockpit).

In the G menu only `+` and Ctrl+N are keys. Removing, clearing and ordering are buttons in the window, so a stray key press never takes anything off your list or starts crafters.

Note: the "Components" list in the G menu itself shows the cost of the first variant of a group for every variant. The planner uses the real cost of the variant and size you selected.

### A whole blueprint
1. Open the terminal (G) and go to the **Blueprints** tab.
2. Click a blueprint.
3. Press **+** (the add key). The planner reads every block of the blueprint, armor included, and puts it into the build list as one **blueprint project**: a heading with its name, the number of blocks and kinds, and one line per block kind. A notification confirms it, for example "Build: blueprint Dart EX-2 added: 361 blocks, 50 kinds".

**Before you decide: the Build Planner box.** Open a blueprint's details (the game's **Blueprint Details** screen). The planner shows a box in the free left column (the blueprint's screenshots move down below it; the game's own buttons stay as they are):
- the number of blocks and kinds,
- the **ore the whole blueprint costs** from scratch (for example "Iron 120,000 · Silicon 3,400 · Nickel 2,100"),
- whether it **can be built from your current stock** (green), or the **missing ore** (red), or the first problem (no crafter, nothing makes a part here). This is the **Order missing** view: parts already in storage are used;
- when only **Order all** (every part made new) would run short, the ore it lacks, in orange,
- an **Add to Build Planner** button. If another blueprint is already in the list, the first click asks ("Again: replace …"), the second replaces it. If this blueprint is already in the list, the button updates it.

The box only reads: nothing is placed, ordered or changed until you press its button. Stock means storage and crafters on your grids, minus what running orders still need.

The planner window says the same for the whole list: when **Order all** is blocked, its Problems box adds whether **Order missing** would still work (green) or is blocked too (red).

Only one blueprint at a time: a second one replaces the first (blocks you picked by hand stay). Blueprint lines have no line limit. The **X** next to the heading takes the whole blueprint out of the list; **Empty list** clears everything. The blueprint is saved with the world like the rest of the list. Two blocks with the same name in different sizes get the size after the name ("Hydrogen Thruster 2.5 m").

## 2. The planner window
Press Ctrl+N anywhere in a world. The **Settings** button at the top left of the window opens the settings (section 5).

The window scrolls (mouse wheel or scroll bar) and is made of boxes, each with a short explanation of what it shows.

**Build list**: what you picked. Every line has `[-]` count `[+]` `[X]` buttons: Shift, Ctrl and Alt give bigger steps, a line that reaches 0 leaves the list, `[X]` removes it at once.

**Parts**
| Column | Meaning |
|---|---|
| need | parts for the whole build list |
| in storage | in containers on the crafters' conveyor network (also on docked grids) |
| on order | already ordered by the planner and not finished |
| missing | need minus storage minus on order |

What you carry yourself is not counted.

**Problems for 'Order all'**: reasons an order is blocked, see section 4. "Order all" stays grey while there is a problem; make the list smaller with `[-]` and it opens again. The box also says whether "Order missing" would still work (storage covers the rest); if it is blocked too, "Order missing" is grey as well. After mining or moving cargo, press **Check** to check again. Warnings that do not block an order are shown in a **Notes** box instead (for example crafters that are not working, by name).

**Orders**: the progress of what you ordered. At the top: how many parts are finished, how many pieces are made of the total, a progress bar and a clock (running / finished in). Then one line per part, **oldest order first**: its number, a progress bar, "made / ordered" and a status:

| Status | Meaning |
|---|---|
| crafting | a crafter works on it now |
| queued, waiting | a crafter has it queued but waits for sub-parts from smaller crafters (smelters, refineries ...) or for earlier jobs |
| waiting for a free crafter | no crafter of the right kind is free yet; it goes in as soon as one is |
| done | everything ordered of it has arrived |
| no progress | nothing arrived for a minute and no crafter works on it: check ore and sub-parts |
| stopped, rest not made | the crafter is gone, or the part could not be sent to any crafter |

Under a line you see what each crafter still has ("Assembler 2: 12 left, crafting") and how many pieces are not queued yet. The numbers count parts that arrive in storage, so a part can stay at 0 for a while when its sub-parts are made first. When everything is done the orders are removed and the ordered blocks leave the build list (see below). **Stop orders** ends running orders early and keeps the list.

**Cargo space**: before an order the planner also checks that the new parts fit into the free capacity of the storage the crafters reach (the game limits cargo by mass). If they do not, "Order all" is blocked with "Not enough cargo space"; above 80 % of the free capacity you get a warning.

How the storage limiter works:
1. It adds up the free capacity (maximum minus current mass) of every storage the crafters reach. Parts that are already on order but have not arrived yet take room too, so they are subtracted.
2. It adds up the mass of the parts you are about to order (pieces x mass of one piece).
3. If the new parts weigh more than the free capacity: **error** "Not enough cargo space", "Order all" is blocked. Add storage, empty some, or order less.
4. If they use more than 80 % of the free capacity (but still fit): **warning** "Cargo space gets tight", the order is allowed.
5. Otherwise nothing is shown. When the game does not report a container's capacity, or a part's mass is unknown, the planner gives no verdict instead of guessing, and never blocks an order on a guess.

## 3. Order
| Button | What it does |
|---|---|
| Check | counts storage, ore and conveyor links again |
| Order all | makes every part in "need", whatever you already have |
| Order missing | makes only the "missing" column; tells you when nothing is missing |
| Empty list | takes every line out of the build list; running orders go on, stopped ones are cleared |

The Orders box has its own button. While orders run it is **Stop orders**: parts not yet given to a crafter are dropped and the box is emptied; crafters finish what is already in their queues. Finished orders clear themselves (and take their blocks off the list); if lines are left that can no longer run, the button says **Remove orders** and clears the box.

**Check** is off while the build list is empty.

After an order its parts appear under Orders, and the build list stays as it is while they run, so you can add more blocks and press **Order missing** for the difference. When all orders are done, the blocks you ordered leave the build list and the orders are removed: the build is ready, and it cannot be ordered twice by mistake. Blocks you added after the last order stay in the list.

How parts are queued:
- Part by part, oldest order first, in batches. Every crafter that is free gets a batch of up to 10 crafts of the oldest part that still waits; if that part needs fewer crafts than there are free crafters, the other crafters go on to the next part.
- When a crafter has finished its batch, it gets the next batch of the oldest part still waiting. So **the more crafters of a kind you have, the faster a big build is made**: build as many Assemblers, Smelters or Refineries as you like, the planner uses all of them (except those named `[NoMake]`).
- Only on crafters that are not working at that moment. A crafter that holds a batch but waits for sub-parts is not given more until its batch arrives.
- If all crafters of a kind are busy, the parts wait and you are told once ("all 2 Smelters are busy ... More Smelters would finish sooner"). The planner looks again every 5 seconds, so a crafter can wait up to 5 seconds for its next batch.
- Only the parts of the blocks are queued. Their sub-parts are requested by the game's crafters themselves.
- All of your crafters are used. Put `[NoMake]` in a crafter's name to keep the planner away from it. A Gearforge named `[Make Ammo]` belongs to LW Cargo Manager and is not used.

When everything has arrived you get "Build: all ordered parts are done, the blocks left the build list" as a green notification with the game's notification sound.

## 4. Problems and notes
| Message | Meaning | What to do |
|---|---|---|
| Ore deficit: X, N more needed | not enough ore for the order (ore that running orders still need is already taken off) | mine or buy, then Check |
| No Assembler to use on this grid | a crafter kind the parts need is missing, not working, or named `[NoMake]` | build or repair one |
| No Assembler can reach storage | the crafter has no conveyor link to the main network | connect it |
| No storage on the conveyor network | nothing to take materials from | add a container |
| X: no conveyor link, not used / not counted | warning: a crafter or container is cut off | connect it, or ignore |
| X is not progressing | no parts arrived for a minute and no crafter is working on it | check ore and sub-parts; **Stop orders** (in the Orders box) if it stays |
| Not enough cargo space | the new parts weigh more than the free capacity of the storage | add or empty containers, or order fewer blocks |
| Not working, not used: Assembler 5 m (2), … | note: these crafters are unfinished, damaged or have no power; the planner leaves them out (at most four names, "+N" for the rest) | finish, repair or power them, or ignore |

## 5. Settings
Open them with the plugin's config button in Pulsar or with **Settings** in the planner window (the window closes; Ctrl+N brings it back). They are saved when you close the dialog and apply at once. The dialog follows the game's language like the window.

| Setting | Default | What it does |
|---|---|---|
| Keep list when done | off | when all orders are done the ordered blocks stay in the list, so the same build can be ordered again |
| Confirm clearing | off | **Empty list** and **Stop orders** need a second click (the button asks for it for 4 seconds) |
| Batch size | 10 | crafts a free crafter gets at once |
| Cargo warning % | 80 | warn when the new parts need more than this share of the free cargo space |
| No progress after (s) | 60 | when a part shows "no progress" |
| Add block | + | the key that adds the selected block in the G menu; Shift, Ctrl and Alt with it give the steps below |
| Open planner | Ctrl+N | the key that opens the planner window |
| Steps: blocks | Shift 5, Ctrl 10, Alt 25, most per line 50 | step sizes and line limit for working blocks |
| Steps: armor | Shift 10, Ctrl 100, Alt 500, most per line 1000 | the same for armor |

## Languages
The window, the notifications and the problem texts follow the game's language: English, Czech, German, Spanish, French, Italian, Polish, Portuguese (Brazil), Russian, Slovak, Turkish, Ukrainian and Simplified Chinese. Block, part and crafter names come from the game itself. Change the language in the game's settings; the planner follows at once (the window shows it the next time it opens). A language the game adds later shows the planner in English.

## 6. Limits of this version
- Progress counts parts arriving in storage. A mod that makes crafters output more than the recipe (for example a "4x faster" crafter mod) makes lines finish too early.
- You cannot cancel parts that are already inside a crafter from the planner; remove them in the crafter's own screen.
- The build list is saved **together with the world**: every time the game saves (Save, Save as, autosave, the save when you leave), the list is written to a small file for that save (in `AppData\LwBuildPlanner\Worlds` inside the game's data folder), and it comes back when you load that world. Changes after the last save are lost with the world: after **exit without saving** or a crash you get the list of the last save, the one that matches the world. An empty list leaves no file. **Save as** gives the new save its own list.
- **Order all** checks storage again on every click, so ore you have just mined counts at once. If a problem is left, or orders are still running (the list stays after ordering, so a second Order all would make everything again), a notification says so and nothing is ordered. Use **Order missing** to add to running orders; it counts what is already on order. After an order click both order buttons wait a moment for the answer, so a quick second click does nothing.
- The list stays while orders run, so after a load it is still there (as of the last save). When all orders are done the ordered blocks leave it by themselves; **Empty list** clears it at any time.
- Orders are not saved. The game saves the crafters' queues, so after loading the crafters finish what is already in them. Wait until they are done before you press **Order missing**: the planner does not see those old queues, so it would order those parts again.
- After a load (or a crash) a block can miss a part whose order was cut short. Add that block again in the G menu with **+** and press **Order missing**: only what is not in storage is made.
- Orders go to the crafters of one grid: the one with the most crafters.
- The planner leaves parts you queued by hand alone, with one exception from the game itself: if an idle crafter still holds a part you queued by hand (for example one stuck for lack of ore), and the planner queues the same part there, the game adds the planner's batch to your entry. Nothing is lost; the crafter makes both together.
- When all orders are done the ordered blocks leave the list, unless **Keep list when done** is on (section 5): then they stay and the same build can be ordered again.
- The add key in the G menu needs Windows; the open key works everywhere (it uses the game's own input). Both can be changed in the settings.
