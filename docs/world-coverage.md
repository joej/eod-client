# World coverage

What the DAoC client's world data contains, measured across a whole install, and what our converter
reads of it. The companion to `docs/nif-coverage.md` (models). The roadmap is checked against both, so
parts of the world can't go unplanned just because we haven't walked into them yet (water did, until a
dry riverbed under a bridge gave it away).

## How it is measured

`mpk worldaudit <client-dir> --out FILE.md` (`tools/AssetExtract`, `src/DaocAssets/Zone/ZoneAudit.cs`)
opens every `zones/zoneNNN` folder and records: the zone's kind (from its data archive), every archive
member by name pattern (digits folded, so `river00` and `river01` group as `river#`), every
`SECTOR.DAT` section and key with example values, each CSV's header and row count, and the leading
bytes of binary files. Each file and key is marked as **used** (read by the converter today) or not.
The report describes the owner's client files, so it is written under `private/` and never committed;
this page holds the findings. Run on 2026-11-01 over the owner's 1.127 OpenDAoC install.

## How the converted world is checked (2026-11-02)

Reading every byte correctly is not the same as building the world correctly: until 2026-11-02, 93% of
all outdoor objects were placed underground while every count looked right. Three checks now judge the
result, and CLAUDE.md requires them before an asset change is handed over:

- **`mpk zonecheck`** (`make check-zones` or `tools/zonecheck-all.sh` for every outdoor zone, 80 s from
  scratch and seconds when little changed; `make check-zoneNNN`, which `tools/play.sh assets` runs, for
  the zone being played, stopping on errors) places each object as the client does, with
  its converted model's real bounds, and compares it with the terrain: under the ground, origin sunk,
  floating, outside the zone, implausible size, missing or empty model. Findings reviewed and believed
  intended are listed in `tools/zonecheck-known.txt` (unverified until compared with retail).
  2026-11-02, all 87 outdoor zones, 95,460 placements: 306 errors and 25,862 warnings at the first run,
  17 and 757 after the fixes it led to; the rest are listed per zone in the private report.
- **The field review** in `mpk worldaudit`: every column and key of the files the converter reads, with
  their values; each one the converter ignores, and each zero/empty/negative value in one it reads, is a
  review item. The 2026-11-02 review produced W14-W18.
- **`tools/shots.sh`**: fixed camera poses (`tools/shots/camhills.txt`) rendered offline, compared before
  and after a change. Every bug the owner reports gets a pose.

## Kinds of zone

| Kind | Zones | How it's built | Converter |
|---|---:|---|---|
| Outdoor | 98 | heightmap (`terrain.pcx` + `offset.pcx`), placed NIFs (`nifs.csv` + `fixtures.csv`), terrain tile textures | **read** (87 convert; the zone table gives the extent, W14) |
| Capital city | 4 | `city.csv` (a piece list, no positions: pieces laid out in city coordinates), the pieces in the zone's `nifs/` folder with `.nhd` cover grids and `_dm_` dark maps, `adjacency.csv` (a piece-to-piece table, probably visibility; unread), `ZONEJUMP.csv` (the gates), `lights.csv`; no terrain | **read** (2026-11-03): City of Camelot (26), Avalon City (50), Jordheim (120), Tir na Nog (209). The whole city sits at region (32000, 32000, 8000), turned 180°: under that placement the streets hold the server's NPCs (205/205 Jordheim, 1,020/1,020 Avalon City, 299/303 Tir na Nog, 243/250 Camelot; no other orientation holds half). `play.sh --zone` lands at the first gate |
| Dungeon | 280 | `dungeon.chunk` (NIF pieces), `dungeon.place`, `dungeon.prop`, `dungeon.water`, `portals.csv` | not read (roadmap M11); listed by `play.sh zones` as "not convertible yet" |
| "Skycity" | 17 | the same chunk files under `skycity.*` | not read (M11) |
| Other / empty | 5 | | |

Three quarters of the zones use a format we have never opened. The chunk files are text (comma-separated
numbers and NIF names), so reading them is format work, not reverse engineering.

