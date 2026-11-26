# 005 Let host apps hook into job-count loads and live change checks

As a RoomKit host app (ControlRoom), I want to register callbacks that run after the job counts load and after a live change check, so that I can keep my own views in step without patching RoomKit scripts.

Category: feature

## What

Add a small, generic hook registry to the shared web scripts in src/RoomKit/wwwroot (for example roomKitHooks.on("jobCounts", fn) and roomKitHooks.on("liveChecked", fn)). In counts.js loadJobCounts, after counts are set, call the jobCounts hooks with data.jobs. In live.js checkChanges, after noteChanges and before refreshShownViews, await the liveChecked hooks with { jobsChanged, recovered, firstCheck } (jobsChanged: previous liveLast.jobs differs from next.jobs; firstCheck: there was no previous liveLast) when board views are visible. A hook that throws must not break counts or live polling. Document the hooks in docs/roomkit.md, add tests, and keep PlayRoom working without registering any hook. Ship it in the next minor release (0.2.0).

## Why

ControlRoom PR #4 (keep overlong rework notes editable, reconcile pending decisions) needs exactly these two points (refreshOpenJob(data.jobs) and recheckPendingCancel) and put them straight into RoomKit files, which no longer live in ControlRoom. Without a hook, ControlRoom would have to overwrite RoomKit globals, which any RoomKit release could silently break.

## Notes

Source: the two hunks of nothisisnotdaniel/controlroom PR #4 in src/RoomKit/wwwroot/counts.js:139 and live.js:361.
