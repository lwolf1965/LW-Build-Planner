# Changelog

Version scheme: STAGE.FEATURE.FIX.BUILD (0.1 = game alpha).

## 0.1.0.1 (not released)
First test build.
- Pick blocks in the G menu with `+` (Shift and Ctrl for bigger steps); limits per list line: armor 1000, other blocks 50.
- Planner window (Ctrl+N, or the plugin's config button in Pulsar): selected block, build list, parts table, problems, orders. In the G menu only `+` and Ctrl+N are keys (Ctrl+B is the game's blueprint key); in the window every change is a button (no `-`, Home or End keys).
- Order all / Order missing; parts are queued only on free crafters and spread evenly; all crafter kinds (Smelter, Refinery, Assembler, Fabricator, Gearforge).
- Only top-level parts are queued; the game's crafters request sub-parts themselves.
- Checks: ore deficit, missing crafter kind, conveyor links (ported from LW Cargo Manager), storage.
- Order tracking by parts arriving in stock; "no progress" after 60 s; HUD notifications.
- `[NoMake]` in a crafter's name keeps the planner away from it; `[Make Ammo]` Gearforges are left to LW Cargo Manager.
- Every build list line has [-] count [+] [X] buttons (Shift and Ctrl for bigger steps); a line at 0 disappears. Order all stays off while the check finds an error.
- Cargo space check: the new parts must fit into the free capacity (by mass) of the storage the crafters reach, otherwise Order all is blocked.
- "All ordered parts are done" is shown as a high-priority success notification (green, with the game's notification sound).
- Orders show progress: per part made / ordered with a progress bar, a status (crafting, queued, waiting for a free crafter, done, no progress, stopped), what each crafter still has, oldest order first, and a clock. Finished lines stay until the next order.
- Parts waiting for a crafter are handed out oldest order first, in batches (up to 10 crafts per crafter): every free crafter gets a batch and a crafter that becomes free gets the next one, so more crafters make a big build faster.
- Arrivals are credited to crafters that stopped working first; a crafter that holds a batch but waits for sub-parts is not given more; one that is idle for 30 s with a lost batch is given a new one.
- The storage limiter and the batch dispatch are pure code with tests (error / warning / no alert thresholds, incoming orders, unit scale, even spread, oldest part first).
- A new world session resets the build list, the check and the orders (nothing of the old world stays in memory).
- Window buttons do not fail on systems without the Windows key API (keys are Windows only; the window works everywhere).
- The window is made of boxes with short explanations and keeps its scroll position while it updates.

Known problem: order tracking counts parts arriving in stock, so with a mod that makes crafters output more than the recipe (seen with a "4x faster" crafter mod) "all ordered parts are done" comes too early.
