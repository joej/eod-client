# Godot features for the client

What Godot 4 offers that the client uses, and what it doesn't use yet, mapped to the roadmap and open
questions. Reviewed 2026-11-03 against `godot/project.godot` and `godot/scripts/` (Godot 4.7, Forward+).

## In use

- **Window and resolution** (2026-11-03): opens maximized; **F11** toggles fullscreen (saved). The 3D
  view renders at the window's size (1920 x 1080 screens: about twice the pixels of the old fixed
  1280 x 720 window).
- **Anti-aliasing** (2026-11-03): 4x MSAA, and alpha to coverage on cut-out foliage (smooths leaf
  edges instead of stair-stepped, shimmering ones).
- **Texture filtering** (2026-11-03): 16x anisotropic (sharp ground at the chase camera's shallow
  angle); mipmaps generated for textures that have none (`.tga`, `.bmp`: they shimmered at distance).
- **Sun shadows** (2026-11-03): the sun casts shadows out to 250 m in four cascades; a dev-overlay
  toggle ("Sun shadows", saved). The original drew fixed shadows instead (`stylemap/SHADOW.BMP`, the
  far-level cards); real shadows are ours.
- glTF import at runtime, standard materials, a custom sky and water shader, distance fog,
  trimesh collision for the ground and the camera spring arm.
- **Frame rate** in the status line (dev overlay), to judge each of these by its cost.

## Not used yet

| Feature | What it would give us | Where |
|---|---|---|
| **MultiMesh** instancing | hundreds of identical trees and rocks in one draw call; today each of ~1,500 zone objects is its own node copy, many with their own trimesh collider | performance, M8 (streaming zones) |
| **Visibility ranges** (HLOD) | swap or fade objects by distance: a native home for DAoC's LOD levels (today we keep only the near level) | performance, M8 |
| **Occlusion culling** | skip what walls hide: cities and dungeons | M11 |
| **Threaded resource loading** | no hitch while a zone loads | M8 |
| Material **detail layer** (own UV set, multiply) | DAoC's dark and detail maps: baked shading on buildings, the moss on Domnann's standing stones | W25 |
| **LightmapGI / SDFGI / VoxelGI**, **SSAO** | computed lighting and contact shading: an optional "enhanced" look beside the classic dark maps | M12 |
| **SSR / reflection probes**, normal-mapped water | Shrouded Isles and Reflective water | W24, M12 |
| **GPU particles** | waterfall mist, spell effects, rain and snow (blocked under roofs by the `.nhd` cover grids) | M13 |
| **FogVolume / volumetric fog** | SECTOR.DAT `[forest]` fog regions | M7d, M13 |
| **Decals** | blob shadows, the ground-target spell ring (`stylemap/target.bmp`) | M10 |
| **Skeleton3D + AnimationPlayer / AnimationTree** | skinned figures and their `.kfa` animations | M7b |
| **AudioStreamPlayer3D** | positional sound and music | M13 |
