# SPDX-License-Identifier: GPL-3.0-or-later
# Sourced by tools/play.sh and tools/zonecheck-all.sh. converter_stamp prints a hash of the converter's
# source (committed tree, uncommitted changes and untracked files under src/DaocAssets and
# tools/AssetExtract). The library records the stamp it was converted with (private/library/.converter);
# a different stamp means the converted models and textures are stale and must be redone.
converter_stamp() {
    (cd "$REPO" && {
        git rev-parse HEAD:src/DaocAssets HEAD:tools/AssetExtract 2>/dev/null
        git diff HEAD -- src/DaocAssets tools/AssetExtract 2>/dev/null
        git ls-files --others --exclude-standard -- src/DaocAssets tools/AssetExtract 2>/dev/null \
            | while read -r f; do echo "$f"; cat "$f"; done
    } | shasum | cut -d' ' -f1)
}
