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

## Kinds of zone

| Kind | Zones | How it's built | Converter |
|---|---:|---|---|
| Outdoor | 98 | heightmap (`terrain.pcx` + `offset.pcx`), placed NIFs (`nifs.csv` + `fixtures.csv`), terrain tile textures | **read** |
| Dungeon | 282 | `dungeon.chunk` (NIF pieces), `dungeon.place`, `dungeon.prop`, `dungeon.water`, `portals.csv` | not read (roadmap M11) |
| "Skycity" | 17 | the same chunk files under `skycity.*` | not read (M11) |
| Other / empty | 5 | | |

Three quarters of the zones use a format we have never opened. The chunk files are text (comma-separated
numbers and NIF names), so reading them is format work, not reverse engineering.

## What a zone describes that we don't draw yet

From `SECTOR.DAT` (375 zones have one) and the data archives. Numbers are zones that contain it.

| Feature | Where | Zones | Roadmap |
|---|---|---:|---|
| Rivers, lakes, swamps | `[riverNN]`: type, name, height, bank lines, texture + ripple overlay, flow, colour, tessellation; `water.pcx` | 100 | M7d |
| Fog | `[fog]`: colour, amount, clip distance | 375 | M7d |
| Sky | `[sky]` colour (329); `[terrain]` horizon and clouds (323–331); `skynif` (5) | 329 | M7d |
| Forest fog regions | `[forest]`: colour, fog, rectangles and circles | 269 | M7d |
| Far ground | `[farground]` texture | 134 | M7d |
| Static lights | `lights.csv` (x, y, z and a fourth value, likely the light type) | 304 | M7d |
| Baked terrain shading | `shadow.pcx`, `shademap.pcx` | 98, 86 | M7d |
| Terrain holes | `[holes]` (probably cave entrances cut in the heightmap) | 98 | M7d |
| Grass and ground cover | `grassmap.pcx`, `densemap.pcx` | 84 | M12 |
| Snow | `snowmap.tga` | 18 | later |
| Per-patch terrain textures | `textures.csv`, `patch#-#.dds`, `lod#-#.dds` (96 zones), possibly the "Atlantis terrain" option (open question W8) | 96 | M7d / W8 |
| Zone jumps | `zonejump.csv`, `[jumpNN]` travel points | 345, 45 | M8 |
| Zone bounds | `bound.csv`, `borders.csv`, `adjacency.csv` | 98, 9, 4 | M8 |
| Ambient sound | `sounds.dat` beside each zone's archives | 389 | M13 |
| Unknown | `[mongens]` (number ranges), `[dungeonfix]` (lighting flags), `[start]` | 97, 129, 370 | to identify |

## Outside the zones

| Folder | Contents | Roadmap |
|---|---|---|
| `anims/` | 4,060 `.kfa` animations | M7b (audit the format first) |
| `light/` | `.tra`, `.col`, `.pcx`: probably day/night lighting tables (unconfirmed) | M13 |
| `water/` | water textures and 3 NIFs | M7d |
| `effects/` | 1,575 effect NIFs, 573 DDS | M13 |
| `sounds/` | 2,703 `.wav`, 49 music `.mp3`, 12 CSVs | M13 |
| `ui/` | 151 XML layouts, 502 DDS, fonts | M9 (UI direction), later |
| `phousing/`, `frontiers/` | housing; RvR frontiers and keeps | further out |
| `zones/trees/` | SpeedTree trees + `treemap.csv` (open question W5) | M12 |

## Reader robustness

Two zone archives fail in our MPK reader: one is truncated, and one has a duplicate member name
(`fixtures.csv`), which the reader rejects outright. Decide whether to tolerate duplicates (first or
last wins) once we know which zones they belong to and whether the client reads them.

## Next audits

The `SECTOR.DAT` and CSV inventory is complete; the binary and chunk formats are only sampled. Before
building on each: the dungeon chunk format (M11), `.kfa` animations (M7b), texture resolution (see
`docs/nif-coverage.md`), and the `light/` tables.
