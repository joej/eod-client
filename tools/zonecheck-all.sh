#!/usr/bin/env bash
# SPDX-License-Identifier: GPL-3.0-or-later
#
# zonecheck-all.sh: converts every outdoor zone's objects from your install and checks each one as
# the client will draw it (mpk zonecheck: objects under, into or above the ground, missing models,
# implausible sizes). This finds placement faults in zones nobody has walked yet.
#
#   tools/zonecheck-all.sh [client-dir]      (default: CLIENT from private/play.conf)
#
# Writes private/audit/zonecheck/zoneNNN.md per zone and private/audit/zonecheck/summary.md. Models
# go into the shared library (private/library), converted once, so later runs are fast. Uses the
# index tools/play.sh builds (private/asset-index/index.json). Everything it writes is derived from
# your client install: it stays under private/ (git-ignored).
set -euo pipefail

REPO=$(cd -- "$(dirname -- "$0")/.." && pwd)
PRIV="$REPO/private"
CLIENT=""
[ -f "$PRIV/play.conf" ] && . "$PRIV/play.conf"
CLIENT=${1:-$CLIENT}
[ -d "$CLIENT/zones" ] || { echo "usage: $0 <client-dir> (no zones/ folder in '$CLIENT')" >&2; exit 1; }
MPK="$REPO/tools/AssetExtract/bin/Debug/net10.0/mpk"
INDEX="$PRIV/asset-index/index.json"
LIB="$PRIV/library"
OUT="$PRIV/audit/zonecheck"
# Always build (incremental): a check run with a stale tool checks old code.
dotnet build "$REPO/tools/AssetExtract/AssetExtract.csproj" -v q --nologo >/dev/null
[ -f "$INDEX" ] || "$MPK" index "$CLIENT" --out "$INDEX"
mkdir -p "$OUT" "$LIB"

# Converted models are reused across runs; when the converter's code changed they are stale.
. "$REPO/tools/lib/converter-stamp.sh"
stamp=$(converter_stamp)
if [ "$(cat "$LIB/.converter" 2>/dev/null)" != "$stamp" ]; then
    echo "converter changed since the library was built: reconverting models and textures"
    rm -rf "$LIB/models" "$LIB/textures"
fi

summary="$OUT/summary.md"
{
    echo "# Zone check, all outdoor zones"
    echo
    echo "| Zone | Name | Placements | Errors | Warnings | Findings |"
    echo "|---:|---|---:|---:|---:|---|"
} > "$summary"

grep -v '^#' "$REPO/src/DaocProtocol/World/zones.tsv" | while IFS=$'\t' read -r zone region ox oy w h name; do
    [ -n "$zone" ] || continue
    n=$(printf %03d "$zone")
    dat=$(find "$CLIENT/zones" -maxdepth 2 -ipath "*/zone$n/dat$n.mpk" | head -1)
    [ -n "$dat" ] || continue
    # Outdoor zones have a heightmap; dungeons and cities (M11) don't.
    "$MPK" list "$dat" 2>/dev/null | grep -qi 'terrain\.pcx' || continue
    z="$OUT/zone$n"
    mkdir -p "$z"
    if ! "$MPK" zone "$dat" --offset $((ox * 8192)) $((oy * 8192)) --size "$w" "$h" --index "$INDEX" --library "$LIB" --out "$z" >"$z/zone.log" 2>&1; then
        echo "| $zone | $name | | | | conversion failed (see zone$n/zone.log) |" >> "$summary"
        echo "zone $zone $name: conversion failed"
        continue
    fi
    line=$("$MPK" zonecheck "$dat" --zone "$z" --library "$LIB" --offset $((ox * 8192)) $((oy * 8192)) --size "$w" "$h" \
        --known "$REPO/tools/zonecheck-known.txt" --out "$OUT/zone$n.md" 2>&1 | head -1 || true)
    echo "zone $zone $name: ${line#Zone check: }"
    # "1536 placements: 0 error(s), 8 warning(s) (sunk into the ground 8)"
    placements=$(sed -E 's/.*: ([0-9]+) placements.*/\1/' <<<"$line")
    errors=$(sed -E 's/.* ([0-9]+) error.*/\1/' <<<"$line")
    warnings=$(sed -E 's/.* ([0-9]+) warning.*/\1/' <<<"$line")
    findings=$(sed -nE 's/.*warning\(s\) \((.*)\)$/\1/p' <<<"$line")
    echo "| $zone | $name | $placements | $errors | $warnings | $findings |" >> "$summary"
done
echo "$stamp" > "$LIB/.converter"
echo "summary -> $summary"
