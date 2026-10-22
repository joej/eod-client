# Future work

Ideas and tangents parked so the current goal — a Godot client that logs in, selects a
character and renders a position against OpenDAoC — stays uncluttered. Nothing here is
scheduled; we triage when the MVC is further along. Open a GitHub issue when a item is ready
to pursue.

## Protocol

- **Modern (1.130) encryption and key transport.** The current official client encrypts from
  its first packet; the server sends no key. Understanding the handshake (0x22 layout, the
  client's key-transport message, RSA parameters, the RC4 key schedule) would let OpenDAoC
  negotiate encryption and, eventually, accept a stock modern client. Pure source- and
  own-capture analysis; a large tangent from the MVC. See `docs/open-questions.md` R1, R6, L11.

## OpenDAoC server

- (none yet)

## Asset pipeline

The owner-run track that turns a client install into files the Godot client can load. Output
never enters the repo or a release (CLAUDE.md, "Game client and assets").

The whole sequence is captured in **`tools/pipeline.sh <client-dir> <model.nif|dir> <out-dir>`**:
it builds the `mpk` tool, converts each model to `.glb`, and gathers the `.dds` those models
reference (loose on disk or extracted from the right `.mpk`) into the same folder, which the Godot
client loads from. The individual stages are the `mpk` subcommands below (`convert`, `textures`).

- **MPK/NPK archive reader — done.** `src/DaocAssets` (`MpkArchive`) reads the `MPAK` container
  with random access (parses only the directory on open, inflates one member on demand),
  verifies the directory and per-member CRC-32, and is bounds-checked throughout. `tools/mpk`
  lists and extracts. Format reimplemented from public specs (DOLSharp, RustDAoC) and verified
  against real client archives; no third-party source copied.
- **NIF → glTF — static path done (items + creatures, shapes + strips, multi-mesh).**
  `src/DaocAssets/Nif` reads geometry (vertices, normals, UVs, triangles; plus the external
  base-texture name) from NetImmerse 4.2.x and Gamebryo 10.1.0.0 NIFs — both `NiTriShapeData` and
  `NiTriStripsData`, and NIFs with more than one mesh — and `GltfExporter` emits a `.glb` via
  **SharpGLTF** (MIT), Z-up → Y-up with winding flipped. `tools/mpk convert` runs it; `godot/scenes/ModelViewer.tscn` loads a
  `.glb` at runtime and orbits it. Byte-verified against a real 4.2.2.0 item (golden hashes in the
  tests). Not Blender: the Niftools addon would add a heavy runtime dependency and its
  old-NetImmerse import path is unproven for our files.
  - **Texturing — done (Godot-side).** The `.glb` embeds **no** texture bytes (stays a
    distributable, geometry-only artifact — assets policy); the glTF material is named after the
    texture (e.g. `PSbeamLt_grey`). `godot/scripts/ModelTexturing.cs` loads `<name>.dds` from the
    folder beside the `.glb` (where `tools/pipeline.sh` gathers them) and assigns it as the albedo.
  - **Texture resolution crosses extensions and is case-insensitive (fixed 2026-10-22).** The
    client's zone NIFs name a texture with a `.tga` that was later re-saved to `.dds` on disk (the
    whole `zones/Nifs` surface was converted to DDS), so an exact-name lookup missed most of them and
    zone buildings rendered slate gray. `AssetIndex.ReadTexture` now resolves a texture by **stem**
    across `.dds`/`.tga`/`.bmp` (preferring `.dds`), and `AssetLibrary.GatherTexture` stores the file
    under a **lower-cased** name so the stem-based Godot loader finds it on case-sensitive filesystems
    too (the NIF reference, the on-disk file and the glTF material name disagree on case). Verified
    live: Camelot Hills went from 13 to 30 gathered textures; 45 of 46 distinct zone models textured
    (526 surfaces), no case-mismatch warnings. The lone untextured model (`giant`) has no
    `NiSourceTexture` at all — correctly left untextured.
    - **Known limitation (next):** the 4.x reader captures **one** texture per model (the first
      `NiSourceTexture`) and applies it to every shape, so a multi-material building (the keep
      references 10 textures: `keepSTONE`, `canvas1`, `Btudorwood`, …) shows only its first skin.
      Per-shape material capture is the follow-up.
    Godot's generic `Image.load()` doesn't recognise `.dds`, but `Image.LoadDdsFromBuffer()` does
    (verified against real client textures), so we read the bytes and decode from the buffer. Both
    the model viewer and the live `WorldView` texture their model; a missing/unknown texture leaves
    the model untextured without error. `--textures <dir>` overrides the default folder. Embedding
    the `.dds` as PNG in the `.glb` was rejected: it would bake client pixels into a derived artifact.
  - **Versions in this client (verified against the install):** 4.2.2.0 (majority, NetImmerse —
    items), 4.2.1.0, 10.1.0.0 (Gamebryo — creatures). Two different header layouts; the reader
    gates on version. Both `NiTriShapeData` paths are **done** (4.2.x and 10.1.0.0, each
    byte-verified against a real file with golden-hash tests). All are defined in niftools `nif.xml`.
  - **10.1.0.0 header (decoded, verified):** after the Version u32 — User Version u32, Num Blocks
    u32, Num Block Types u16, [SizedString × types], Block Type Index (u16 × blocks). Unlike 4.2.x,
    block bodies carry **no inline type string** (type comes from the index table), and the
    intermediate blocks (NiNode, NiKeyframeController/Data, NiSkin*) have large version-gated
    layouts. Since the goal is geometry, the reader reads the header to bound/validate the file then
    locates each `NiTriShapeData` body by a strict self-validating scan (every field
    consistency-checked). 10.1.0.0 geometry adds Keep/Compress-flags bytes, a leading Data-Flags
    u16, a Consistency-Flags u16, and a Has-Triangles bool vs 4.2.x.
  - **`NiTriStripsData` — done (both 4.x and 10.1.0.0).** Many items (weapons especially) and some
    creatures store triangle **strips** rather than a flat list. Each header layout shares its
    geometry-common read with its `NiTriShapeData`, then parses the strips (`Num Strips`, `Strip
    Lengths`, per-strip points; 10.1.0.0 has a `Has Points` bool, 4.x does not — it is
    `since 10.0.1.3`) and converts to triangles (winding flipped on odd triangles, degenerate stitch
    triangles and the 0xFFFF restart sentinel dropped). **Multi-mesh** NIFs work (e.g. Zombie.NIF
    yields 44 meshes). Byte-verified golden tests for 10.1.0.0 (Skel01) and 4.1.0.12 (a shortbow).
  - **Coverage (measured across figures/items/effects, 4,620 NIFs):** versions 10.1.0.0 (2132),
    4.2.2.0 (1577), 4.2.1.0 (662), 4.1.0.12 (249); all handled by the shape+strips paths. A full
    end-to-end convert of all 617 `figures` NIFs: 614 ok, 3 correctly rejected (no triangle-geometry
    block). The ~410 strips-only 4.x files that used to be rejected now convert.
  - **First proof — done.** 4.2.2.0 item (`bowsprit_alb_01`) and 10.1.0.0 creature
    (`ambrat_black`) both convert and render in Godot. No `.kf` files exist in the client, so no
    skeleton/skinning; creatures are internally rigged (`NiSkinInstance`/`Bip01`) but we take
    bind-pose geometry and defer skin to later.
  - **Licensing:** write from `nif.xml` (GPL-3.0, compatible). Reference only, do not port:
    `dol-leodagan/niflib.net` (DAoC-proven C#, but **GPL-2.0** — version-incompatible to derive
    from) and `niftools/niflib` (NOASSERTION). `BifrostTitan/daoc-niflib` has **no license** —
    off-limits even as reference. SharpGLTF is MIT.
