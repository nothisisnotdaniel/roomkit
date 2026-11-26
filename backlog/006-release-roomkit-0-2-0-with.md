# 006 Release RoomKit 0.2.0 with the host hooks (after: #5)

As the RoomKit owner, I want 0.2.0 published to GitHub Packages, so that ControlRoom can pin the version with the host hooks.

Category: ci

## What

After #5 merges, run /factory release --project RoomKit, pick 0.2.0, merge the release PR, and check that 0.2.0 shows on the GitHub Packages feed. Then tick this line.

## Why

The ControlRoom redo of PR #4 pins RoomKit 0.2.0 and waits on this line; without the release its restore fails.
