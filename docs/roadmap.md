# Roadmap

Where this project has been and where it is going: a macOS Godot 4 (.NET) client that lets a
person log into an OpenDAoC server and actually play — move around a textured world under their own
control, see other players and NPCs, chat, and (further out) fight and use their character.

This replaces the original "MVC" framing (render a capsule at a server-sent position), which is
**done**. The milestones below are grouped into what we've accomplished (M0–M4) and the stepwise
goals that lead to a minimally playable client (M5–M9), with honest notes on the two genuine walls.

## Guiding definitions

- **Minimum Viable Client (MVC) — done.** Protocol proven end to end: log in, pick a character,
  enter the world, hold a stable session. A capsule stands at the right coordinates.
- **Minimum Viable *Player* (MVP) — the next real target (M4–M6).** A person can log in, **walk
  their own character around a textured world**, **see other players and NPCs move**, and **chat**.
  No combat, inventory, or skills required. This is the "minimally playing" state. It is reachable
  over **TCP alone** — UDP is an optimization, not a prerequisite (the server accepts TCP position
  updates, which our keep-alive already sends).
- **Mostly playing (M7–M9).** Combat you can see and participate in, your character's vitals and
  inventory, targeting and basic interaction — enough that the game reads as the game, within the
  1.65 ruleset OpenDAoC serves.

Scope for the milestones below: **client protocol 1.127**, against **OpenDAoC** (encryption off);
**no game assets shipped** — everything is converted at runtime from the player's own DAoC install.
Supporting the current official **1.130** client is a later target of the same project, not a
separate one — see "Later: the live (1.130) client" below.

---

## Accomplished

Each finished milestone carries a `**Done** <date> · shot <name>` line: the completion date and the
screenshot that shows it. Shot names refer to images in the owner's progress archive (`YYYY-MM-DD_NN`
prefix); the archive itself is not in this repo, and **whether any shot is published is the owner's
decision** — this line only names them in milestone order so a published timeline can follow it.
"shot: none" means the milestone has no single representative image (e.g. a protocol-only step).

### M0 — Protocol foundation ✅
**Done** 2026-10-13 · shot: none (protocol library — nothing visual to show)
Packet framing and the client checksum, ported from DOLSharp/OpenDAoC and verified byte-for-byte
against a recorded 1.127e session. Endian-explicit readers/writers. `DaocConnection` (background
receive loop → channel) and `PacketLog`. ~63 protocol tests.

### M1 — Login through the character screen ✅
**Done** 2026-10-14 · shot: none (character list seen live in the Godot Run window; not archived)

The full account flow, verified live against OpenDAoC and against captures: 0xF4/0x22 handshake,
0xA7 login, the character screen (0xFC/0xFE overview; 0xFF create/customise/delete with the
cross-realm slot and stat rules). `ClientSession` is a linear request/reply state machine
(`Connected → LoggedIn → CharacterScreen`).

### M2 — Enter the world and stay (the original MVC) ✅
**Done** 2026-10-14 · shot: none (capsule at the server coordinates, seen live; not archived)

World entry burst (0x9D→0xB1, 0xD4→0x20 position…0x4E, 0xE8, 0xBF→0x2D), keep-alive (0xA9 every 1 s,
0xA3 every 10 s), and a clean `&quit` logout. The Godot client logs in, lists characters, enters the
world, and shows the character as a capsule at the server's coordinates; sessions hold 5+ minutes.
`tools/DaocCli` does the same headlessly for testing. **Gates 1–3 complete.**

### M3 — Static asset pipeline (beyond the original plan) ✅
**Done** 2026-10-28 · shot 2026-10-24_06_scale-fixed-keep-and-village
(earlier steps: 2026-10-22_02_first-textured-terrain, 2026-10-24_13_keep-tan-towers-wide)

A whole track the MVC never promised, now working and tested (~48 asset tests):
- **MPK/NPK reader** with CRC verification and lazy inflate.
- **NIF → glTF converter** for static meshes: NetImmerse 4.1/4.2.x (items, buildings) and Gamebryo
  10.1.0.0 (creatures), NiTriShape and NiTriStrips, multi-mesh; per-shape texture resolution and
  per-shape `NiMaterialProperty` colour (the W3a tan-keep fix); Z-up→Y-up; **models convert to metres
  at 0.025 m/unit (W1 resolved)**.
