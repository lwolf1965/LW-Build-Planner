# Contributing

Thanks for helping with LW Build Planner. Ideas, bug reports and pull requests are all welcome.

## Ideas and bug reports

- **Ideas:** open an issue with the *Suggestion* form. Say what you want to achieve in the game, not only how.
- **Bugs:** open an issue with the *Bug report* form. The plugin version and the lines with `[LwBuildPlanner]`
  from the game log are the most useful part (`%AppData%\SpaceEngineers2\Temp\Logs`).
- The plugin is single player only for now.

## What fits this plugin

**One plugin, one job.** This one plans builds and orders their parts from your crafters: pick blocks,
see what is missing, order it, follow the orders. An idea outside that (cargo sorting, cameras, autopilot ...)
belongs in a separate plugin, so it will be declined here even when it is a good idea. Cargo sorting and
unloading are in [LW Cargo Manager](https://github.com/lwolf1965/LW-Cargo-Manager).

## Pull requests

Please open an issue first for anything bigger than a small fix, so we can agree on the approach.

Rules the code follows:

- **Decisions live in `ClientPlugin/Policy/`**, which never references game, Harmony or Avalonia types.
  Every decision gets a test.
- **The world is touched only on the game thread**, through `PlannerEngine.Enqueue`. The window shows snapshots
  and queues actions.
- **Block costs, recipes and names come from the game at runtime**, never from tables or a file, so mods and
  game updates keep working.
- **Crafters:** queue only on crafters that are not crafting, only top-level parts (the game requests the
  sub-parts itself), and never touch what the player queued by hand. Crafters named `[NoMake]` or `[Make Ammo]`
  are left alone.
- **Limits are on blocks** in the build list, not on parts.
- **No network access.** The plugin writes only the build list of each world (`PlanSave`, under the game's data
  folder, file name sanitized by `PlanFile.FileName`, written and removed only at a world save) and its settings (`Config.xml` in the plugin's Pulsar data
  folder). Any other file access needs a good reason and an update of [SECURITY.md](SECURITY.md).

Before you open the pull request:

1. Run the tests: `dotnet run --project tests`. It must end with `ALL PASSED`.
2. Test in the game through Pulsar from your local build: load a world, go back to the menu, load again,
   and check the log for errors.
3. Do not change the version number or `LwBuildPlanner.xml`; the maintainer does that at release.
4. Update `Docs/UserManual.md` if players will see the change.

## Building

See *Install (development)* in the [README](README.md). Copy `Directory.Build.props.user.example` to
`Directory.Build.props.user` and set your game folder.

## Licence

By contributing you agree that your work is released under the project's [MIT licence](LICENSE).
