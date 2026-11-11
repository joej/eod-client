#!/usr/bin/env bash
# SPDX-License-Identifier: GPL-3.0-or-later
#
# uicheck.sh: builds the game windows offline at several interface sizes and checks them (tools/uicheck.py): overlapping
# buttons, controls spilling out of their window or drawn smaller than their minimum, window sizes against the skin's
# XML. Needs Camelot Hills converted (make zone000) and the install (CLIENT in private/play.conf) for the skin.
#
#   tools/uicheck.sh [scale ...]      (default: 1 1.15 1.5 2)
set -euo pipefail
REPO=$(cd "$(dirname "$0")/.." && pwd)
cd "$REPO"
CLIENT=$(sed -n 's/^CLIENT=//p' private/play.conf 2>/dev/null || true)
GODOT=${GODOT:-$(command -v godot-mono || command -v godot)}
OUT=private/build/uicheck
mkdir -p "$OUT"
dotnet build godot/EodClient.csproj -v q --nologo >/dev/null
scales=("$@"); [ ${#scales[@]} -eq 0 ] && scales=(1 1.15 1.5 2)
dumps=()
for s in "${scales[@]}"; do
  "$GODOT" --path godot -- --offline 574200 532800 2500 1 --zone private/zones/zone000/zone.csv --library private/library \
    --terrain private/zones/zone000/terrain.glb --ui-scale "$s" --ui-dump "$REPO/$OUT/ui-$s.json" 2>&1 | grep '^\[ui\]' || true
  dumps+=("$OUT/ui-$s.json")
done
python3 -I tools/uicheck.py "${dumps[@]}" ${CLIENT:+--skin "$CLIENT/ui/atlantis"}