## What a zone describes that we don't draw yet

From `SECTOR.DAT` (375 zones have one) and the data archives. Numbers are zones that contain it.

| Feature | Where | Zones | Roadmap |
|---|---|---:|---|
| Rivers, lakes, swamps | `[riverNN]`: type, name, height, bank lines, texture + ripple overlay, flow, colour, tessellation; `water.pcx` | 100 | M7d: **read** (`ZoneWater`, `mpk water`) except `extend_*`, tessellation, colour |
| Fog | `[fog]` clip (the view distance) and amount (fogged share, measured back from clip); its colour keys aren't read by the client | 375 | M7d: **read** |
| Sky | not SECTOR.DAT: `zones/sky/sky_<name>.dat`, chosen by `zones/zones.mpk` → `zones.dat` `skydome` (zone, else region, else `sky_default`); SECTOR.DAT `[sky]` colour and `[terrain]` horizon/clouds are not read by the client | 329 | M7d: **read** (`SkyDefinition`, `mpk sky`) |
| Forest fog regions | `[forest]`: colour, fog, rectangles and circles | 269 | M7d |
| Far ground | `[farground]` texture | 134 | M7d |
| Static lights | `lights.csv` (x, y, z, type: a row of `zones/textures/LIGHTDEF.CSV`, colour and a range of 256/512/1024 units; −1 none): torches, fires and lamps, not day/night lighting (daoc-re RE/69) | 304 | **read** (2026-11-09, `ZoneLights`: an omni light each, no shadows; 1,667 in the converted zones) |
| Baked terrain shading | `shadow.pcx`, `shademap.pcx` | 98, 86 | M7d |
| Terrain holes | `[holes]` (probably cave entrances cut in the heightmap) | 98 | M7d |
| Grass and ground cover | `grassmap.pcx`, `densemap.pcx` | 84 | M13 |
| Snow | `snowmap.tga` | 18 | later |
| Per-patch terrain textures | `textures.csv`, `patch#-#.dds`, `lod#-#.dds` (96 zones), possibly the "Atlantis terrain" option (open question W8) | 96 | M7d / W8 |
| Zone jumps | `zonejump.csv`, `[jumpNN]` travel points | 345, 45 | M8 |
| Zone bounds | `bound.csv` (used since 2026-11-08: the invisible edge walls, see `ZoneBounds`), `borders.csv`, `adjacency.csv` | 98, 9, 4 | M8 |
| Ambient sound | `sounds.dat` beside each zone's archives | 389 | M12 |
| Unknown | `[mongens]` (number ranges), `[dungeonfix]` (lighting flags), `[start]` | 97, 129, 370 | to identify |

## Outside the zones

