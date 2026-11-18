# Security Policy

## Supported versions

Only the latest version is supported. See the [changelog](CHANGELOG.md).

## What the plugin does and does not do

- It runs inside the game as a Pulsar plugin, compiled from this source on your machine.
- It makes **no network connections**, starts no programs and loads no other code.
- It writes **one kind of file**: the build list of each world, a small text file in the game's data folder
  (usually `%AppData%\SpaceEngineers2\LwBuildPlanner\Worlds\<world>.txt`). The file name is made from the world's
  save name with only letters, digits, `-` and `_`, so it cannot leave that folder. Its messages go to the game log.
- It asks Windows for the state of its own keys (`+` on the main keyboard and the numpad, `N`, Shift, Ctrl, Alt),
  only while a terminal screen (which also hosts the G menu) is open and the game window is in front. It does not
  record or store key presses. Ctrl+N anywhere in a world comes from the game's own input.
- It only changes the world on your own crafters, on the in-process game server (single player), by queueing
  recipes the same way the game's production screen does.

A change that breaks any of these points is a security problem. Please report it.

## Reporting a vulnerability

Please do **not** open a public issue for a security problem.

Use GitHub's private reporting: the **Security** tab of this repository, then **Report a vulnerability**.
Include the plugin version, what you found, and how to reproduce it.

This is a hobby project with one maintainer. Expect a first answer within about a week.
