#!/bin/sh
# The app's updater: run by the LaunchAgent UPDATER_LABEL (at load, so about a minute after
# install or login, and then every 15 min) as
#   /bin/sh <support>/updater.sh --factory-root <root> [--start-delay <s>]
# where <support> is ~/Library/Application Support/SUPPORT_FOLDER and <s> (1-600) is how many seconds it first waits
# (sleep, before it reads anything; without sleep on PATH it goes on at once). Then it decides whether the run is due:
# the first run since the agent loaded (launchctl print shows "runs = 1"; a failed print counts as due), a missing or
# broken <support>/last-check stamp (not a private 0600 file of this user holding epoch seconds plus one line
# break, or in the future), or at least the interval chosen in the app (settings.json "updateInterval" in minutes,
# 30 when missing, refused or not one of 30, 60, 120, 360, 720) minus 60 s since the stamp. A run that is not due
# ends at once, with no log line, lock or network call. A due run writes the current time to last-check and goes on.
# When the user turned on "Install updates automatically" (settings.json is exactly {"autoUpdate":true},
# {"autoUpdate":true,"channel":"stable"|"develop"} or {"autoUpdate":true,"channel":"stable"|"develop","updateInterval":N}),
# the install is a release (installed-release holds its tag) and no factory job is running, it installs a newer
# GitHub release (on the develop channel also a -develop prerelease) with the same download and checks as
# `launch-agent.sh install --release`, swaps it in, confirms it through the public GET /health route (counted only
# while launchd runs the server and its pid is the one listening on the port) and rolls back when it doesn't
# answer or the marker can't be updated. With the server it also installs the release's own updater.sh (as
# <support>/updater.sh) and rewrites the updater plist, keeping the old ones as updater.sh.previous and
# updater.plist.previous, which a rollback puts back; it never reloads or runs the updater, so both count from the
# next run or load ("<UTC> updater_kept missing|bad|write_failed" before the final line when they are kept as they
# were). After every bootout it waits until launchd has removed the service, and a
# failed bootstrap is tried again a few times (one that never succeeds is bootstrap_failed). It writes
# update-result.json for the server's push and prints one final line, "<UTC ISO-8601> <word>[ <tags>]", which
# launchd appends to ~/Library/Logs/LOG_FOLDER/updater.log. The words:
# off, not_release, busy, job_running, current, latest_failed, download_failed, check_failed, skipped_failed,
# updated, rolled_back, bootstrap_failed, swap_failed, rollback_failed.
# While a factory job runs, the run logs "<UTC> waiting <installed>", lets go of its lock, waits 15 min and checks
# again (setting, release, lock, jobs), at most RETRY_WAITS times (5 h 45 min); then it ends job_running. Ticks
# while it waits are not due (or find the lock) and end at once or busy. A waiting run holds no lock, so launch-agent.sh install|uninstall never wait for
# it, and their bootout ends it.
# POSIX sh. Runs only launchctl, curl, lsof, tar, shasum and (once <support>/signing-cert.sha1 pins the signing
# certificate) codesign --verify (plus standard tools), all with fixed arguments.
# Never reads, copies or sends the access token. Nothing from the download runs here.

set -u
# The app's values, the only lines that differ between RoomKit's copy of this script and an app's copy
# (roomkit:app block: variable assignments only).
# roomkit:app-begin
APP_NAME="MyRoom"
LABEL="local.myroom.server"
UPDATER_LABEL="local.myroom.updater"
SUPPORT_FOLDER="MyRoom"
LOG_FOLDER="MyRoom"
REPO_SLUG="example/myroom"
RELEASES="https://github.com/example/myroom/releases"
APP_PORT=8200
TARBALL_PREFIX="myroom"
SERVER_NAME="MyRoom.Server"
SOLUTION_FILE="MyRoom.slnx"
# roomkit:app-end
umask 077
# Only the one log line reaches the log (fd 3); every other output is dropped.
exec 3>&1 1>/dev/null 2>/dev/null

# The updater's own LaunchAgent, with the same values as launch-agent.sh (updater_plist prints it).
UPDATER_INTERVAL=900
UPDATER_START_DELAY=60
FIXED_PATH="/usr/local/share/dotnet:/opt/homebrew/bin:/usr/local/bin:/usr/bin:/bin:/usr/sbin:/sbin"
# A release's updater.sh is installed only when it is at most this long (the script is about 40 KiB).
MAX_UPDATER_BYTES=262144
# RELEASES (in the roomkit:app block) is the one repository releases come from (the same as launch-agent.sh).
# The develop channel's one release list (newest first; the API never shows drafts without a token).
LIST_URL="https://api.github.com/repos/$REPO_SLUG/releases?per_page=100"
MAX_LIST_BYTES=1048576
# settings.json is at most this long: the longest form the server writes, with its line break, is 62 bytes.
MAX_SETTINGS_BYTES=64
# The update interval (minutes) when settings.json has none, is refused or holds another value.
DEFAULT_INTERVAL_MINUTES=30
# A run counts as due this many seconds before the interval has fully passed (launchd ticks drift a little).
DUE_SLACK_SECONDS=60
# last-check is at most this long: 15 digits (far beyond any real epoch, and safe in sh arithmetic) plus its line
# break.
MAX_STAMP_BYTES=16
# The texts the shared functions below fail with are the log words here.
LATEST_FAILED=latest_failed
DOWNLOAD_FAILED=download_failed
RELEASE_BAD=check_failed
# The same size caps as launch-agent.sh --release.
MAX_TARBALL_BYTES=209715200
MAX_SUMS_BYTES=4096
MAX_UNPACKED_BYTES=536870912
MAX_ENTRIES=10000
# The public, token-free route the swapped-in server must answer on: 15 tries, 2 s apart, 2 s each.
HEALTH_URL="http://127.0.0.1:$APP_PORT/health"
HEALTH_TRIES=15
# After a bootout, launchctl print is polled until the service is gone: at most this many polls, 0.5 s apart (15 s).
WAIT_POLLS=30
# A failed bootstrap is tried again this many times, 1 s apart (launchd may still be removing the old service).
BOOTSTRAP_RETRIES=5
# A lock older than this (minutes) is left over from a run that died, and is removed.
LOCK_MINUTES=60
# launchctl print indents the service's own fields with one tab (nested ones with more).
TAB=$(printf '\t')
# A job's state.json larger than this counts as a running job (it is never read).
MAX_STATE_BYTES=65536
# The fingerprint copy is 40 hex characters plus one line break.
MAX_FINGERPRINT_BYTES=41
# While a job runs: wait this many seconds, at most RETRY_WAITS times (23 x 15 min = 5 h 45 min).
RETRY_SECONDS=900
RETRY_WAITS=23

