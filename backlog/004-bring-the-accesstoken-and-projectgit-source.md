# 004 Bring the AccessToken and ProjectGit source-scan tests over from ControlRoom

As the RoomKit owner, I want the source-scan guards for AccessToken and ProjectGit to live in RoomKit, so that the security properties they checked stay covered after the move.

Category: test

## What

ControlRoom job 429 deleted three source-scan tests because the code they read now lives only in RoomKit: AccessTokenTests.Matches_UsesFixedTimeEqualsOnBytes (AccessToken.Matches uses CryptographicOperations.FixedTimeEquals on bytes), and the ProjectStatusEndpointTests and ReleasesEndpointTests checks that ProjectGit starts only the fixed git calls and uses no HttpClient. Add equivalent tests to tests/RoomKit.Tests that read src/RoomKit/AccessToken.cs and src/RoomKit/ProjectGit.cs. Take the originals from ControlRoom history before merge a1cd7df.

## Why

RoomKit has no copies of these guards today, so a change that swaps the fixed-time compare for a plain string compare, or adds network access to ProjectGit, would pass every test in both repos.

## Notes

Proposed by job 429-extract-roomkit-repo (approver escalation, deleted tests).
