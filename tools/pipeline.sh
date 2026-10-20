#!/bin/sh
# Asset pipeline: turn a legitimate owner's own DAoC client install into files the Godot client
# loads from a local folder. Owner-run tooling — point it at your own install. The output is the
# client's content and NEVER belongs in this repo or a release (see CLAUDE.md, "Game client and
# assets"); write it somewhere git-ignored (private/ is ignored).
#
# Usage, from anywhere:
#   tools/pipeline.sh <client-dir> <model.nif | dir-of-nifs> <out-dir>
#
#   <client-dir>  root of the client install (searched for loose .dds and .mpk archives)
#   <model>       one .nif, or a directory searched recursively for *.nif
#   <out-dir>     where the .glb models and .dds textures are written (created if missing)
#
# Example (a creature from a mounted install into a git-ignored folder):
#   tools/pipeline.sh /Volumes/DAOC/OpenDAoC /Volumes/DAOC/OpenDAoC/figures/ambrat_black.nif \
#       private/assets/ambrat
#
# Stages (each is the `mpk` tool, built from tools/AssetExtract):
#   1. convert   each .nif -> .glb (geometry + material name; no texture bytes embedded)
#   2. textures  gather the .dds those models reference (loose on disk, or from the right .mpk)
# Then the Godot client loads <name>.glb and its <texture>.dds from <out-dir>.
# NiTriStripsData models (some creatures) are skipped with a note; that format isn't read yet.
set -eu

if [ "$#" -ne 3 ]; then
    sed -n '2,20p' "$0" >&2   # print the usage header
    exit 2
fi

client=$1
model=$2
out=$3

repo=$(CDPATH= cd -- "$(dirname -- "$0")/.." && pwd)
tool_proj="$repo/tools/AssetExtract"

[ -d "$client" ] || { echo "client-dir not found: $client" >&2; exit 1; }
[ -e "$model" ]  || { echo "model not found: $model" >&2; exit 1; }
mkdir -p "$out"

# .NET on this machine lives here; don't require the caller's shell to have set it up.
if [ -d /usr/local/share/dotnet ]; then
    PATH=/usr/local/share/dotnet:$PATH
    unset DOTNET_ROOT 2>/dev/null || true
fi
command -v dotnet >/dev/null 2>&1 || { echo "dotnet not found on PATH" >&2; exit 1; }

echo "building the mpk tool…"
dotnet build "$tool_proj" -v q --nologo >/dev/null
# Resolve the built binary so we invoke it directly (fast for many files) instead of `dotnet run`.
# The output is named `mpk`; prefer the net10.0 build (it carries the SharpGLTF deps that `convert`
# needs — the net8.0 output is the library target and is not fully runnable here).
mpk=$(find "$tool_proj/bin" -type f -name mpk -path '*net10.0*' 2>/dev/null | head -n 1)
[ -n "$mpk" ] || mpk=$(find "$tool_proj/bin" -type f -name mpk 2>/dev/null | head -n 1)
[ -n "$mpk" ] || { echo "could not find the built mpk binary under $tool_proj/bin" >&2; exit 1; }

# Collect the .nif list (one file, or every *.nif under a directory; case-insensitive extension).
nifs=$(mktemp)
trap 'rm -f "$nifs"' EXIT
if [ -d "$model" ]; then
    find "$model" -type f \( -iname '*.nif' \) > "$nifs"
else
    printf '%s\n' "$model" > "$nifs"
fi
count=$(wc -l < "$nifs" | tr -d ' ')
[ "$count" -gt 0 ] || { echo "no .nif models found at: $model" >&2; exit 1; }

echo "stage 1: convert $count model(s) -> .glb"
converted=0
skipped=0
while IFS= read -r nif; do
    [ -n "$nif" ] || continue
    base=$(basename "$nif")
    stem=${base%.*}
    if "$mpk" convert "$nif" --out "$out/$stem.glb" >/dev/null 2>"$nifs.err"; then
        converted=$((converted + 1))
        echo "  ok   $stem.glb"
    else
        skipped=$((skipped + 1))
        echo "  skip $base ($(cat "$nifs.err"))"
    fi
done < "$nifs"
rm -f "$nifs.err"

echo "stage 2: gather referenced textures -> .dds"
# `mpk textures` takes the model path or a dir; hand it whatever the caller gave us so it reads the
# same set, and let it resolve loose-vs-archive. It returns non-zero only when something is missing,
# which is not fatal to the pipeline (a model may name a texture we can't locate).
"$mpk" textures "$client" "$model" --out "$out" || true

echo
echo "done: $converted converted, $skipped skipped -> $out"
echo "load in Godot:  godot-mono --path godot --scene scenes/ModelViewer.tscn -- --model $out/<name>.glb"
