#!/bin/sh
# Installs, removes and reports on the macOS LaunchAgent that starts the app's server at login.
# Usage: scripts/launch-agent.sh install|uninstall|status
#        scripts/launch-agent.sh install --tailnet-host <mac>.<tailnet>.ts.net  (adds --TailnetHost <name>)
#        scripts/launch-agent.sh install --tailnet-host <name> --tailnet-port <port>  (also adds --TailnetPort <port>,
#          for a tailscale serve --https=<port> other than 443)
#        scripts/launch-agent.sh install --factory-root <path>  (the factory root; default: the folder above the app's)
#        scripts/launch-agent.sh install --release [vX.Y.Z] [--tailnet-host <name>]
#          (installs the ready-built release from GitHub instead of publishing: vX.Y.Z, or without a tag the
#          latest one, or the newest develop build when settings.json picks the develop channel, plus the updater LaunchAgent UPDATER_LABEL that runs <support>/updater.sh about a minute
#          after install or login and then every 15 min; each run checks only when the interval chosen in the app,
#          30 min by default, has passed)
# POSIX sh. Runs only dotnet (publish), launchctl, sleep and, with --release, curl, tar, shasum and (once
# signing-cert.sha1 is committed next to the app's solution file) codesign --verify, all with fixed arguments. Never reads
# the access token file. After every bootout it waits until launchd has removed the service, and a failed bootstrap
# is tried again a few times.

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

# Every 15 min, and at load (install or login): each run first waits UPDATER_START_DELAY seconds, then checks only
# when it is due (the interval from settings.json, see updater.sh).
UPDATER_INTERVAL=900
UPDATER_START_DELAY=60
UPDATE_RUNNING="An update is running. Try again in a few minutes."
USAGE="usage: scripts/launch-agent.sh install [--release [vX.Y.Z]] [--tailnet-host <name> [--tailnet-port <port>]] [--factory-root <path>]|uninstall|status"
# RELEASES (in the roomkit:app block) is the one repository releases come from (renaming it breaks --release).
# The develop channel's one release list (newest first), the same as updater.sh.
LIST_URL="https://api.github.com/repos/$REPO_SLUG/releases?per_page=100"
MAX_LIST_BYTES=1048576
# <support>/settings.json is at most this long (the same cap as updater.sh).
MAX_SETTINGS_BYTES=64
LATEST_FAILED="Can't find the latest release. Nothing was changed."
DOWNLOAD_FAILED="Download failed. Nothing was changed."
RELEASE_BAD="The release download doesn't check out. Nothing was changed."
SIGNING_CERT_BAD="signing-cert.sha1 must hold one 40-character SHA-1 fingerprint. Nothing was changed."
# The pinned signing certificate (its SHA-1 fingerprint) every release file must be signed with; empty means the
# signature check is off. Set only from <repo>/signing-cert.sha1, never from the environment.
SIGNING_CERT_SHA1=""
# Size caps for --release (the real tarball is about 42 MiB, unpacked about 112 MiB in under 400 entries).
MAX_TARBALL_BYTES=209715200
MAX_SUMS_BYTES=4096
MAX_UNPACKED_BYTES=536870912
MAX_ENTRIES=10000
# After a bootout, launchctl print is polled until the service is gone: at most this many polls, 0.5 s apart (15 s).
WAIT_POLLS=30
# A failed bootstrap is tried again this many times, 1 s apart (launchd may still be removing the old service).
BOOTSTRAP_RETRIES=5
FIXED_PATH="/usr/local/share/dotnet:/opt/homebrew/bin:/usr/local/bin:/usr/bin:/bin:/usr/sbin:/sbin"

fail() {
    printf '%s\n' "$1" >&2
    exit 1
}

# Refuses values that can't be written into the plist as plain text.
check_text() {
    case $1 in
        *[[:cntrl:]]*) fail "$2" ;;
    esac
}

