#!/usr/bin/env bash
# SPDX-License-Identifier: GPL-3.0-or-later
#
# play.sh: from a fresh `git clone` to playing, in one command.
#
#   tools/play.sh [command] [options]
#
# Commands (default: all):
#   assets   convert your own DAoC install into private/ (models, textures, terrain, water, sky):
#            every outdoor zone, or only the --zone one; this runs the Makefile, which redoes only
#            what is out of date (a no-op when nothing changed)
#   build    build the .NET solution and prepare the Godot project
#   play     start the client, log in and show your characters (or enter the world with --enter)
#   all      assets, build, play
#   zones [TEXT]  list the zones in your install (number, name, region, kind, converted or not),
#            only those whose name contains TEXT if given; use one with --zone
#   config   show the saved settings
#
# Options (saved to private/play.conf, so you give each one once):
#   --client DIR    root of your DAoC client install (the folder holding game.dll and zones/)
#   --host HOST     server address, e.g. its Tailscale IP
#   --port N        server port (default 10300)
#   --account NAME  account name
#   --password-cmd CMD  a command that prints the password (a password manager or vault lookup);
#                   only the command is saved, never the password
# Per run only:
#   --enter SLOT    enter the world with the character in this slot (Albion 0-9, Midgard 10-19,
#                   Hibernia 20-29) instead of stopping at the character list
#   --zone Z        go to zone Z, a number or part of a name ("salis", "cursed forest"; see
#                   tools/play.sh zones): convert only that zone, and with --enter move the character
#                   to its middle (GM command /jump: the account must be a GM). Without --zone the character starts where it was
#                   saved, and the client shows whichever zone the server puts it in.
#   --fresh         re-index the install and reconvert everything, even if nothing changed
#
# The password is never saved: it comes from DAOC_PASSWORD in the environment, else from the saved
# --password-cmd, else the script asks for it. It reaches the client through the environment, not
# the command line.
#
# Everything it writes from your client install goes under private/, which git ignores. It is the
# game's content: never commit it or hand it to anyone (see CLAUDE.md, "Game client and assets").
set -euo pipefail

REPO=$(cd -- "$(dirname -- "$0")/.." && pwd)
PRIV="$REPO/private"
CONF="$PRIV/play.conf"
LIB="$PRIV/library"

die() { echo "play.sh: $*" >&2; exit 1; }
step() { printf '\n== %s\n' "$*"; }

# --- arguments and saved settings -------------------------------------------------------------
CMD=all
case "${1:-}" in assets|build|play|all|config|zones) CMD=$1; shift ;; esac
FILTER=""
if [ "$CMD" = zones ] && [ $# -gt 0 ] && [ "${1#-}" = "$1" ]; then FILTER=$1; shift; fi

CLIENT="" HOST="" PORT="" ACCOUNT="" ZONE="" PASSWORD_CMD=""
[ -f "$CONF" ] && . "$CONF"
ENTER="" GOTO="" FRESH=0 CHANGED=0
while [ $# -gt 0 ]; do
    case "$1" in
        --client)  CLIENT=${2:?}; CHANGED=1; shift 2 ;;
        --host)    HOST=${2:?}; CHANGED=1; shift 2 ;;
        --port)    PORT=${2:?}; CHANGED=1; shift 2 ;;
        --account) ACCOUNT=${2:?}; CHANGED=1; shift 2 ;;
        --zone)    GOTO=${2:?}; shift 2 ;;
        --password-cmd) PASSWORD_CMD=${2:?}; CHANGED=1; shift 2 ;;
        --enter)   ENTER=${2:?}; shift 2 ;;
        --fresh)   FRESH=1; shift ;;
        -h|--help) sed -n '4,40p' "$0"; exit 0 ;;
        *) die "unknown option: $1 (see tools/play.sh --help)" ;;
    esac
done
PORT=${PORT:-10300}
# Older play.conf files saved a ZONE; it is per run now (--zone), so a saved one is ignored.
unset ZONE

save_conf() {
    mkdir -p "$PRIV"
    {
        echo "# tools/play.sh settings (git-ignored). The password is never stored here."
        printf 'CLIENT=%q\nHOST=%q\nPORT=%q\nACCOUNT=%q\nPASSWORD_CMD=%q\n' \
            "$CLIENT" "$HOST" "$PORT" "$ACCOUNT" "$PASSWORD_CMD"
    } > "$CONF"
}
[ "$CHANGED" = 1 ] && save_conf

