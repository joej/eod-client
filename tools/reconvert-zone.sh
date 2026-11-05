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
MPK_DLL="$REPO/tools/AssetExtract/bin/Debug/net10.0/mpk.dll"
mpk() { dotnet "$MPK_DLL" "$@"; }   # a function, so a repo path with spaces still works

# Build the tool fresh so the current exporter fix is included.
dotnet build "$REPO/tools/AssetExtract/AssetExtract.csproj" -c Debug -v quiet >/dev/null
[ -f "$MPK_DLL" ] || { echo "reconvert-zone.sh: no $MPK_DLL after the build" >&2; exit 1; }

TMP=$(mktemp -d); trap 'rm -rf "$TMP"' EXIT
ok=0; miss=0
for glb in "$MODELS"/*.glb; do
  stem=$(basename "$glb" .glb)
  # NPK name may differ in case from the model stem (e.g. Bfarmhouse.npk); match case-insensitively.
  npk=$(find "$NIFDIR" -maxdepth 1 -iname "$stem.npk" 2>/dev/null | head -1)
  [ -n "$npk" ] || { echo "  miss  $stem (no $stem.npk)"; miss=$((miss+1)); continue; }
  # Clear any NIF left from a previous model (case-insensitive: members may be .NIF). A stale leftover
  # would otherwise be picked below and silently converted under the wrong name — the bug that put one
  # model's materials into another's .glb.
  find "$TMP" -maxdepth 1 -iname "*.nif" -delete 2>/dev/null || true
  # Extract the whole NPK (one or few members) and take the .nif it yields — robust to member-name case.
  if ! mpk extract "$npk" --out "$TMP" >/dev/null 2>"$TMP/err"; then
    echo "  FAIL  $stem (extract: $(head -1 "$TMP/err"))"; miss=$((miss+1)); continue
  fi
  nif=$(find "$TMP" -maxdepth 1 -iname "*.nif" | head -1)
  [ -n "$nif" ] || { echo "  miss  $stem (no .nif in $(basename "$npk"))"; miss=$((miss+1)); continue; }
  if mpk convert "$nif" --out "$glb" >/dev/null 2>"$TMP/err"; then
    echo "  ok    $stem.glb"; ok=$((ok+1))
  else
    echo "  FAIL  $stem (convert: $(head -1 "$TMP/err"))"; miss=$((miss+1))
  fi
done
echo "done: $ok reconverted, $miss missed -> $MODELS"
