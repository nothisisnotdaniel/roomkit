# RoomKit

RoomKit is the shared core of a factory control app. ControlRoom is built on it, and another factory's app
(PlayRoom) can reference the packed library so that fixes to sign-in, push, updates and backlog editing reach both.
An app supplies its names (`RoomKitOptions`), its own tabs and endpoints, and one small `room.js`. RoomKit's
core does the rest. An app that shows a factory's backlogs and jobs also passes a `FactoryModule` with how its
factory keeps jobs (`IJobFormat`); an app without a factory (for example PRWatch) leaves it out.

## What is in it

- **Server:** `RoomKitApp` builds and runs the app. It does the `Host` check (only `127.0.0.1:<port>` and the
  tailnet host, plus `<tailnet host>:<TailnetPort>` when `--TailnetPort` is set), listens on loopback only, allows a method only on its route (`POST` only on the fixed routes),
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
  push sources before the host is built), `IPushSource` and `PushEvent` (an app's own push notifications) and
  `IPushBadgeSource` (the app badge count on every push, see App badge).
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
- `GET /api/update` always has a `lastCheck` key: the time of the updater's last check from its `last-check` stamp
  in the data folder next to the token file, as UTC `yyyy-MM-ddTHH:mm:ssZ` (for example `"2026-03-01T09:30:00Z"`),
  so an app can show whether the updater still runs. It is `null` when the stamp is missing, or is refused by the
  updater's private-file rules (a symlink, a directory, not a `0600` regular file of the current user in a `0700`
  folder, larger than 32 bytes), or does not hold only digits with at most one trailing line break, or lies beyond
  the date range or later than now. A bad stamp never fails the response, and the server never writes it.
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

### App badge

A push payload may carry `badge`, a whole number from 0 to 999, separate from `count` (the number of items an
`approval` notification names). When a push arrives, `sw.js` sets the app icon's badge to it with
`navigator.setAppBadge`, and `0` clears it with `navigator.clearAppBadge`; a missing `badge`, or any other value (a
negative number, more than 999, a fraction, a string, `true`, `null`, an object), leaves the badge as it is. The
notification is shown the same way with or without `badge`, also when the browser has no Badging API or the call
fails. iOS (16.4 and later) shows the badge only for a web app added to the Home Screen with notification permission.

While the app is open, a host script can call `setRoomKitBadge(count)` from `push.js`: 1 to 999 sets the badge, `0`
clears it, and any other value does nothing. It returns a promise that resolves to `true` when the Badging call was
made and settled, and to `false` otherwise (an invalid value, no Badging API, or a failed call); it never throws or
rejects. Loading `push.js` does not touch the badge.