# Ask for a missing setting (interactive only) and remember it.
need() {
    local var=$1 prompt=$2 value
    [ -n "${!var}" ] && return
    [ -t 0 ] || die "$prompt not set: pass it as an option (see tools/play.sh --help)"
    read -r -p "$prompt: " value
    [ -n "$value" ] || die "$prompt is required"
    printf -v "$var" '%s' "$value"
    save_conf
}

if [ "$CMD" = config ]; then
    echo "settings in $CONF"
    echo "  client  ${CLIENT:-(not set)}"
    echo "  host    ${HOST:-(not set)}:$PORT"
    echo "  account ${ACCOUNT:-(not set)}"
    echo "  password ${PASSWORD_CMD:+from --password-cmd}${PASSWORD_CMD:-asked each run (or DAOC_PASSWORD)}"
    exit 0
fi

# --- tools ------------------------------------------------------------------------------------
# Microsoft's SDK on macOS lives here; don't depend on the calling shell having it on PATH.
if [ -d /usr/local/share/dotnet ]; then PATH=/usr/local/share/dotnet:$PATH; unset DOTNET_ROOT; fi
command -v dotnet >/dev/null || die "dotnet not found (install the .NET 10 SDK)"

find_godot() {
    if [ -n "${GODOT:-}" ]; then echo "$GODOT"; return; fi
    for g in godot-mono godot4-mono godot; do
        command -v "$g" >/dev/null && { echo "$g"; return; }
    done
    [ -x /Applications/Godot_mono.app/Contents/MacOS/Godot ] && { echo /Applications/Godot_mono.app/Contents/MacOS/Godot; return; }
    die "Godot 4 .NET not found (install it, or set GODOT=/path/to/godot)"
}

ZONES="$PRIV/zones"
TSV="$REPO/src/DaocProtocol/World/zones.tsv"

# The outdoor zones in this install, one per line: number, name, region, converted or not (from the
# Makefile's zone table and each zone's check), whose name contains $1 (any case) if given.
zone_list() {
    need CLIENT "DAoC client folder (holds game.dll and zones/)"
    local filter=${1:-}
    make -s -C "$REPO" CLIENT="$CLIENT" list | awk -v tsv="$TSV" -v f="$(printf %s "$filter" | tr '[:upper:]' '[:lower:]')" '
        BEGIN { FS = "\t"; while ((getline l < tsv) > 0) if (l !~ /^#/) { split(l, c, "\t"); region[c[1]] = c[2] } FS = " " }
        {
            n = $1; name = substr($0, 7, 30); sub(/ +$/, "", name); rest = substr($0, 38)
            if (f != "" && index(tolower(name), f) == 0) next
            if (rest ~ /not convertible/) { sub(/:/, ",", rest); state = rest }
            else {
                kind = ""; if (rest ~ /^city: /) { kind = "city, "; rest = substr(rest, 7) }
                state = kind (rest ~ /^not converted/ ? "not converted" : "converted")
                if (rest ~ /[1-9][0-9]* error/) { match(rest, /[0-9]+ error/); state = state ", " substr(rest, RSTART, RLENGTH) "s in its check" }
            }
            printf "%4d  %-30s region %-4s %s\n", n, name, region[n], state
        }'
}

# --zone takes a number or part of a name; a name must match exactly one zone (an exact name wins).
resolve_zone() {
    local want=$1 matches exact
    if [[ "$want" =~ ^[0-9]+$ ]]; then
        matches=$(zone_list | awk -v n="$((10#$want))" '$1 == n')
        [ -n "$matches" ] || die "no zone $want in your install (see: tools/play.sh zones)"
    else
        matches=$(zone_list "$want")
    fi
    exact=$(printf '%s\n' "$matches" | awk -v w="$(printf %s "$want" | tr '[:upper:]' '[:lower:]')" '
        { name = substr($0, 7, 30); sub(/ +$/, "", name); if (tolower(name) == w) print $0 }')
    [ -n "$exact" ] && matches=$exact
    case $(printf '%s' "$matches" | grep -c .) in
        0) die "no zone named like '$want' (see: tools/play.sh zones)" ;;
        1) if grep -q 'not convertible' <<<"$matches"; then
               die "$(printf '%s' "$matches" | awk '{ $1 = $1; print }'): we can't convert that kind of zone yet"
           fi
           printf '%s\n' "$matches" | awk '{ print $1 }' ;;
        *) printf '%s\n' "$matches" >&2; die "'$want' matches several zones: give its number or more of its name" ;;
    esac
}

