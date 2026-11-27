# 013 Report the updater last check as lastCheck in GET /api/update

As the ControlRoom owner, I want GET /api/update to report when the updater last checked, so that ControlRoom can show whether the updater still runs.

Category: feature

## What

In UpdateEndpoint, read <support>/last-check through PrivateFolder on each GET /api/update with the update-file rules: no-follow, a regular 0600 file owned by the user, at most 32 bytes, digits plus at most one line break, not in the future by the TimeProvider. Never write or delete it. Add lastCheck to UpdateResponse as UTC YYYY-MM-DDTHH:MM:SSZ, or null when the stamp is missing, refused or invalid; a bad stamp never turns the response into an error. Test valid, missing, symlinked, loose-mode, oversized, non-digit and future stamps. Document it, then release the next minor version.

## Why

ControlRoom #163 (job 477) wants to show Last checked under the update interval, but UpdateEndpoint, UpdateResponse and PrivateFolder are internal in RoomKit 0.2.0, so ControlRoom cannot add the field itself without copying the private-file security checks.
