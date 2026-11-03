#!/usr/bin/env bash
# SPDX-License-Identifier: GPL-3.0-or-later
#
# zonecheck-all.sh: converts every outdoor zone from your install and checks each placement as the
# client will draw it (mpk zonecheck: objects under, into or above the ground, missing models,
# implausible sizes). This finds placement faults in zones nobody has walked yet.
#
#   tools/zonecheck-all.sh [client-dir]      (default: CLIENT from private/play.conf)
#
# A front end for `make check-zones` (see the Makefile): only what is out of date is redone. Writes
# private/zones/zoneNNN/ (terrain, objects, water, sky, zonecheck.md) and private/zones/summary.md.
# Exit status 1 when any zone check has errors. Everything it writes is derived from your client
# install: it stays under private/ (git-ignored).
set -euo pipefail
REPO=$(cd -- "$(dirname -- "$0")/.." && pwd)
CLIENT=""
[ -f "$REPO/private/play.conf" ] && . "$REPO/private/play.conf"
CLIENT=${1:-$CLIENT}
[ -d "$CLIENT/zones" ] || { echo "usage: $0 <client-dir> (no zones/ folder in '$CLIENT')" >&2; exit 1; }
jobs=$(sysctl -n hw.ncpu 2>/dev/null || nproc 2>/dev/null || echo 4)
exec make -C "$REPO" -j"$jobs" CLIENT="$CLIENT" check-zones