INSTALLED=""
TAG=""
WORK=""
LOCKED=no
# The pid of a running wait (sleep), killed when the run ends.
SLEEP_PID=""
# The pinned signing certificate; empty means the signature check is off. Set only from the support folder's
# signing-cert.sha1 (written by launch-agent.sh install --release), never from the environment.
SIGNING_CERT_SHA1=""
# The release's own updater: empty while it is used, else why it was kept out (missing, bad, write_failed), logged as
# "<UTC> updater_kept <why>" just before the final line.
UPDATER_KEPT=""
# Its staged copies (temp files in the support folder, removed on every exit).
STAGED_SCRIPT=""
STAGED_PLIST=""
COPY_TEMP=""
# yes once this run renamed the release's copy over the installed updater script or plist (a rollback puts it back).
SCRIPT_SWAPPED=no
PLIST_SWAPPED=no

# Prints the one log line: the time, the word, then the installed and the latest tag once they are known and
# checked. Only v+SemVer tags ever follow the word.
log_line() {
    details=""
    if [ -n "$INSTALLED" ]; then
        details=" $INSTALLED"
    fi
    if [ -n "$TAG" ]; then
        details="$details $TAG"
    fi
    printf '%s %s%s\n' "$(date -u +%Y-%m-%dT%H:%M:%SZ)" "$1" "$details" >&3
}

# Logs the word and ends the run (the EXIT trap removes the work folder and the lock).
finish() {
    if [ -n "$UPDATER_KEPT" ]; then
        printf '%s updater_kept %s\n' "$(date -u +%Y-%m-%dT%H:%M:%SZ)" "$UPDATER_KEPT" >&3
    fi
    log_line "$1"
    exit 0
}

# What the shared functions call on a failed fetch or check: a failed check is also recorded as a result.
fail() {
    if [ "$1" = "$RELEASE_BAD" ]; then
        write_result update_failed check
    fi
    finish "$1"
}

cleanup() {
    if [ -n "$SLEEP_PID" ]; then
        kill "$SLEEP_PID"
    fi
    if [ -n "$WORK" ]; then
        rm -rf "$WORK"
    fi
    for staged in "$STAGED_SCRIPT" "$STAGED_PLIST" "$COPY_TEMP"; do
        if [ -n "$staged" ]; then
            rm -f "$staged"
        fi
    done
    if [ "$LOCKED" = yes ]; then
        rmdir "$LOCK"
    fi
}

# True for a regular file (not a symlink) with mode exactly 0600, owned by this user, of at most $2 bytes.
private_file() {
    if [ ! -f "$1" ] || [ -L "$1" ]; then
        return 1
    fi
    [ -n "$(find "$1" -prune -type f -perm 600 -user "$USER_ID")" ] || return 1
    size=$(wc -c <"$1") || return 1
    [ $((size + 0)) -le "$2" ]
}

# True for a folder (not a symlink) with mode exactly 0700, owned by this user.
private_folder() {
    if [ ! -d "$1" ] || [ -L "$1" ]; then
        return 1
    fi
    [ -n "$(find "$1" -prune -type d -perm 700 -user "$USER_ID")" ]
}

# Finds a program on PATH as an absolute path, or fails.
find_program() {
    found=$(command -v "$1") || return 1
    case $found in
        /*) printf '%s' "$found" ;;
        *) return 1 ;;
    esac
}

# Writes $2 into the support file $1 atomically: a 0600 temp file in the support folder, then rename.
write_private() {
    if [ -d "$1" ]; then
        return 1
    fi
    temp=$(mktemp "$SUPPORT/.updater.XXXXXX") || return 1
    if printf '%s\n' "$2" >"$temp" && chmod 600 "$temp" && mv -f "$temp" "$1"; then
        return 0
    fi
    rm -f "$temp"
    return 1
}

# Writes update-result.json: $1 is updated or update_failed, $2 the failure code.
write_result() {
    run=$(date -u +%Y%m%dT%H%M%SZ)
    if [ "$1" = updated ]; then
        json="{\"run\":\"$run\",\"result\":\"updated\",\"version\":\"${TAG#v}\",\"previous\":\"${INSTALLED#v}\"}"
    else
        json="{\"run\":\"$run\",\"result\":\"update_failed\",\"version\":\"${TAG#v}\",\"reason\":\"$2\"}"
    fi
    write_private "$RESULT" "$json"
}

# True when no job is in an active stage. A missing jobs folder, or a state.json that is not a regular file, is
# larger than MAX_STATE_BYTES or can't be read, counts as a running job, so the updater never swaps the server
# under one.
jobs_idle() {
    if [ ! -d "$ROOT/factory/jobs" ]; then
        return 1
    fi
    for state in "$ROOT"/factory/jobs/*/state.json; do
        if [ ! -e "$state" ] && [ ! -L "$state" ]; then
            continue
        fi
        if [ ! -f "$state" ] || [ -L "$state" ]; then
            return 1
        fi
        size=$(wc -c <"$state") || return 1
        [ $((size + 0)) -le "$MAX_STATE_BYTES" ] || return 1
        LC_ALL=C grep -Eq '"stage"[[:space:]]*:[[:space:]]*"(spike|spec|build|review)"' "$state"
        case $? in
            1) ;;
            *) return 1 ;;
        esac
    done
    return 0
}

# Waits $1 seconds with the sleep found on PATH. It runs in the background so a TERM (launchctl bootout) ends the
# run at once through its trap instead of after the sleep.
pause() {
    "$SLEEP" "$1" &
    SLEEP_PID=$!
    wait "$SLEEP_PID"
    SLEEP_PID=""
}

