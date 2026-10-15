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

## Tooling

- **EoD Launcher** (`tools/Launcher/`) — built, not yet run on Windows. Finish validating once
  the client/server loop is proven.
- **pktmon → pcap converter** — our own script (see `docs/open-questions.md`); candidate for
  `tools/` if others would find it useful.
