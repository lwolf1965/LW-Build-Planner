# LW Build Planner: user manual

Version 0.1.0.1 (test build). Single player.

## What it is for
You want to build something, for example ten hydrogen thrusters. The planner tells you which parts that needs, what is already in your storage, and orders the rest from your crafters.

## 1. Pick blocks
1. Open the G menu.
2. Click the block group, then the variant in the row on the right, then the size (the small squares).
3. Press `=` to add one to the build list. `-` removes one.

| Block | Key alone | with Shift | with Ctrl | Most per line |
|---|---|---|---|---|
| Armor | 1 | 10 | 100 | 1000 |
| Everything else | 1 | 5 | 10 | 50 |

A notification confirms each key, for example "Build Planner: Mk3 200 (Block size 1) +1 (now 3)".
Numpad `+` and Insert also add. End clears the whole list.

Note: the "Components" list in the G menu itself shows the cost of the first variant of a group for every variant. The planner uses the real cost of the variant and size you selected.

## 2. The planner window
Press Home while the G menu is open (or use the plugin's config button in Pulsar).

**Build list**: what you picked.

**Parts**
| Column | Meaning |
|---|---|
| need | parts for the whole build list |
| in storage | in containers on the crafters' conveyor network (also on docked grids) |
| on order | already ordered by the planner and not finished |
| missing | need minus storage minus on order |

What you carry yourself is not counted.

**Problems**: reasons an order is blocked, see section 4.

**Orders**: parts the planner has ordered. "waiting" = not queued yet because no crafter was free. "in crafters" = queued and not finished. The last column shows which crafters, or why a line waits.

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
- Only on crafters that are not working at that moment, spread evenly.
- If all crafters of a kind are busy, the parts wait and you are told once ("all 2 Smelters are busy ... More Smelters would finish sooner"). The planner looks again every 5 seconds.
- Only the parts of the blocks are queued. Their sub-parts are requested by the game's crafters themselves.
- All of your crafters are used. Put `[NoMake]` in a crafter's name to keep the planner away from it. A Gearforge named `[Make Ammo]` belongs to LW Cargo Manager and is not used.

When everything has arrived you get "Build Planner: all ordered parts are done".

## 4. Problems and notes
| Message | Meaning | What to do |
|---|---|---|
| Ore deficit: X, N more needed | not enough ore for the order (ore that running orders still need is already taken off) | mine or buy, then Check |
| No Assembler to use on this grid | a crafter kind the parts need is missing, not working, or named `[NoMake]` | build or repair one |
| No Assembler can reach storage | the crafter has no conveyor link to the main network | connect it |
| No storage on the conveyor network | nothing to take materials from | add a container |
| X: no conveyor link, not used / not counted | warning: a crafter or container is cut off | connect it, or ignore |
| X is not progressing | no parts arrived for a minute and no crafter is working on it | check ore and sub-parts; Clear orders if it stays |

## 5. Limits of this version
- You cannot cancel parts that are already inside a crafter from the planner; remove them in the crafter's own screen.
- The list and the orders are forgotten when you leave the world.
- Orders go to the crafters of one grid: the one with the most crafters.
- Keys cannot be changed yet.

## 6. With LW Cargo Manager
The two plugins do not get in each other's way. Cargo Manager moves finished parts from the crafters into your `[Parts]` containers; the planner counts parts in crafters and containers alike.