# Reads the settings.json text $1 (without its trailing line break) by the server's rules and sets AUTO_UPDATE
# (true|false), CHANNEL (stable|develop) and INTERVAL_MINUTES (one of the five choices, else the default); fails for
# any other text.
parse_settings() {
    rest=$1
    case $rest in
        '{"autoUpdate":true'*)
            AUTO_UPDATE=true
            rest=${rest#'{"autoUpdate":true'}
            ;;
        '{"autoUpdate":false'*)
            AUTO_UPDATE=false
            rest=${rest#'{"autoUpdate":false'}
            ;;
        *) return 1 ;;
    esac
    CHANNEL=stable
    INTERVAL_MINUTES=$DEFAULT_INTERVAL_MINUTES
    case $rest in
        '}') return 0 ;;
        ',"channel":"stable"'*) rest=${rest#',"channel":"stable"'} ;;
        ',"channel":"develop"'*)
            CHANNEL=develop
            rest=${rest#',"channel":"develop"'}
            ;;
        *) return 1 ;;
    esac
    case $rest in
        '}') return 0 ;;
        ',"updateInterval":'*'}')
            number=${rest#',"updateInterval":'}
            number=${number%'}'}
            ;;
        *) return 1 ;;
    esac
    case $number in
        0 | [1-9] | [1-9][0-9] | [1-9][0-9][0-9] | [1-9][0-9][0-9][0-9] | [1-9][0-9][0-9][0-9][0-9] \
            | [1-9][0-9][0-9][0-9][0-9][0-9]) ;;
        *) return 1 ;;
    esac
    case $number in
        30 | 60 | 120 | 360 | 720) INTERVAL_MINUTES=$number ;;
    esac
    return 0
}