- **Zone static objects — done (placement).** A zone's object placement is plain CSV inside its
  `dat<NNN>.mpk`: `nifs.csv` (NIF index → model file) and `fixtures.csv` (per-object NIF index,
  position, angle, scale, in zone-local coords). `ZoneLayout` joins them and applies the zone's
  region offset (from the OpenDAoC zone table, units of 8192) so positions match a player-position
  packet — verified: Camelot Hills' keep lands ~6 Godot units from Eodarms' spawn. `mpk zone
  <client> <dat.mpk> --offset X Y --out DIR` converts the referenced models (packed one-per-archive
  under `zones/Nifs/`, found by name) and writes `zone.csv` (a placement manifest). `WorldView`
  loads the manifest and instances each object around the character. Verified live: 1,536 Camelot
  Hills objects placed. No reverse engineering — the client ships this as CSV.
  - **Convert-once asset library + install index + override layer — done** (decided & built
    2026-10-20). `mpk index <client> --out index.json` walks the install once and records where every
    `.nif`/`.dds` lives (loose path or archive+member) — archives opened at most once; the index is
    saved under `private/` and doubles as documentation (26,303 assets on the real install). The
    **library** (`AssetLibrary`) is backed by the index: `<lib>/models/<name>.glb` and
    `<lib>/textures/<name>.dds` are produced once per asset, cached on disk, and reused — so a second
    build or a second zone is instant (Camelot Hills went from a >110s whole-install scan to 1s). The
    library is **client-derived content**: owner-local under `private/`, git-ignored, never
    redistributed — it exists so the client loads from it instead of us shipping the originals.
  - **`overrides/` is the path to a redistributable, open asset set.** Unlike the derived library,
    overrides are assets **we or contributors author** (original work, not converted from the
    client), so they are cleanly distributable under their authors' license and can live in a public
    repo. An `overrides/<name>` wins over the converted asset at both build and load time (library
    and Godot both resolve overrides first). Over time the project can populate overrides and invite
    contributions until a player needs no original game files at all — and because a Godot client has
    no 32-bit ceiling (the 2001-era assets do), overrides can be higher-res / remastered. Core
    direction, not polish.
  - `mpk zone` now takes `--index` + `--library` and references the library by name; `WorldView`
    loads zone models from the library (overrides → models, textures from overrides → textures).
    Verified live: Camelot Hills, 1,531 objects placed from the library, session healthy.
