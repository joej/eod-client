# Roadmap

Where this project has been and where it is going: a macOS Godot 4 (.NET) client that lets a
person log into an OpenDAoC server and actually play — move around a textured world under their own
control, see other players and NPCs, chat, and (further out) fight and use their character.

This replaces the original "MVC" framing (render a capsule at a server-sent position), which is
**done**. The milestones below are grouped into what we've accomplished (M0–M6), the milestone that
reaches a client you can walk the DAoC world in (M7, the MVP), and the post-MVP milestones toward
mostly playing (M8–M12), with honest notes on the two genuine walls.

**How this list is kept honest (2026-11-01).** It was first built from where we stood: the protocol,
one zone, and bugs we happened to see. That missed whole parts of the world (water, sky, dungeons,
moving between zones). It is now checked against measured inventories of what the client actually
ships: `docs/nif-coverage.md` (every model file) and `docs/world-coverage.md` (every zone's data, and
the client's other data folders). When either inventory turns up something with no home here, it gets
one.

## Guiding definitions

- **Minimum Viable Client (MVC) — done.** Protocol proven end to end: log in, pick a character,
  enter the world, hold a stable session. A capsule stands at the right coordinates.
- **Minimum Viable *Player* (MVP) — the target (M4–M7).** A person can log in, **walk their own
  character around the DAoC world as it looks in-game** (real terrain and buildings), **see their
  character and other players/NPCs render as DAoC models** (not capsules) and move, and **chat**.
  Not a playable game — no combat, inventory, or skills — but the world and its inhabitants render as
  DAoC. The movement/entity/chat parts (M4–M6) are reachable over **TCP alone** (UDP is an
  optimization, not a prerequisite); the models part (M7) is the hardest asset work and completes it.
- **Mostly playing (M8–M12).** Moving between zones, combat you can see and participate in, your
  character's vitals and inventory, targeting and basic interaction, dungeons and cities — enough that the game reads as the game, within the
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
- ~~Build a **world-packet dispatcher**~~. `NetworkBridge` (now `src/DaocProtocol/Session/NetworkBridge.cs`) is the new seam between
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
> GM so the movement monitor stops kicking the fast debug camera.

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

### M7 — The world and its inhabitants render as DAoC (textured world + skinned models) ✅
**Done** 2026-11-06 · shot 2026-11-06_21_prydwen-gate-models-and-teleporter
(earlier steps: 2026-11-06_22_prydwen-keep-across-the-river)
*What you walk through, and who you see, looks like DAoC — not placeholders.*
Verified live 2026-11-06 by the owner walking Camelot Hills: the keep, village, water and sky as DAoC, the
player's Catacombs figure in armour, and the zone's NPCs and creatures as animated DAoC figures (zombies,
giants, horses, wolves and bears included, after W40 and W43); effects such as the teleporter's ring drawn.

This is the MVP-completing milestone and the hardest. A live walk-through of Prydwen Keep
(2026-10-31) catalogued exactly what "renders as DAoC" still needs; it splits into two buckets plus a
small control item. Buckets **a** and **b** are independent and **a** gives a fast, dramatic visible
win, so they may be sequenced M7a → M7b.

**M7a — static assets texture correctly ("the world looks like DAoC").** The client places real
terrain and building geometry. The walk-through found the surfaces wrong in four ways; the first three
are fixed (status 2026-11-01):
- ~~**Magenta walls/columns**~~ **Fixed 2026-10-31** (W3d, commit 51f0ed2). It was not unbound
  textures: untextured shapes with a default-white colour exported no colour at all, and Godot painted
  its magenta placeholder. The exporter now writes a colour for every untextured shape.
- ~~**Flat tan stone and shimmering placeholder panels**~~ **Fixed 2026-10-31** (W3e, commits e4f50e9,
  ead9f94). They were helper geometry the retail client never draws (collision hulls, climb/pick
  volumes, shadow casters, Hidden-flagged shapes), plus near and far LOD levels drawn on top of each
  other. The reader now applies game.dll's visibility rules and keeps only the near LOD level. Prydwen
  Keep renders as a complete textured keep. **Residual (W3f):** a few numbered collision meshes on
  4.1.0.12 buildings (round-tower top, BritBarn, Btavern) still show a placeholder colour; whether to
  hide them waits on a retail screenshot of those spots. Some wall faces seen from inside a keep are
  back-face culled (low priority).
- ~~**Foliage with no alpha cut-out**~~ **Fixed 2026-11-01.** The reader now parses
  `NiAlphaProperty` (inherited down the node tree) and `NiStencilProperty`, and the exporter writes the
  glTF alpha mode: DAoC's trees are alpha-tested (flags 0x1221, threshold 75), so their leaves export
  as a two-sided cut-out at 0.29 and Godot imports them as alpha-scissor. Rules adapted from
  OfflineDAoC (GPL-3.0). Verified on elm1, bpinea and oak1 rendered in Godot (before: solid quads;
  after: leafy canopies), plus tests on real tree NIFs.
- ~~**Fixtures sit flush on terrain**~~ **Fixed 2026-11-02** (commits 0478b56, 698f368). `fixtures.csv`'s
  On Ground column (and a Z of 0) mean "stand on the terrain"; we had read Z literally, which put 93% of
  all outdoor objects underground. Found by the owner in Camelot Hills, then measured across all 87
  outdoor zones by the new zone check (`mpk zonecheck`, `tools/zonecheck-all.sh`), which also found
  nine collapsed and two half-size terrains (W14). Open from the same review: a second fixture rotation
  (W15), terrain flips and borrowed textures (W16).

**M7b — characters and creatures are DAoC models ("the inhabitants look like DAoC").** The capsules
become real figures:
- **Skinning** (NiSkinInstance/bone weights → glTF skin) and **animation** (.kfa clips). Static
  bind-pose geometry converts today; this makes it rigged and animated.
- **Figure assembly** from body parts + equipment (parts by bone name); realm/class/race appearance.
- **Reference-grade prior art exists**: OfflineDAoC's GPL-3.0 pipeline has working skinning, figure
  assembly and .kfa decode (`docs/offlinedaoc-asset-pipeline.md`). Adopt the approach with attribution;
  this de-risks M7b substantially. DAOC_Navigator is a second GPL reference.
- Fixes the floating-capsule placement too: real models ground against the terrain.
- **Audited 2026-11-02** (`docs/nif-coverage.md`, "Figures and animation"): every 10.1 figure and
  animation file now decodes block by block to its footer (2,874 of 2,892 figures, all 1,542 10.1
  `.kfa` clips, no failures), so the 10.1 layouts the reader needs are checked against the whole
  install. Two kinds of figure: classic (one self-contained skinned file, e.g. `bcommonm.nif`) and
  Catacombs (skeleton + parts bound by bone name). Steps: 10.1 node-tree reader → skin export → one
  classic figure end to end → `.kfa` clips → Catacombs assembly → model-id tables for NPCs. Which
  kind players get is open question W13.
- **Status 2026-11-04:** classic figures for NPCs (2,071 model ids, idle/walk/run/back) and **Catacombs
  figures for players**: every model id's Catacombs version (`monsters.csv` "Cata Nif" and CatSkin
  columns; 697 of 736) is assembled from its race skeleton and one part per slot, bound by bone name,
  with the 10.1 `Cat_*.kfa` clips (`mpk figures` → `models/cata<id>.glb`; `mpk cata <id>` for one).
  Your character and other players are drawn with them, NPCs with the classic ones (as retail, W13).
  Seen live: Eodarms as the Catacombs Briton in the City of Camelot. **Appearance (2026-11-04):** the
  character list's and 0x4B's appearance bytes (`CharacterLook`) pick the face, eye colour, hair style and
  hair colour from the race's options (`models/fig3_<n>.json`, every hair style in the figure), and the
  four face sliders and the mood drive the head's morph targets. **Retail's reading (2026-11-06, W30):**
  the bytes as retail's creator stores them, the morph targets by their position in the head, and the skin
  tone as retail draws it (the material colour on the head and the model's bare-skin limbs, with a faint
  untinted glow on every part). A character made in the retail creator is drawn by its model id (the field
  also carries its body size). **Worn items (2026-11-04):** players wear their body, arms, legs,
  gloves, boots and cloak (0x15 for others, the character list for you; `objects.csv`'s Catacombs columns;
  robes on the long body; hood up). One figure per race and sex (`cata_fig<N>.glb`, 36 in all, 58 MB)
  carries every mesh variant; each model id's own skins are in `cata_models.json`. **Helms and weapons
  (2026-11-04):** the helm on the head (hair hidden), weapons sheathed on the hip or back or in the hand,
  a shield on the arm, by the item NIFs' own frames (`cata_gear.json`, 1,815 item models). **Attack mode
  (2026-11-05):** H asks the server for attack mode (0x74); its answer draws the weapons into the hands and
  the figure stands in the weapon's combat idle (`canims.csv`: one-hand, two-hand, polearm, staff, bow,
  crossbow, hand to hand, scythe). Other players' drawn state comes from their position and heading
  updates (S→C 0xA9 / 0xBA), which now also move them. Seen live: Eodarms drawing his sword in the City of
  Camelot. **Dyes and emblems (2026-11-05):** armour, cloaks, helms and weapons wear their dye
  (`tints.csv`), and cloaks and shields their guild emblem: the logo over a two-colour pattern on the item's
  own surface (`models/colours.json`). Not yet: tattoos, the creator's outfit (W38), NPC gear on classic
  figures (W31, W33), swings and other combat animation (M10).

