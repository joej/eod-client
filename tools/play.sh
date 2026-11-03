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
#   --zone N        go to zone N (src/DaocProtocol/World/zones.tsv; 0 = Camelot Hills): convert only
#                   that zone, and with --enter move the character to its middle (GM command /jump:
#                   the account must be a GM). Without --zone the character starts where it was
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
case "${1:-}" in assets|build|play|all|config) CMD=$1; shift ;; esac

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
        -h|--help) sed -n '4,38p' "$0"; exit 0 ;;
        *) die "unknown option: $1 (see tools/play.sh --help)" ;;
    esac
done
PORT=${PORT:-10300}
# Older play.conf files saved a ZONE; it is per run now (--zone), so a saved one is ignored.
unset ZONE
[ -z "$GOTO" ] || [[ "$GOTO" =~ ^[0-9]+$ ]] || die "--zone takes a zone number (see src/DaocProtocol/World/zones.tsv)"

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
        make -C "$REPO" -j"$jobs" CLIENT="$CLIENT" "check-zone$n" \
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
