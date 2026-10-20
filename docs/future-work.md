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
- **NIF → glTF — static path done for both formats (4.2.x items + 10.1.0.0 creatures).**
  `src/DaocAssets/Nif` reads `NiTriShapeData` geometry (vertices, normals, UVs, triangles; plus the
  external base-texture name) from NetImmerse 4.2.x and Gamebryo 10.1.0.0 NIFs, and `GltfExporter`
  emits a `.glb` via **SharpGLTF** (MIT), Z-up → Y-up with winding flipped. `tools/mpk convert` runs it; `godot/scenes/ModelViewer.tscn` loads a
  `.glb` at runtime and orbits it. Byte-verified against a real 4.2.2.0 item (golden hashes in the
  tests). Not Blender: the Niftools addon would add a heavy runtime dependency and its
  old-NetImmerse import path is unproven for our files.
  - **Texturing — deferred to a Godot-side step (needs an extracted-`.dds` folder).** The `.glb`
    deliberately embeds **no** texture bytes (keeps it a distributable, geometry-only artifact —
    assets policy); instead the glTF material is named after the texture (e.g. `PSbeamLt_grey`).
    glTF only sanctions PNG/JPEG and Godot's glTF importer won't follow a `.dds` satellite, so the
    plan is to texture in `ModelViewer.cs`: after loading the `.glb`, read each material's name,
    load `<name>.dds` from an owner-provided folder via Godot's native DDS support, and assign it.
    Not yet written or verified — do it when a folder of extracted `.dds` is available to test
    against. Converting the `.dds` to PNG and embedding it in the `.glb` is rejected: it would bake
    client pixels into a derived artifact.
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
  - **Remaining geometry — `NiTriStripsData` (~10%).** Some creatures (e.g. `Skel01.NIF`) store
    triangle **strips**, not a flat list; those are correctly rejected today ("No NiTriShapeData
    type"). Next geometry task: decode `NiTriStripsData` (strip arrays → triangles) for both header
    layouts. Also pending: multi-mesh NIFs (more than one TriShape in a file).
  - **First proof — done.** 4.2.2.0 item (`bowsprit_alb_01`) and 10.1.0.0 creature
    (`ambrat_black`) both convert and render in Godot. No `.kf` files exist in the client, so no
    skeleton/skinning; creatures are internally rigged (`NiSkinInstance`/`Bip01`) but we take
    bind-pose geometry and defer skin to later.
  - **Licensing:** write from `nif.xml` (GPL-3.0, compatible). Reference only, do not port:
    `dol-leodagan/niflib.net` (DAoC-proven C#, but **GPL-2.0** — version-incompatible to derive
    from) and `niftools/niflib` (NOASSERTION). `BifrostTitan/daoc-niflib` has **no license** —
    off-limits even as reference. SharpGLTF is MIT.

## Tooling

- **EoD Launcher** (`tools/Launcher/`) — built, not yet run on Windows. Finish validating once
  the client/server loop is proven.
- **pktmon → pcap converter** — our own script (see `docs/open-questions.md`); candidate for
  `tools/` if others would find it useful.
