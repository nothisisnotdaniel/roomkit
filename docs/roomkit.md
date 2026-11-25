# RoomKit

RoomKit is the shared core of a factory control app. ControlRoom is built on it, and another factory's app
(PlayRoom) can reference the packed library so that fixes to sign-in, push, updates and backlog editing reach both.
An app supplies its names (`RoomKitOptions`), its own tabs and endpoints, and one small `room.js`. RoomKit's
core does the rest. An app that shows a factory's backlogs and jobs also passes a `FactoryModule` with how its
factory keeps jobs (`IJobFormat`); an app without a factory (for example PRWatch) leaves it out.

## What is in it

- **Server:** `RoomKitApp` builds and runs the app. It does the `Host` check (only `127.0.0.1:<port>` and the
  tailnet host), listens on loopback only, allows a method only on its route (`POST` only on the fixed routes),
  puts the access token gate on every `/api/` path, sets the security headers and handles the `version` and
  `token` commands.
- **Core APIs:** push (subscribe, test, the push watcher and its sources), the update check and its settings,
  `/health` and `/api/version` (see Core).
- **Factory module APIs:** backlogs and backlog editing (`/api/backlogs`, `/api/backlog/change`, worktree
  edits while a job runs), stories, search, history and token totals, the Health view (`/api/doctor`), inbox,
  tasks and the job push source (see Factory module).
- **Web app shell:** `app.css`, the service worker `sw.js`, `token.js` and `push.js`; with the factory module also
  `edit.js`, `counts.js`, `search.js`, `capture.js`, `history.js`, `doctor.js` and `live.js`.
- **Scripts:** `scripts/launch-agent.sh` and `scripts/updater.sh` install and update macOS launch agents.
  `scripts/launch-agent.ps1` and `scripts/updater.ps1` install and update per-user Windows tasks.

The public types are few. Most of the code is `internal`. The public ones are:

- The plug-in API: `RoomKitApp`, `RoomKitOptions`, `RoomRoutes`, `RoomServices` (the hook that adds services and
  push sources before the host is built), `IPushSource` and `PushEvent` (an app's own push notifications).
- The factory module: `FactoryModule`, `IJobFormat`, `RoomJob`, `RoomJobFolder` and `FactoryRoot` (the factory root
  an endpoint gets from the services).
- Backlog reading and editing that an app's own endpoints use: `Backlog`, `BacklogEntry`, `BacklogDependency`,
  `BacklogFile`, `BacklogReader`, `BacklogSource`, `BacklogEditor`, `BacklogEdit`, `AddPosition`,
  `MoveDirection`, `EditableText`, `BacklogWorktree`, `WorktreeState`, `ProjectBacklog`, `BacklogsEndpoint`,
  `BacklogsResponse`, `PlanItem`, `PlanDependency` and `BlockedItem`.
- The factory's own backlog in its develop worktree: `FactoryBacklog` (the folder from `FACTORY_DEVELOP_DIR`, else
  `<root>-factory-develop`, checked against `git worktree list`) and `FactorySource` (where it is read, or why not).
- Stories: `StoryMarkdown`, `StoryBlock` and `StoryInline`.
- Projects and git: `ProjectFinder`, `ProjectFolder`, `ProjectGit` and `GitResult`.
- Changes: `ChangeFingerprint`, `ChangesResponse` and `ProjectChanges`.
- Other machines' item claims (#409): `ClaimWatcher` (a hosted service of the factory module that reads
  `refs/factory/claims/*` from each repo's `origin` every 60 s, read-only), `ClaimState` and `ItemClaim`.
- Safe file access: `SafeFileReader`, `SafeFileWriter`, `NoFollowStat`, `PathStat` and `PathKind`.

## Core

Every RoomKit app gets the core, with no factory root, job format or git:

- Kestrel on loopback only, the security headers (CSP, `nosniff`), the `Host` check (`421 {"error":"bad_host"}`),
  the method rule (`POST` only on `/api/update/settings`, the four `/api/push/*` routes and the app's own
  `RoomRoutes.MapPost` routes) and the `/api/` token gate (`401`), in that order on every request.
- The static files `app.css`, `sw.js`, `token.js` and `push.js`, and the app's own `wwwroot`.
- The `version` and `token` commands, `/health`, `/api/version`, the update check with `/api/update` and
  `/api/update/settings`, and push (`/api/push`, subscribe, unsubscribe, settings, test).
- The push watcher: every 30 s it asks each registered `IPushSource` for new `PushEvent`s and sends each one once
  per subscription through RoomKit's push sender, and it always sends the updater's result (`updated`,
  `update_failed`, with `notifyUpdate` on). A source that throws loses that scan's events; only a fixed text is
  logged, never its message, and the other sources still send.

An app registers a source in the `addServices` hook of `RoomKitApp.Run` or `RoomKitApp.Create`:

```csharp
return RoomKitApp.Run(args, MyRoomApp.Options, MyRoomApp.Map,
    addServices: services => services.AddPushSource(provider => new ReviewSource()));
```

A `PushEvent` has a kind (lower-case, 1 to 40 characters, not one of RoomKit's own kinds), a title and a tag (one
line, at most 120 characters each); the device gets exactly `{"kind","title","tag"}`. The shipped `sw.js` shows the
title from `PushAppTitles` by kind, else `PushFallbackTitle`.

Without a `FactoryModule` there are no backlog, story, search, inbox, tasks, history or doctor endpoints (`404`,
and `POST /api/backlog/change` is `405`), `edit.js`, `counts.js`, `search.js`, `capture.js`, `history.js`,
`doctor.js` and `live.js` are not served (`404`), no `worktrees` folder is made and `CaptureNote` is not needed.

