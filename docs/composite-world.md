# What a DAoC player actually sees: the composite world

A modern DAoC client is not "load a map and walk around." What fills the screen is a **composite of
several independent sources**, each with its own file or packet, its own coordinate encoding, and its
own moment of arrival. Understanding that split is the first real architectural decision for anyone
building a client — it decides what you implement first, where "classic vs. enhanced" visuals plug in,
and why some things are in the client's own files while others only ever come from the server.

This is a lessons-learned writeup from building [eod-client](https://github.com/joej/eod-client), an
open-source (GPL-3.0) Godot/C# client. Links below point at the actual code, so a developer can read
how each piece is handled, not just that it is.

> Scope and sourcing: everything here is about *what the client composes and how*. It uses the owner's
> own extracted assets (which never enter the repo) and our OpenDAoC server. Protocol facts come from
> the OpenDAoC/DOLSharp source (GPL) and our own captures; see
> [`docs/protocol.md`](https://github.com/joej/eod-client/blob/main/docs/protocol.md).

## The six sources

Standing in Camelot Hills, everything you see comes from one of these:

| What you see | Source | Baked or live? | Coordinates | Our handling |
|---|---|---|---|---|
| **Terrain** (hills, ground, tile textures) | zone heightmap + ground tiles | baked (client files) | region-global, baked into the mesh | one `terrain.glb` dropped in at load |
| **Static scenery** (keeps, houses, trees, fences, rocks, wells) | the zone's fixture list | baked (client files) | region-global, listed per object | a `zone.csv` of `model, x, y, z, angle, scale`, each placed at load |
| **Ground vegetation** (grass tufts) | per-zone density masks | baked (client files) | procedural scatter | **not applied yet** — see "The gap" below |
| **NPCs / mobs** | live `0xDA` create + `0xA1` updates | live (server pushes) | create: region-global **big-endian u32**; updates: **zone-local 16-bit** | a scene entity per object id, spawned by `0xDA`, moved by `0xA1` |
| **Other players** | live `0x4B` packets | live (server pushes) | region-global **little-endian float32** | a scene entity per object id |
| **Dynamic world objects** (doors, siege, dropped items) | live `0xD9` / `0x12` / `0xE1` | live (server pushes) | region-global big-endian u32 | partially handled |

Two things fall straight out of this table, and both surprised us in practice.

### 1. The keep and a tree are the same kind of object

It is tempting to assume "big landmark buildings" are special. They are not. In our zone data the keep
is **one line of `zone.csv`**, identical in form to a tree or a fence:

```
keep.glb,574208,530432,2880,0,1
elm1.glb,...
```

The Camelot Hills fixture list is ~1530 such placements — about 1380 of them trees — and the keep,
the Cotswold houses, the standing stones and the rocks all arrive by the exact same path at load time.
There is no separate "building system." If you can place a tree, you can place a keep. (What *is*
genuinely different per model is how its NIF is textured — some shapes carry textures, some only a
material colour, some bind textures by name at runtime — but that is a mesh-conversion story, not a
placement one; it has its own writeup.)

So when it *looks* like "large structures appear as NPCs show up," they are unrelated. The buildings
were always there, placed from the local zone file at load; the NPCs stream in over the next seconds
from the server. They share a world but not a path, and moving the camera simply brings
already-placed geometry into view at roughly the same time the live entities populate. Two clocks, one
scene.

Code: static scenery is built in
[`WorldView.BuildZone()`](https://github.com/joej/eod-client/blob/main/godot/scripts/WorldView.cs)
from the `zone.csv`; the terrain is loaded by `TryAddTerrain()` in the same file.

### 2. Baked vs. live is the real fault line, and it sets your coordinate bugs

The deepest split is **what ships in the client's own files vs. what the server pushes live** — and it
is not cosmetic, because the two halves use *different coordinate encodings*:

- **Baked** (terrain, fixtures) is **region-global** and, in our pipeline, already converted to metres
  in the `.glb`. It just drops in.
- **Live** entity packets are a grab-bag of **three** encodings, not one. My own position and other
  players (`0x20`/`0xA9`/`0x4B`) carry **little-endian float32** region-global coordinates. The
  *create* packets that spawn NPCs and world objects (`0xDA`/`0xD9`) carry region-global **big-endian
  u32 integers** — same frame, opposite byte order, and not floats. And the high-frequency NPC
  *update* (`0xA1`) carries **big-endian 16-bit zone-local** offsets you must add the zone's origin to
  before you can place anything. Get that last one wrong and your NPCs cluster at the zone corner,
  500k units from where they belong.

That asymmetry — one world, but baked geometry and three different live encodings depending on the
packet — is the single most common place a new client renders a correct-looking world with
everything in the wrong spot.

Code: the live entity layouts (and the endianness notes) are in
[`WorldEntityPackets.cs`](https://github.com/joej/eod-client/blob/main/src/DaocProtocol/Packets/WorldEntityPackets.cs);
the zone-local → region-global → engine conversion is `WorldView.ZoneLocalToGodot()`, which looks up
the zone offset from the GPL zone table in
[`Zones.cs`](https://github.com/joej/eod-client/blob/main/src/DaocProtocol/World/Zones.cs).

## The seam that makes the live half manageable

The live sources (NPCs, players, objects appearing, moving, leaving) all arrive as opcodes on one TCP
stream. The structural piece that keeps this sane is a **dispatcher**: the receive loop stops counting
opcodes and routes each to a handler that raises a typed event; the renderer subscribes and spawns,
moves or removes a scene entity. In our client that is
[`NetworkBridge`](https://github.com/joej/eod-client/blob/main/src/DaocProtocol/Session/NetworkBridge.cs) — the
one place the protocol library meets the engine (it lives in the protocol library, with no engine code). Everything above it is Godot; everything below it is
plain .NET with no engine dependency, which keeps the protocol testable and the renderer replaceable.

If you are building a client, this seam is worth creating early. It is the thing the rest of the world
(chat, combat, vitals) hangs off.

## The gap: DAoC data we don't apply yet — and why your ground looks empty

Compare a screenshot of the real client to a fresh one of yours and the ground looks barer, even when
your terrain textures are correct. The missing ingredient is **ground vegetation**, and it is a third
source most people miss:

DAoC does **not** place grass as objects in the fixture list. Each zone's `dat` archive carries
**ground-cover density masks** (`grass` / `dense` / `shade` / `water` layers), and the client reads
them and **procedurally scatters** grass-clump billboards across the terrain, weighted by the mask.
It is data you already have but a *system* you have to implement — read the mask, then scatter cheap
quads (a `MultiMeshInstance3D`'s worth) where the mask says grass. We extract the terrain and the
fixtures but not these masks yet; it is the biggest single reason a faithful-but-textured ground still
reads as "empty."

The lesson generalises: **"make it look like the game" is often not better art — it is a source you
haven't wired up.** Vegetation is the clearest case, but dynamic objects (doors, siege) are another.

## Three paths to "what the player sees" — and where enhancement lives

Pulling it together, there are three distinct *paths* to the final image, and a client can advance each
independently:

1. **Assets** — the converted meshes and textures (terrain, fixtures, models). This is where
   classic-vs-enhanced lives: because our asset library is a convert-once pool with an override layer,
   a higher-resolution or AI-upscaled texture with the same name transparently replaces the classic
   one, with no renderer change. (This is the "quality tiers and community reskin packs" direction in
   our [roadmap](https://github.com/joej/eod-client/blob/main/docs/roadmap.md), M13.)
2. **Pipeline** — what the extractor/exporter does at convert time: scale, UV handling, material
   colours, mipmaps, filtering. Getting this right is faithful-but-crisp for free, and it is where most
   of the hard-won mesh/scale lessons live.
3. **Algorithmic additions** — things the client *generates* rather than loads: procedural grass from
   density masks, generated normal maps, interpolated movement between sparse position updates. The
   original client did this too; a modern one can do more.

The payoff of separating them: you can ship a faithful classic look, then improve any one path —
sharper textures, procedural vegetation, new art packs — without touching the others, and invite
others to contribute along whichever path suits their skills.

---

*Part of the [eve of darkness](https://eveofdarkness.org) devlog. The companion piece covers the
mesh-conversion side — DAoC's NIF versions, the one-scale-constant calibration, and the
no-UV / material-colour shapes — where a different set of lessons live.*
