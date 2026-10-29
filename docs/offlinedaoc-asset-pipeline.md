# OfflineDAoC's asset pipeline — prior art for our NIF renderer

A GPL-3.0 fork of DAoC server+client tooling, `rambodolphin13/OfflineDAoC`, has already built a
working DAoC asset pipeline: NIF → glTF conversion, MPK reading, character skinning and animation,
and two renderers (a Three.js browser "World Workshop" editor and a Godot 4 client). This document
records what they solved, where it agrees with or diverges from our own `DaocAssets` NIF reader, and
what is worth adopting.

It is a **study of their published code**, read in 2026-10-28 from a git-ignored clone under
`private/reference/offlinedaoc` (not committed). We treat it the way we treat DOLSharp / OpenDAoC /
DAOC_Navigator: license-compatible reference, adapt-with-attribution if we port anything, never a
blind lift. Lineage: shadowofze's OfflineDAoC (the original bot server, in our CLAUDE.md) → this fork,
which added the whole asset/client layer.

## Licensing

GPL-3.0 throughout, with preserved upstream notices (their `THIRD_PARTY.md`): DOL/OpenDAoC server,
BuildNav, and — relevant to us — **`nif.xml` is NifTools', GPL-3.0**, the same spec file our own
CLAUDE.md already points at. Textures are upscaled with Real-ESRGAN (BSD-3, not shipped); Three.js is
MIT; Godot is MIT. They ship **no game assets** — everything is converted from the user's own
installed client at runtime — which is the same policy we adopted. So it is clean to study and to
adapt with a file-header attribution. Caveat flagged for any future port: their `net/DaocClient.cs`
and the C# `NiLib` (in their BuildNav tool) may reuse code licensed elsewhere; check file headers
before adapting those two specifically.

## Architecture: spec-driven vs. hand-coded

The central difference, and the reason they have skinning/figures and we (deliberately) don't:

- **Theirs (`pipeline/netimmerse.py`, ~470 lines)** parses NifTools `nif.xml` at run time and
  interprets every block generically — field order, version gates, `cond`/`vercond` expressions,
  arrays, Ref/Ptr linking. One reader covers **all** block types and every version DAoC ships
  (3.x, 4.1.0.12, 4.2.1.0, 4.2.2.0, 10.1.0.0), including skinning and animation blocks, for free. A
  `QUIRKS` table carries DAoC-specific deviations on top. A corpus harness (`corpus_test_nif.py`)
  parses every `.nif` in the client (loose and inside `.npk`/`.mpk`) and groups pass/fail/leftover
  bytes by version string.
- **Ours (`src/DaocAssets/Nif/NifFile.cs`, ~850 lines)** is hand-coded per block and **geometry-only**
  by design: it models exactly NiNode/NiTriShape(/Strips)/NiTexturingProperty/NiSourceTexture/
  NiMaterialProperty for 4.2.x, and for 10.1.0.0 it *scans* for geometry bodies without modelling the
  intermediate blocks (no skinning). It skips unknown blocks by resyncing to the next type string.

Trade-off: their approach is far more complete and was cheaper to extend to skinning; ours is smaller,
dependency-free, strongly validated, and needs no `nif.xml` at run time. For the MVC (static buildings
and items) ours is sufficient. For the "unbuilt frontiers" (skinned creatures/players), theirs is the
model to follow — and their spec-driven reader is a better foundation for that than extending our
hand-coded one block by block.

Note they also use **pyffi** in one place — the character glTF exporter `nif_to_gltf.py` imports pyffi,
not their own reader — and their notes say pyffi *can't* read DAoC's old 4.x item NIFs, so they keep a
separate `tools/pet-art/nif4_geom.py` for those. Our single reader handles 4.x cleanly; that is a point
in our favour and a reason not to adopt pyffi.

## Where we are independently confirmed

