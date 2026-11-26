# 008 Narrow the jq error output of the GitHub Packages login check

As the RoomKit maintainer, I want the login check to print only NuGet's problem messages on failure, so that a future NuGet output shape can't echo more into the log.

Category: security

## What

In .github/workflows/release.yml, step "Check the GitHub Packages login", replace `jq -r '.. | .text? // empty'` with a narrower path such as `.problems[]?.text, .searchResult[]?.problems[]?.text`, and update the workflow tests.

## Why

review-security (job 434, round 2 nit 2): today every `text` field holds only error messages, but the recursive path would print any future field named `text` to the public log.

## Notes

Proposed by job 434-nuget-token-off-cmdline.a2c01aa145d94d0b8d1c7dc4cce77c33 (`factory/jobs/434-nuget-token-off-cmdline.a2c01aa145d94d0b8d1c7dc4cce77c33/`).
