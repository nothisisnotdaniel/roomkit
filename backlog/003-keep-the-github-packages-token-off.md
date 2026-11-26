# 003 Keep the GitHub Packages token off the nuget push command line

As the owner of RoomKit, I want the release workflow to hand GITHUB_TOKEN to dotnet nuget push without putting it on the command line, so that no other process on the runner can read it.

Category: security

## What

In .github/workflows/release.yml, step "Push to GitHub Packages", stop passing --api-key "$GITHUB_TOKEN". Instead add a temporary NuGet source credential for the github source in that step only (for example a job-local nuget.config written to the runner temp folder, or dotnet nuget update source with the token from env), push with --source github, and remove the temporary config afterwards. Extend the release workflow text test so it fails if --api-key appears in release.yml.

## Why

Raised by the security review of job 431-roomkit-own-repo: a token on a command line can be seen through the process list by other processes on the runner. Actions masks it in logs and the runner is ephemeral, so the risk is low, but the release job holds packages: write.

## Notes

Proposed by job 431-roomkit-own-repo.fba5df193b59469fbd65fb747d088dcf (security nit 3, round 1).