# Prints the update interval in minutes from settings.json; the default when the folder or file is refused, the
# file can't be read or its text is not one of the allowed forms.
settings_interval() {
    if ! private_folder "$SUPPORT" || ! private_file "$SETTINGS" "$MAX_SETTINGS_BYTES"; then
        printf '%s' "$DEFAULT_INTERVAL_MINUTES"
        return 0
    fi
    text=$(cat "$SETTINGS") || text=""
    size=$(wc -c <"$SETTINGS") || size=$((MAX_SETTINGS_BYTES + 1))
    if [ $((size + 0)) -le $((${#text} + 1)) ] && parse_settings "$text"; then
        printf '%s' "$INTERVAL_MINUTES"
    else
        printf '%s' "$DEFAULT_INTERVAL_MINUTES"
    fi
}

# True when this is the first run since launchd loaded the updater: launchctl print shows "runs = 1" at one tab. A
# missing launchctl, a failed print or no runs line counts as the first run.
first_run() {
    tool=$(find_program launchctl) || return 0
    printed=$("$tool" print "gui/$USER_ID/$UPDATER_LABEL") || return 0
    runs=$(printf '%s\n' "$printed" | LC_ALL=C sed -n "s/^${TAB}runs = \([0-9][0-9]*\)\$/\1/p")
    case $runs in
        "" | *[!0-9]*) return 0 ;;
    esac
    [ "$runs" = 1 ]
}

# Prints the epoch seconds in last-check: a private 0600 regular file of this user holding only digits and one line
# break. Fails for anything else.
stamp_seconds() {
    private_file "$STAMP" "$MAX_STAMP_BYTES" || return 1
    stamp=$(cat "$STAMP") || return 1
    case $stamp in
        "" | *[!0-9]*) return 1 ;;
    esac
    stamp_size=$(wc -c <"$STAMP") || return 1
    [ $((stamp_size + 0)) -eq $((${#stamp} + 1)) ] || return 1
    printf '%s' "$stamp"
}

# True when this run should check: the first run since load, no valid stamp, a stamp in the future, or at least the
# chosen interval minus DUE_SLACK_SECONDS since the stamp. $1 is the current epoch seconds.
run_due() {
    first_run && return 0
    last=$(stamp_seconds) || return 0
    [ "$last" -le "$1" ] || return 0
    interval=$(settings_interval)
    [ $(($1 - last)) -ge $((interval * 60 - DUE_SLACK_SECONDS)) ]
}

# Prints the version of the last update_failed result with reason check or no_answer, if any.
last_skipped_version() {
    private_file "$RESULT" 4096 || return 1
    LC_ALL=C sed -En \
        's/^[{]"run":"[0-9]{8}T[0-9]{6}Z","result":"update_failed","version":"([0-9A-Za-z.-]+)","reason":"(check|no_answer)"[}]$/\1/p' \
        "$RESULT"
}

# True when launchd runs the server (top-level "state = running" in launchctl print) and its pid is the only
# process listening on APP_PORT that lsof shows, so an answer from another local process that took the port
# after the bootout is never counted. Fails closed on anything else.
server_owns_port() {
    printed=$("$LAUNCHCTL" print "$SERVICE") || return 1
    printf '%s\n' "$printed" | LC_ALL=C grep -qx "${TAB}state = running" || return 1
    pid=$(printf '%s\n' "$printed" | LC_ALL=C sed -n "s/^${TAB}pid = \([1-9][0-9]*\)\$/\1/p")
    case $pid in
        "" | *[!0-9]*) return 1 ;;
    esac
    listeners=$("$LSOF" -nP -iTCP:"$APP_PORT" -sTCP:LISTEN -t) || return 1
    [ "$listeners" = "$pid" ]
}

# True when GET /health answers exactly {"version":"$1"} from the launchd server within HEALTH_TRIES tries. No
# Authorization header and no curl config file (-q): the route is public.
health_is() {
    tries=0
    while :; do
        tries=$((tries + 1))
        body=$("$CURL" -q -fsS --max-time 2 --noproxy '*' "$HEALTH_URL" && printf x) && body=${body%x} \
            && [ "$body" = "{\"version\":\"$1\"}" ] && server_owns_port && return 0
        if [ "$tries" -ge "$HEALTH_TRIES" ]; then
            return 1
        fi
        sleep 2
    done
}

# Polls launchctl print until it no longer shows the service (exits non-zero), 0.5 s after each poll that still
# shows it, at most WAIT_POLLS polls; then goes on anyway.
wait_gone() {
    polls=0
    while [ "$polls" -lt "$WAIT_POLLS" ]; do
        polls=$((polls + 1))
        "$LAUNCHCTL" print "$SERVICE" || return 0
        sleep 0.5
    done
    return 0
}

# Boots the server out and waits until launchd has removed it.
bootout_server() {
    "$LAUNCHCTL" bootout "$SERVICE"
    wait_gone
}

# Bootstraps the server plist, trying again 1 s later up to BOOTSTRAP_RETRIES times; returns the last status.
bootstrap_server() {
    attempts=0
    while :; do
        "$LAUNCHCTL" bootstrap "$DOMAIN" "$PLIST" && return 0
        boot_status=$?
        attempts=$((attempts + 1))
        if [ "$attempts" -gt "$BOOTSTRAP_RETRIES" ]; then
            return "$boot_status"
        fi
        sleep 1
    done
}

# The old server could not be brought back, or does not answer.
end_rollback_failed() {
    write_result update_failed rollback_failed
    finish rollback_failed
}

# A swap rename failed: $1 is yes when server was already moved to server.previous (it is moved back).
end_swap_failed() {
    if [ "$1" = yes ]; then
        if [ -e "$SERVER" ] || [ -L "$SERVER" ] || ! mv "$PREVIOUS" "$SERVER"; then
            bootstrap_server
            end_rollback_failed
        fi
    fi
    bootstrap_server || end_rollback_failed
    if health_is "${INSTALLED#v}"; then
        write_result update_failed swap
        finish swap_failed
    fi
    end_rollback_failed
}

# Stages the release's updater.sh and the updater plist before the bootout: the script only when it is a regular file
# (not a symlink) of at most MAX_UPDATER_BYTES whose first line is exactly #!/bin/sh, and only when ROOT and HOME
# can go into the plist. Sets UPDATER_KEPT (missing, bad, write_failed) when it is not used. Nothing is run.
stage_updater() {
    source_script="$WORK/out/$TOP/updater.sh"
    if [ ! -e "$source_script" ] && [ ! -L "$source_script" ]; then
        UPDATER_KEPT=missing
        return
    fi
    if [ ! -f "$source_script" ] || [ -L "$source_script" ]; then
        UPDATER_KEPT=bad
        return
    fi
    script_size=$(wc -c <"$source_script") || {
        UPDATER_KEPT=bad
        return
    }
    first_line=$(head -n 1 "$source_script") || {
        UPDATER_KEPT=bad
        return
    }
    if [ $((script_size + 0)) -gt "$MAX_UPDATER_BYTES" ] || [ "$first_line" != '#!/bin/sh' ]; then
        UPDATER_KEPT=bad
        return
    fi
    case $ROOT$HOME in
        *[[:cntrl:]]*)
            UPDATER_KEPT=bad
            return
            ;;
    esac
    STAGED_SCRIPT=$(mktemp "$SUPPORT/.updater.XXXXXX") || {
        STAGED_SCRIPT=""
        UPDATER_KEPT=write_failed
        return
    }
    STAGED_PLIST=$(mktemp "$SUPPORT/.updater.XXXXXX") || {
        STAGED_PLIST=""
        UPDATER_KEPT=write_failed
        return
    }
    if ! cat "$source_script" >"$STAGED_SCRIPT" || ! chmod 700 "$STAGED_SCRIPT" \
        || ! updater_plist >"$STAGED_PLIST" || ! chmod 644 "$STAGED_PLIST"; then
        UPDATER_KEPT=write_failed
    fi
}

# Copies the installed file $1 to $2 with mode $3 through a temp file in the support folder, then rename.
copy_previous() {
    if [ ! -f "$1" ] || [ -L "$1" ] || [ -d "$2" ]; then
        return 1
    fi
    COPY_TEMP=$(mktemp "$SUPPORT/.updater.XXXXXX") || {
        COPY_TEMP=""
        return 1
    }
    cat "$1" >"$COPY_TEMP" && chmod "$3" "$COPY_TEMP" && mv -f "$COPY_TEMP" "$2" || return 1
    COPY_TEMP=""
}

# Puts back what this run renamed over the installed updater script and plist, from their .previous copies.
# Failures are ignored: the caller ends with its own word.
restore_updater() {
    if [ "$SCRIPT_SWAPPED" = yes ] && mv -f "$UPDATER_SCRIPT_PREVIOUS" "$UPDATER_SCRIPT"; then
        SCRIPT_SWAPPED=no
    fi
    if [ "$PLIST_SWAPPED" = yes ] && mv -f "$UPDATER_PLIST_PREVIOUS" "$UPDATER_PLIST"; then
        PLIST_SWAPPED=no
    fi
}

# Installs the staged updater script and plist (the new server folder is in place, not yet bootstrapped): the
# installed ones are first copied to updater.sh.previous and updater.plist.previous, then the staged files are
# renamed over them. Every write is a rename. On a failure what was renamed is put back (write_failed). The updater's
# own service is never touched: the new plist counts from its next load, the new script from its next run.
install_updater_files() {
    if [ -n "$UPDATER_KEPT" ]; then
        return
    fi
    if [ -L "$UPDATER_SCRIPT_PREVIOUS" ] || [ -L "$UPDATER_PLIST_PREVIOUS" ] \
        || ! copy_previous "$UPDATER_SCRIPT" "$UPDATER_SCRIPT_PREVIOUS" 700 \
        || ! copy_previous "$UPDATER_PLIST" "$UPDATER_PLIST_PREVIOUS" 644; then
        UPDATER_KEPT=write_failed
        return
    fi
    if ! mv -f "$STAGED_SCRIPT" "$UPDATER_SCRIPT"; then
        UPDATER_KEPT=write_failed
        return
    fi
    STAGED_SCRIPT=""
    SCRIPT_SWAPPED=yes
    if ! mv -f "$STAGED_PLIST" "$UPDATER_PLIST"; then
        UPDATER_KEPT=write_failed
        restore_updater
        return
    fi
    STAGED_PLIST=""
    PLIST_SWAPPED=yes
}

# Swaps the checked release in, confirms it and rolls back when it does not answer.
swap() {
    # From here on a signal must not leave the server half swapped.
    trap '' HUP INT TERM
    stage_updater
    bootout_server
    rm -rf "$PREVIOUS"
    if [ -e "$PREVIOUS" ] || [ -L "$PREVIOUS" ] || ! mv "$SERVER" "$PREVIOUS"; then
        end_swap_failed no
    fi
    if ! mv "$WORK/out/$TOP" "$SERVER"; then
        end_swap_failed yes
    fi
    install_updater_files
    # A new server that launchd never starts is put back without polling /health.
    bootstrap_server || roll_back bootstrap_failed bootstrap_failed
    if health_is "${TAG#v}"; then
        # The marker must name the running release, else the next run installs it again: when it can't be
        # written, the old version (which the marker still names) is put back as for a failed swap.
        if write_private "$MARKER" "$TAG"; then
            write_result updated
            finish updated
        fi
        roll_back swap_failed swap
    fi
    roll_back rolled_back no_answer
}

# Puts server.previous back in place of the new server, then the updater script and plist this run replaced, and
# confirms the old version; logs $1 and writes result reason $2, or rollback_failed when the rename fails or the old
# version does not answer.
roll_back() {
    bootout_server
    rm -rf "$SERVER"
    if [ -e "$SERVER" ] || [ -L "$SERVER" ] || ! mv "$PREVIOUS" "$SERVER"; then
        restore_updater
        bootstrap_server
        end_rollback_failed
    fi
    restore_updater
    bootstrap_server || end_rollback_failed
    if health_is "${INSTALLED#v}"; then
        write_result update_failed "$2"
        finish "$1"
    fi
    end_rollback_failed
}

# --- Shared with launch-agent.sh (kept byte-identical; UpdaterScriptTests compares them) -------------------

valid_tag() {
    case $1 in
        v*) ;;
        *) return 1 ;;
    esac
    case $1 in
        *[!0-9A-Za-z.-]*) return 1 ;;
    esac
    printf '%s\n' "${1#v}" \
        | LC_ALL=C grep -Eq '^(0|[1-9][0-9]*)\.(0|[1-9][0-9]*)\.(0|[1-9][0-9]*)(-(0|[1-9][0-9]*|[0-9A-Za-z-]*[A-Za-z-][0-9A-Za-z-]*)(\.(0|[1-9][0-9]*|[0-9A-Za-z-]*[A-Za-z-][0-9A-Za-z-]*))*)?$'
}

latest_tag() {
    # The trailing x keeps a trailing line break in the answer, so only the exact URL passes.
    answer=$("$CURL" --proto =https --tlsv1.2 -fsS --max-time 30 -o /dev/null -w '%{redirect_url}' \
        "$RELEASES/latest" && printf x) || fail "$LATEST_FAILED"
    answer=${answer%x}
    case $answer in
        "$RELEASES/tag/"*) ;;
        *) fail "$LATEST_FAILED" ;;
    esac
    found=${answer#"$RELEASES/tag/"}
    valid_tag "$found" || fail "$LATEST_FAILED"
    TAG=$found
}

