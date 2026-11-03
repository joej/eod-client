#!/usr/bin/env bash
# SPDX-License-Identifier: GPL-3.0-or-later
#
# shots.sh: renders a fixed set of camera poses of a converted zone, offline (no server, no login),
# into private/progress/shots/<label>/. Run it before and after an asset or rendering change and
# compare the two folders: the world is checked the same way every time instead of by walking it.
#
#   tools/shots.sh [label] [poses-file]     (defaults: a timestamp, tools/shots/camhills.txt)
#
# The poses file names its zone in a "# zone: N" line (default 0, Camelot Hills); the zone must be
# converted (make zoneN).
set -euo pipefail
REPO=$(cd -- "$(dirname -- "$0")/.." && pwd)
PRIV="$REPO/private"
LABEL=${1:-$(date +%Y-%m-%d_%H-%M-%S)}
POSES=${2:-$REPO/tools/shots/camhills.txt}
ZONE=$(sed -n 's/^#[[:space:]]*zone:[[:space:]]*\([0-9][0-9]*\).*/\1/p' "$POSES" | head -1)
ZONE=${ZONE:-0}
REGION=$(awk -F'\t' -v z="$ZONE" '$1 == z { print $2; exit }' "$REPO/src/DaocProtocol/World/zones.tsv")
[ -n "$REGION" ] || { echo "shots.sh: no zone $ZONE in zones.tsv" >&2; exit 1; }
Z="$PRIV/zones/zone$(printf %03d "$ZONE")"
OUT="$PRIV/progress/shots/$LABEL"
[ -f "$Z/zone.csv" ] || { echo "shots.sh: no converted zone in $Z (run make zone$ZONE)" >&2; exit 1; }
GODOT=${GODOT:-$(command -v godot-mono || command -v godot4-mono || command -v godot)}
# Always build the client first (incremental): shots of stale code show nothing about the change.
dotnet build "$REPO/godot/EodClient.csproj" -v q --nologo >/dev/null
# The world opens at the first pose's position (the zone and terrain around it load from there).
read -r _ x y z _ < <(grep -v '^\s*#' "$POSES" | grep -v '^\s*$' | head -1)
cd "$REPO"
"$GODOT" --path godot -- --offline "$x" "$y" "$z" "$REGION" --zone "$Z/zone.csv" --library "$PRIV/library" \
    --terrain "$Z/terrain.glb" --shots "$POSES" --shots-out "$OUT" 2>&1 | grep -E '^\[shots\]|ERROR' | grep -vE 'leaked at exit|Pages in use|never freed' || true
echo "shots -> $OUT"