- **MPK format: validated field-for-field.** Their `daoc_mpk.py` and our `Mpk/MpkArchive.cs` agree
  exactly: "MPAK" magic, a 16-byte header XOR-masked by byte index (crc / dir-size / name-size /
  count), three zlib regions (name, directory, per-member), and 0x11C = 284-byte directory records
  (256-byte name + the same seven uint32s). **Ours additionally verifies the directory and per-member
  CRC-32s and sizes; theirs does not.** No change needed to our MPK path.
- **Coordinate convention: confirmed.** Their `Z_UP_TO_Y_UP` matrix is `(x, y, z) → (x, z, -y)`,
  identical to our `GltfExporter.ToYUp`.
- **W3a mechanism (texturing): confirmed.** Their static path reads the base texture from
  `NiTexturingProperty` and the colour from `NiMaterialProperty` diffuse, exactly as we now do.
- **Multiple UV sets per shape are real**, and DAoC **heads carry the face mapping in UV set 1** (both
  their `nif_to_gltf.py` and `nif_static.py` select UV sets per material slot). Our reader keeps only
  the first UV set — fine for buildings, but character heads will need set 1.

## What they answer that we had open

- **W1 (world unit → metres): measured.** They use `UNITS_TO_METRES = 0.025` ("a DAoC humanoid is ~72
  units ≈ 1.8 m"), applied in both the character and zone paths. Our `GltfExporter` leaves scale at 1
  with W1 explicitly open. **Recommendation: adopt 0.025** (verify against a known model's height
  before baking it in; W1 says to measure, and this is the measurement, cross-checkable).
- **W3c (collision / climb volumes): our hypothesis was wrong — corrected.** We had guessed these were
  identified by **material name + alpha ≈ 0.25**. The client (and so their pipeline) identifies them
  by **node name**, and cites the `game.dll` offsets:
  - The first depth-first node named exactly `collidee` (any case) is the model's wall geometry; the
    client hides that whole subtree and triangle-tests it (`game.dll 0x45f865`, `0x483ac6`). City
    collision patches use `__patch_collidee`; door leaves carry `collideeNN`.
  - Nodes whose name **starts with** `climb` (but not `sclimb`, the later Climb-Walls keep points) are
    collision + a climb trigger (`game.dll 0x482bf0` strncmp `climb`, `0x464995`).
  The alpha ≈ 0.25 we saw is incidental, not the signal. **This is the correct way to drop gameplay
  volumes**: by node name, matching the client, not by an alpha threshold.

## Where they diverge from us — the one that matters for our keep render

- **Untextured, UV-less shapes: they DROP; we RENDER in material colour (W3a).** Their static converter
  (`nif_static.py`, the `if not uv and texture_name(tp) is None` guard) treats a shape with no UVs and
  no texture as **helper geometry and discards it** (counted as hidden triangles), *unless* it is an
  effect shape whose material is animated. Our W3a resolution does the opposite: we render such a shape
  in its `NiMaterialProperty` diffuse colour — which is exactly what makes the keep's tan towers appear.
  Under their rule those towers would vanish.
  - Reconciliation: our keep's bodies carry a deliberate `NiMaterialProperty` (tan "Material #17") and
    **are** the visible walls, not helpers; theirs is tuned against *effect* NIFs (emitter boxes,
    alpha-0 planes) and *door* collision leaves. Neither rule is universally correct.
  - **Likely-right rule (open):** drop a UV-less/untextured shape only when it is under a
    `collidee`/`climb` node, OR has no `NiMaterialProperty` at all; otherwise render it in its material
    colour. We should revisit our W3a drop/keep decision with their counter-examples in hand before we
    convert effect NIFs, so we don't render emitter boxes as solid tan slabs. Tracked in W3c.

## What they solved that we have not (the "unbuilt frontiers")

Reference-grade, directly relevant when we build the skinned-figure track:

- **Skinning** (`nif_character.py`): linear-blend skinning with a real glTF skin. Weights from
  `NiSkinInstance` / `skin_data.bone_list[].vertex_weights`, top-4 by weight, normalised, inverse-bind
  from bone rest world matrices. Their hard-won bug notes are worth having: writing the row-vector
  matrix row-by-row **is** the column-major glTF layout (transposing was "the stretched-arms bug"); and
  some monster NIFs store bones out of the skinned pose, so they rebuild the bind pose from the skin
  data's own bone transforms when it reduces edge-stretch.
- **Figure assembly** (`build_cata()`): the current player/NPC path assembles a figure from **separate**
  files — skeleton `sfigNNN.mpk`, parts `figNNN.mpk` — driven by gamedata CSVs (`fig3map`/`fig3parts`,
  `pskins`, face/hair/eye/skin colour maps). Parts skin to bones **by name** (each part's
  `NiStringsExtraData` lists its skin bones in order). Worn items map to part slot + mesh variant.
- **Animation** (`.kfa` clips): full keyframe decode for both 4.2.x and Gamebryo Catacombs clips;
  quaternion or Euler (sampled at 30 fps) keys; clip list/speeds from CSVs. We model none of this.

## DAoC NIF quirks worth porting (their reverse-engineering, so we don't re-derive)

All found empirically against the real client; cited to `game.dll` offsets where noted above.

- **Doors**: a node named `door L01`/`R01`/`door01`, or inside a `doorNNN` group, **with** a
  `NiKeyframeController` is an animated leaf and must NOT be baked into the static mesh (their "Mag Mell
  bug"). Export closed/open poses from first/last keyframe. Portcullises slide (translation keys only).
- **Weapon grip**: node `held` carries the weapon frame; a roll correction fixes hammers/scythes drawn
  upside-down.
- **Embedded textures**: `NiSourceTexture` with `use_external = 0` → `NiPixelData`; handles 24/32-bit
  with channel masks and 8-bit paletted via `NiPalette` (256 RGBA; palette-without-alpha → force
  opaque). We currently only read **external** texture names (`NiSourceTexture` file name); embedded
  pixels we don't decode. Some city/effect models need this.
- **Detail / x2 maps** modulate over the base and must be doubled **in shader**, not pre-doubled in the
  PNG (pre-doubling clips at white).
- **Two-texture terrain/tree blend**: texture slots indexed by the shape's `NiIntegerExtraData`
  (`Texture1Index`/`Texture2Index`/`DetailIndex`/`DarkIndex`); blended by vertex alpha — with Hibernia
  terrain swapped for reasons they flag as "not understood from the client code yet".
- **NiLODNode**: keep only the most-detailed child. **NiBillboardNode**: billboard mode from flag bits
  5–6. **NaN transforms**: some scene nodes carry broken matrices (char-screen `Box02`) and must be
  skipped. **NiTriStrips**: alternate winding per strip, drop degenerate — which our reader already does.

## Where they offer nothing — still open for us too

- **W3b frontiers `!`-token keep textures: unsolved there as well.** Their `texture_name()` takes the
  literal `NiSourceTexture` filename, strips path/extension and lowercases it — no `!`-token rewriting
  and no realm/keep-level texture binding anywhere in the static/zone/texture-index code. So the
  frontiers keep-skin problem (W3b) is a real gap in their pipeline, not borrowable prior art. (Their
  `build_emblems.py`, not studied, may touch claimable-keep emblems; separate matter.)

## Bottom line

This is a boon: it independently confirms our MPK reader, coordinate map and W3a texturing mechanism;
it measures W1 (0.025); it **corrects** our W3c (collision is node-name `collidee`/`climb`, not
alpha); it flags a real design question in our W3a drop/keep rule; and it is a reference-grade,
license-compatible model for the skinning/animation/figure track we have not built. It does **not**
solve W3b (frontiers keep textures). Our protocol-first MVC thesis and our small, validated,
dependency-free reader remain the right shape for where we are; their pipeline is the map for where the
asset track goes next.
