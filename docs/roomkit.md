# RoomKit

RoomKit is the shared core of a factory control app. ControlRoom is built on it, and another factory's app
(PlayRoom) can reference the packed library so that fixes to sign-in, push, updates and backlog editing reach both.
An app supplies its names (`RoomKitOptions`), how its factory keeps jobs (`IJobFormat`), its own tabs and
endpoints, and one small `room.js`. RoomKit does the rest.

## What is in it

- **Server:** `RoomKitApp` builds and runs the app. It does the `Host` check (only `127.0.0.1:<port>` and the
  tailnet host), listens on loopback only, allows a method only on its route (`POST` only on the fixed routes),
  puts the access token gate on every `/api/` path, sets the security headers and handles the `version` and
  `token` commands.
- **Shared views and APIs:** backlogs and backlog editing (`/api/backlogs`, `/api/backlog/change`, worktree
  edits while a job runs), stories, search, history and token totals, the Health view (`/api/doctor`), inbox,
  tasks, push (subscribe, test, the job watcher), the update check and its settings, `/health` and `/api/version`.
- **Web app shell:** `app.css`, the service worker `sw.js`, `token.js`, `push.js`, `edit.js`, `counts.js`,
  `search.js`, `capture.js`, `history.js`, `doctor.js` and `live.js`.
- **Scripts:** `scripts/launch-agent.sh` and `scripts/updater.sh` install and update macOS launch agents.
  `scripts/launch-agent.ps1` and `scripts/updater.ps1` install and update per-user Windows tasks.

The public types are few. Most of the code is `internal`. The public ones are:

- The plug-in API: `RoomKitApp`, `RoomKitOptions`, `IJobFormat`, `RoomJob`, `RoomJobFolder`, `RoomRoutes` and
  `FactoryRoot` (the factory root an endpoint gets from the services).
- Backlog reading and editing that an app's own endpoints use: `Backlog`, `BacklogEntry`, `BacklogDependency`,
  `BacklogFile`, `BacklogReader`, `BacklogSource`, `BacklogEditor`, `BacklogEdit`, `AddPosition`,
  `MoveDirection`, `EditableText`, `BacklogWorktree`, `WorktreeState`, `ProjectBacklog`, `BacklogsEndpoint`,
  `BacklogsResponse`, `PlanItem`, `PlanDependency` and `BlockedItem`.
- Stories: `StoryMarkdown`, `StoryBlock` and `StoryInline`.
- Projects and git: `ProjectFinder`, `ProjectFolder`, `ProjectGit` and `GitResult`.
- Changes: `ChangeFingerprint`, `ChangesResponse` and `ProjectChanges`.
- Safe file access: `SafeFileReader`, `SafeFileWriter`, `NoFollowStat`, `PathStat` and `PathKind`.

## Options

`RoomKitOptions` holds the values that make the server one app. `RoomKitApp` checks them before anything
starts.

| Property | Example (ControlRoom) | Used for |
|----------|-----------------------|----------|
| `AppName` | `Control Room` | the name people read |
| `ShortName` | `ControlRoom` | the `version` line, the update check's `User-Agent`, the push subject |
| `DataFolderName` | `ControlRoom` | the folder under `~/Library/Application Support` with the token, push, update and worktree files |
| `SolutionFileName` | `ControlRoom.slnx` | the root locator: the factory root is the parent of the folder holding this file |
| `DefaultPort` | `8100` | the port, bound to `127.0.0.1` only |
| `ReleaseRepository` | `nothisisnotdaniel/controlroom` | the GitHub repository the update check asks |
| `CaptureNote` | `Draft from ControlRoom` | the dated note on a captured draft story |
| `InformationalVersion` | the app assembly's | `/health`, `/api/version` and the `version` command (`dev` when empty); RoomKit's own version is never used |

## Job format

`IJobFormat` says how the factory keeps its jobs. The Health view, history, the push watcher and the change
fingerprint read jobs only through it:

- `JobsFolderPath`: the jobs folder relative to the factory root, for example `factory/jobs`.
- `JobFolders(root)`: every job folder as a `RoomJobFolder` (id and path), with no symlinks and every name
  matching `IdPattern`.
- `ReadAll(root)`: every readable job as a `RoomJob` (stage, project, mode, tokens, updated time, CI status
  and more; a value it can't read is `null`).
- `RecordFileNames`: the files in a job folder whose changes the change fingerprint watches.
- `Stages`, `ActiveStages` (checked for being stuck), `BlockedStages` (a push when a job moves into one),
  `DoneStage` (a push when a job finishes), `ReviewKeys` and `IdPattern`.

ControlRoom's format is `StateJsonJobFormat` (`factory/jobs/<id>/state.json` and `human.json`). The sample app in
`tests/RoomKit.Tests` uses a different one (`work/jobs/<id>/job.txt`).

## Web assets and tabs

RoomKit's `wwwroot` files are static web assets with base path `/`, so an app serves them at `/app.css`,
`/sw.js` and so on, next to its own `wwwroot`. A path in both folders fails the build. They load without the
`Development` environment, through the static web assets manifest.

The app's `wwwroot` holds `index.html` (its tabs), `manifest.webmanifest`, its icons, its own scripts and
`room.js`. `room.js` is a classic script whose top-level `const`s the RoomKit scripts read as globals. It must
declare:

- `TokenKey`, `CaptureProjectKey` and `HistoryProjectKey`: the localStorage keys. Changing `TokenKey` signs out
  every device.
- `PushAppTexts` with `homeScreen` and `denied`: merged into the push texts.
- `CacheVersion`, `CacheName` and `Shell`: the service worker's cache and the files it installs (list
  `/room.js` and every app file).
