#!/usr/bin/env bash
# SPDX-License-Identifier: GPL-3.0-or-later
#
# play.sh: from a fresh `git clone` to playing, in one command.
#
#   tools/play.sh [command] [options]
#
# Commands (default: all):
#   assets   convert your own DAoC install into private/ (models, textures, terrain, water, sky);
#            this runs the Makefile (make check-zoneNNN), which redoes only what is out of date
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
#   --zone N        zone to convert (default 0, Camelot Hills; see src/DaocProtocol/World/zones.tsv)
#   --password-cmd CMD  a command that prints the password (a password manager or vault lookup);
#                   only the command is saved, never the password
# Per run only:
#   --enter SLOT    enter the world with the character in this slot (Albion 0-9, Midgard 10-19,
#                   Hibernia 20-29) instead of stopping at the character list
#   --fresh         re-index the install and reconvert all models and this zone, even if nothing changed
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
ENTER="" FRESH=0 CHANGED=0
while [ $# -gt 0 ]; do
    case "$1" in
        --client)  CLIENT=${2:?}; CHANGED=1; shift 2 ;;
        --host)    HOST=${2:?}; CHANGED=1; shift 2 ;;
        --port)    PORT=${2:?}; CHANGED=1; shift 2 ;;
        --account) ACCOUNT=${2:?}; CHANGED=1; shift 2 ;;
        --zone)    ZONE=${2:?}; CHANGED=1; shift 2 ;;
        --password-cmd) PASSWORD_CMD=${2:?}; CHANGED=1; shift 2 ;;
        --enter)   ENTER=${2:?}; shift 2 ;;
        --fresh)   FRESH=1; shift ;;
        -h|--help) sed -n '4,34p' "$0"; exit 0 ;;
        *) die "unknown option: $1 (see tools/play.sh --help)" ;;
    esac
done
PORT=${PORT:-10300}
ZONE=${ZONE:-0}

save_conf() {
    mkdir -p "$PRIV"
    {
        echo "# tools/play.sh settings (git-ignored). The password is never stored here."
        printf 'CLIENT=%q\nHOST=%q\nPORT=%q\nACCOUNT=%q\nZONE=%q\nPASSWORD_CMD=%q\n' \
            "$CLIENT" "$HOST" "$PORT" "$ACCOUNT" "$ZONE" "$PASSWORD_CMD"
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
    echo "  zone    $ZONE"
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

# --- zone lookup ------------------------------------------------------------------------------
zone_dir()  { printf '%s/zones/zone%03d' "$PRIV" "$ZONE"; }

# The conversion itself is the Makefile: it redoes only the outputs whose inputs (the install, or the
# part of the converter that makes them) changed, so this is a no-op when everything is current.
do_assets() {
    need CLIENT "DAoC client folder (holds game.dll and zones/)"
    [ -d "$CLIENT/zones" ] || die "no zones/ folder in $CLIENT: is that the client's root folder?"
    local n; n=$(printf %03d "$ZONE")
    command -v make >/dev/null || die "make not found (macOS: xcode-select --install)"
    step "assets: zone $ZONE from $CLIENT"
    if [ "$FRESH" = 1 ]; then
        echo "--fresh: re-indexing the install and reconverting everything"
        make -C "$REPO" clean-library "clean-zone$n" >/dev/null
        rm -f "$PRIV/build/index.done"
    fi
    local jobs; jobs=$(sysctl -n hw.ncpu 2>/dev/null || nproc 2>/dev/null || echo 4)
    make -C "$REPO" -j"$jobs" CLIENT="$CLIENT" "check-zone$n" \
        || die "zone $ZONE: conversion failed or its zone check found errors (see $(zone_dir)/zonecheck.md)"
    echo "assets ready in $(zone_dir) (library: $LIB)"
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
    local z; z=$(zone_dir)
    [ -f "$z/zone.csv" ] || die "no converted zone in $z: run tools/play.sh assets first"
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
    local args=(--autologin "$HOST" "$ACCOUNT" "$PORT"
        --zone "$z/zone.csv" --library "$LIB" --terrain "$z/terrain.glb")
    [ -n "$ENTER" ] && args+=(--enter "$ENTER")
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
