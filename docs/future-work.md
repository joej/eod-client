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
- **NIF → glTF — next.** DAoC meshes are NIF 4.x, which Godot can't import; textures are DDS,
  which it can. First proof: convert one character mesh (loose `.nif` on disk, or pulled from an
  `.npk` via `DaocAssets`) to glTF and render it in Godot. Likely path: Blender headless +
  Niftools addon, or a native converter from niftools `nif.xml`. See the asset-pipeline plan in
  CLAUDE.md.

## Tooling

- **EoD Launcher** (`tools/Launcher/`) — built, not yet run on Windows. Finish validating once
  the client/server loop is proven.
- **pktmon → pcap converter** — our own script (see `docs/open-questions.md`); candidate for
  `tools/` if others would find it useful.
