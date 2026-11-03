#!/usr/bin/env bash
# SPDX-License-Identifier: GPL-3.0-or-later
#
# shots.sh: renders a fixed set of camera poses of a converted zone, offline (no server, no login),
# into private/progress/shots/<label>/. Run it before and after an asset or rendering change and
# compare the two folders: the world is checked the same way every time instead of by walking it.
#
#   tools/shots.sh [label] [poses-file]     (defaults: a timestamp, tools/shots/camhills.txt)
#
# Uses the zone tools/play.sh converted (private/zones/zoneNNN, ZONE from private/play.conf).
set -euo pipefail
REPO=$(cd -- "$(dirname -- "$0")/.." && pwd)
PRIV="$REPO/private"
ZONE=0
[ -f "$PRIV/play.conf" ] && . "$PRIV/play.conf"
LABEL=${1:-$(date +%Y-%m-%d_%H-%M-%S)}
POSES=${2:-$REPO/tools/shots/camhills.txt}
Z="$PRIV/zones/zone$(printf %03d "${ZONE:-0}")"
OUT="$PRIV/progress/shots/$LABEL"
[ -f "$Z/zone.csv" ] || { echo "shots.sh: no converted zone in $Z (run tools/play.sh assets)" >&2; exit 1; }
GODOT=${GODOT:-$(command -v godot-mono || command -v godot4-mono || command -v godot)}
# The world opens at the first pose's position (the zone and terrain around it load from there).
read -r _ x y z _ < <(grep -v '^\s*#' "$POSES" | grep -v '^\s*$' | head -1)
cd "$REPO"
"$GODOT" --path godot -- --offline "$x" "$y" "$z" --zone "$Z/zone.csv" --library "$PRIV/library" \
    --terrain "$Z/terrain.glb" --shots "$POSES" --shots-out "$OUT" 2>&1 | grep -E '^\[shots\]|ERROR' | grep -vE 'leaked at exit|Pages in use|never freed' || true
echo "shots -> $OUT"
