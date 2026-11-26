# 009 Mark the push login check tests as skipped on Windows

As a RoomKit contributor, I want PushLoginCheckScriptTests to show as skipped on Windows, so that the test report does not claim they passed there.

Category: test

## What

In tests/RoomKit.Tests/PushLoginCheckScriptTests.cs, replace the early return on Windows (and when jq is missing) with a proper skip, so the runner reports them as skipped.

## Why

review-code (job 434, round 2 nit 1): the tests return early and count as passed on Windows, which hides that they never ran.

## Notes

Proposed by job 434-nuget-token-off-cmdline.a2c01aa145d94d0b8d1c7dc4cce77c33 (`factory/jobs/434-nuget-token-off-cmdline.a2c01aa145d94d0b8d1c7dc4cce77c33/`).