- **Texture resolution** across .dds/.tga/.bmp; a **convert-once asset library** with an install
  **index** and an **overrides/** layer; **zone static-object placement** (from the client's own
  nifs.csv/fixtures.csv) and **real zone terrain** (heightmap PCX → tiled, ground-textured mesh).
- The Godot `WorldView` can already render a full textured zone — terrain plus ~1,500 placed,
  textured buildings — and a standalone `ModelViewer` orbits any converted model (with a headless
  `--shot` capture). `keep.NIF` renders as the tan stone keep, in metres.

**What this means:** the world can be *drawn*. What's missing is making it *live*.

---

## The path to minimally playing (MVP)

The gap from M3 to "a person can play" is almost entirely **interpreting live world-state packets we
currently drain-and-count**, plus **sending the player's own actions**. Every opcode we need is
documented in DOLSharp/OpenDAoC source (GPL, portable with attribution), so this is implementation,
not research — with two exceptions called out under "Walls" below.

### M4 — The player moves (self-driven movement over TCP) ✅
*You control your character; the world is drawn around you.*
**Done 2026-10-30.** Verified live on macOS (Apple M2, Godot 4.7.2 .NET) against our OpenDAoC fork:
Eodarms walks across real, textured Camelot Hills terrain, follows slopes up and down, with a chase
camera behind — and the server accepts the self-driven position (no link-death, no rubber-band kick).
- ~~Replace the free-fly debug camera with a **chase camera** on the player.~~ `SpringArm3D` chase
  camera seated behind the body, colliding with terrain and scenery so it rides above slopes and
  pulls in off walls; Tab toggles the old free-fly debug camera.
- ~~Feed keyboard/mouse input into the player's position/heading and let the **existing 0xA9 sender**
  transmit it.~~ `ClientSession.MoveToAsync` + event-driven send (~10 Hz moving, ~1 Hz idle) on top
  of the keep-alive; WASD/mouse drive position and heading.
- ~~**Unify the world scale.**~~ **Done 2026-10-29.** Region units and NIF units share one scale,
  **0.025 m/unit**; `WorldView.RegionMetresPerUnit = 0.025` and the terrain glb (already metres from the
  exporter) is no longer double-scaled. Calibrated live: the keep renders at ~52 m on real Camelot
  Hills terrain with the player capsule human-height in its courtyard (commit cf5a0b0; W1 closed).
- ~~Collide with terrain so the character walks on the ground (Godot physics on the terrain mesh).~~
  A downward raycast pins the body to the terrain trimesh. Key fix: the exported terrain is wound
  face-down, so the collider needed `ConcavePolygonShape3D.BackfaceCollision = true` for the ground
  ray to register (the symptom was floating at spawn and walking horizontally off slopes).
- **Done when:** ~~you spawn in Camelot Hills and walk your character across real, textured terrain,
  and the server accepts your position (no link-death, no rubber-band kick).~~ **Met.**
- *Known remainder (not M4-blocking):* placed models whose glTF materials are named generically
  (`meshN`) instead of after their texture stay untextured — an export-pipeline (material-naming)
  fix, tracked for the asset track. The unfocused-window send throttle (macOS pauses an idle
  background window, which can starve 0xA9 and trip link-death) is a play-comfort fix for later.

### M5 — The world is alive (incoming entity state)
*Other players and NPCs appear and move.*
- Build a **world-packet dispatcher**: the receive loop stops counting opcodes and routes them to
  handlers. This is the structural piece the client has lacked (there is no `NetworkBridge` yet —
  this milestone creates that seam between protocol and Godot).
- Parse and apply the in-world server packets, in rough priority: **0xA1-family object/NPC position
  and spawn/despawn**, player spawns, **remove-object**, and heading/state updates. Represent each as
  a scene entity (capsule or converted model) that moves as updates arrive.
- Interpolate between updates so movement is smooth, not teleporting.
- **Done when:** standing in a populated zone, you see NPCs and any other players/bots move around in
  real time at roughly the right places.

### M6 — Talk (chat in and out)
*You can communicate.*
- Parse incoming chat/message packets (say/broadcast/send/system) into an on-screen log.
- Send chat and slash-commands from a text input (we already send `&quit` as 0xAF; chat is the same
  command channel).
- **Done when:** you can `/say` and see it, and read what NPCs/system/other players send.

> **MVP reached at M6:** log in → walk a textured world → see others move → chat. This is the
> "minimally playing" milestone and the right moment for a tagged release and a community show-and-tell.

---

## The path to mostly playing

These are larger and some touch the walls. Order is provisional; each is independently valuable.

### M7 — Your character, visibly (skinned models + animation)
*Characters and creatures look like themselves and move.*
- The hardest asset work: **skinning** (NiSkinInstance/bone weights → glTF skin) and **animation**
  (.kfa clips). Static bind-pose geometry exists today; this makes it rigged and animated.
- **Reference-grade prior art exists**: OfflineDAoC's GPL-3.0 pipeline has working skinning, figure
  assembly (parts by bone name) and .kfa decode — see `docs/offlinedaoc-asset-pipeline.md`. Adopt the
  approach with attribution; this de-risks M7 substantially.
- Figure assembly from body parts + equipment; realm/class/race appearance.
- **Done when:** your character and the NPCs render as DAoC models with idle/walk/run animation.

### M8 — Vitals, targeting and interaction
*The HUD and the basics of "doing things."*
- Parse health/mana/endurance, level/XP, and the stat/update packets into a HUD.
- Target an entity; basic interact (talk to NPC, open a merchant/dialog) — read-only first.
- **Done when:** you can select a target, see its and your vitals, and talk to an NPC.

### M9 — Combat and inventory you can see
*The game reads as the game.*
- Interpret combat packets (attack/animation/damage/death) so fights are visible; send basic combat
  actions.
- Parse inventory and equipment; show the character's items; equip/move within the 1.65 ruleset.
- **Done when:** you can engage a mob, see the exchange and outcome, and manage what you're wearing.

---

## The two walls (known hard problems)

Neither blocks MVP; both matter for a fuller client. Documenting them so they're planned, not
stumbled into.

1. **UDP movement (port 10400).** High-frequency position updates run over UDP on retail; the
   framing is partly known but the update rate and client-prediction/reconciliation are
   **undocumented** (our clearest protocol gap — see CLAUDE.md "Gap: UDP Movement"). **We route
   around it**: TCP 0xA9 position works for both sending our movement and receiving others' (M4/M5),
   just at lower frequency. UDP becomes a smoothness optimization later, and analysing it against a
   live OpenDAoC session is itself a worthwhile protocol contribution.
2. **Skinned/animated characters (M7).** Hard in its own right, but no longer uncharted —
   OfflineDAoC and DAOC_Navigator are GPL references for the skinning/animation/figure-assembly path.

## Later: the live (1.130) client

The project targets protocol **1.127 now** — one `PacketLib` version, encryption off, the path every
OpenDAoC shard already uses. The longer-horizon goal is that **the current official client (1.130)
plays fully on OpenDAoC**, which means learning the whole 1.130 opcode set and supporting it on both
sides. This is a version target that moves over time, not a different project; it comes after MVP so
the client is actually playable at 1.127 first.

Groundwork already done (see `docs/open-questions.md`, section "1.130"): a retail 1.130 session
captured (R1), the live client run against a 1.130-crypto OpenDAoC fork, the world-entry crash
isolated to the server sending a 1.68-era `0xA1` ObjectUpdate (E1), the unhandled entry opcodes
cleared as harmless (E2), and a first action→opcode map built from scripted input (E3).

The real blocker is **client-side encryption/key transport**: the 1.130 client encrypts from the
first packet (RSA-wrapped session key, modified RC4), and no published tool turns that off for 1.130
the way launchers do for 1.127 (`docs/future-work.md`). Until that's solved, 1.130 work stays
server-side (make OpenDAoC serve the live client correctly) and analysis (captures, opcode mapping).

Also out of scope for now, as genuine tangents: sound, a polished UI, and retail-server interop.

---

## Immediate next steps (M5 kickoff)

M4 is complete (self-driven movement on textured terrain, live-verified 2026-10-30). Next is M5 —
making the world alive by interpreting the in-world packets the client currently drains and counts.

1. **Build the world-packet dispatcher** (the missing `NetworkBridge` seam): replace the
   drain-and-count loop in `WorldView._Process` with a router from opcode to handler. This is the
   structural piece M5–M9 all build on.
2. **Parse the 0xA1-family object/NPC position and spawn/despawn packets** (the bulk of what the
   server already sends us — ~23 packets/s were mostly 0xA1) and render each as a scene entity that
   moves as updates arrive; interpolate between updates.
3. Then player spawns, remove-object, and heading/state updates (see M5 for the full list).
