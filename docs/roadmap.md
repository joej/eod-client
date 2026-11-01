# Roadmap

Where this project has been and where it is going: a macOS Godot 4 (.NET) client that lets a
person log into an OpenDAoC server and actually play — move around a textured world under their own
control, see other players and NPCs, chat, and (further out) fight and use their character.

This replaces the original "MVC" framing (render a capsule at a server-sent position), which is
**done**. The milestones below are grouped into what we've accomplished (M0–M6) and the stepwise
goals that lead to a client you can walk the DAoC world in (M7–M9), with honest notes on the two
genuine walls.

## Guiding definitions

- **Minimum Viable Client (MVC) — done.** Protocol proven end to end: log in, pick a character,
  enter the world, hold a stable session. A capsule stands at the right coordinates.
- **Minimum Viable *Player* (MVP) — the target (M4–M7).** A person can log in, **walk their own
  character around the DAoC world as it looks in-game** (real terrain and buildings), **see their
  character and other players/NPCs render as DAoC models** (not capsules) and move, and **chat**.
  Not a playable game — no combat, inventory, or skills — but the world and its inhabitants render as
  DAoC. The movement/entity/chat parts (M4–M6) are reachable over **TCP alone** (UDP is an
  optimization, not a prerequisite); the models part (M7) is the hardest asset work and completes it.
- **Mostly playing (M8–M9).** Combat you can see and participate in, your character's vitals and
  inventory, targeting and basic interaction — enough that the game reads as the game, within the
  1.65 ruleset OpenDAoC serves.

Scope for the milestones below: **client protocol 1.127**, against **OpenDAoC** (encryption off);
**no game assets shipped** — everything is converted at runtime from the player's own DAoC install.
Supporting the current official **1.130** client is a later target of the same project, not a
separate one — see "Later: the live (1.130) client" below.

---

## Accomplished

eveofdarkness.org builds its progress panel from this file, so milestone entries follow one
shape. When a milestone finishes, add ✅ to its heading **and**, on the very next line, its Done line:

```
### M4 — The player moves (self-driven movement over TCP) ✅
**Done** 2026-10-30 · shot 2026-10-30_16_chase-camera-up-a-slope
(earlier steps: 2026-10-29_15_keep-at-scale-capsule-in-courtyard)
*You control your character; the world is drawn around you.*
Prose about what was done goes here, below.
```

- **Done line:** `**Done** YYYY-MM-DD · shot <name>`, or `**Done** YYYY-MM-DD · shot: none (<why>)`
  when there's no single representative image (e.g. a protocol-only step). Nothing else on the line.
- **Earlier steps (optional):** the line right after, `(earlier steps: <name>, <name>)`, for shots that
  show the way there.
- **Shot names** are image names in the owner's progress archive without the extension, in the form
  `YYYY-MM-DD_NN_short-description` (the date is the capture date). The archive itself is not in this
  repo, and **whether any shot is published is the owner's decision**: naming a shot here doesn't
  publish it.
- **Unfinished milestones** keep the italic one-line summary right under the heading; the site shows
  it for the next milestone.
- Headings stay `### M<n> — Title`, numbered in order; "MVP reached at M<n>" marks the MVP.

The site's build stops rather than guess if a ✅ milestone has no Done line in this form.

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

## The path to walking the DAoC world (MVP)

The gap from M3 to "a person can play" is almost entirely **interpreting live world-state packets we
currently drain-and-count**, plus **sending the player's own actions**. Every opcode we need is
documented in DOLSharp/OpenDAoC source (GPL, portable with attribution), so this is implementation,
not research — with two exceptions called out under "Walls" below.

### M4 — The player moves (self-driven movement over TCP) ✅
**Done** 2026-10-30 · shot 2026-10-30_16_chase-camera-up-a-slope
(earlier steps: 2026-10-29_15_keep-at-scale-capsule-in-courtyard)
*You control your character; the world is drawn around you.*
Verified live on macOS (Apple M2, Godot 4.7.2 .NET) against our OpenDAoC fork:
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

### M5 — The world is alive (incoming entity state) ✅
**Done** 2026-10-31 · shot 2026-10-31_17_npcs-in-cotswold-village
*Other players and NPCs appear and move.*
Verified live on macOS against our OpenDAoC fork: standing in Cotswold, the village's NPCs appear as
capsules around the buildings and move in real time (176 entities tracked in one session). The
receive loop no longer just counts packets — a dispatcher routes them to handlers and the world fills
in.
- ~~Build a **world-packet dispatcher**~~. `godot/scripts/NetworkBridge.cs` is the new seam between
  the protocol library and Godot: it parses each in-world opcode and raises typed events, replacing
  `WorldView`'s drain-and-count loop. The structural piece the client had lacked.
