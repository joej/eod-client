# World-scale calibration (W1 follow-up)

Finding the one remaining scale constant: **`RegionMetresPerUnit`** in `godot/scripts/WorldView.cs`.

This is a short, repeatable recipe you run on your own install (it needs converted assets, which
never enter the repo). It produces one number, or confirms the provisional `1/100`.

## Why this exists

DAoC has two independent unit systems, and each has exactly one conversion to Godot metres:

| System | Used by | Metres conversion | Status |
|---|---|---|---|
| **NIF units** | mesh geometry (buildings, items, creatures) | `GltfExporter` bakes `× 0.025` | **measured** — a ~72-unit humanoid = ~1.8 m (W1), guarded by `HumanoidConvertsToAPlausibleHeightInMetres` |
| **Region units** | `0x20`/`0xA9` positions, zone placements, terrain heightmap | `WorldView.RegionMetresPerUnit` | **provisional `1/100`** — never measured; the CLAUDE.md "divide by 100, tune from captures" placeholder |

A model `.glb` arrives already in metres, so `WorldView` never re-scales it for units. Terrain,
live positions and zone-object placements all pass through `RegionMetresPerUnit`. Calibration pins
that one constant so a building and the player stand at believable, consistent sizes.

**Known-size reference:** at `0.025 m/unit`, `keep.NIF` converts to **~16 m tall, ~52 × 65 m
footprint** (W1 note, verified rendering). A real DAoC keep is a large multi-storey stone structure,
so ~16 m / ~50 m across is the "looks right" target. The humanoid is the second anchor: a converted
character should stand ~1.8 m — roughly chest-to-waist height against that keep's ground floor.

## Prerequisites

- Your own DAoC install, and a one-time `mpk index` of it:
  ```sh
  AssetExtract mpk index <client-dir> --out <LIB>/index.json
  ```
  (`AssetExtract` = `dotnet run --project tools/AssetExtract --` or the built binary. `<LIB>` is your
  convert-once asset library, kept under `private/` — git-ignored.)
- The current build: `dotnet build godot/EodClient.csproj -c Debug`.
- Test credentials loaded the usual way (`DAOC_PASSWORD=...`); Eodarms is Albion slot 0 and spawns
  in Camelot Hills, the zone we calibrate against.

## Camelot Hills constants (from `World/zones.tsv`, OpenDAoC-Database)

Zone id **0**, region **1**, map cell **x=67, y=59**. The zone's region offset is the map cell ×
8192:

- **offsetX = 67 × 8192 = 548864**
- **offsetY = 59 × 8192 = 483328**

Eodarms spawns at region `(574797, 530161, 2896)` — inside this zone, so terrain, placed objects and
the live player position all share one coordinate frame. That is exactly the condition we need:
everything visible at once, at real coordinates.

## Step 1 — Convert the zone + terrain

The `dat<NNN>.mpk` for Camelot Hills is the zone-0 archive in your install (zone data archives are
named by zone id; confirm with `mpk list`). Convert its terrain and static objects into `<LIB>`:

```sh
# Terrain -> one .glb in region coordinates
AssetExtract mpk terrain  <dat000.mpk> --offset 548864 483328 \
    --index <LIB>/index.json --library <LIB> --out <LIB>/camelot-hills-terrain.glb

# Static objects -> zone.csv placement manifest + per-model .glb in the library
AssetExtract mpk zone     <dat000.mpk> --offset 548864 483328 \
    --index <LIB>/index.json --library <LIB> --out <LIB>
```

`mpk zone` writes `zone.csv` (rows `glb,x,y,z,angle,scale` in region coords) and the distinct models
it references into `<LIB>`. The keep, if this zone has one, is among them.

Optional cross-check — convert the standalone keep so you can compare it against the *placed* keep:

```sh
AssetExtract mpk convert <path-to>/keep.NIF --out <LIB>/keep.glb
```

## Step 2 — Render everything together

Launch the client windowed (not `--headless` — you need to see it), auto-logging in and entering the
world, with the zone, terrain and your character model all pointed at `<LIB>`:

```sh
DAOC_PASSWORD="$(daoc_secret test_password)" godot-mono --path godot -- \
    --autologin "$(daoc_secret test_ip)" "$(daoc_secret test_user)" 10300 \
    --enter 0 --stay 120 \
    --library <LIB> \
    --terrain <LIB>/camelot-hills-terrain.glb \
    --zone    <LIB>/zone.csv \
    --model   <LIB>/<your-character>.glb
```

`--enter 0` selects Albion slot 0 (Eodarms); `--stay 120` holds the session two minutes so you can
look around. The free-fly debug camera is on `WorldView` — fly down to ground level near the keep.

## Step 3 — Read the fit, and adjust the one constant

Judge against the two anchors:

1. **Keep footprint / height.** The keep should read as ~50 m across, ~16 m tall — a building you'd
   walk up to, not a cottage and not a skyscraper filling the zone.
2. **Player height against the keep.** The character (feet on the ground) should stand ~1.8 m —
   about chest height to the keep's ground-floor doorway.

Both anchors already share the *model* scale (0.025), so the only thing that can be off is how far
apart things are placed and how big the terrain is — i.e. `RegionMetresPerUnit`:

- **Buildings sit at the right size but too far apart / too close** (the keep is correct but the next
  building is across a continent, or on top of you) → the placement scale disagrees with the model
  scale. Multiply `RegionMetresPerUnit` by (apparent spacing ÷ expected spacing).
- **Everything (terrain + buildings) is uniformly too big or too small relative to the player** →
  the whole region frame is off by that ratio; scale `RegionMetresPerUnit` by it.
- **It all looks right** → `1/100` is correct; record the confirmation and close the W1 follow-up.

The fix is a one-line change in `godot/scripts/WorldView.cs`:

```csharp
private const float RegionMetresPerUnit = 1f / 100f;   // ← the calibrated value
```

Rebuild (`dotnet build godot/EodClient.csproj -c Debug`) and re-render to confirm.

## Step 4 — Record the result

- Update the **W1 follow-up** in `docs/open-questions.md`: the measured `RegionMetresPerUnit`, how it
  was read (keep footprint and/or player height against terrain), and mark it resolved.
- If the value moved off `1/100`, note it in `docs/roadmap.md` M4 (step 1) as done.
- One devlog-worthy fact if it holds: a converted keep, real terrain and a live server position all
  agreeing in one Godot scene is the first time the asset pipeline and the protocol meet on screen.

## Notes / gotchas

- **Headless gives no image.** Godot's headless mode uses a dummy rasterizer; use a real window (and
  `ModelViewer --shot` if you want a saved PNG of a single model — it renders windowed, then quits).
- **Terrain must load before you judge spacing** — if you see the flat green placeholder instead of
  the heightmap, the `--terrain` path or its texturing didn't resolve; check the `[world] terrain …`
  log line.
- **This tunes only `RegionMetresPerUnit`.** The model scale (`GltfExporter.UnitsToMetres = 0.025`)
  is already measured and CI-guarded; don't change it to fix a placement-spacing problem.