| Folder | Contents | Roadmap |
|---|---|---|
| `anims/` | 4,060 `.kfa` animations | M7b (audit the format first) |
| `light/` | `SYSPAL.PCX`, `SYSTEM.COL`, `TRAN555/565.TRA`: legacy palette/translucency tables, not referenced by the client (checked 2026-11-03). Day/night lighting is not in `lights.csv` (each zone's are fixed coloured lights; `terrain/lights.csv` is a developer copy) | none |
| `zones/sky/` | `sky.mpk` (25 `sky_<name>.dat`: sun, moon, stars, cloud layers, per weather × time of day canopy and cloud gradients, ambient/sun light, fog colour, storms, rain, snow) and its textures | M7d (day), M12 (clock, weather) |
| `zones/zones.mpk` | `zones.dat`: every zone's name, region, offsets, size, type, entry music, sky; each region's sky, grass (`grass_alb.csv`/`.dds`) and expansion | M7d, M8, M13 |
| `water/` | the shader water's parts (2026-11-03): always loaded `lavabase/lavadetail/lavaglow.dds`, `water_wake.dds`, `footsplash.NIF`, `lava1/2_sfx.NIF`; Shrouded Isles and Reflective water add `Displacement1.bmp`, `Droplet1.bmp`; Reflective adds `bump3.tga`. `acidcloud/flame000/hotglow.tga`: no known user. Modes and settings: open question W24 | M13 |
| `.nhd` files (1,087: beside zone models in `zones/Nifs/`, in `frontiers/nifs/`, and per city piece) | **weather cover grids**, not walk heights: per model, where rain and snow are blocked (drop spawn height, and hiding drops under a roof). Format: `NHD`, u16 version 1, u16 name length + name, u16 cell size (model units), i32 x0, x1, y0, y1 (cells), then (x1−x0)×(y1−y0) i16 heights, row-major; −2500 = no surface. Loaded for any zone model whose `.nhd` exists beside it (no CSV column). Nothing in movement or collision reads them: walking on objects (M9) needs the models' collision geometry. Sign of the heights in model Z: to confirm visually | M12 (weather) |
| `stylemap/` | weapon swing trails (`metaledge.bmp` generic, `flameedge.bmp` flame; IDs in `gamedata.mpk` `stylemap.csv`), `SHADOW.BMP` (shadow texture, likely the blob shadow), `target.bmp` (ground-target spell ring), `cloak.nif` (a template model; user not traced) | M10 |
| `charman/` | `names.dat`: the random name generator (`[<race>-first/second/third]` fragment lists). `summary.mpk`, `startlocs.mpk` (class and start-location texts): not referenced, leftovers of an old character creation | character screen |
| `shaders/` | `vcalphatextureblender.nsb`: a shader a NIF material can name (per material, not a global switch); which NIFs use it, and any tie to Atlantis terrain (W8), unchecked | M13 |
| `effects/` | 1,575 effect NIFs, 573 DDS | M12 |
| `sounds/` | 2,703 `.wav`, 49 music `.mp3`, 12 CSVs | M12 |
| `ui/` | 151 XML layouts, 502 DDS, fonts | M9 (UI direction), later |
| `icons/` | 15 icon sheets (`spl_*`, `cbt_*`, `itm_*`, `skl_*`: 320 × 320, 100 icons of 32 × 32), `brdr_0.bmp` (border frames), cursors; `icons/classic/` the old set (`user.dat` `ClassicIcons`). The server's number for a skill, style or spell reaches a sheet cell through `gamedata.mpk` `icons.csv`, `spells.csv`, `styles.csv` | **read** (2026-11-09, `mpk icons`, `IconSheets`, `IconTable`: the quickbars). Not yet: the style arrows (icons.csv's eight arrow columns), cursors, the classic set |
| `phousing/`, `frontiers/` | housing; RvR frontiers and keeps | further out |
| `zones/trees/` | SpeedTree trees + `treemap.csv` (open question W5) | M13 |
| `zones/trees/tree_clusters.mpk` | `tree_clusters.csv`: a model name that is no file (`Elm1CL5.nif`) → a tree and up to ten offsets; the client builds the group. Used by the tutorial zones and zone 335 | **read** (2026-11-08, `TreeClusters`: one tree per offset, each on the ground; W53) |
| `Tutorial/` | the tutorial, region 27 (`zones.dat` `tutorial=1`): zones 27-29 with their own models, textures and `sounds.dat`; the client loads them from here (zone 27 also has an older copy in `zones/`) | **read** (2026-11-08): nine same-name files differ from `zones/Nifs`, so they convert into the library's `tutorial` scope |
| `Newtowns/zones/Nifs` | seven replacement zone models (border keeps, the Camelot wall, a teleporter, a demon portal); when the folder exists the client loads a zone model from it first, by name (`docs/sound.md`) | **read** (2026-11-08): the index prefers it |

## Reader robustness

Two zone archives fail in our MPK reader: one is truncated, and one has a duplicate member name
(`fixtures.csv`), which the reader rejects outright. Decide whether to tolerate duplicates (first or
last wins) once we know which zones they belong to and whether the client reads them.

## Next audits

The `SECTOR.DAT` and CSV inventory is complete; the binary and chunk formats are only sampled. Before
building on each: the dungeon chunk format (M11), `.kfa` animations (M7b), texture resolution (see
`docs/nif-coverage.md`), and the `light/` tables.
