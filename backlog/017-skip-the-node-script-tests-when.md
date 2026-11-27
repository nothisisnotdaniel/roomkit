# 017 Skip the node script tests when node is missing

As a contributor on a machine without node, I want the JS script tests to be skipped rather than fail, so that a missing tool does not look like a broken build.

Category: test

## What

Make EditRowScriptTests and HostHooksScriptTests skip (with a reason naming node) when node is not on PATH, instead of failing; keep them running and required in CI where node is present.

## Why

Job 493 review-code: the spec asked for a skip, but the tests fail without node, matching HostHooksScriptTests. A local run without node then reports false failures.

## Notes

Proposed by job 493-drop-approved-items (review-code nit 1). Check that CI still installs node so the tests never skip there.