download() {
    "$CURL" --proto =https --proto-redir =https --tlsv1.2 -fsSL --max-time 300 --max-filesize "$2" \
        -o "$WORK/$1" "$RELEASES/download/$TAG/$1" || fail "$DOWNLOAD_FAILED"
    if [ ! -f "$WORK/$1" ] || [ -L "$WORK/$1" ]; then
        fail "$DOWNLOAD_FAILED"
    fi
}

check_and_extract() {
    lines=0
    expected=""
    while IFS= read -r line || [ -n "$line" ]; do
        case $line in
            *"  $TARBALL")
                lines=$((lines + 1))
                expected=${line%"  $TARBALL"}
                ;;
        esac
    done <"$WORK/SHA256SUMS"
    [ "$lines" -eq 1 ] || fail "$RELEASE_BAD"
    case $expected in
        "" | *[!0-9a-f]*) fail "$RELEASE_BAD" ;;
    esac
    [ ${#expected} -eq 64 ] || fail "$RELEASE_BAD"
    actual=$("$SHASUM" -a 256 "$WORK/$TARBALL") || fail "$RELEASE_BAD"
    [ "${actual%% *}" = "$expected" ] || fail "$RELEASE_BAD"

    # Every name is the top folder or inside it (so never absolute), with no empty, . or .. part.
    "$TAR" -tzf "$WORK/$TARBALL" >"$WORK/names" 2>/dev/null || fail "$RELEASE_BAD"
    [ -s "$WORK/names" ] || fail "$RELEASE_BAD"
    while IFS= read -r name || [ -n "$name" ]; do
        case $name in
            "$TOP" | "$TOP/" | "$TOP/"*) ;;
            *) fail "$RELEASE_BAD" ;;
        esac
        case /$name/ in
            */../* | */./* | *//?*) fail "$RELEASE_BAD" ;;
        esac
        case $name in
            *[[:cntrl:]]*) fail "$RELEASE_BAD" ;;
        esac
    done <"$WORK/names"
    # Every entry is a regular file or a folder: no symlink, hard link or device.
    "$TAR" -tvzf "$WORK/$TARBALL" >"$WORK/types" 2>/dev/null || fail "$RELEASE_BAD"
    while IFS= read -r entry || [ -n "$entry" ]; do
        case $entry in
            -* | d*) ;;
            *) fail "$RELEASE_BAD" ;;
        esac
    done <"$WORK/types"
    # Size: at most MAX_ENTRIES entries and MAX_UNPACKED_BYTES of file content, counted by unpacking to a pipe
    # (never to disk) and stopping one byte past the cap.
    entries=$(wc -l <"$WORK/names") || fail "$RELEASE_BAD"
    [ $((entries + 0)) -le "$MAX_ENTRIES" ] || fail "$RELEASE_BAD"
    unpacked=$("$TAR" -xOzf "$WORK/$TARBALL" 2>/dev/null | head -c $((MAX_UNPACKED_BYTES + 1)) | wc -c) \
        || fail "$RELEASE_BAD"
    [ $((unpacked + 0)) -le "$MAX_UNPACKED_BYTES" ] || fail "$RELEASE_BAD"

    mkdir "$WORK/out" || fail "$RELEASE_BAD"
    "$TAR" -xzf "$WORK/$TARBALL" -C "$WORK/out" >/dev/null 2>&1 || fail "$RELEASE_BAD"
    # Checked again: only the top folder, only files and folders in it, the server executable and the app shell.
    [ "$(ls -A "$WORK/out")" = "$TOP" ] || fail "$RELEASE_BAD"
    if [ ! -d "$WORK/out/$TOP" ] || [ -L "$WORK/out/$TOP" ]; then
        fail "$RELEASE_BAD"
    fi
    others=$(find "$WORK/out/$TOP" ! -type f ! -type d) || fail "$RELEASE_BAD"
    [ -z "$others" ] || fail "$RELEASE_BAD"
    if [ ! -f "$WORK/out/$TOP/$SERVER_NAME" ] || [ ! -x "$WORK/out/$TOP/$SERVER_NAME" ] \
        || [ ! -f "$WORK/out/$TOP/wwwroot/index.html" ]; then
        fail "$RELEASE_BAD"
    fi
    verify_signature
}

