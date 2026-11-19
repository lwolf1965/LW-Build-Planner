## What this changes

<!-- One or two sentences. Link the issue: "Fixes #12". -->

## How it was tested

- [ ] `dotnet run --project tests` passes
- [ ] Loaded in the game through Pulsar from a local build
- [ ] World load, back to menu, load again: no plugin errors in the log

## Checklist

- [ ] The change is about build planning (one plugin, one job)
- [ ] New decisions are in `ClientPlugin/Policy/` and have a test
- [ ] The world is touched only through `PlannerEngine.Enqueue`
- [ ] Nothing the player queued by hand is changed
- [ ] No network access was added; no file access besides the per-world build list (see SECURITY.md) and Config.xml
- [ ] Version number and `LwBuildPlanner.xml` are unchanged
- [ ] `Docs/UserManual.md` is updated if players see the change
