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

- **MPK/NPK archive reader — done.** `src/DaocAssets` (`MpkArchive`) reads the `MPAK` container
  with random access (parses only the directory on open, inflates one member on demand),
  verifies the directory and per-member CRC-32, and is bounds-checked throughout. `tools/mpk`
  lists and extracts. Format reimplemented from public specs (DOLSharp, RustDAoC) and verified
  against real client archives; no third-party source copied.
- **NIF → glTF — 4.2.x static path done.** `src/DaocAssets/Nif` reads NetImmerse 4.2.x NIFs
  (`NiTriShapeData` geometry: vertices, normals, UVs, triangles; plus the external base-texture
  name from `NiSourceTexture`) and `GltfExporter` emits a `.glb` via **SharpGLTF** (MIT), Z-up →
  Y-up with winding flipped. `tools/mpk convert` runs it; `godot/scenes/ModelViewer.tscn` loads a
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
    gates on version. All three are defined in niftools `nif.xml`.
  - **Geometry (300-file sample):** ~73% `NiTriShapeData`, ~10% `NiTriStripsData`, rest
    node-only. Implement TriShape first, TriStrips second.
  - **First proof:** one static item mesh (e.g. 4.2.2.0 `bowsprit_alb_01`, pure TriShape, no
    skin) → glTF → rendered in Godot. No `.kf` files exist in the client, so no skeleton/skinning
    is needed for the proof; creatures are internally rigged (`NiSkinInstance`/`Bip01`) but we
    read bind-pose geometry and defer skin to later.
  - **Licensing:** write from `nif.xml` (GPL-3.0, compatible). Reference only, do not port:
    `dol-leodagan/niflib.net` (DAoC-proven C#, but **GPL-2.0** — version-incompatible to derive
    from) and `niftools/niflib` (NOASSERTION). `BifrostTitan/daoc-niflib` has **no license** —
    off-limits even as reference. SharpGLTF is MIT.

## Tooling

- **EoD Launcher** (`tools/Launcher/`) — built, not yet run on Windows. Finish validating once
  the client/server loop is proven.
- **pktmon → pcap converter** — our own script (see `docs/open-questions.md`); candidate for
  `tools/` if others would find it useful.
