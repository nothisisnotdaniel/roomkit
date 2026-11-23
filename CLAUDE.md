# RoomKit — conventions

Every factory agent reads this file before working on RoomKit. Keep it short and accurate.

- **What it is:** one or two sentences on what this project does and who it is for.
- **Stack:** none yet. The first factory job picks the stack, and its builder fills in this line.
- **Test command:** none yet.
- **Layout:** where the source code, the tests and the entry points live.
- **Style:** naming, formatting, libraries to prefer or avoid, and anything reviewers should enforce.
- **Git:** `develop` is the working branch: factory work branches off it as `factory/<job-id>` and is merged back with `--no-ff`. `main` holds releases only and is pushed to only for a new release.
