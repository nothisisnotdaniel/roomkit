# RoomKit backlog

`/factory next --project RoomKit` runs the first open item, checks it off and records the job id.
Each item is a user story in its own file under `backlog/`, numbered `NNN-slug.md`; the line links it as `#N`.
The order of the lines is the priority. Add items with `python3 factory/tools/job.py add --project RoomKit '<json>'`
(or by hand: write the story file first, then the line), and keep a number once it is given.
Each story has one `Category: <name>` line after its story sentence, one of: feature, bug, security, ux, ui, tidy-up, test, ci, docs.
`- [?]` items are follow-ups the factory proposed: change `[?]` to `[ ]` to approve one, or delete the line and its file.
`- [h]` items are tasks for you. `(after: #3)` waits until item 3 is done; `(after: Times#5)` names another project's item.
- [x] [#1](backlog/001-move-roomkit-into-its-own-repository.md) Move RoomKit into its own repository with CI and a release workflow → 431-roomkit-own-repo.fba5df193b59469fbd65fb747d088dcf
- [x] [#2](backlog/002-release-roomkit-v1-0-0-and.md) Release RoomKit v1.0.0 and give ControlRoom and PlayRoom read access to the package (after: #1)
- [x] [#3](backlog/003-keep-the-github-packages-token-off.md) Keep the GitHub Packages token off the nuget push command line → 434-nuget-token-off-cmdline.a2c01aa145d94d0b8d1c7dc4cce77c33
- [x] [#4](backlog/004-bring-the-accesstoken-and-projectgit-source.md) Bring the AccessToken and ProjectGit source-scan tests over from ControlRoom → 440-source-scan-tests.9982c43b979e4092badf81796391071e
- [x] [#5](backlog/005-let-host-apps-hook-into-job.md) Let host apps hook into job-count loads and live change checks → 441-host-hook-registry.788d4a25e68b491789f7bf277df98fe8
- [h] [#6](backlog/006-release-roomkit-0-2-0-with.md) Release RoomKit 0.2.0 with the host hooks (after: #5)
- [ ] [#9](backlog/009-mark-the-push-login-check-tests.md) Mark the push login check tests as skipped on Windows
- [ ] [#7](backlog/007-confirm-the-first-release-push-to.md) Confirm the first release push to GitHub Packages works without --api-key
- [ ] [#8](backlog/008-narrow-the-jq-error-output-of.md) Narrow the jq error output of the GitHub Packages login check
