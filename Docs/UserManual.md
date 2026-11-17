# LW Build Planner: user manual

Version 0.1.0.1 (test build). Single player.

## What it is for
You want to build something, for example ten hydrogen thrusters. The planner tells you which parts that needs, what is already in your storage, and orders the rest from your crafters.

## 1. Pick blocks
1. Open the G menu.
2. Click the block group, then the variant in the row on the right, then the size (the small squares).
3. Press **+** (the `+`/`=` key or numpad `+`) to add it to the build list. Shift and Ctrl give bigger steps:

| Block | + alone | Shift + | Ctrl + | Most per line |
|---|---|---|---|---|
| Armor | 1 | 10 | 100 | 1000 |
| Everything else | 1 | 5 | 10 | 50 |

A notification confirms each press, for example "Build Planner: Mk3 200 (Block size 1) +1 (now 3)".

4. **Ctrl+N** opens the planner window. The block you selected last is shown under **Selected block**, with **Add** buttons for the same three steps.

In the G menu only `+` and Ctrl+N are keys. Removing, clearing and ordering are buttons in the window, so a stray key press never takes anything off your list or starts crafters.

Note: the "Components" list in the G menu itself shows the cost of the first variant of a group for every variant. The planner uses the real cost of the variant and size you selected.

## 2. The planner window
Press Ctrl+N while the G menu is open (or use the plugin's config button in Pulsar).

The window scrolls (mouse wheel or scroll bar) and is made of boxes, each with a short explanation of what it shows.

**Build list**: what you picked. Every line has `[-]` count `[+]` `[X]` buttons: Shift and Ctrl give bigger steps, a line that reaches 0 leaves the list, `[X]` removes it at once.

**Parts**
| Column | Meaning |
|---|---|
| need | parts for the whole build list |
| in storage | in containers on the crafters' conveyor network (also on docked grids) |
| on order | already ordered by the planner and not finished |
| missing | need minus storage minus on order |

What you carry yourself is not counted.

**Problems**: reasons an order is blocked, see section 4. "Order all" stays grey while there is a problem; make the list smaller with `[-]` and it opens again.

**Orders**: the progress of what you ordered. At the top: how many parts are finished, how many pieces are made of the total, a progress bar and a clock (running / finished in). Then one line per part, **oldest order first**: its number, a progress bar, "made / ordered" and a status:

| Status | Meaning |
|---|---|
| crafting | a crafter works on it now |
| queued, waiting | a crafter has it queued but waits for sub-parts from smaller crafters (smelters, refineries ...) or for earlier jobs |
| waiting for a free crafter | no crafter of the right kind is free yet; it goes in as soon as one is |
| done | everything ordered of it has arrived |
| no progress | nothing arrived for a minute and no crafter works on it: check ore and sub-parts |
| stopped, rest not made | you pressed Stop waiting (or the crafter is gone) |

Under a line you see what each crafter still has ("Assembler 2: 12 left, crafting") and how many pieces are not queued yet. The numbers count parts that arrive in storage, so a part can stay at 0 for a while when its sub-parts are made first. When everything is done the lines stay on screen (with the time it took) until you start the next order or press Clear orders.

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
| Clear list | empties the build list (orders stay) |
| Stop waiting | drops parts that are not queued yet; crafters finish what they have |
| Clear orders | empties the order list; crafters keep their queues |

After an order the build list is empty and its parts appear under Orders.

How parts are queued:
- Part by part, oldest order first, in batches. Every crafter that is free gets a batch of up to 10 crafts of the oldest part that still waits; if that part needs fewer crafts than there are free crafters, the other crafters go on to the next part.
- When a crafter has finished its batch, it gets the next batch of the oldest part still waiting. So **the more crafters of a kind you have, the faster a big build is made**: build as many Assemblers, Smelters or Refineries as you like, the planner uses all of them (except those named `[NoMake]`).
- Only on crafters that are not working at that moment. A crafter that holds a batch but waits for sub-parts is not given more until its batch arrives.
- If all crafters of a kind are busy, the parts wait and you are told once ("all 2 Smelters are busy ... More Smelters would finish sooner"). The planner looks again every 5 seconds, so a crafter can wait up to 5 seconds for its next batch.
- Only the parts of the blocks are queued. Their sub-parts are requested by the game's crafters themselves.
- All of your crafters are used. Put `[NoMake]` in a crafter's name to keep the planner away from it. A Gearforge named `[Make Ammo]` belongs to LW Cargo Manager and is not used.

When everything has arrived you get "Build Planner: all ordered parts are done" as a green notification with the game's notification sound.

## 4. Problems and notes
| Message | Meaning | What to do |
|---|---|---|
| Ore deficit: X, N more needed | not enough ore for the order (ore that running orders still need is already taken off) | mine or buy, then Check |
| No Assembler to use on this grid | a crafter kind the parts need is missing, not working, or named `[NoMake]` | build or repair one |
| No Assembler can reach storage | the crafter has no conveyor link to the main network | connect it |
| No storage on the conveyor network | nothing to take materials from | add a container |
| X: no conveyor link, not used / not counted | warning: a crafter or container is cut off | connect it, or ignore |
| X is not progressing | no parts arrived for a minute and no crafter is working on it | check ore and sub-parts; Clear orders if it stays |
| Not enough cargo space | the new parts weigh more than the free capacity of the storage | add or empty containers, or order fewer blocks |

## 5. Limits of this version
- Progress counts parts arriving in storage. A mod that makes crafters output more than the recipe (for example a "4x faster" crafter mod) makes lines finish too early.
- You cannot cancel parts that are already inside a crafter from the planner; remove them in the crafter's own screen.
- The list and the orders are forgotten when you leave the world.
- Orders go to the crafters of one grid: the one with the most crafters.
- `+` and Ctrl+N cannot be changed yet, and they need Windows. On other systems open the window with the plugin's config button in Pulsar (the selected block is not brought along there).

## 6. With LW Cargo Manager
The two plugins do not get in each other's way. Cargo Manager moves finished parts from the crafters into your `[Parts]` containers; the planner counts parts in crafters and containers alike.
