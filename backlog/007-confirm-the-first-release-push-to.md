# 007 Confirm the first release push to GitHub Packages works without --api-key

As the RoomKit maintainer, I want the first release after job 434 checked for a successful GitHub Packages push, so that the source-credential login is proven end to end.

Category: security

## What

After the next release run, confirm in its log that "Check the GitHub Packages login" and "Push to GitHub Packages" both succeeded and the package is on GitHub Packages. If the push fails on write permission, fix it with the temp nuget.config approach (for example the packages: write grant or the feed URL), never by putting --api-key "$GITHUB_TOKEN" back on the command line.

## Why

review-security (job 434, round 1 nit 2 and round 2 nit 1): the login check proves only that the token can log in and read the feed; NuGet has no push dry run, so only a real release proves the push. Without this check a broken push could go unnoticed or be fixed by bringing the token back into argv.

## Notes

Proposed by job 434-nuget-token-off-cmdline.a2c01aa145d94d0b8d1c7dc4cce77c33 (`factory/jobs/434-nuget-token-off-cmdline.a2c01aa145d94d0b8d1c7dc4cce77c33/`).