- ~~Parse and apply the in-world server packets~~: 0xA1 ObjectUpdate (NPC/mob position, the bulk of
  the stream — verified byte-for-byte against the 1.127e capture), 0xDA NPCCreate, 0x4B PlayerCreate,
  0xD9 ObjectCreate, 0xA2 RemoveObject, 0xE1 ObjectDelete. Each is a capsule, realm-tinted from its
  create packet, placed by converting zone-local 0xA1 coords to region-global via the zone offset.
- ~~Interpolate between updates~~ so movement glides rather than teleporting (per-NPC updates are ~1 Hz).
- **Done when:** standing in a populated zone, you see NPCs and any other players/bots move around in
  real time at roughly the right places. **Met** (Cotswold NPCs, live).

> **Known remainder (not M6-blocking):** the 0xDA/0x4B *create* packets that carry realm colour and
> name arrive during the world-init burst, which `ClientSession.EnterWorldAsync` consumes before
> `WorldView` starts draining — so entities first seen via a bare 0xA1 render neutral grey instead of
> realm-tinted and named. Buffer the init-burst packets through the bridge to fix it. Also found: the
> 0xA1 health byte exceeds 100 for some NPCs (open question W4). Server-side, the test account was made
> GM so the movement monitor stops kicking the fast debug camera (daoc-re #2).

### M6 — Talk (chat in and out) ✅
**Done** 2026-10-31 · shot 2026-10-31_19_say-hello-in-cotswold
*You can communicate.*
Verified live on macOS against our OpenDAoC fork: standing in Camelot Hills, the chat log fills with
the server's zone-entry and system lines, `/say hello` echoes back on the Say channel, and `/who`
returns "1 player currently online" — both the receive and send halves of the one text channel work.
- ~~Parse incoming chat/message packets~~ into an on-screen log: the single 0xAF Message packet
  (`src/DaocProtocol/Packets/ChatPackets.cs`), decoded to a chat-type byte + text, colour-coded by
  channel. The UI-control types (SocialInterface structured strings, the centred ScreenCenter banner)
  are classified out of the log. Verified against the 1.127e capture (`ChatPacketTests`).
- ~~Send chat and slash-commands from a text input~~: `ClientSession.SayAsync` / `SendCommandAsync`
  ride the same 0xAF command channel as `&quit` — a typed `/say` (or any `/command`) is rewritten to
  the `&`-prefixed wire form the server reads. A `LineEdit` in `WorldView` sends on Enter and returns
  control to the game, so typing never drives the character.
- **Done when:** you can `/say` and see it, and read what NPCs/system/other players send. **Met.**

### M7 — The world and its inhabitants render as DAoC (textured world + skinned models)
*What you walk through, and who you see, looks like DAoC — not placeholders.*

This is the MVP-completing milestone and the hardest. A live walk-through of Prydwen Keep
(2026-10-31) catalogued exactly what "renders as DAoC" still needs; it splits into two buckets plus a
small control item. Buckets **a** and **b** are independent and **a** gives a fast, dramatic visible
win, so they may be sequenced M7a → M7b.

**M7a — static assets texture correctly ("the world looks like DAoC").** Today the client places real
terrain and building geometry, but the surfaces are wrong in three repeating ways (all seen live):
- **Magenta walls/columns** — the debug fallback showing through because some shapes' textures aren't
  bound when a model is loaded via the zone-placement path (interiors are full of it). Make the
  zone/world load path bind these the way the close-up path already does. (W3a live follow-up.)
- **Flat tan stone** — the ~2/3 of keep shapes that store **no UVs** render in their material colour
  (tan) instead of tiled stone; the stairs show it starkly (textured risers, tan step tops). Resolve
  the frontiers-style name-bound textures and the UV-less classic shapes to real stone. (W3/W3a/W3b.)
- **Foliage with no alpha cut-out** — trees are DAoC's correct crossed-plane billboards, but without
  alpha-testing the transparent background reads as solid dark rectangles. Apply the alpha cut-out
  (the crossed-plane geometry itself is right — don't replace it).
- **Fixtures sit flush on terrain** — houses/daises that currently float or sink get placed on the
  ground correctly (the keep already does; generalise it).

**M7b — characters and creatures are DAoC models ("the inhabitants look like DAoC").** The capsules
become real figures:
- **Skinning** (NiSkinInstance/bone weights → glTF skin) and **animation** (.kfa clips). Static
  bind-pose geometry converts today; this makes it rigged and animated.
- **Figure assembly** from body parts + equipment (parts by bone name); realm/class/race appearance.
- **Reference-grade prior art exists**: OfflineDAoC's GPL-3.0 pipeline has working skinning, figure
  assembly and .kfa decode (`docs/offlinedaoc-asset-pipeline.md`). Adopt the approach with attribution;
  this de-risks M7b substantially. DAOC_Navigator is a second GPL reference.
- Fixes the floating-capsule placement too: real models ground against the terrain.

**M7c — strafe (small, but MVP).** Add strafe-left/right (Q/E) to the existing WASD + turn rig: with
the mouse-chase camera, forward + strafe is how you actually navigate. A couple of input reads and a
lateral vector, not a milestone's worth of work, but it belongs in the MVP.

- **Done when:** you walk the keep and the village and the stone/wood/foliage read as DAoC (no magenta,
  no flat-tan fills), your character and the NPCs render as DAoC models with idle/walk/run animation,
  and you can strafe.

> **MVP reached at M7:** log in → walk the DAoC world as it looks in-game (real terrain and buildings)
> → see your character and NPCs as **DAoC models**, not capsules → chat. This is the "walk around the
> game world" milestone — not a playable game (no combat or skills), but the client renders the world
> and its inhabitants as DAoC. The right moment for a tagged release and a community show-and-tell.

---

## The path to mostly playing (post-MVP)

These come **after** the MVP (M4–M7): M8–M10 are the post-MVP milestones. They are larger and some
touch the walls; order is provisional, each is independently valuable, and M10 (asset quality tiers)
runs in parallel rather than in sequence.

### M8 — Vitals, targeting, interaction — and solid world (post-MVP)
*The HUD, the basics of "doing things," and a world you can't walk through.*
- Parse health/mana/endurance, level/XP, and the stat/update packets into a HUD.
- Target an entity; basic interact (talk to NPC, open a merchant/dialog) — read-only first.
- **Fixture/interior collision** — deferred from the M7 walk-through, and intentionally **not** MVP
  ("looks like DAoC" ≠ "collides like DAoC"). Today the ground raycast follows only the terrain
  heightmap (a separate collision layer), so building geometry isn't solid: the player sinks into a
  raised dais/keep floor, passes through a slope or stairs, and walks through walls. DAoC resolves the
  player against the `collidee`/`climb` node-name volumes inside each model (identified in W3c); apply
  those to stand on floors/daises/stairs, block at walls, and climb climb-volumes. *Possible early-lite
  (still M8):* extend the ground ray to also hit floor geometry so you stand on daises — ~80% of the
  indoor "feels right" for little work, but still collision, so it stays post-MVP.
- **Jump** (spacebar) — also deferred from the walk-through, post-MVP. Not needed to walk the world,
  and it opens a protocol question (the 0xBE jump packet seen in 1.130 RE).
- **UI direction** (noted 2026-11-01; input for the HUD, not a separate milestone). Start from the
  retail UI: list the windows a player relies on (vitals, target, chat, quickbar, map, inventory,
  group) and keep their layout and behaviour, since players have years of muscle memory. Give them a
  cleaner look in the dev overlay's style: dark translucent panels, small monospace labels, one accent
  colour. Modernising can regress a dense window, so compare each one with the original. The community
  already reskins the retail client's windows (Tubben's, MxN, Ghost and Derida UI, all for 1.127E UI
  scaling; listed on prondrknorr.com). Later, the client could read the skin a player has installed,
  the same way it reads their game assets. That depends on the skin format, which is unchecked: read
  one pack before planning it. Never ship a skin with the client: none states a licence.
- **Done when:** you can select a target, see its and your vitals, talk to an NPC, and stand on/bump
  into the world's solid geometry instead of passing through it.

### M9 — Combat and inventory you can see
*The game reads as the game.*
- Interpret combat packets (attack/animation/damage/death) so fights are visible; send basic combat
  actions.
- Parse inventory and equipment; show the character's items; equip/move within the 1.65 ruleset.
- **Done when:** you can engage a mob, see the exchange and outcome, and manage what you're wearing.

### M10 — Asset quality tiers and community reskin packs
*Classic by default; progressively better by choice — and others can contribute whole new looks.*
Post-MVP, parallel to M8–M9 (not blocking them), building on the models M7 brings. The asset library
was built for this from the start
(convert-once flat library + an `overrides/` layer that wins at load time; see
`docs/project` asset-library notes): "enhanced" is just a better-named-the-same asset layered over the
classic one, so most of this needs **no renderer changes**, only content and a pack selector. The
intent is a visible progression over time *and* an open door for the community to reskin the game —
with their own rendering, AI upscales, or new art — which should draw contributors and build the
project's community.

The tiers (cheapest first; each is independently shippable):
- **Tier 0 — faithful but crisp (free).** Rendering settings on the textures we already convert:
  mipmaps, anisotropic filtering, correct texture repeat/tiling on terrain. Helps *both* looks; the
  current soft ground is partly this. Verified faithful: a real-client reference shot (Cotswold
  gathering) shows DAoC's base grass is genuinely low-detail, so flat-ish ground is correct — the
  "alive" difference is vegetation and real models, below.
- **Tier 1 — upscale the owner's own textures.** Run the extracted DDS through an upscaler
  (ESRGAN-class) at convert time → `overrides/`. Same art, 4–8× resolution. Stays within the assets
  policy (a transform of legitimately-extracted files; output never ships in the repo/release).
- **Tier 2 — detail DAoC had that we don't apply yet.** The biggest "why does the real shot look
  alive and ours looks empty" factor, and it is **DAoC data we currently skip, not new art**:
  - **Ground vegetation (grass tufts).** DAoC does *not* place grass as objects in `zone.csv`; the
    client reads per-zone **ground-cover density masks** (`grass`/`dense`/`shade`/`water` layers in the
    zone's `dat<NNN>.mpk`; see `docs/future-work.md`) and **procedurally scatters** grass-clump
    billboards weighted by them. We extract the terrain and placed objects but not these masks. Plan:
    read the masks (the extractor already reads mpks), then scatter billboards over the terrain with a
    `MultiMeshInstance3D` (thousands of cheap quads), density from the mask — exactly as the original.
  - Also in this tier: generated normal maps from diffuse textures, PBR materials, better foliage/tree
    rendering.
- **Tier 3 — genuinely new assets.** Hand-made or licensed models/textures that replace DAoC's. Same
  `overrides/` mechanism, but this is where fresh/licensed art is needed to distribute anything (the
  "no proprietary content" line); a real art project rather than a pipeline tweak.

Enabling work (do the small part before override content piles up):
- **Named packs**, not one flat `overrides/`: make the load path "selected pack(s) → base library"
  (e.g. `packs/classic/`, `packs/hd/`), so classic-vs-enhanced is a selector and packs can stack.
  Formalising this early turns packs into a first-class, contributable unit instead of a retrofit.
- A pack manifest + contribution guide so an outside contributor can ship a reskin pack.

- **Done when:** the player can choose a look (at least classic vs. one enhanced pack), grass/vegetation
  renders from the zone masks, and the pack format + guide let someone outside the project publish a
  reskin that drops in without code changes.

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

## Immediate next steps (M7 kickoff)

M6 is complete (chat in and out over the 0xAF text channel, live-verified 2026-10-31 — `/say` echoes
and system/command lines read in Camelot Hills). **M7 is the MVP-completing milestone**: the world and
its inhabitants render as DAoC, not primitives. A live Prydwen Keep walk-through (2026-10-31) set the
scope above (M7a texturing, M7b models, M7c strafe). Recommended order — M7a first gives a fast,
dramatic visible win and de-risks the hard skinning work:

1. **M7c strafe (Q/E)** — do it first as a quick standalone win; it's a couple of input reads on the
   existing movement rig and the owner wants it for chase-camera navigation.
2. **M7a, highest-impact first — kill the magenta.** Interiors are full of the debug-magenta fallback:
   fix the zone/world load path so model textures bind the way the close-up path already does. Then the
   UV-less flat-tan → real stone (W3a/W3b), then foliage alpha cut-out, then flush fixture placement.
3. **M7b skinning + figures** — the hardest part: NiSkinInstance/bone weights → glTF skin, figure
   assembly from parts (by bone name) + equipment, `.kfa` animation (idle/walk/run). Reference prior
   art: OfflineDAoC's GPL-3.0 pipeline (`docs/offlinedaoc-asset-pipeline.md`), adopt with attribution;
   DAOC_Navigator is a second GPL reference.

Fold in when convenient (not blocking): the carried-over M5 remainder — buffer the world-init burst so
NPC *create* packets (realm colour + name) aren't lost and entities stop rendering neutral grey; it's
the same create data M7b needs to pick the right model per entity.
