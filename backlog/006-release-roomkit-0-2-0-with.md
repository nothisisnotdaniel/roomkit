# 006 Release RoomKit 0.2.0 with the host hooks (after: #5)

As the RoomKit owner, I want 0.2.0 published to GitHub Packages, so that ControlRoom can pin the version with the host hooks.

Category: ci

## What

After #5 merges, run /factory release --project RoomKit, pick 0.2.0, merge the release PR, and check that 0.2.0 shows on the GitHub Packages feed. Then tick this line.

## Why

The ControlRoom redo of PR #4 pins RoomKit 0.2.0 and waits on this line; without the release its restore fails.

## Folded in from #7 (backlog audit, 2026-11-26)

Confirm the first release push to GitHub Packages works without --api-key: After the next release run, confirm in its log that "Check the GitHub Packages login" and "Push to GitHub Packages" both succeeded and the package is on GitHub Packages. If the push fails on write permission, fix it with the temp nuget.config approach (for example the packages: write grant or the feed URL), never by putting --api-key "$GITHUB_TOKEN" back on the command line.