if [ "$CMD" = zones ]; then
    zone_list "$FILTER"
    exit 0
fi
if [ -n "$GOTO" ]; then
    GOTO=$(resolve_zone "$GOTO")
    echo "zone $GOTO"
fi

# The conversion itself is the Makefile: it redoes only the outputs whose inputs (the install, or the
# part of the converter that makes them) changed, so this is a no-op when everything is current.
do_assets() {
    need CLIENT "DAoC client folder (holds game.dll and zones/)"
    [ -d "$CLIENT/zones" ] || die "no zones/ folder in $CLIENT: is that the client's root folder?"
    command -v make >/dev/null || die "make not found (macOS: xcode-select --install)"
    if [ "$FRESH" = 1 ]; then
        echo "--fresh: re-indexing the install and reconverting everything"
        make -C "$REPO" clean >/dev/null
        rm -f "$PRIV/build/index.done"
    fi
    local jobs; jobs=$(sysctl -n hw.ncpu 2>/dev/null || nproc 2>/dev/null || echo 4)
    if [ -n "$GOTO" ]; then
        local n; n=$(printf %03d "$GOTO")
        step "assets: zone $GOTO from $CLIENT"
        make -C "$REPO" -j"$jobs" CLIENT="$CLIENT" "check-zone$n" figures \
            || die "zone $GOTO: conversion failed or its zone check found errors (see $ZONES/zone$n/zonecheck.md)"
    else
        # The character may be saved in any zone, and the server can move it to any other.
        step "assets: every outdoor zone from $CLIENT (about 2 minutes the first time)"
        make -C "$REPO" -j"$jobs" CLIENT="$CLIENT" zones \
            || die "conversion failed (see the make output above)"
        echo "zone check results: $ZONES/summary.md"
    fi
    echo "assets ready in $ZONES (library: $LIB)"
}

do_build() {
    step "build"
    dotnet build "$REPO/eod-client.slnx" -v q --nologo
    local godot; godot=$(find_godot)
    # A fresh clone has no .godot/ cache yet; one headless import pass creates it.
    if [ ! -d "$REPO/godot/.godot/imported" ]; then
        echo "preparing the Godot project (first run)…"
        "$godot" --headless --path "$REPO/godot" --import >/dev/null 2>&1 || die "Godot import failed"
    fi
}

do_play() {
    need HOST "server address (e.g. its Tailscale IP)"
    need ACCOUNT "account"
    [ -d "$ZONES" ] || die "no converted zones in $ZONES: run tools/play.sh assets first"
    [ -z "$GOTO" ] || [ -n "$ENTER" ] || die "--zone needs --enter: it moves a character that is in the world"
    if ! nc -z -w 3 "$HOST" "$PORT" 2>/dev/null; then
        die "can't reach $HOST:$PORT (is the server up, and is this machine on the tailnet? try: tailscale status)"
    fi
    if [ -z "${DAOC_PASSWORD:-}" ] && [ -n "$PASSWORD_CMD" ]; then
        DAOC_PASSWORD=$(bash -c "$PASSWORD_CMD" </dev/null) || die "--password-cmd failed"
        [ -n "$DAOC_PASSWORD" ] || die "--password-cmd printed nothing"
    fi
    if [ -z "${DAOC_PASSWORD:-}" ]; then
        [ -t 0 ] || die "set DAOC_PASSWORD (no terminal to ask for it)"
        read -r -s -p "password for $ACCOUNT: " DAOC_PASSWORD; echo
    fi
    export DAOC_PASSWORD

    local godot; godot=$(find_godot)
    local args=(--autologin "$HOST" "$ACCOUNT" "$PORT" --zones "$ZONES" --library "$LIB")
    [ -n "$ENTER" ] && args+=(--enter "$ENTER")
    [ -n "$GOTO" ] && args+=(--goto "$GOTO")
    step "play: $ACCOUNT@$HOST:$PORT"
    # The window opens on this machine's display (over ssh/tmux on a Mac: the logged-in desktop).
    cd "$REPO" && exec "$godot" --path "$REPO/godot" -- "${args[@]}"
}

case "$CMD" in
    assets) do_assets ;;
    build)  do_build ;;
    play)   do_play ;;
    all)
        do_assets
        do_build
        do_play ;;
esac
