# 002 Release RoomKit v1.0.0 and give ControlRoom and PlayRoom read access to the package

As the owner, I want RoomKit 1.0.0 published on GitHub Packages and readable by the ControlRoom and PlayRoom repositories' Actions, so that their CI can restore the package.

Category: ci

## What

Run /factory release --project RoomKit and merge the 'release v1.0.0' PR, check that RoomKit.1.0.0 shows under the repository's Packages, then in the package settings grant nothisisnotdaniel/controlroom and nothisisnotdaniel/playroom Actions read access. Set up a local read:packages credential for the github NuGet source (user-level, never in a repo).

## Why

ControlRoom job 429 pins RoomKit 1.0.0 and cannot build or pass CI until the package exists and its Actions may read it.