- **Zone terrain (ground geometry) — done.** A zone's heightmap is `terrain.pcx` + `offset.pcx`
  (both 256×256 8-bit RLE PCX) in its `dat<NNN>.mpk`, with scale constants in `SECTOR.DAT` (INI:
  `[terrain] scalefactor`, `offsetfactor`). The height formula, verified byte-for-byte against the
  live spawn (Eodarms Z=2896 = terrain 162 × 8 + offset 50 × 32), is
  `Z = terrain[cell] × scalefactor + offset[cell] × offsetfactor`. `Pcx` decodes the maps,
  `ZoneTerrain` reads them + the constants and builds a grid mesh (one vertex per cell, UVs 0..1)
  in region-global coordinates; `mpk terrain <dat.mpk> --offset X Y --out FILE.glb` emits it as a
  .glb (256×256 → 65,536 verts, 130,050 tris). `WorldView` loads it via `--terrain`, at the origin
  scaled 1/100 to match the scene, replacing the flat placeholder. Verified live: Camelot Hills
  terrain + 1,531 objects + character, session healthy.
  - **Next for terrain: the patch textures.** `ter<NNN>.mpk` holds `patchXXXX-YY.dds` tiles (the
    painted ground surface). Not wired yet — the terrain renders as plain green. Mapping tiles to
    the heightmap grid (and the grass/dense/shade maps in `dat`) is a later step.

## Tooling

- **EoD Launcher** (`tools/Launcher/`) — built, not yet run on Windows. Finish validating once
  the client/server loop is proven.
- **pktmon → pcap converter** — our own script (see `docs/open-questions.md`); candidate for
  `tools/` if others would find it useful.