To keep the badge current while the app is closed, a host sets one `IPushBadgeSource` in the `addServices` hook with
`services.SetPushBadge(source)` or `services.SetPushBadge(provider => …)` (built from the app's services). Every push
RoomKit's server sends then ends with `badge`: the factory module's pushes, an app's `PushEvent`s, the test push and
the update pushes. The push sender calls `Current` once per send, only when there is at least one subscription, and
sends the same body to every subscription. `0` is sent as `"badge":0`, a count above 999 as `999`, and `null` or a
negative count leaves `badge` out. When the source throws, the push is still sent, without `badge`, and only the
fixed text `Push badge failed; sent without a badge` is logged, never the exception's message. Without a badge
source the payloads are unchanged. A second `SetPushBadge` call throws `InvalidOperationException`; `null` throws
`ArgumentNullException`.

```csharp
return RoomKitApp.Run(args, MyRoomApp.Options, MyRoomApp.Map,
    addServices: services => services.SetPushBadge(provider => new OpenItemsBadge(provider)));
```

## Host hooks

With the factory module, `counts.js` declares the frozen `const roomKitHooks`, so a host app runs its own code at
two points without patching RoomKit's scripts or replacing its globals. `roomKitHooks.on(name, fn)` registers `fn`
for one of two names; any other name, or an `fn` that is not a function, throws a `TypeError` and registers nothing.
There is no way to remove a hook.

- `jobCounts`: after `loadJobCounts` has set new Jobs tab counts, `fn(jobs)` gets the board answer's `data.jobs`
  array, the one the counts were taken from. Not called when the fetch fails, the answer has no jobs array, or the
  answer is dropped for a newer request (and not for the counts `pollBoard` sets).
- `liveChecked`: after each successful live check while the board views are shown (`boardViewsVisible()`), after
  the changes are noted (`liveLast` holds the new answer) and before the shown views refresh,
  `fn({ jobsChanged, recovered, firstCheck })`: `jobsChanged` is true when the jobs fingerprint differs from the
  previous answer's, `recovered` when the check follows a lost connection, and `firstCheck` when there was no
  previous answer. Not called when the check fails or is skipped (hidden page, token view, a check still pending).

A name's hooks run in registration order, and each is awaited, so an async hook holds back the next hook and the
view refresh until it settles. A hook that throws or rejects is caught and written once with `console.error`; the
next hooks, the counts, `loadJobCounts`'s result and the live checks go on as before. Load the host script that
registers hooks after `counts.js` (for example as a `defer` script after RoomKit's), and register at load time, so
the hooks are in place before the first check. An app that registers none behaves as before.

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

RoomKit has its own SemVer version, independent of the apps that use it. It lives in its own repository,
`nothisisnotdaniel/roomkit`. A release is a pull request from `develop` into `main` titled `release vX.Y.Z`: once it
is merged, the release workflow (`.github/workflows/release.yml`) runs the tests on macOS and Windows, packs
`RoomKit.X.Y.Z.nupkg` with `dotnet pack src/RoomKit -c Release -p:Version=X.Y.Z`, writes `SHA256SUMS`, attests
the package's build provenance, creates the GitHub release `vX.Y.Z` with the package and `SHA256SUMS`, and pushes
the package to GitHub Packages. There are no `-develop` builds: a push to `develop` publishes nothing, and a
title with `-develop` is refused. A SemVer prerelease title such as `release v1.3.0-rc.1` is a GitHub prerelease
and is pushed to GitHub Packages like a release.

The package feed is GitHub Packages:

```
https://nuget.pkg.github.com/nothisisnotdaniel/index.json
```

Its NuGet feed needs a token for every restore, even for public packages. The one-time setup for a consumer:

1. Create a classic personal access token with only the `read:packages` scope.
2. Store it outside every repository, in one of two ways:
   - as a user-level package source, in the user's own NuGet config (not the app's `nuget.config`):

     ```sh
     read -rs ROOMKIT_TOKEN   # paste the token; it is not echoed and stays out of the shell history
     dotnet nuget add source https://nuget.pkg.github.com/nothisisnotdaniel/index.json --name github \
       --username <github-user> --password "$ROOMKIT_TOKEN" --store-password-in-clear-text
     unset ROOMKIT_TOKEN
     ```

     On macOS and Linux NuGet cannot encrypt the password, so it refuses to store it without
     `--store-password-in-clear-text`, and the token then sits in plain text in `~/.nuget/NuGet/NuGet.Config`.
     The environment variable below keeps it out of every file.

   - or as the environment variable `NuGetPackageSourceCredentials_github`, with the value
     `Username=<github-user>;Password=<token>`, for a source named `github` in the app's `nuget.config`
     (which then holds only the URL, never a credential).
3. Never commit the token or a `nuget.config` that holds it.

A consumer's CI needs no stored secret: once the owner has granted that repository access to the package (the
package's settings, "Manage Actions access"), its workflow uses `permissions: packages: read` and passes the run's
`GITHUB_TOKEN` as the password, for example through `NuGetPackageSourceCredentials_github`.

A local build works too: `dotnet pack src/RoomKit -c Release -p:Version=X.Y.Z -o <folder>` makes the package in a
folder that an app can name as a package source while it tries out an unreleased change.

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
