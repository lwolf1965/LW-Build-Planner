# LW Build Planner

Space Engineers 2 client plugin: pick blocks in the G menu, see the parts they need against your storage, and order those parts from your crafters in one go.

Status: **0.1.0.1, test build, not released.** Single player only. Works alongside [LW Cargo Manager](https://github.com/lwolf1965/LW-Cargo-Manager), which collects the finished parts into `[Parts]`.

## What it does
- **Pick** a block variant and size in the G menu and add it to a build list with one key.
- **Planner window**: build list, parts (need, in storage, on order, missing), problems, running orders.
- **Order all** or **Order missing**: the parts are queued on crafters that are free, spread evenly. The game's crafters request the sub-parts from smaller crafters themselves.
- **Checks** before ordering: ore deficit, missing crafter kind, no conveyor link, no storage.
- **Follows the orders**: tells you when all crafters of a kind are busy, when an order makes no progress, and when everything is done.
- Block costs and recipes are read from the game every time a world loads, so mods and game updates are picked up.

Manual: [Docs/UserManual.md](Docs/UserManual.md). Changes: [CHANGELOG.md](CHANGELOG.md).

## Keys (while the G menu is open)
| Key | Action |
|---|---|
| `=` (also numpad `+`, Insert) | add the selected block |
| `-` | remove it |
| Shift / Ctrl with those | bigger steps |
| Home | open the planner window |
| End | clear the build list |

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
- The keys are fixed and read with a Windows call (no Linux support yet).

## For contributors
See [CONTRIBUTING.md](CONTRIBUTING.md). `ClientPlugin/Policy` is pure logic (no game types) and is compiled into `tests/`. Everything that touches the world runs on the game thread through `PlannerEngine.Enqueue`.

License: MIT.