## Factory module

`new FactoryModule(jobs)` checks the job format (a jobs folder that is not a plain relative path throws
`ArgumentException`) and turns on the factory features when it is passed to `RoomKitApp.Run` or
`RoomKitApp.Create`:

- The factory root: `--FactoryRoot <path>`, else the parent of the folder holding `SolutionFileName`; without one
  the app stops with a fixed text.
- The services `FactoryRoot`, `FactoryBacklog`, `BacklogWorktree` (the `worktrees` folder next to the token file)
  and the backlog write lock.
- The endpoints `/api/backlogs`, `/api/backlog/change` (`POST`), `/api/story`, `/api/search`, `/api/inbox`,
  `/api/tasks`, `/api/history` and `/api/doctor`.
- The web files `edit.js`, `counts.js`, `search.js`, `capture.js`, `history.js`, `doctor.js` and `live.js`.
- The job and backlog push source: a job moving into a blocked stage, into the done stage (with `notifyDone`) and
  a new `[?]` backlog line.

The module needs `CaptureNote` (one line; missing or multi-line throws `ArgumentException`).

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
| `CaptureNote` | `Draft from ControlRoom` | only with the factory module: the dated note on a captured draft story |
| `InformationalVersion` | the app assembly's | `/health`, `/api/version` and the `version` command (`dev` when empty); RoomKit's own version is never used |

## Job format

`IJobFormat` (factory module only) says how the factory keeps its jobs. The Health view, history, the push watcher and the change
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

- `TokenKey`: the localStorage key of the token. Changing it signs out every device.
- `CaptureProjectKey` and `HistoryProjectKey`: needed only with the factory module, the localStorage keys of the
  Capture and History views.
- `PushAppTexts` with `homeScreen` and `denied`: merged into the push texts.
- `CacheVersion`, `CacheName` and `Shell`: the service worker's cache and the files it installs (list
  `/room.js` and every app file).
- `PushAppTitles` with `updated`, `PushFallbackTitle`, `PushDefaultTag` and `PushUpdatePrefix`: the service
  worker's notification texts.

`index.html` loads `<script src="/room.js" defer></script>` as its first script, and `sw.js` starts with
`importScripts("/room.js");`. The service worker loads shell files network-first (`fetch` with `cache: "no-cache"`;
the cached copy only when the network fails), so a changed file reaches the app on its next load. Install
pre-caches the shell past the HTTP cache and calls `skipWaiting()`; activate deletes every other cache and calls
`clients.claim()`. Raise `CacheVersion` whenever a shell file changes, so old caches are dropped; ControlRoom's
`ShellCacheLedger` test fails the build when the shell changed without it.

## Scripts

`scripts/launch-agent.sh` and `scripts/updater.sh` ship in the package under `scripts/`. Copy both into the app's
`scripts/` folder and change only the block between `# roomkit:app-begin` and `# roomkit:app-end`. It holds only
variable assignments: `APP_NAME`, `LABEL`, `UPDATER_LABEL`, `SUPPORT_FOLDER`, `LOG_FOLDER`, `REPO_SLUG`,
`RELEASES`, `APP_PORT`, `TARBALL_PREFIX`, `SERVER_NAME` and `SOLUTION_FILE`. Everything outside the block stays
byte-identical to RoomKit's copy, so a newer RoomKit's scripts can be taken over by keeping the block.

Windows apps copy `scripts/launch-agent.ps1` and `scripts/updater.ps1` from the same package. Keep them together. The installer uses the updater's verification, task and swap functions. Set the assignments between `# roomkit:app-begin` and `# roomkit:app-end` in the updater to the app's data folder, task prefix, executable, ZIP prefix, port, GitHub repository and relative job paths. Job files carry a JSON `stage` field. Each account gets its own tasks and data under `%LOCALAPPDATA%`.

Publish the single-file executable, `wwwroot` and both scripts at the root of `<prefix>-<version>-win-x64.zip`. Add one SHA-256 entry for that filename to the release's `SHA256SUMS`. The installer accepts `install -FactoryRoot <path> [-Release <tag>] [-TailnetHost <host>]`, `status` and `uninstall`. A running update checks the scheduled server's PID, listener and health version before advancing the release marker.

The Windows updater reads up to ten pages of 100 releases, with a 1 MiB limit per page. An incomplete release scan or an unreadable job state defers installation. Before stopping the server, the updater saves the previous and target versions in its private staging folder. Its scheduled task can use a recovery copy while the server directory is being replaced. If a swap is interrupted or the result file is held open, recovery resumes on the next enabled, due invocation with the update lock and no active job. Uninstall removes the installed code, recovery copy and staging folders and preserves the token and settings.

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
2. Write the options (and, for a factory app, the job format):

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

3. `Program.cs`: `return RoomKitApp.Run(args, MyRoomApp.Options, MyRoomApp.Map, new FactoryModule(MyRoomApp.Jobs));`
   for a factory app, or `return RoomKitApp.Run(args, MyRoomApp.Options, MyRoomApp.Map);` for the core only
   (then leave out `CaptureNote` and `Jobs`).
4. Add `wwwroot/index.html`, `manifest.webmanifest`, the icons, the app's scripts and `room.js` (see Web assets
   and tabs).
5. Copy the scripts for each supported platform and set the app assignments described under Scripts.

`RoomRoutes.MapPost` takes only a fixed path under `/api/`, so the token gate always covers an app's `POST` route.
A `GET` route goes through `RoomRoutes.App`. Every request has already passed the `Host` check, the method rule
and the token gate. `RoomKitApp.ConfigureKestrel` is the listening setup (loopback only) for an app that builds
its own host.
