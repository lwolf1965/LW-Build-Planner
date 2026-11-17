# LW Build Planner

Space Engineers 2 client plugin: pick blocks in the G menu, see the parts they need against your storage, and order those parts from your crafters in one go.

Status: **0.1.0.1, test build, not released.** Single player only. Works alongside [LW Cargo Manager](https://github.com/lwolf1965/LW-Cargo-Manager), which collects the finished parts into `[Parts]`.

## What it does
- **Pick** a block variant and size in the G menu and press `+` to add it to a build list (Shift / Ctrl for bigger steps).
- **Planner window**: build list, parts (need, in storage, on order, missing), problems, running orders.
- **Order all** or **Order missing**: the parts are queued in batches on the crafters that are free, oldest part first; a crafter that finishes gets the next batch. Build as many crafters as you can: more crafters make a big build faster. The game's crafters request the sub-parts from smaller crafters themselves.
- **Checks** before ordering: ore deficit, missing crafter kind, no conveyor link, no storage, not enough cargo space (blocks the order; warning above 80 % of the free capacity).
- **Follows the orders**: tells you when all crafters of a kind are busy, when an order makes no progress, and when everything is done.
- Block costs and recipes are read from the game every time a world loads, so mods and game updates are picked up.

Manual: [Docs/UserManual.md](Docs/UserManual.md). Changes: [CHANGELOG.md](CHANGELOG.md).

## Keys
While the G menu is open:
| Key | Action |
|---|---|
| `+` (also numpad `+`) | add the selected block; Shift and Ctrl for bigger steps |
| Ctrl+N | open the planner window with the selected block |

Everything else (remove, clear, order) is a button in the window, so no stray key press takes blocks off the list or starts crafters.

## Install (development)
The plugin is not in the PluginHub yet.
1. Copy `Directory.Build.props.user.example` to `Directory.Build.props.user`, set `Game2` (folder with `SpaceEngineers2.exe`) and `Pulsar` (for example `$(APPDATA)\Pulsar`).
2. `dotnet build`: the DLL is copied to `<Pulsar>\Modern\Local`.
3. Start the game through Pulsar and enable **LwBuildPlanner** in the plugin list.

`dotnet run --project tests` runs the logic checks (must end with `ALL PASSED`).
`dotnet build -c Release` also copies the DLL to `Release\`.

## Known limits
- Parts already inside a crafter cannot be cancelled from the planner yet.
- The build list and the orders are not saved; they are empty after a restart.
- Orders go to the crafters of one grid (the one with the most crafters). Storage on other grids counts when those crafters reach it by conveyor.
- What you carry is not counted, only storage on the conveyor network.
- `+` and Ctrl+N are fixed and read with a Windows call. Elsewhere the window opens from the plugin's config button.

## For contributors
Rules: [AGENTS.md](AGENTS.md). `ClientPlugin/Policy` is pure logic (no game types) and is compiled into `tests/`. Everything that touches the world runs on the game thread through `PlannerEngine.Enqueue`.

License: MIT.