- `PushAppTitles` with `updated`, `PushFallbackTitle`, `PushDefaultTag` and `PushUpdatePrefix`: the service
  worker's notification texts.

`index.html` loads `<script src="/room.js" defer></script>` as its first script, and `sw.js` starts with
`importScripts("/room.js");`. Raise `CacheVersion` whenever a shell file changes.

## Scripts

`scripts/launch-agent.sh` and `scripts/updater.sh` ship in the package under `scripts/`. Copy both into the app's
`scripts/` folder and change only the block between `# roomkit:app-begin` and `# roomkit:app-end`. It holds only
variable assignments: `APP_NAME`, `LABEL`, `UPDATER_LABEL`, `SUPPORT_FOLDER`, `LOG_FOLDER`, `REPO_SLUG`,
`RELEASES`, `APP_PORT`, `TARBALL_PREFIX`, `SERVER_NAME` and `SOLUTION_FILE`. Everything outside the block stays
byte-identical to RoomKit's copy, so a newer RoomKit's scripts can be taken over by keeping the block.

Windows apps copy `scripts/launch-agent.ps1` and `scripts/updater.ps1` from the same package. Keep them together. The installer uses the updater's verification, task and swap functions. Set the assignments between `# roomkit:app-begin` and `# roomkit:app-end` in the updater to the app's data folder, task prefix, executable, ZIP prefix, port, GitHub repository and relative job paths. Job files carry a JSON `stage` field. Each account gets its own tasks and data under `%LOCALAPPDATA%`.

Publish the single-file executable, `wwwroot` and both scripts at the root of `<prefix>-<version>-win-x64.zip`. Add one SHA-256 entry for that filename to the release's `SHA256SUMS`. The installer accepts `install -FactoryRoot <path> [-Release <tag>] [-TailnetHost <host>]`, `status` and `uninstall`. A running update checks the scheduled server's PID, listener and health version before advancing the release marker.

The Windows updater reads up to ten pages of 100 releases, with a 1 MiB limit per page. An incomplete release scan or an unreadable job state defers installation. If another process holds the result file open, the updater keeps the outcome and previous code in its private staging folder and retries the write on its next invocation.

## Feed and versioning

RoomKit has the same version as the ControlRoom release that ships it. The release workflow packs it with
`dotnet pack src/RoomKit -c Release -p:Version=<version>` and attaches `RoomKit.<version>.nupkg` to that GitHub
release, for stable releases and prereleases. The package is in `SHA256SUMS` and in the build provenance
attestation, like the tarball.

An app restores it from a local folder feed, `~/Library/Application Support/RoomKit/feed`:

```sh
mkdir -p ~/Library/Application\ Support/RoomKit/feed
gh release download vX.Y.Z --repo nothisisnotdaniel/controlroom --pattern 'RoomKit.*.nupkg' \
  --dir ~/Library/Application\ Support/RoomKit/feed
```

A local build works too: `dotnet pack src/RoomKit -c Release -p:Version=X.Y.Z -o ~/Library/Application\ Support/RoomKit/feed`.
The app's `nuget.config` names that folder as a package source.

Why not GitHub Packages: its NuGet feed needs a personal access token with `read:packages` for every restore,
even for public packages. The app on this Mac and in its CI would need a stored secret, and the release workflow
would need `packages: write`. The release asset needs no new secret or permission.

## Plugging in an app

1. Create a `Microsoft.NET.Sdk.Web` project and reference the `RoomKit` package.
2. Write the options and the job format:

   ```csharp
   internal static class MyRoomApp
   {
       public static readonly RoomKitOptions Options = new()
       {
           AppName = "My Room",
           ShortName = "MyRoom",
           DataFolderName = "MyRoom",
           SolutionFileName = "MyRoom.slnx",
           DefaultPort = 8200,
           ReleaseRepository = "example/myroom",
           CaptureNote = "Draft from MyRoom",
           InformationalVersion = typeof(MyRoomApp).Assembly
               .GetCustomAttribute<AssemblyInformationalVersionAttribute>()?.InformationalVersion,
       };

       public static readonly IJobFormat Jobs = new MyJobFormat();

       public static void Map(RoomRoutes routes)
       {
           routes.App.MapGet("/api/board", () => Results.Ok());
           routes.MapPost("/api/board/act", () => Results.Ok());
       }
   }
   ```

3. `Program.cs`: `return RoomKitApp.Run(args, MyRoomApp.Options, MyRoomApp.Jobs, MyRoomApp.Map);`
4. Add `wwwroot/index.html`, `manifest.webmanifest`, the icons, the app's scripts and `room.js` (see Web assets
   and tabs).
5. Copy the scripts for each supported platform and set the app assignments described under Scripts.

`RoomRoutes.MapPost` takes only a fixed path under `/api/`, so the token gate always covers an app's `POST` route.
A `GET` route goes through `RoomRoutes.App`. Every request has already passed the `Host` check, the method rule
and the token gate. `RoomKitApp.ConfigureKestrel` is the listening setup (loopback only) for an app that builds
its own host.