# Sets SIGNING_CERT_SHA1 from the fingerprint file $1: empty when the file is missing. Returns 1 when it is present
# but not a regular file (a symlink counts as present) holding exactly 40 hex characters and at most one trailing
# line break, so an invalid file never turns the check off.
fingerprint_file() {
    SIGNING_CERT_SHA1=""
    if [ ! -e "$1" ] && [ ! -L "$1" ]; then
        return 0
    fi
    if [ ! -f "$1" ] || [ -L "$1" ]; then
        return 1
    fi
    size=$(wc -c <"$1") || return 1
    case $((size + 0)) in
        40) ;;
        41) [ "$(tail -c 1 "$1" | od -An -tx1 | tr -d ' \n')" = 0a ] || return 1 ;;
        *) return 1 ;;
    esac
    fingerprint=$(head -c 40 "$1") || return 1
    case $fingerprint in
        *[!0123456789abcdefABCDEF]*) return 1 ;;
    esac
    [ ${#fingerprint} -eq 40 ] || return 1
    SIGNING_CERT_SHA1=$fingerprint
}

# With a pinned certificate, checks the extracted server and every *.dylib and createdump in the top folder with
# codesign --verify --strict against a requirement naming that certificate (and the identifier for the server).
# Any failure, or no absolute codesign on PATH, is RELEASE_BAD. Without one, runs nothing.
verify_signature() {
    if [ -z "$SIGNING_CERT_SHA1" ]; then
        return 0
    fi
    CODESIGN=$(command -v codesign) || fail "$RELEASE_BAD"
    case $CODESIGN in
        /*) ;;
        *) fail "$RELEASE_BAD" ;;
    esac
    "$CODESIGN" --verify --strict \
        "-R=identifier \"$SERVER_NAME\" and certificate leaf = H\"$SIGNING_CERT_SHA1\"" \
        "$WORK/out/$TOP/$SERVER_NAME" >/dev/null 2>&1 || fail "$RELEASE_BAD"
    for signed in "$WORK/out/$TOP"/*.dylib "$WORK/out/$TOP/createdump"; do
        if [ ! -e "$signed" ] && [ ! -L "$signed" ]; then
            continue
        fi
        "$CODESIGN" --verify --strict "-R=certificate leaf = H\"$SIGNING_CERT_SHA1\"" "$signed" >/dev/null 2>&1 \
            || fail "$RELEASE_BAD"
    done
}

# XML-escapes one value (no control characters, checked before).
xml_escape() {
    printf '%s' "$1" | sed -e 's/&/\&amp;/g' -e 's/</\&lt;/g' -e 's/>/\&gt;/g' -e 's/"/\&quot;/g' -e "s/'/\&apos;/g"
}

# True when $1 is greater than $2, both numbers without leading zeros (compared as text, so any length works).
number_greater() {
    if [ ${#1} -ne ${#2} ]; then
        [ ${#1} -gt ${#2} ]
        return
    fi
    [ "$1" != "$2" ] && [ "$(printf '%s\n%s\n' "$1" "$2" | LC_ALL=C sort | tail -n 1)" = "$1" ]
}

# True when $1 is ASCII-greater than $2 (two different prerelease identifiers that are not numbers).
text_greater() {
    [ "$(printf '%s\n%s\n' "$1" "$2" | LC_ALL=C sort | tail -n 1)" = "$1" ]
}

# True when tag $1 is newer than tag $2 by SemVer precedence: the higher X.Y.Z; for the same X.Y.Z, no prerelease
# beats a prerelease; two prereleases compare identifier by identifier (numbers as numbers and below names, names
# in ASCII order, and more identifiers win when all before are equal). Both tags passed valid_tag.
semver_newer() {
    a=${1#v}
    b=${2#v}
    a_core=${a%%-*}
    b_core=${b%%-*}
    a_pre=""
    b_pre=""
    case $a in
        *-*) a_pre=${a#*-} ;;
    esac
    case $b in
        *-*) b_pre=${b#*-} ;;
    esac
    a_rest=$a_core.
    b_rest=$b_core.
    while [ -n "$a_rest" ]; do
        a_id=${a_rest%%.*}
        b_id=${b_rest%%.*}
        a_rest=${a_rest#*.}
        b_rest=${b_rest#*.}
        if [ "$a_id" != "$b_id" ]; then
            number_greater "$a_id" "$b_id"
            return
        fi
    done
    if [ -z "$a_pre" ] || [ -z "$b_pre" ]; then
        [ -z "$a_pre" ] && [ -n "$b_pre" ]
        return
    fi
    a_rest=$a_pre.
    b_rest=$b_pre.
    while :; do
        if [ -z "$a_rest" ]; then
            return 1
        fi
        if [ -z "$b_rest" ]; then
            return 0
        fi
        a_id=${a_rest%%.*}
        b_id=${b_rest%%.*}
        a_rest=${a_rest#*.}
        b_rest=${b_rest#*.}
        if [ "$a_id" = "$b_id" ]; then
            continue
        fi
        case $a_id in
            *[!0-9]*) a_number=no ;;
            *) a_number=yes ;;
        esac
        case $b_id in
            *[!0-9]*) b_number=no ;;
            *) b_number=yes ;;
        esac
        if [ "$a_number" = yes ] && [ "$b_number" = yes ]; then
            number_greater "$a_id" "$b_id"
        elif [ "$a_number" = yes ]; then
            return 1
        elif [ "$b_number" = yes ]; then
            return 0
        else
            text_greater "$a_id" "$b_id"
        fi
        return
    done
}

# True for a tag the develop channel installs: exactly vX.Y.Z or vX.Y.Z-develop.N (after valid_tag).
channel_tag() {
    valid_tag "$1" || return 1
    printf '%s\n' "$1" \
        | LC_ALL=C grep -Eq '^v(0|[1-9][0-9]*)\.(0|[1-9][0-9]*)\.(0|[1-9][0-9]*)(-develop\.[1-9][0-9]*)?$'
}

# The develop channel: lists the releases (one fixed GET, no Authorization, no redirects, 1 MiB at most) and sets
# TAG to the newest channel_tag among their tag_name values. Any failure, or no such tag, fails with LATEST_FAILED.
develop_tag() {
    # The trailing x keeps the answer's last line break, so nothing is cut before the tags are read.
    answer=$("$CURL" --proto =https --tlsv1.2 -fsS --max-time 30 --max-filesize "$MAX_LIST_BYTES" "$LIST_URL" \
        && printf x) || fail "$LATEST_FAILED"
    answer=${answer%x}
    [ ${#answer} -le "$MAX_LIST_BYTES" ] || fail "$LATEST_FAILED"
    best=""
    # Only "tag_name":"<value>" pairs: in a JSON string a quote is escaped, so text inside one never matches.
    names=$(printf '%s\n' "$answer" | LC_ALL=C grep -Eo '"tag_name"[[:space:]]*:[[:space:]]*"[^"\\]*"' \
        | LC_ALL=C sed -E 's/^"tag_name"[[:space:]]*:[[:space:]]*"([^"]*)"$/\1/')
    # Read line by line from a here-document: no word splitting or globbing of the names.
    while IFS= read -r name; do
        channel_tag "$name" || continue
        if [ -z "$best" ] || semver_newer "$name" "$best"; then
            best=$name
        fi
    done <<NAMES
$names
NAMES
    [ -n "$best" ] || fail "$LATEST_FAILED"
    TAG=$best
}

# Prints the updater LaunchAgent plist: /bin/sh UPDATER_SCRIPT --factory-root ROOT --start-delay UPDATER_START_DELAY,
# at load and every UPDATER_INTERVAL seconds, PATH FIXED_PATH, both outputs to UPDATER_LOG (ROOT and the paths have no
# control characters, checked before).
updater_plist() {
    printf '<?xml version="1.0" encoding="UTF-8"?>\n'
    printf '<!DOCTYPE plist PUBLIC "-//Apple//DTD PLIST 1.0//EN" "http://www.apple.com/DTDs/PropertyList-1.0.dtd">\n'
    printf '<plist version="1.0">\n'
    printf '<dict>\n'
    printf '    <key>Label</key>\n'
    printf '    <string>%s</string>\n' "$UPDATER_LABEL"
    printf '    <key>ProgramArguments</key>\n'
    printf '    <array>\n'
    printf '        <string>/bin/sh</string>\n'
    printf '        <string>%s</string>\n' "$(xml_escape "$UPDATER_SCRIPT")"
    printf '        <string>--factory-root</string>\n'
    printf '        <string>%s</string>\n' "$(xml_escape "$ROOT")"
    printf '        <string>--start-delay</string>\n'
    printf '        <string>%s</string>\n' "$UPDATER_START_DELAY"
    printf '    </array>\n'
    printf '    <key>EnvironmentVariables</key>\n'
    printf '    <dict>\n'
    printf '        <key>PATH</key>\n'
    printf '        <string>%s</string>\n' "$FIXED_PATH"
    printf '    </dict>\n'
    printf '    <key>RunAtLoad</key>\n'
    printf '    <true/>\n'
    printf '    <key>StartInterval</key>\n'
    printf '    <integer>%s</integer>\n' "$UPDATER_INTERVAL"
    printf '    <key>StandardOutPath</key>\n'
    printf '    <string>%s</string>\n' "$(xml_escape "$UPDATER_LOG")"
    printf '    <key>StandardErrorPath</key>\n'
    printf '    <string>%s</string>\n' "$(xml_escape "$UPDATER_LOG")"
    printf '</dict>\n'
    printf '</plist>\n'
}

# --- Main -----------------------------------------------------------------------------------------------------

# Exactly --factory-root <absolute root> [--start-delay <1-600>]; any other argument list leaves ROOT empty, which
# ends the run job_running without a delay or a retry.
ROOT=""
START_DELAY=""
if [ $# -eq 2 ] && [ "$1" = --factory-root ]; then
    case $2 in
        /*) ROOT=$2 ;;
    esac
elif [ $# -eq 4 ] && [ "$1" = --factory-root ] && [ "$3" = --start-delay ]; then
    case $4 in
        [1-9] | [1-9][0-9] | [1-5][0-9][0-9] | 600)
            case $2 in
                /*)
                    ROOT=$2
                    START_DELAY=$4
                    ;;
            esac
            ;;
    esac
fi
# A signal ends the run through cleanup (which also stops a running wait).
trap cleanup EXIT
trap 'exit 1' HUP INT TERM
# No sleep on PATH: no start delay and no retry.
SLEEP=$(find_program sleep) || SLEEP=""
if [ -n "$START_DELAY" ] && [ -n "$SLEEP" ]; then
    pause "$START_DELAY"
fi

case ${HOME:-} in
    /*) ;;
    *) finish off ;;
esac
USER_ID=$(id -u) || finish off
SUPPORT="$HOME/Library/Application Support/$SUPPORT_FOLDER"
SETTINGS="$SUPPORT/settings.json"
MARKER="$SUPPORT/installed-release"
SIGNING_CERT="$SUPPORT/signing-cert.sha1"
RESULT="$SUPPORT/update-result.json"
LOCK="$SUPPORT/.update.lock"
SERVER="$SUPPORT/server"
PREVIOUS="$SUPPORT/server.previous"
AGENTS="$HOME/Library/LaunchAgents"
PLIST="$AGENTS/$LABEL.plist"
UPDATER_SCRIPT="$SUPPORT/updater.sh"
UPDATER_PLIST="$AGENTS/$UPDATER_LABEL.plist"
UPDATER_LOG="$HOME/Library/Logs/$LOG_FOLDER/updater.log"
UPDATER_SCRIPT_PREVIOUS="$SUPPORT/updater.sh.previous"
UPDATER_PLIST_PREVIOUS="$SUPPORT/updater.plist.previous"
STAMP="$SUPPORT/last-check"

# 0. Only a due run goes on, and it first records the time; a run that is not due ends at once and leaves no trace.
# A clock that can't be read counts as due and writes no stamp; a stamp that can't be written does not stop the run.
NOW=$(date +%s) || NOW=""
case $NOW in
    "" | *[!0-9]*) NOW="" ;;
esac
if [ -n "$NOW" ]; then
    run_due "$NOW" || exit 0
    if private_folder "$SUPPORT"; then
        write_private "$STAMP" "$NOW"
    fi
fi

waits=0
while :; do
    # Every try starts with nothing known (a retry may end off or not_release).
    INSTALLED=""
    # 1. The setting: exactly {"autoUpdate":true} (stable), {"autoUpdate":true,"channel":"stable"|"develop"} or
    # {"autoUpdate":true,"channel":"stable"|"develop","updateInterval":N}, with or without one line break, in the
    # private support folder.
    if ! private_folder "$SUPPORT" || ! private_file "$SETTINGS" "$MAX_SETTINGS_BYTES"; then
        finish off
    fi
    settings=$(cat "$SETTINGS") || finish off
    parse_settings "$settings" || finish off
    [ "$AUTO_UPDATE" = true ] || finish off
    # $(cat) drops every trailing line break: the size allows the text plus at most one.
    settings_size=$(wc -c <"$SETTINGS") || finish off
    [ $((settings_size + 0)) -le $((${#settings} + 1)) ] || finish off

    # 2. A release install: the marker holds a valid tag, and the server folder and plist are in place.
    private_file "$MARKER" 200 || finish not_release
    installed=$(cat "$MARKER") || finish not_release
    valid_tag "$installed" || finish not_release
    INSTALLED=$installed
    if [ ! -d "$SERVER" ] || [ -L "$SERVER" ] || [ -L "$PREVIOUS" ] || [ -L "$AGENTS" ] \
        || [ ! -f "$PLIST" ] || [ -L "$PLIST" ]; then
        finish not_release
    fi
    LAUNCHCTL=$(find_program launchctl) || finish not_release
    LSOF=$(find_program lsof) || finish not_release
    DOMAIN="gui/$USER_ID"
    SERVICE="$DOMAIN/$LABEL"

    # 3. One run at a time (launch-agent.sh install/uninstall wait for the lock too).
    if mkdir "$LOCK"; then
        LOCKED=yes
    elif [ -d "$LOCK" ] && [ ! -L "$LOCK" ] && [ -n "$(find "$LOCK" -prune -mmin +"$LOCK_MINUTES")" ]; then
        rm -rf "$LOCK"
        mkdir "$LOCK" || finish busy
        LOCKED=yes
    else
        finish busy
    fi

    # 4. Never while a factory job is running: wait (without the lock) and check again from step 1, at most
    # RETRY_WAITS times. An invalid argument list never waits.
    if [ -z "$ROOT" ]; then
        finish job_running
    fi
    jobs_idle && break
    if [ "$waits" -ge "$RETRY_WAITS" ] || [ -z "$SLEEP" ]; then
        finish job_running
    fi
    log_line waiting
    rmdir "$LOCK"
    LOCKED=no
    waits=$((waits + 1))
    pause "$RETRY_SECONDS"
done

# 5. Only a newer release that has not just failed its check or start: on stable the latest final release, on
# develop the newest vX.Y.Z or vX.Y.Z-develop.N.
CURL=$(find_program curl) || finish latest_failed
if [ "$CHANNEL" = develop ]; then
    develop_tag
else
    latest_tag
    case $TAG in
        *-*) finish current ;;
    esac
fi
semver_newer "$TAG" "$INSTALLED" || finish current
skipped=$(last_skipped_version) && [ -n "$skipped" ] && [ "v$skipped" = "$TAG" ] && finish skipped_failed

# 6. Download and check exactly like launch-agent.sh install --release; nothing is touched before this passes.
WORK=$(mktemp -d "$SUPPORT/.release.XXXXXX") || {
    WORK=""
    finish download_failed
}
TOP="$TARBALL_PREFIX-${TAG#v}-osx-arm64"
TARBALL="$TOP.tar.gz"
TAR=$(find_program tar) || fail "$RELEASE_BAD"
SHASUM=$(find_program shasum) || fail "$RELEASE_BAD"
# The pinned signing certificate: missing is off; present but not a private 0600 file of this user holding a valid
# fingerprint is a failed check, so a broken copy never turns the check off.
if [ -e "$SIGNING_CERT" ] || [ -L "$SIGNING_CERT" ]; then
    private_file "$SIGNING_CERT" "$MAX_FINGERPRINT_BYTES" || fail "$RELEASE_BAD"
fi
fingerprint_file "$SIGNING_CERT" || fail "$RELEASE_BAD"
download "$TARBALL" "$MAX_TARBALL_BYTES"
download SHA256SUMS "$MAX_SUMS_BYTES"
check_and_extract

# 7. Swap, confirm, roll back.
swap
