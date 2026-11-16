# Changelog

Version scheme: STAGE.FEATURE.FIX.BUILD (0.1 = game alpha).

## 0.1.0.1 (not released)
First test build.
- Pick blocks in the G menu (`=` / `-`, Shift and Ctrl for bigger steps); limits per list line: armor 1000, other blocks 50.
- Planner window (Home, or the plugin's config button in Pulsar): build list, parts table, problems, orders.
- Order all / Order missing; parts are queued only on free crafters and spread evenly; all crafter kinds (Smelter, Refinery, Assembler, Fabricator, Gearforge).
- Only top-level parts are queued; the game's crafters request sub-parts themselves.
- Checks: ore deficit, missing crafter kind, conveyor links (ported from LW Cargo Manager), storage.
- Order tracking by parts arriving in stock; "no progress" after 60 s; HUD notifications.
- `[NoMake]` in a crafter's name keeps the planner away from it; `[Make Ammo]` Gearforges are left to LW Cargo Manager.

- Every build list line has [-] count [+] [X] buttons (Shift and Ctrl for bigger steps); a line at 0 disappears. Order all stays off while the check finds an error.
- Cargo space check: the new parts must fit into the free capacity (by mass) of the storage the crafters reach, otherwise Order all is blocked.
- "All ordered parts are done" is shown as a high-priority success notification.

Known problem: order tracking counts parts arriving in stock, so with a mod that makes crafters output more than the recipe (seen with a "4x faster" crafter mod) "all ordered parts are done" comes too early.
