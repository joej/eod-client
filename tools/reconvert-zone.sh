#!/bin/sh
# Re-convert the zone models currently in the library with the current exporter, pulling each
# model's .nif from its per-model NPK in the client's zones/Nifs. Owner-run; overwrites the .glb
# in place. Usage: tools/reconvert-zone.sh <client-dir> [library-dir]
set -eu
REPO=$(cd "$(dirname "$0")/.." && pwd)
CLIENT=${1:?usage: reconvert-zone.sh <client-dir> [library-dir]}
LIB=${2:-"$REPO/private/library"}
NIFDIR="$CLIENT/zones/Nifs"
MODELS="$LIB/models"
MPK="dotnet $REPO/tools/AssetExtract/bin/Debug/net10.0/mpk.dll"

# Build the tool fresh so the current exporter fix is included.
dotnet build "$REPO/tools/AssetExtract/AssetExtract.csproj" -v quiet >/dev/null

TMP=$(mktemp -d); trap 'rm -rf "$TMP"' EXIT
ok=0; miss=0
for glb in "$MODELS"/*.glb; do
  stem=$(basename "$glb" .glb)
  # NPK name may differ in case from the model stem (e.g. Bfarmhouse.npk); match case-insensitively.
  npk=$(find "$NIFDIR" -maxdepth 1 -iname "$stem.npk" 2>/dev/null | head -1)
  [ -n "$npk" ] || { echo "  miss  $stem (no $stem.npk)"; miss=$((miss+1)); continue; }
  rm -f "$TMP"/*.nif
  # Extract the whole NPK (one or few members) and take the .nif it yields — robust to member-name case.
  $MPK extract "$npk" --out "$TMP" >/dev/null 2>&1 || true
  nif=$(find "$TMP" -maxdepth 1 -iname "*.nif" | head -1)
  [ -n "$nif" ] || { echo "  miss  $stem (no .nif in $(basename "$npk"))"; miss=$((miss+1)); continue; }
  if $MPK convert "$nif" --out "$glb" >/dev/null 2>&1; then
    echo "  ok    $stem.glb"; ok=$((ok+1))
  else
    echo "  FAIL  $stem (convert)"; miss=$((miss+1))
  fi
done
echo "done: $ok reconverted, $miss missed -> $MODELS"
