# RoomKit — conventions

Every factory agent reads this file before working on RoomKit. Keep it short and accurate.

- **What it is:** the shared core of a factory control app, as a .NET class library and NuGet package
  (`RoomKit`): sign-in, Host check, loopback listening, the web app shell, push, updates, backlogs, search,
  history and health, plus the opt-in factory module. ControlRoom and PlayRoom are built on it; it holds no value
  of either app (`RoomKitLibraryTests` checks that). `docs/roomkit.md` describes the public API and is the
  package's readme.
- **Stack:** .NET 10 (`net10.0`). `src/RoomKit` is a `Microsoft.NET.Sdk.Razor` class library with
  `FrameworkReference Microsoft.AspNetCore.App` and no `PackageReference`; its web files are static web assets at
  base path `/`. Tests use xUnit with `Microsoft.AspNetCore.Mvc.Testing` to host sample apps in memory. No new
  package without a reason in the spec.
- **Test command:** `dotnet test` (from `RoomKit/`; `dotnet build RoomKit.slnx` must stay at 0 warnings). Tests
  never open a real port or read the real factory root: they build temporary roots (`TempFactoryRoot`,
  `TempTokenFolder`) or a repo copy (`RepoCopy`), each in its own temp folder deleted on dispose. Work in another
  process (`dotnet pack`, `/bin/sh`, PowerShell) is awaited for its exit with a named, bounded timeout, never with
  a delay. Script tests that need `/bin/sh` return early on Windows.
- **Layout:**
  - `RoomKit.slnx`, `Directory.Build.props` (nullable, implicit usings, code style in build), `.editorconfig`,
    `.gitattributes` (LF everywhere), `.gitignore` (`bin/`, `obj/`).
  - `src/RoomKit/`: the library (`RoomKitApp`, `RoomKitOptions`, `FactoryModule`, `IJobFormat` and the endpoints
    and services), `wwwroot/` (the shared web files) and `scripts/` (`launch-agent.sh`/`.ps1`,
    `updater.sh`/`.ps1`, packed into the package).
  - `tests/RoomKit.Tests/`: the tests, the sample apps (`SampleRoom`, `CoreRoom`) and `Shared/` (fixtures:
    `TempFactoryRoot`, `TempTokenFolder`, `PushFakes`, `PushTestData`, `RepoCopy`, `WindowsReleaseFixture` and
    its `.ps1`). `WorkflowTests` reads the two workflows as text, `ReleaseVersionScriptTests` runs the release
    script, `RepoDocsTests` checks this file and `docs/roomkit.md`.
  - `docs/roomkit.md`: the library's docs (the package readme).
  - `.github/workflows/ci.yml` (tests on macOS and Windows for PRs into and pushes to `develop`),
    `.github/workflows/release.yml` and `.github/scripts/release-version.sh` (the release, see Git).
- **Style:** C# with file-scoped namespaces (`RoomKit`, tests `RoomKit.Tests`), `sealed` classes, explicit types
  where the repo uses them, `StringComparison.Ordinal` for every string compare, XML doc summaries on public types.
  Every public type is named in `docs/roomkit.md`. Workflows: `permissions: {}` at the top, the least per job,
  every action pinned to a full commit SHA with its tag in a comment, `persist-credentials: false` on every
  checkout, and PR fields, outputs and tokens reach a `run:` script only through `env:`.
- **Git:** `develop` is the working branch: factory work branches off it as `factory/<job-id>` and is merged back with `--no-ff`. `main` holds releases only and is pushed to only for a new release.
  A release is a pull request from `develop` into `main` titled `release vX.Y.Z` (SemVer, no `-develop`). When it
  is merged, `release.yml` tests, packs `RoomKit.X.Y.Z.nupkg`, attests it, creates the GitHub release `vX.Y.Z` on
  the merge commit and pushes the package to GitHub Packages (`https://nuget.pkg.github.com/nothisisnotdaniel/index.json`).
