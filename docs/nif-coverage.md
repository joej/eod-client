# NIF coverage

What DAoC's model files contain, what our converter (`src/DaocAssets/Nif`) does with each part, and
what it still drops. This replaces finding gaps one visible bug at a time (magenta walls, tan
towers, opaque trees) with a measured list.

## How it is measured

`mpk audit <client-dir> --out FILE.md` (`tools/AssetExtract`) opens every NIF in an install, loose or
inside `.mpk`/`.npk` archives, and records each file's format version, every block type, and the
rendering features those blocks use. Each type gets a coverage status from `NifSurvey.Coverage`,
which must stay in step with the reader: **handled**, **partial** (the note says what is dropped), or
**unhandled**. The report describes the owner's client files, so it is written under `private/` and
never committed. This page holds the findings.

**The understanding check.** NetImmerse 4.x blocks carry no size field: the reader finds block
boundaries by scanning for the next class-name string. For the 18 types the survey decodes (every
type the converter uses, plus the common render-state properties), it decodes each block's whole body
from NifTools' `nif.xml` layouts and checks that the decode ends exactly where the next block begins,
or at the file footer. A layout we had wrong would end in the wrong place. On the 2026-11-01 run,
**every decoded block in the install ended exactly on a real boundary** (about 380,000 blocks in
5,311 4.x files). The check can fail: a test feeds it a block with one stray byte
(`NifSurveyTests`). Gamebryo 10.1 headers carry an exact block-type table, so their type counts are
exact, but their bodies aren't decoded yet.

Run it after any reader change; the integrity section must stay clean.

## What the install contains (2026-11-01)

10,993 files with a `.nif` name. 10,912 are NIFs we can identify; 76 are SpeedTree files under a
`.nif` name (open question W5); 5 are empty.

| Folder | 4.x | 10.1 | 2.x/3.x |
|---|---:|---:|---:|
| zones | 1,970 | 960 | 45 |
| figures | 42 | 2,892 | 0 |
| items | 1,179 | 1,259 | 0 |
| effects | 1,272 | 303 | 0 |
| phousing | 416 | 109 | 0 |
| frontiers | 397 | 28 | 0 |
| other | 35 | 5 | 0 |

## Fixed by the audit itself

- **The block scanner accepted text as class names.** 3ds Max's exporter writes
  `"NiMAXPSysConverter flag"` into string extra data, and texture names like `Nicicle.tga` occur. The
  scanner took these for blocks: **496 files**, mostly effects but also buildings. Worse than the
  dropped last block it caused, the false entry shifted the index of every later block, so child and
  geometry references past it pointed at the wrong blocks. In Camelot Hills, `bhouse2` (a Tudor house)
  converted to 24 KB of wall fragments; it now converts to 618 KB, a complete textured house, and
  `b-forge1` gained its roof and walls. A class name must now be a plain identifier. Nine files (spell effects and epic weapons) still contain an identifier-like false match
  inside a block we don't decode; the report lists them.
- **2.x/3.x files were misreported** as "newer than supported". The reader now names the format.

## What the audit cannot see

It checks that we *read* every block correctly, not that we *assemble* the scene correctly. On
2026-11-01 a live look at Prydwen Keep found an assembly bug it couldn't have caught: far LOD levels,
skipped in the tree walk, were re-added by a fallback without their parents' transforms, so 15 of
Camelot Hills' 46 models drew a shifted second copy (open question W3e). Assembly needs its own checks:
structural tests on real files (e.g. "no shape under a skipped LOD level is emitted") and rendered
comparisons of a sample of models from each family.

## The gaps, ranked

By how much of the world they affect and how directly they block the roadmap (M7 = the world and its
inhabitants look like DAoC).

1. **Gamebryo 10.1 is a geometry scan, not a reader** (W7). 960 zone and 1,259 item files: no node
   tree, so no transforms, helper/hidden rules or per-shape textures (the file's first texture goes on
   every mesh), and no alpha. Multi-part 10.1 world objects are probably assembled wrong today. 2,892
   of 2,934 figure files are 10.1, so M7b needs a real 10.1 reader anyway. Largest single gap.
2. **Multi-texturing is dropped.** We read only the base slot. 1,463 files use a **dark map**
   (`NiTexturingProperty` dark slot, almost always on UV set 1): baked lighting, the biggest visual
   difference on buildings and dungeons. Also detail (1,410 files), glow (506), gloss (397), bump (245)
   and decal (127) maps. The second UV sets they need are dropped too (geometry with two or more UV sets
   occurs in about 3,000 files).
3. **Vertex colours are dropped** (about 3,200 files, mostly shading baked into zone objects), along
   with `NiVertexColorProperty`, which decides whether they replace or modulate lighting (vertex mode 2
   + lighting mode 1 in 2,366 files).
4. **2.x/3.x NIFs are unreadable** (W6). 45 zone files. Some are in zones' NIF tables (barrows in 8
   zones, rush clumps in 4), so those objects are missing from the world.
5. **Animation is ignored**: keyframe controllers in 2,344 files (doors, windmills, flags; figures),
   skinning in 2,939 (M7b), UV scrolling in 768 (water, fire), morphs in 1,242 (mostly figures).
6. **Billboards** (`NiBillboardNode`, 877 files): shapes that turn to face the camera (effect sprites,
   some foliage) render as fixed planes.
7. **Render state we don't apply**: emissive materials (1,530 files), z-write off on 1,892 files
   (usually blended effects), texture apply modes REPLACE/HILIGHT2 (155 files), non-wrapping clamp
   modes (about 1,000 files).
8. **Smaller**: embedded `NiPixelData` textures (16 files), `NiTextureEffect` environment or projected
   maps (667 files), lights inside models, particle systems (effects; post-MVP).

**Free improvement found:** 4.x `NiLODNode` stores its real distance ranges, and the survey decodes
them exactly (567 blocks). The reader picks the near level by node name today; it can use the ranges.

**Confirmed safe:** no 4.x scene object in the install carries the optional bounding box, so the
reader's bail-out on that field never triggers.

## Not yet audited

The same treatment is due for the other inputs the pipeline reads, starting with textures:

- **Texture resolution.** NIFs often name a `.tga` that the client ships only as `.dds`. The asset
  library resolves these, but `mpk textures` reported 14 of 30 references for `bhouse2`/`b-forge1` as
  missing when the `.dds` sat loose in `zones/Nifs`: two code paths disagree. `tools/reconvert-zone.sh`
  also rebuilds models without gathering the textures that newly recovered geometry needs.
- DDS formats, alpha and mipmaps; `.kfa` animation files (M7b); and the zone data files (`nifs.csv`,
  `fixtures.csv`, the heightmap and terrain textures).