**M7c — strafe (small, but MVP).** ~~Done 2026-10-31~~ (commit 87c7d90). Add strafe-left/right (Q/E) to the existing WASD + turn rig: with
the mouse-chase camera, forward + strafe is how you actually navigate. A couple of input reads and a
lateral vector, not a milestone's worth of work, but it belongs in the MVP.

**M7d — the zone's environment (from the world-data audit).** Each zone's `SECTOR.DAT` and data
archive describe more than terrain and objects, and we draw none of it yet:
- ~~**Water.**~~ **First pass done 2026-11-01:** `ZoneWater` reads `[riverNN]`, `mpk water` writes
  `water.glb` + `water.csv`, and the client draws each body with a scrolling water shader (Camelot Hills'
  river now runs under the bridge). Still to do: the `extend_*` flags, tessellation, the colour fields,
  lava and ocean types, swimming (M8). The `ripl` overlay turned out not to be used by current clients (W9). The data: `[riverNN]` sections (100 zones): type `RIVER`/`LAKE`/`SWAMP`, a name ("Lake Avalon"),
  surface height, left and right bank lines, texture (`watery`) with a ripple overlay (`ripl`), flow
  speed, colour, tessellation; plus `water.pcx` masks. Camelot Hills' river runs under the bridge
  (height 1916; the riverbed is dry in our client today).