# XML-escapes one value (no control characters, checked before).
xml_escape() {
    printf '%s' "$1" | sed -e 's/&/\&amp;/g' -e 's/</\&lt;/g' -e 's/>/\&gt;/g' -e 's/"/\&quot;/g' -e "s/'/\&apos;/g"
}

# Refuses a path that is a symlink.
refuse_link() {
    if [ -L "$1" ]; then
        fail "Refusing to write through a symlink: $1"
    fi
}

usage() {
    printf '%s\n' "$USAGE" >&2
    exit 2
}

# True for a lower-case DNS name of 1-63-character labels (a-z, 0-9, -, no label starting or ending with -)
# ending in .ts.net with at least two labels before it: the same rule as HostCheck.IsValidTailnetHost.
# True for a port from 1 to 65535: ASCII digits, no sign, space or leading zero.
valid_tailnet_port() {
    case $1 in
        '' | 0* | *[!0123456789]*) return 1 ;;
    esac
    if [ ${#1} -gt 5 ]; then
        return 1
    fi
    [ "$1" -le 65535 ]
}

valid_tailnet_host() {
    case $1 in
        *.ts.net) ;;
        *) return 1 ;;
    esac
    if [ ${#1} -gt 253 ]; then
        return 1
    fi
    rest=${1%.ts.net}
    labels=0
    while :; do
        label=${rest%%.*}
        if [ -z "$label" ] || [ ${#label} -gt 63 ]; then
            return 1
        fi
        case $label in
            -* | *- | *[!abcdefghijklmnopqrstuvwxyz0123456789-]*) return 1 ;;
        esac
        labels=$((labels + 1))
        if [ "$label" = "$rest" ]; then
            break
        fi
        rest=${rest#*.}
    done
    [ "$labels" -ge 2 ]
}

# True for "v" plus a SemVer version (prerelease part allowed, so also a develop build such as
# v1.2.4-develop.7; no build metadata) on one line. Only such a tag goes into a URL or path.
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

case ${1:-} in
    install | uninstall | status) ;;
    *) usage ;;
esac
COMMAND=$1
shift
TAILNET_HOST=""
HAS_TAILNET=no
TAILNET_PORT=""
HAS_PORT=no
FACTORY_ROOT=""
HAS_ROOT=no
RELEASE=no
TAG=""
if [ "$COMMAND" = install ]; then
    # install [--release [vX.Y.Z]] [--tailnet-host <name> [--tailnet-port <port>]] [--factory-root <path>]: in any
    # order, each at most once; --tailnet-port only with --tailnet-host.
    while [ $# -gt 0 ]; do
        case $1 in
            --tailnet-host)
                if [ "$HAS_TAILNET" = yes ] || [ $# -lt 2 ]; then
                    usage
                fi
                if ! valid_tailnet_host "$2"; then
                    fail "--tailnet-host must be a name like mac.tailnet.ts.net. Nothing was changed."
                fi
                HAS_TAILNET=yes
                TAILNET_HOST=$2
                shift 2
                ;;
            --tailnet-port)
                if [ "$HAS_PORT" = yes ] || [ $# -lt 2 ]; then
                    usage
                fi
                if ! valid_tailnet_port "$2"; then
                    fail "--tailnet-port must be a port from 1 to 65535. Nothing was changed."
                fi
                HAS_PORT=yes
                TAILNET_PORT=$2
                shift 2
                ;;
            --factory-root)
                if [ "$HAS_ROOT" = yes ] || [ $# -lt 2 ]; then
                    usage
                fi
                case $2 in
                    /*) ;;
                    *) fail "--factory-root must be an absolute path to a folder. Nothing was changed." ;;
                esac
                if [ ! -d "$2" ]; then
                    fail "--factory-root must be an absolute path to a folder. Nothing was changed."
                fi
                HAS_ROOT=yes
                FACTORY_ROOT=$2
                shift 2
                ;;
            --release)
                if [ "$RELEASE" = yes ]; then
                    usage
                fi
                RELEASE=yes
                shift
                if [ $# -gt 0 ]; then
                    case $1 in
                        --tailnet-host | --tailnet-port | --factory-root) ;;
                        *)
                            valid_tag "$1" || usage
                            TAG=$1
                            shift
                            ;;
                    esac
                fi
                ;;
            *) usage ;;
        esac
    done
    if [ "$HAS_PORT" = yes ] && [ "$HAS_TAILNET" = no ]; then
        usage
    fi
elif [ $# -ne 0 ]; then
    usage
fi

case ${HOME:-} in
    /*) ;;
    *) fail "HOME must be an absolute path." ;;
esac
check_text "$HOME" "HOME contains a control character."

AGENTS="$HOME/Library/LaunchAgents"
PLIST="$AGENTS/$LABEL.plist"
LOG_DIR="$HOME/Library/Logs/$LOG_FOLDER"
LOG="$LOG_DIR/server.log"
SUPPORT="$HOME/Library/Application Support/$SUPPORT_FOLDER"
SERVER="$SUPPORT/server"
SERVER_NEW="$SUPPORT/server.new"
PREVIOUS="$SUPPORT/server.previous"
UPDATER_PLIST="$AGENTS/$UPDATER_LABEL.plist"
UPDATER_SCRIPT="$SUPPORT/updater.sh"
UPDATER_LOG="$LOG_DIR/updater.log"
# The updater keeps its last script and plist here when it installs a release's copy of itself.
UPDATER_SCRIPT_PREVIOUS="$SUPPORT/updater.sh.previous"
UPDATER_PLIST_PREVIOUS="$SUPPORT/updater.plist.previous"
MARKER="$SUPPORT/installed-release"
# The updater's stamp of its last real check (epoch seconds); removed with the updater so the next run checks.
LAST_CHECK="$SUPPORT/last-check"
SIGNING_COPY="$SUPPORT/signing-cert.sha1"
UPDATE_LOCK="$SUPPORT/.update.lock"

LAUNCHCTL=$(command -v launchctl) || fail "launchctl not found on PATH."
case $LAUNCHCTL in
    /*) ;;
    *) fail "launchctl not found on PATH." ;;
esac
USER_ID=$(id -u) || fail "Can't read the user id."
DOMAIN="gui/$USER_ID"
SERVICE="$DOMAIN/$LABEL"
UPDATER_SERVICE="$DOMAIN/$UPDATER_LABEL"
if [ "$COMMAND" != status ]; then
    SLEEP=$(command -v sleep) || fail "sleep not found on PATH."
    case $SLEEP in
        /*) ;;
        *) fail "sleep not found on PATH." ;;
    esac
fi

# Polls launchctl print for service $1 until it no longer shows it (exits non-zero), 0.5 s after each poll that
# still shows it, at most WAIT_POLLS polls; then goes on anyway.
wait_gone() {
    polls=0
    while [ "$polls" -lt "$WAIT_POLLS" ]; do
        polls=$((polls + 1))
        "$LAUNCHCTL" print "$1" >/dev/null 2>&1 || return 0
        "$SLEEP" 0.5
    done
    return 0
}

# Boots out service $1 (not loaded is fine) and waits until launchd has removed it.
bootout_service() {
    "$LAUNCHCTL" bootout "$1" >/dev/null 2>&1
    wait_gone "$1"
}

# Bootstraps plist $1, trying again 1 s later up to BOOTSTRAP_RETRIES times; returns the last status.
bootstrap_plist() {
    attempts=0
    while :; do
        "$LAUNCHCTL" bootstrap "$DOMAIN" "$1" >/dev/null && return 0
        boot_status=$?
        attempts=$((attempts + 1))
        if [ "$attempts" -gt "$BOOTSTRAP_RETRIES" ]; then
            return "$boot_status"
        fi
        "$SLEEP" 1
    done
}

# Stops while the updater holds its lock (a lock older than 1 h is left over and does not count).
refuse_while_updating() {
    if [ -e "$UPDATE_LOCK" ] || [ -L "$UPDATE_LOCK" ]; then
        if [ -z "$(find "$UPDATE_LOCK" -prune -mmin +60 2>/dev/null)" ]; then
            fail "$UPDATE_RUNNING"
        fi
    fi
}

# Writes stdin into $1 with mode $2 through a temp file in the same folder, then rename; refuses a symlink.
put_file() {
    if [ -L "$1" ] || [ -d "$1" ]; then
        return 1
    fi
    put_temp=$(mktemp "$(dirname "$1")/.$TARBALL_PREFIX.XXXXXX") || return 1
    if cat >"$put_temp" && chmod "$2" "$put_temp" && mv -f "$put_temp" "$1"; then
        return 0
    fi
    rm -f "$put_temp"
    return 1
}

# Boots out the updater and removes its plist, the copied script, their .previous copies, the release marker, the
# fingerprint copy and the last-check stamp.
remove_updater() {
    bootout_service "$UPDATER_SERVICE"
    rm -f "$UPDATER_PLIST" "$UPDATER_SCRIPT" "$UPDATER_SCRIPT_PREVIOUS" "$UPDATER_PLIST_PREVIOUS" "$MARKER" \
        "$SIGNING_COPY" "$LAST_CHECK" || fail "Can't remove the updater."
}

do_status() {
    if [ -e "$PLIST" ] || [ -L "$PLIST" ]; then
        printf 'installed: yes\n'
    else
        printf 'installed: no\n'
    fi
    if "$LAUNCHCTL" print "$SERVICE" >/dev/null 2>&1; then
        printf 'running: yes\n'
    else
        printf 'running: no\n'
    fi
    if [ -e "$UPDATER_PLIST" ] || [ -L "$UPDATER_PLIST" ]; then
        printf 'updater: yes\n'
    else
        printf 'updater: no\n'
    fi
    printf 'log: %s\n' "$LOG"
    exit 0
}

do_uninstall() {
    refuse_link "$HOME/Library"
    refuse_link "$AGENTS"
    refuse_link "$HOME/Library/Application Support"
    refuse_link "$SUPPORT"
    refuse_link "$UPDATER_PLIST"
    refuse_link "$UPDATER_SCRIPT_PREVIOUS"
    refuse_link "$UPDATER_PLIST_PREVIOUS"
    refuse_while_updating
    remove_updater
    bootout_service "$SERVICE"
    rm -f "$PLIST" || fail "Can't remove $PLIST"
    rm -rf "$SERVER" "$SERVER_NEW" "$PREVIOUS" || fail "Can't remove $SERVER"
    printf 'Uninstalled. The log stays at %s\n' "$LOG"
    exit 0
}

# Finds a program on PATH as an absolute path without control characters, or stops.
find_program() {
    found=$(command -v "$1") || fail "$1 not found on PATH."
    case $found in
        /*) ;;
        *) fail "$1 not found on PATH." ;;
    esac
    check_text "$found" "The $1 path contains a control character."
    printf '%s' "$found"
}

# Publishes the server from the sources into server.new; launchd runs it with dotnet.
publish_server() {
    if ! "$DOTNET" publish "$REPO/src/$SERVER_NAME" -c Release -o "$SERVER_NEW" >&2 \
        || [ ! -f "$SERVER_NEW/$SERVER_NAME.dll" ]; then
        rm -rf "$SERVER_NEW"
        fail "dotnet publish failed. Nothing was changed."
    fi
    PROGRAM=$DOTNET
    PROGRAM_FILE="$SERVER/$SERVER_NAME.dll"
}

# Removes the release work folder on every way out of the script.
cleanup_work() {
    if [ -n "${WORK:-}" ]; then
        rm -rf "$WORK"
    fi
}

# Sets TAG from the redirect of .../releases/latest (the redirect is not followed, nothing is downloaded).
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

# Downloads one file of release TAG, at most $2 bytes, into the work folder.
download() {
    "$CURL" --proto =https --proto-redir =https --tlsv1.2 -fsSL --max-time 300 --max-filesize "$2" \
        -o "$WORK/$1" "$RELEASES/download/$TAG/$1" || fail "$DOWNLOAD_FAILED"
    if [ ! -f "$WORK/$1" ] || [ -L "$WORK/$1" ]; then
        fail "$DOWNLOAD_FAILED"
    fi
}

# Checks the tarball against its one SHA256SUMS line, checks every archive entry before extracting, extracts
# into $WORK/out and checks the result again.
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

# True when <support>/settings.json picks the develop channel: a regular file (not a symlink) of at most
# MAX_SETTINGS_BYTES holding {"autoUpdate":true|false,"channel":"develop"} or
# {"autoUpdate":true|false,"channel":"develop","updateInterval":N} (N 1-6 digits, 0 or no leading zero) and at most one line
# break. Anything else (missing, other content, a symlink) is the stable channel.
develop_channel() {
    settings_file="$SUPPORT/settings.json"
    if [ ! -f "$settings_file" ] || [ -L "$settings_file" ]; then
        return 1
    fi
    settings_size=$(wc -c <"$settings_file") || return 1
    [ $((settings_size + 0)) -le "$MAX_SETTINGS_BYTES" ] || return 1
    settings=$(cat "$settings_file") || return 1
    case $settings in
        '{"autoUpdate":true,"channel":"develop"}' | '{"autoUpdate":false,"channel":"develop"}') ;;
        '{"autoUpdate":true,"channel":"develop","updateInterval":'*'}' \
            | '{"autoUpdate":false,"channel":"develop","updateInterval":'*'}')
            settings_number=${settings#*'"updateInterval":'}
            settings_number=${settings_number%'}'}
            case $settings_number in
                0 | [1-9] | [1-9][0-9] | [1-9][0-9][0-9] | [1-9][0-9][0-9][0-9] | [1-9][0-9][0-9][0-9][0-9] \
                    | [1-9][0-9][0-9][0-9][0-9][0-9]) ;;
                *) return 1 ;;
            esac
            ;;
        *) return 1 ;;
    esac
    # $(cat) drops every trailing line break: the size allows the text plus at most one.
    [ $((settings_size + 0)) -le $((${#settings} + 1)) ]
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

# Downloads, checks and extracts release TAG into server.new; launchd runs its SERVER_NAME directly. Without
# a tag it takes the channel the user chose: the newest develop build on develop (settings.json), else the latest
# release. Nothing from the download runs here.
install_release() {
    WORK=$(mktemp -d "$SUPPORT/.release.XXXXXX") || fail "$DOWNLOAD_FAILED"
    trap cleanup_work EXIT
    trap 'exit 1' HUP INT TERM
    if [ -z "$TAG" ]; then
        if develop_channel; then
            develop_tag
        else
            latest_tag
        fi
    fi
    TOP="$TARBALL_PREFIX-${TAG#v}-osx-arm64"
    TARBALL="$TOP.tar.gz"
    download "$TARBALL" "$MAX_TARBALL_BYTES"
    download SHA256SUMS "$MAX_SUMS_BYTES"
    check_and_extract
    mv "$WORK/out/$TOP" "$SERVER_NEW" || fail "$RELEASE_BAD"
    PROGRAM="$SERVER/$SERVER_NAME"
    PROGRAM_FILE=""
}

do_install() {
    SCRIPT_DIR=$(cd "$(dirname "$0")" && pwd) || fail "Can't find the script folder."
    REPO=$(cd "$SCRIPT_DIR/.." && pwd) || fail "Can't find the $APP_NAME folder."
    [ -f "$REPO/$SOLUTION_FILE" ] || fail "Can't find $SOLUTION_FILE next to the scripts folder."
    if [ "$HAS_ROOT" = yes ]; then
        check_text "$FACTORY_ROOT" "The factory root path contains a control character."
        ROOT=$(cd "$FACTORY_ROOT" && pwd) || fail "Can't find the factory root."
    else
        ROOT=$(cd "$REPO/.." && pwd) || fail "Can't find the factory root."
    fi
    check_text "$ROOT" "The factory root path contains a control character."

    if [ "$RELEASE" = yes ]; then
        UPDATER_SOURCE="$SCRIPT_DIR/updater.sh"
        if [ ! -f "$UPDATER_SOURCE" ] || [ -L "$UPDATER_SOURCE" ]; then
            fail "Can't find scripts/updater.sh. Nothing was changed."
        fi
        # The pinned signing certificate, read before any download: missing is off, invalid stops here.
        fingerprint_file "$REPO/signing-cert.sha1" || fail "$SIGNING_CERT_BAD"
        CURL=$(find_program curl) || exit 1
        TAR=$(find_program tar) || exit 1
        SHASUM=$(find_program shasum) || exit 1
    else
        DOTNET=$(command -v dotnet) || fail "dotnet not found on PATH."
        case $DOTNET in
            /*) ;;
            *) fail "dotnet not found on PATH." ;;
        esac
        check_text "$DOTNET" "The dotnet path contains a control character."
    fi

    refuse_link "$HOME/Library"
    refuse_link "$AGENTS"
    refuse_link "$PLIST"
    refuse_link "$HOME/Library/Logs"
    refuse_link "$LOG_DIR"
    refuse_link "$LOG"
    refuse_link "$HOME/Library/Application Support"
    refuse_link "$SUPPORT"
    refuse_link "$SERVER"
    refuse_link "$SERVER_NEW"
    refuse_link "$PREVIOUS"
    refuse_link "$UPDATER_PLIST"
    refuse_link "$UPDATER_SCRIPT"
    refuse_link "$UPDATER_SCRIPT_PREVIOUS"
    refuse_link "$UPDATER_PLIST_PREVIOUS"
    refuse_link "$UPDATER_LOG"
    refuse_link "$MARKER"
    refuse_link "$SIGNING_COPY"
    refuse_while_updating

    mkdir -p "$AGENTS" || fail "Can't create $AGENTS"
    mkdir -p "$LOG_DIR" || fail "Can't create $LOG_DIR"
    chmod 700 "$LOG_DIR" || fail "Can't set the mode of $LOG_DIR"
    if [ ! -d "$SUPPORT" ]; then
        mkdir -p "$HOME/Library/Application Support" || fail "Can't create $SUPPORT"
        mkdir -m 700 "$SUPPORT" || fail "Can't create $SUPPORT"
    fi

    rm -rf "$SERVER_NEW" || fail "Can't remove $SERVER_NEW"
    if [ "$RELEASE" = yes ]; then
        install_release
    else
        publish_server
    fi

    # The updater is stopped first: a release install installs it again below, a source install has none.
    if [ "$RELEASE" = yes ] || [ -e "$UPDATER_PLIST" ]; then
        remove_updater
    fi
    # Not loaded is fine: the agent may not be installed yet.
    bootout_service "$SERVICE"

    rm -rf "$SERVER" || fail "Can't remove $SERVER"
    mv "$SERVER_NEW" "$SERVER" || fail "Can't move the published server into place."

    refuse_link "$AGENTS"
    refuse_link "$PLIST"
    TEMP=$(mktemp "$AGENTS/.$LABEL.XXXXXX") || fail "Can't write $PLIST"
    {
        printf '<?xml version="1.0" encoding="UTF-8"?>\n'
        printf '<!DOCTYPE plist PUBLIC "-//Apple//DTD PLIST 1.0//EN" "http://www.apple.com/DTDs/PropertyList-1.0.dtd">\n'
        printf '<plist version="1.0">\n'
        printf '<dict>\n'
        printf '    <key>Label</key>\n'
        printf '    <string>%s</string>\n' "$LABEL"
        printf '    <key>ProgramArguments</key>\n'
        printf '    <array>\n'
        printf '        <string>%s</string>\n' "$(xml_escape "$PROGRAM")"
        if [ -n "$PROGRAM_FILE" ]; then
            printf '        <string>%s</string>\n' "$(xml_escape "$PROGRAM_FILE")"
        fi
        printf '        <string>--FactoryRoot</string>\n'
        printf '        <string>%s</string>\n' "$(xml_escape "$ROOT")"
        if [ -n "$TAILNET_HOST" ]; then
            printf '        <string>--TailnetHost</string>\n'
            printf '        <string>%s</string>\n' "$(xml_escape "$TAILNET_HOST")"
        fi
        if [ -n "$TAILNET_PORT" ]; then
            printf '        <string>--TailnetPort</string>\n'
            printf '        <string>%s</string>\n' "$TAILNET_PORT"
        fi
        printf '    </array>\n'
        printf '    <key>WorkingDirectory</key>\n'
        printf '    <string>%s</string>\n' "$(xml_escape "$SERVER")"
        printf '    <key>EnvironmentVariables</key>\n'
        printf '    <dict>\n'
        printf '        <key>PATH</key>\n'
        printf '        <string>%s</string>\n' "$FIXED_PATH"
        printf '    </dict>\n'
        printf '    <key>RunAtLoad</key>\n'
        printf '    <true/>\n'
        printf '    <key>KeepAlive</key>\n'
        printf '    <true/>\n'
        printf '    <key>ThrottleInterval</key>\n'
        printf '    <integer>10</integer>\n'
        printf '    <key>StandardOutPath</key>\n'
        printf '    <string>%s</string>\n' "$(xml_escape "$LOG")"
        printf '    <key>StandardErrorPath</key>\n'
        printf '    <string>%s</string>\n' "$(xml_escape "$LOG")"
        printf '</dict>\n'
        printf '</plist>\n'
    } >"$TEMP" || {
        rm -f "$TEMP"
        fail "Can't write $PLIST"
    }
    chmod 644 "$TEMP" || {
        rm -f "$TEMP"
        fail "Can't set the mode of $PLIST"
    }
    mv -f "$TEMP" "$PLIST" || {
        rm -f "$TEMP"
        fail "Can't write $PLIST"
    }

    bootstrap_plist "$PLIST" \
        || fail "launchctl bootstrap failed. Check: launchctl print $SERVICE"

    if [ "$RELEASE" = yes ]; then
        install_updater
        printf 'Installed %s: %s\n' "$TAG" "$PLIST"
    else
        printf 'Installed: %s\n' "$PLIST"
    fi
    printf 'Log: %s\n' "$LOG"
    if [ "$RELEASE" = yes ]; then
        printf 'Updater: %s\n' "$UPDATER_PLIST"
    fi
    exit 0
}

# Copies updater.sh (0700) and writes the release marker (the tag, 0600), the fingerprint copy (0600, or removes
# it when signing is off) and the updater plist (0644), then bootstraps it. The updater runs only when "Install
# updates automatically" is on in the app. launchd starts it at load (it first waits a minute, so the new server is
# up) and every 15 min, and a run checks only when the chosen interval has passed; while a factory job runs, the run
# itself checks again every 15 min.
install_updater() {
    put_file "$UPDATER_SCRIPT" 700 <"$UPDATER_SOURCE" || fail "Can't write $UPDATER_SCRIPT"
    printf '%s\n' "$TAG" | put_file "$MARKER" 600 || fail "Can't write $MARKER"
    if [ -n "$SIGNING_CERT_SHA1" ]; then
        printf '%s\n' "$SIGNING_CERT_SHA1" | put_file "$SIGNING_COPY" 600 || fail "Can't write $SIGNING_COPY"
    else
        rm -f "$SIGNING_COPY" || fail "Can't remove $SIGNING_COPY"
    fi
    updater_plist | put_file "$UPDATER_PLIST" 644 || fail "Can't write $UPDATER_PLIST"
    bootstrap_plist "$UPDATER_PLIST" \
        || fail "launchctl bootstrap failed. Check: launchctl print $UPDATER_SERVICE"
}

case $COMMAND in
    install) do_install ;;
    uninstall) do_uninstall ;;
    status) do_status ;;
esac
