# 001 Move RoomKit into its own repository with CI and a release workflow

As the owner of ControlRoom and PlayRoom, I want RoomKit's code, tests and docs in this repository with its own CI and releases to GitHub Packages, so that PlayRoom gets RoomKit fixes without waiting for a ControlRoom release.

Category: ci

## What

Import src/RoomKit/, tests/RoomKit.Tests/ and docs/roomkit.md from ControlRoom develop with their history (git subtree split or git filter-repo on a scratch clone, never on ControlRoom/), keeping the same paths. Add RoomKit.slnx, Directory.Build.props, .editorconfig and .gitignore copied from ControlRoom, and copy in the shared test fixtures RoomKit.Tests.csproj links today (TempFactoryRoot, TempTokenFolder, PushFakes, PushTestData, RepoCopy, WindowsReleaseFixture, TestPartsGuardTests), adjusted to find RoomKit.slnx; drop the ControlRoom.Server.Tests using. Set RepositoryUrl https://github.com/nothisisnotdaniel/roomkit in RoomKit.csproj and keep InternalsVisibleTo ControlRoom.Server.Tests. Add .github/workflows/ci.yml (PRs and pushes to develop: dotnet test -c Release on macOS and Windows) and .github/workflows/release.yml (merged PR develop -> main titled 'release vX.Y.Z': version check, test, dotnet pack -p:Version=X.Y.Z, SHA256SUMS, build provenance attestation, gh release create vX.Y.Z, dotnet nuget push to https://nuget.pkg.github.com/nothisisnotdaniel/index.json with GITHUB_TOKEN); top-level permissions: {}, and only the release job gets contents, packages and id-token write plus attestations write; actions pinned by SHA. Fill in CLAUDE.md (stack, test command dotnet test, layout, git) and rewrite docs/roomkit.md 'Feed and versioning' for GitHub Packages, including the one-time read:packages credential setup for consumers. Acceptance: dotnet build has 0 warnings and dotnet test passes with every moved test; git log --follow on src/RoomKit/RoomKitApp.cs shows pre-move commits; dotnet pack -p:Version=1.2.3 gives RoomKit.1.2.3.nupkg with id RoomKit, readme, scripts, static web assets and the repository URL; a workflow-text test checks release.yml's trigger, push target and permissions.

## Why

ControlRoom#151 was split on 2026-11-26: the factory merges only one repo per job, so the move into RoomKit runs here first. ControlRoom's switch to the package (job 429) can only restore once this repo publishes RoomKit 1.0.0. Without it, PlayRoom keeps waiting for ControlRoom releases to get RoomKit fixes.

## Notes

Split from ControlRoom#151 (job 429-extract-roomkit-repo); its spec.md in factory/jobs/ has the full Part A scope. PlayRoom changes are out of scope; no RoomKit prereleases from develop.