- ~~**Sky and fog.**~~ **First pass done 2026-11-01:** the client's sky is `zones/sky/sky_<name>.dat`
  (chosen per zone or region in `zones/zones.mpk` → `zones.dat`; Camelot Hills → `sky_albion`), not
  SECTOR.DAT's `[sky]` colour or horizon/cloud names, which the client doesn't read. `mpk sky` writes
  `sky.csv` (canopy and cloud gradients, ambient and sun light, fog colour for every weather and time of
  day; cloud layers; sun) and the client draws clear daytime: a sky shader with the canopy gradient, two
  scrolling cloud layers and the sun disk, the file's light, and fog from SECTOR.DAT `[fog]` (clip = view
  distance, amount = fogged share measured back from it: Camelot Hills fogs 340–400 m). **View distance
  is a player setting** (`ClientSettings`, saved in `user://settings.cfg`; F3 or the dev overlay for now):
  classic (the zone's own, the default), ×1.5, ×2, ×4, with the fog scaled to match; a long view
  outgrows the single loaded zone until M8 streams its neighbours. Still to do:
  canopy band elevations and the east/west rule (guessed; W10), `[forest]` fog regions (266 zones),
  `[farground]`, `[rotate]`, moon and stars, and the clock (M12).
- **Static lights.** `lights.csv` (304 zones): torches and lamps.
- **Baked terrain shading.** `shadow.pcx` and `shademap.pcx` (98 and 86 zones).
- Also listed, smaller or unknown yet: terrain holes (`[holes]`, probably cave entrances), snow maps (18
  zones), `textures.csv` + `patch` textures (possibly the "Atlantis terrain" option, W8), `[mongens]`
  (unknown). Dynamic shadows and day/night are renderer and server matters (M12).

**Scope of the MVP.** One outdoor zone at a time, looking and behaving like DAoC: Camelot Hills first,
then any of the 98 heightmap zones. Walking across a zone boundary, dungeons and cities come after
(M8, M11).

- **Done when:** you walk the keep and the village and the stone/wood/foliage read as DAoC (no magenta,
  no flat-tan fills), water fills the rivers and lakes, the sky and fog match the zone, your character
  and the NPCs render as DAoC models with idle/walk/run animation, and you can strafe.

> **MVP reached at M7:** log in → walk a DAoC zone as it looks in-game (real terrain, buildings, water,
> sky) → see your character and NPCs as **DAoC models**, not capsules → chat. This is the "walk around the
> game world" milestone — not a playable game (no combat or skills), but the client renders the world
> and its inhabitants as DAoC. The right moment for a tagged release and a community show-and-tell.

---

## The path to mostly playing (post-MVP)

These come **after** the MVP (M4–M7). Order is the intended sequence, but each is independently
valuable. M13 (asset quality tiers) comes last (owner, 2026-11-08): the classic game first, complete
with its own sound, intro movie and pregame screens, then the better-looking versions.

### M8 — Move through the world ✅
**Done** 2026-11-07 · shot 2026-11-07_23_swimming-the-river-below-prydwen
(earlier steps: 2026-11-07_24_died-and-release-timer, 2026-11-07_25_region-loading-screen)
*Walk out of one zone into the next; travel and die like a player.*
- **Adjacent zones (streaming built 2026-11-07).** The zones of the region within view distance load
  as you move (`WorldView.Streaming`): built a few milliseconds per frame on the main thread (a worker
  thread was 20-40 times slower, waiting on the render thread), models shared between zones loaded once,
  far zones dropped. `MoveToAsync` reports the zone you have walked into; a GM `/jump` within the region
  streams too. The converter now joins each zone's terrain to its south and east neighbours' (the last
  row and column were flat, leaving a step along every border). Retail loads only at region changes
  (owner, 2026-11-07; W46); no loading pause inside a region. Walked live 2026-11-07 (Eodarms, Camelot
  Hills ↔ Salisbury Plains, both ways): the server answered each crossing with "You have entered …" and
  the ground met at the border. Open: single build steps of up to ~50 ms (big models).
  *Zone borders (owner's request, built 2026-11-07):* the dev overlay's "Zone borders" switch (or `--borders`)
  draws a wall and a line along each zone rectangle of the region, with each zone's name and number on its
  side, following you along the edge.
- **Zone jumps and teleports.** `zonejump.csv` (345 zones) and `[jumpNN]` travel points; the server's
  region-change and teleport packets; entering a dungeon or city (until M11, as a placeholder).
  *Built 2026-11-07:* each zone's doorways go to `jumps.csv` (235 in 91 zones); walking through one sends
  its zone point (C→S 0x90) and the server moves us; a region change shows a loading screen. The dev
  overlay's "Jump points" switch (or `--jumps`) draws each doorway. Open: W47, a live walk through a gate,
  teleporter NPCs (needs click-to-target and interact, M9), retail's loading pictures. A gate walk was
  confirmed live by the owner (2026-11-07, into Camelot City) and teleporters by Quitcheck through Master Visur.
  *Loading screen (2026-11-07):* `make loading` (`mpk loading`) takes the region loading pictures, tips,
  bar and tip font from the user's install; a region change shows one of the destination's pictures with
  a random tip and a bar that fills while the zones around the arrival point build (faster behind the
  screen). Checked live: Quitcheck teleported Camelot City → Prydwen Keep through Master Visur. Open: W49
  (retail's exact layout), the first-login splash (`pregame/splash.mpk`).
- **Death, release and respawn**, which move the player to a bind point, often in another zone.
  *Built 2026-11-07:* the server's death (0xAE) drops the character into its Death clip and stops it moving;
  every 0xA9 then says it is dead; a panel counts the release timer down (0xF3) with a Release button
  (or type /release); the move to the bind point and the revive (0x89) bring it back. Other players fall
  and get up too; 0xAD vitals show in the status line. Verified live as Quitcheck (killed by two falls).
  Open: W48 (retail's quicker release), the dead screen's look, a release into another region.
  *Future improvement (owner, 2026-11-07, after Camelot Unchained's death dialog, which offers the local
  keep or the home island):* release choices on the death panel. OpenDAoC already has more than the bind
  point: `/release city` (the realm's capital), `/release house`, `/release bind`, and in the frontiers or a
  battleground a release to the realm's portal keep. The panel can offer those as buttons with no server
  change. A release to the *nearest* keep or town would be new: a release type added to OpenDAoC's
  GamePlayer.Release (server-side work).
- **Swimming** in the water M7 draws. *Built 2026-11-07:* the zones' water surfaces get colliders on their
  own layer; in water deeper than 1.4 m the character floats with its head out, without gravity or jump,
  plays its figure's swim clips (animnifs.csv "Swim Alt": tread, swim, swim backwards), and sends state flag
  0x04 in each 0xA9; a dive into deep water takes no landing (no fall damage). Retail's numbers (daoc-re
  RE/58, applied the same day): swims with the feet 0.9 m under, floats them 1.0 m under, 85 units/s scaled by
  0xB6's water speed, wading at half speed.
  Verified live 2026-11-07 (Quitcheck ran from Prydwen Keep into the river, swam, and climbed out). Open:
  W51 (retail's swim depth, float height and speed), underwater view, other players' and NPCs' swim clips.
- **Gravity (started 2026-11-04):** the character stands on terrain and on model floors (streets,
  stairs, bridges, walls, roofs), walks up and down steps, falls under gravity off a ledge, jumps (Space),
  and reports landings so the server charges fall damage. Retail's gravity, jump and step values and its
  collision hulls are W32; walls don't stop the character yet (M9 "solid world").
- **Done when:** you can walk from Camelot Hills into Salisbury Plains and back, use a zone jump, and
  die, release and come back at your bind point. *All verified live 2026-11-07:* the walk both ways and the
  Camelot City gate (owner, Eodarms), death by falls and release to the bind point (Quitcheck), plus
  swimming and the region loading screen.

### M9 — Vitals, targeting, interaction — and solid world
*The HUD, the basics of "doing things," and a world you can't walk through.*
- Parse health/mana/endurance, level/XP, and the stat/update packets into a HUD.
- Target an entity; basic interact (talk to NPC, open a merchant/dialog) — read-only first.
  *Started 2026-11-07 (for the teleporters):* a left click targets a creature or player (0xB0; a ring at its
  feet, its name in the status line), a right click without dragging talks to it (0x7A), Esc clears; an
  NPC's words open a window whose [keywords] whisper back. Checked live with Master Visur. Open: the
  target window and its vitals, merchants (0x17).
  *Names (2026-11-07):* each creature and player shows its name and, under it, the NPC's trade or the
  player's guild in angle brackets (<Hastener>, <Teleporter>, <Weapon Merchant>); monsters in their con
  colour. Dev switch "Names". Open: W50 (retail's exact look).
- **Map window (M; owner's request 2026-11-07, built the same day):** retail's own map pictures from the
  user's install (`make maps`: `ui/maps`, the zone maps z<NNN>.dds and region maps r<NNN>.dds, placed by
  `regions.dat`): the zone you are in, or the region, with an arrow where you stand and face, and the zone's
  description. A click on either map runs you there (auto-run in a straight line, as a player); a movement
  key stops it. Checked live (Quitcheck at Prydwen Keep). A city's or dungeon's picture covers only part of
  its zone: `regions.dat` gives that part per zone (`zoneN_offsetx/offsety/width/height`, world units; 299
  zones), which the owner's Camelot City shots showed we had missed (fixed 2026-11-07). Retail's window (daoc-re
  RE/57): M in every preset (Shift+M the realm war map), 270 x 498, zone and area combos, "Zone Description"
  and "Follow Location" switches, a purple arrow from `ui/isles/map_icons.tga`. Open: the area maps
  (`areas.dat`, z<NNN>_<NN>.dds, chosen by position and height), group and quest marks, retail's look.
- **Fixture/interior collision** — deferred from the M7 walk-through, and intentionally **not** MVP
  ("looks like DAoC" ≠ "collides like DAoC"). Today the ground raycast follows only the terrain
  heightmap (a separate collision layer), so building geometry isn't solid: the player sinks into a
  raised dais/keep floor, passes through a slope or stairs, and walks through walls. DAoC resolves the
  player against the `collidee`/`climb` node-name volumes inside each model (identified in W3c); apply
  those to stand on floors/daises/stairs, block at walls, and climb climb-volumes. *Possible early-lite
  (still M9):* extend the ground ray to also hit floor geometry so you stand on daises — ~80% of the
  indoor "feels right" for little work, but still collision, so it stays post-MVP.
- **Jump** (spacebar) — also deferred from the walk-through, post-MVP. Not needed to walk the world,
  and it opens a protocol question (the 0xBE jump packet seen in 1.130 RE).
- **UI direction (decided 2026-11-04): a skinnable interface built on the retail one, then a clean,
  modern client of greater utility on the same framework.** The retail client draws its interface from
  files in the install, read at start: `ui/uimain.xml` includes ~80 window layouts (`merchant_window.xml`,
  `chat_window.xml`, …), each giving a window's size, frame, controls, positions and art (TGA/DDS/BMP).
  `ui/atlantis` (83 layouts) and `ui/isles` (67) are two complete skins; `pregame/` (21) the login and
  character screens; community skins (Tubben's, MxN, Ghost, Derida) replace these files. Controls name
  an `Adapter`/`AdapterName` and `Data` (what game value they show) and an `OnClickEvent` (what they
  do); 267 element types in all. Behaviour itself is in `game.dll`; ours comes from the protocol.
  Plan: (1) a loader that reads the user's own installed layouts and art and draws a window from them
  (the skin; local only, like models: never shipped); (2) the adapters wired to our protocol state,
  window by window, starting with what M9 needs (vitals, target, chat, quickbar); (3) our own clean
  skin and layouts on the same framework (dark translucent panels, small labels, one accent colour, as
  the dev overlay), with more utility where the retail window is thin. Keep each window's retail
  layout and behaviour as the reference (players' muscle memory) and compare before changing it.
  First windows for the loader (owner, 2026-11-07, with retail's screenshots): the pregame screens,
  starting with realm selection (`pregame/realm_selection.xml`: a 1024 x 768 canvas and three realm buttons;
  the lore text in `pregame/realmdesc.mpk`), then character selection, creation and customising. In the
  world, retail's Game Options window is the reference for our settings (its Display Names switches are
  in Settings → Game since 2026-11-07).
  Skins the community already publishes for retail must load in our client unchanged, and our own skin
  is written in the same format. Where the format can't express something we want, we extend it
  (documented, and a retail skin without the extension still loads) rather than inventing a second format.
  *How retail loads a skin (daoc-re RE/59, 2026-11-08):*
  - `ui/uimain.xml` is the root for every skin: 76 `<Include>`s, each resolved against the chosen skin's
    folder. A file the skin lacks is taken from `ui/Atlantis/`, so a skin holds only what it changes.
  - The player chooses "Atlantis Skin" or "Custom Skin" (`user.dat` `[interface] item1` = 0 or 8). A
    custom skin is the folder `ui/custom/`, so only one is installed at a time; `ui/custom/maps/`
    replaces the map pictures and `ui/custom/oldstyle/` the old bitmap window pieces. Every community
    pack (UI-Chooser/BobsUI, MxN, Whatties, Tubben's, Ghost, Derida, Aurora) installs this way, by hand
    or with its own tool.
  - The format has three layers: resources (textures, icon sheets, bitmap and TrueType fonts),
    templates (image cut-outs, three- and nine-slice frames, buttons, scroll bars, with `<Parent>`
    inheritance; 34 top-level element types) and windows (`WindowTemplate`: size, frame, title,
    resizing and about 25 control kinds).
  - The contract between a skin and the game is **adapter names** (the values a control shows; retail's
    XML uses 663, community skins bind more that the stock windows never show) and **event names** (what
    a button does; 223). Twenty spare windows (`custom0_window`–`custom19_window`) are free for a skin's
    own use.
  - Ours: the same lookup order, so an installed `ui/custom/` and the user's retail files load
    unchanged; several skins kept side by side and picked in Settings (folder or zip) instead of one
    `custom/` folder; adapters and events implemented from our protocol state, the names cross-checked
    with DAoC UI Forge's reference lists (`tokajer/daoc-ui-forge-core`, GPL-3.0: read, attributed).
- **UI review: Camelot Unchained's client**, before step (3): its fonts, window design, HUD and
  interaction patterns, for observations our own skin can use. Its interface code is public
  (`csegames/CSE-UI`: TypeScript/React UI rendered in HTML/CSS by Coherent, MPL-2.0 per folder; fonts carry
  their own licences, e.g. a commercial icon font and a foundry EULA, so they are reference only).
  Write the findings up next to the retail comparison; adopt ideas, not assets.
- **Done when:** you can select a target, see its and your vitals, talk to an NPC, and stand on/bump
  into the world's solid geometry instead of passing through it.

### M10 — Combat and inventory you can see
*The game reads as the game.*
- Interpret combat packets (attack/animation/damage/death) so fights are visible; send basic combat
  actions.
- Parse inventory and equipment; show the character's items; equip/move within the 1.65 ruleset.
- **Done when:** you can engage a mob, see the exchange and outcome, and manage what you're wearing.

### M11 — Dungeons and cities
*Go inside: the 299 zones that aren't heightmaps.*
Measured 2026-11-01 (`docs/world-coverage.md`): 282 dungeon zones and 17 "skycity" zones are built
from a different, chunk-based format: `dungeon.chunk` (a list of NIF pieces), `dungeon.place`
(placements), `dungeon.prop` (props), `dungeon.water`, and `portals.csv` (connections between
pieces, 299 zones), with `nifproxy.csv`/`texproxy.csv` alternates. The files are text, so this is
format work like the NIF audit, not reverse engineering. Capital cities may be among the skycity zones
(unconfirmed).
- Audit the chunk format across all 299 zones, then assemble a dungeon from its pieces and placements.
- Portals and the dungeon visibility rules; dungeon water and lighting (`[dungeonfix]`).
- **Done when:** you can enter a dungeon from its zone jump and walk it, rendered from its pieces.

### M12 — Atmosphere: effects, sound and music
*The world moves and makes noise, and the game opens as retail does.*
- **Day and night**, and weather: lighting from `terrain/lights.csv` and the zones' light settings
  (the `light/` folder is legacy and unused), and the server sends the time. Rain and snow stop under
  roofs by each model's `.nhd` cover grid (`docs/world-coverage.md`).
- **Spell and visual effects**: 1,575 effect NIFs, particles (unhandled in the NIF audit), UV-scrolling
  and keyframe animation; doors and windmills.
- **Sound and music** (on the roadmap at the owner's request, 2026-11-08). The install's own files, played
  as the client plays them; nothing converted ships, as with models. What there is:
  - `sounds/`: 2,703 `.wav` and 49 music `.mp3`, named by 12 tables (`cc_*.csv`). Their names carry
    the kind: `s_` one sound, `g_` a group picked at random (`cc_groups.csv`, 1,037), `q_` a sequence
    with delays (`cc_queues.csv`), `l_` a loop (`cc_loops.csv`), `mp3_` music (`cc_mp3s.csv`,
    `cc_music.csv`). Per thing: `cc_creatures.csv` (attack, three pain levels, death, evade, body hit,
    per creature), `cc_weapons.csv` (activate, swing, hit by weight, parried, blocked), `cc_doors.csv`,
    `cc_spells.csv`, `cc_interface.csv` (clicks), `CC_events.csv` (level up per realm, money,
    experience, friends); `cc_sounds.csv` (4,439 rows) is the master list.
  - `gamedata.mpk animsound.csv`: a sound on an animation frame (footsteps, a horse's startle).
  - Each zone's `sounds.dat` (389 zones): sound regions with ambient loops and music; `zones.dat`
    names each zone's entry music.
  - `Tutorial/sounds`: the tutorial's voice-over and music.
  - Which game event plays which entry is mapped in `docs/sound.md` (daoc-re, 2026-11-08): the row from
    the model's sound set (`monsters.csv`, `objects.csv`, `spells.csv`), the column from the packet
    (0xBC's result, 0x72/0x1B's spell phases, 0xEF area music, 0x99 doors, 0xC9/0xD3 server sounds),
    animation sounds as an animation starts; not yet confirmed by ear (W52). We play them from the
    protocol events we already raise. Godot plays wav and mp3 natively (`AudioStreamPlayer3D` for
    positional sound), so no conversion is needed. Order: interface clicks and footsteps, zone
    ambience and music, combat (with M10), spells.
- **The intro movie and the company logo** (`movie/intro_movie.bik`, 5 min, 555 x 416, 30 fps;
  `ealogo.bik`), shown at start as retail does, skippable. Bink, which Godot can't play: converted
  once to Ogg Theora (Godot's video format) by ffmpeg, which reads Bink. Homebrew's ffmpeg has no
  Theora encoder (checked 2026-11-08), so the converter needs an ffmpeg built with libtheora, or
  another route.
- **Done when:** a day passes visibly in Camelot Hills, a spell shows its effect, the zone has its
  ambient sound and music, a fight sounds like one, and the intro plays at start.

### M13 — Asset quality tiers and community reskin packs
*Classic by default; progressively better by choice — and others can contribute whole new looks.*
After the classic game is complete (M8–M12), building on the models M7 brings. The asset library
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

**Trees across the tiers** (decided 2026-11-01). We choose what to draw rather than copy one client
setting: the client ships each common tree twice, as a classic hand-built NIF and as a SpeedTree file
(a parametric recipe the SpeedTree runtime builds into geometry; open question W5). Godot loads glTF
(and FBX) at runtime, so every route below ends in a glTF in the library or a pack.
- **Classic (default):** the converted NIF trees, done (alpha cut-out and two-sided since 2026-11-01).
- **Cheap wins on any tree (Tier 0/2):** alpha antialiasing on cut-out leaves (softer, non-flickering
  edges), a wind vertex shader (sway by height), `MultiMesh` instancing for the ~1,000 trees in a zone,
  and per-instance visibility ranges with a cross-fade.
- **Enhanced (Tier 2): SpeedTree near, NIF far.** Convert the client's SpeedTree files to meshes with
  **our own converter**, once, at conversion time (the SpeedTree runtime is commercial; we can't ship
  it, and the free Learning Edition can't export). Draw the SpeedTree mesh up close and the classic NIF
  beyond a distance, cross-faded. The client's `zones/trees/treemap.csv` is the map to follow: 55
  classic trees have a SpeedTree replacement (including Camelot Hills' elm1, elm2, bpinea, bwillow,
  oak1), with per-tree textures, z offset, scale and colour factors our converter should apply; 16 Trials
  of Atlantis trees are SpeedTree-only (7 have no NIF at all) and need a far version we make (a
  simplified mesh or a billboard card) — relevant only if Atlantis zones are served (W5). **First: a format-feasibility check** — is the early SpeedTree format
  (`__IdvSpt_02_`) documented, or read by open-source tools (Oblivion used `.spt` too)? Reading it is
  analysis of data files, which our rules allow; disassembling the client is not. The free SpeedTree
  Learning Edition may serve as a reference viewer for checking our output, if it opens these files
  (unconfirmed).
**Water across the tiers** (2026-11-01, W8). The client has three water modes over the same
`[riverNN]` data, and modern profiles default to the upper two: **Classic** (what we draw now, the
faithful default), **Shrouded Isles** (animated ripples and droplets driven by `water/Displacement1.bmp`
and `Droplet1.bmp`), and **Reflective** (SI plus a reflection, bump-mapped with `water/bump3.tga`, at a
selectable resolution and update rate). In Godot: SI as a normal-map ripple and droplet animation in
the water shader; Reflective via screen-space reflections or a reflection probe (Godot 4 has no
built-in planar reflection). Retail screenshots of one river in each mode are the reference.

- **New trees (Tier 3, can ship):** CC0 tree packs (often glTF already), or trees made in Blender
  (the Sapling add-on, or hand-modelled) or Tree It (free; exports OBJ/FBX; commercial use per its
  forum terms, to confirm), normalised by a headless Blender script. Also
  [glTF Procedural Trees](https://gltf-trees.donmccurdy.com/) (MIT, browser, exports glTF), built on
  proctree.js (BSD, Paul Brunt): small and parameter-driven, so it could be ported to C# as a headless
  generator in our converter. Use clearly licensed (CC0) bark and leaf textures, not the demo's.
  [NervForge](https://nervtech.org/nervforge/) (free, browser, exports glTF/GLB, maple/willow
  presets) has no stated terms for exported trees: fine for experiments, not for a shipped pack until
  the author confirms. Its MIT repo (`roche-emmanuel/nervland_adventures`) needs the author's private
  engine to build, so it's algorithm reference only. A replacement must match a small
  contract: the same file name as the tree it replaces (e.g. `elm1.glb`) in a pack, metres, Y up,
  origin at the base of the trunk, leaf materials cut-out and double-sided, optionally a low-detail
  version for distance.

Enabling work (do the small part before override content piles up):
- **Named packs**, not one flat `overrides/`: make the load path "selected pack(s) → base library"
  (e.g. `packs/classic/`, `packs/hd/`), so classic-vs-enhanced is a selector and packs can stack.
  Formalising this early turns packs into a first-class, contributable unit instead of a retrofit.
- A pack manifest + contribution guide so an outside contributor can ship a reskin pack.

- **Done when:** the player can choose a look (at least classic vs. one enhanced pack), grass/vegetation
  renders from the zone masks, and the pack format + guide let someone outside the project publish a
  reskin that drops in without code changes.

### Further out (unscheduled)
Tracked so they're planned, not stumbled into: **character creation in the Godot UI** (the protocol
side is done); **map and minimap**; **group, guild, quest and trade windows**; **mounts** (the server
already sends 0x4E ControlledHorse at world entry); **housing** (`phousing/`: 410 NIFs); **RvR
frontiers, keeps, doors and sieges** (`frontiers/`); **the tutorial** (`Tutorial/`: region 27, one zone per
realm (27 Constantine's Sound, 28 Grenlock's Sound, 29 Lamfhota's Sound) with its own models, maps and
recorded voice-over the server plays line by line; whether our server offers it is unchecked;
`docs/sound.md`); **`Newtowns/`** (seven replacement models: border keeps, the Camelot wall, a
teleporter, a demon portal; when the folder exists the client loads zone models from it first, by the
same name, so our converter should too; `docs/sound.md`);
and **releasing for others** (setup instructions and
a guide to converting your own install; Gate 4 in CLAUDE.md is still open).

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

## Immediate next steps (M7, status 2026-11-01)

M7 is under way. Done: a **world-data audit** (`mpk worldaudit`, `docs/world-coverage.md`) that
restructured this roadmap; strafe (M7c); the magenta and placeholder surfaces (M7a, W3d/W3e); foliage
cut-outs (M7a); a development overlay in the Godot client (`godot/scripts/DevOverlay.cs`: layer
toggles, wireframe and unshaded render modes, and click-to-inspect, which reports a surface's model,
placement, mesh, texture, UVs and transparency); and a **NIF coverage audit** (`mpk audit`,
`docs/nif-coverage.md`) that measures what every model file in the client contains against what the
converter reads. It fixed a block-scanner bug that dropped the last block of 496 files, and it ranks
the remaining gaps. The order below follows that ranking instead of bugs seen in the world:

1. **The zone's environment (M7d).** ~~Water~~ and ~~sky and fog~~ (first passes done 2026-11-01). Next:
   static lights (`lights.csv`) and baked terrain shading (`shadow.pcx`, `shademap.pcx`). Data verified by the world audit,
   small, and visible at once.
2. **A real Gamebryo 10.1 reader** (node tree, transforms, per-shape properties; W7). 960 zone and
   1,259 item models are 10.1 and are read today by a geometry scan with no transforms, and 2,892 of
   the 2,934 figure models are 10.1, so M7b needs it anyway. Check it with the audit's byte-exact decode
   check, as for 4.x. Some floating or sunken fixtures may be 10.1 models missing their transforms:
   recheck **fixture seating** (M7a) after this, before writing placement fixes.
3. **Dark maps, second UV sets and vertex colours (M7a).** Baked lighting and shading on buildings and
   dungeons: dark maps in 1,463 files, vertex colours in about 3,200. The largest remaining difference
   between our surfaces and the game's.
4. **M7b skinning + figures**, the hardest part: NiSkinInstance/bone weights → glTF skin, figure
   assembly from parts (by bone name) plus equipment, and `.kfa` animation (idle/walk/run). Reference
   prior art: OfflineDAoC's GPL-3.0 pipeline (`docs/offlinedaoc-asset-pipeline.md`), adopted with
   attribution; DAOC_Navigator is a second GPL reference. Keep the NIF node names through to Godot
   (meshes arrive as `MeshNN` today): figure assembly matches parts by name, and the inspect readout
   becomes more useful. Audit `.kfa` files the same way first.
5. Then the rest of the audit's list as it bears on what's on screen: 2.x/3.x models missing from zones
   (W6), billboards, keyframe animation for doors and windmills, LOD ranges.

Waiting on the owner:
- a retail screenshot of a round-tower top, the BritBarn climb or the Btavern door, to settle the W3f
  placeholder meshes;
- retail screenshots of one spot under each tree setting, to settle which trees the client draws (W5:
  the most-placed Camelot Hills trees also exist as SpeedTree files).

Fold in when convenient (not blocking): the carried-over M5 remainder — buffer the world-init burst so
NPC *create* packets (realm colour + name) aren't lost and entities stop rendering neutral grey; it's
the same create data M7b needs to pick the right model per entity.
