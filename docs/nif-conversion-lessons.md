# Converting DAoC's 3D assets: scale, transforms, and what the file format leaves unsaid

This one is long, and I want to apologize for that up front. It is a genuinely technical subject, and the parts that matter are the specifics: the exact scale constant, the byte layout of a transform, which version of the file format gates which field. A short version would just be a list of claims you would have to take on faith. If you are converting Dark Age of Camelot assets yourself, or any old NetImmerse or Gamebryo content, the detail is the whole point, so I have kept it.

Everything here comes from building [eod-client](https://github.com/joej/eod-client), an open-source (GPL-3.0) Godot and C# client, and from a handful of other GPL projects we checked our work against. Links go to the public source so you can read the real implementation. Where a finding came from someone else's published reverse engineering of the retail client, I say so and cite them; that work is theirs, not ours.

## Two coordinate systems, and the surprise that one number covers both

DAoC positions things in two unrelated unit systems, and for a long time we assumed they needed two different conversions into Godot's metres.

The first is NIF units. Every mesh, whether a building, an item, or a creature, stores its geometry in the units of the NIF file it came from. The second is region units, which the server uses for live positions in the `0x20` and `0xA9` packets, and which the zone files use for object placements and the terrain heightmap. A character walking across a zone moves in region units; the keep they walk up to is built in NIF units. Nothing in the data says the two are related.

The finding that took a while to trust is that they are in fact the same scale: **0.025 metres per unit**, which is to say divide by forty. One conversion covers both systems.

We did not take that on faith. The number first appears in the OfflineDAoC project's pipeline as a constant, and we confirmed it against our own data using two objects whose real-world size we could reason about independently.

The first anchor is a human figure. A Gamebryo humanoid fixture in our test set measures 71.8 NIF units from foot to head, which at 0.025 comes out to 1.79 metres. That is a normal adult height, and we guard it in CI with a test called `HumanoidConvertsToAPlausibleHeightInMetres` so a future change to the exporter cannot quietly break the scale.

The second anchor is a building. `keep.NIF` converts to roughly 16 to 17 metres tall with a footprint around 52 by 65 metres. That reads as a plausible multi-storey stone keep, the kind of structure you walk up to rather than a cottage or a skyscraper. We verified it by rendering it in Godot and looking at it next to the human-height capsule.

With both the model scale and a second check, we could confirm the region frame numerically. The keep's placement in region coordinates is X 574208, which at 0.025 lands at Godot X 14355. The player spawn at region X 574797 lands at 14370. Both sit on terrain that spans 0 to 15366 in the same frame. Everything agrees.

The constant lives in two places, one per system:

- `src/DaocAssets/Nif/GltfExporter.cs:96` bakes `× 0.025` into every `.glb` it writes, so a converted model arrives already in metres.
- `godot/scripts/WorldView.cs:30` applies the same `0.025` to the raw region coordinates that never pass through the exporter, the live positions and the zone placements.

The reasoning and the measurement recipe are written up in [`docs/world-scale-calibration.md`](https://github.com/joej/eod-client/blob/main/docs/world-scale-calibration.md).

### The double-scale bug

Before we understood that the two systems shared a scale, we assumed region units were something else, and the client applied its own separate factor of 1/100 on top of whatever the exporter had already done to a model.

That works fine for a model, because a model is a discrete object you place at a point. It fails badly for the terrain, because the exporter bakes its `0.025` into the terrain mesh as well. The terrain glb is already in metres when it loads. Scaling it a second time by the region factor shrank it to one fortieth of its correct size and pushed it about five kilometres away from the player. The result rendered as a small distant tangle we started calling the kaleidoscope.

Once the single-scale finding landed, the fix was small. The terrain glb loads at scale 1, because it is already in metres, and only the raw live and placement coordinates get multiplied by 0.025. The comment block at `godot/scripts/WorldView.cs:19` records the history for the next person who reads it. The general lesson is that when a scale factor is baked into your exported assets, you need to track exactly which path each piece of geometry took to the screen. Applying the factor a second time has no visible effect on a discrete object you place at a point, which is what makes it easy to miss, but it severely distorts anything that was already converted.

## The file format comes in four versions across two engine eras

DAoC ships NIF files in four format versions, and they split across two different engines with meaningfully different headers.

The NetImmerse era covers versions 4.1.0.12, 4.2.1.0, and 4.2.2.0. These hold items and buildings. The Gamebryo era is version 10.1.0.0, which holds creatures and characters, and those are internally rigged and skinned. Our reader implements the 4.2.x path in full and a separate path for 10.1.0.0, and it rejects anything older than 4.1.0.1.

The two headers are not variations on a theme; they are built differently.

A 4.2.x file, after the version word, gives a block count and then a sequence of blocks. Each block is an inline sized type-name string followed by its body. The bodies are not self-describing about their length, so a reader that does not model a given block type has to scan forward for the next recognizable type string to find where it resumes. Our reader does exactly that, which lets it skip blocks it does not care about without knowing their size.

A 10.1.0.0 file is laid out around a block-type table. After the version word comes a user-version word, a block count, a table of type names, and then a per-block index into that table using a `u16` for each block. The bodies carry no inline type string at all. This matters because the intermediate blocks in a character file, the scene nodes and the keyframe controllers and the skin data, have large layouts that are themselves gated by version in ways that are easy to get subtly wrong. Rather than model all of that for a geometry-only reader, our 10.1.0.0 path validates the header and then scans for the geometry bodies, the triangle shapes and strip shapes, by checking their fields against strict self-validating constraints.

A few specific layout facts from the NifTools `nif.xml` schema, which we verified against real retail 1.127 client files, are useful to have if you write your own reader:

- A NIF boolean is one byte from version 4.1.0.1 onward.
- At 4.2.2.0 the geometry block has no explicit "has UV" byte. The UV set count lives in the low six bits of a Data Flags `u16`, and that field only exists up to 4.2.2.0.
- At 4.2.2.0 there is also no "has triangles" byte; that field is a 10.1.0.0 addition. The triangles follow their count directly.
- At 10.1.0.0 the geometry gains a Consistency Flags `u16`, and bit 0x1000 of Data Flags signals that tangents are present.

These version facts are collected in the header comment at `src/DaocAssets/Nif/NifReader.cs:9`, and the header dispatch between the two eras is at `src/DaocAssets/Nif/NifFile.cs:26`.

### Validating the transform layout against another reader

The single most useful thing we did for correctness was check our transform layout against a second, independent GPL implementation rather than trusting our own reading of the schema.

The transform on a NIF object sits inside its `NiAVObject` record. The question that bit us is which fields are present at DAoC's versions, because the schema has fields that exist only for certain engines. DAOC_Navigator, a GPL-3.0 C# project with a full per-block NIF reader called NIFLib, served as the reference. Its reader has a field named `UnkownShort1` in that area, and the useful finding is that this field is gated to the Bethesda era and is absent for DAoC's 4.2.2.0 and 10.1.0.0. Our reader is correct precisely because it does not read that short.

For the record, our `NiAVObject` read goes straight through name, an extra-data reference, a controller reference, a flags `u16`, the translation, a three-by-three rotation, the scale, a velocity, the property references, and a has-bounding-volume boolean, with no extra short in the middle. The code is at `src/DaocAssets/Nif/NifFile.cs:343`. The OfflineDAoC project's Z-up to Y-up matrix independently matched ours as well, which was a second confirmation on a different piece.

## Transforms, and turning Z-up into Y-up

A NIF local transform is a rigid transform: a three-by-three rotation, a single uniform scale, and a translation. We model it with a small struct whose `Apply` on a point computes rotation times vector times scale plus translation, and whose direction variant applies rotation only, with no scale and no translation, so that normals stay the right length. Composing a parent with a child multiplies the rotations, multiplies the scales, and applies the parent to the child's translation. That code is at `src/DaocAssets/Nif/NifFile.cs:43`.

To build a usable mesh, the reader indexes every block, reads the node and shape transforms and their references, then walks from the scene root down the hierarchy composing transforms as it goes. At each shape it bakes the accumulated world transform into that shape's vertices and normals, so the exported mesh needs no scene graph of its own. There is a fallback that bakes any shape the root walk never reached at its own local transform, so a file whose root is not the first block does not silently lose geometry. The walk is at `src/DaocAssets/Nif/NifFile.cs:145` and the bake at `:234`.

The conversion into Godot's coordinate space happens in the exporter. NIF is Z-up; glTF and Godot are Y-up, right-handed, in metres. The axis map is `(x, y, z)` to `(x, z, -y)`. Positions run through `ToYUp`, which applies that swap and the `0.025` scale together. Normals run through a direction variant that applies the swap without the scale. Both are at `src/DaocAssets/Nif/GltfExporter.cs:98`.

### The winding trap

This is the detail that cost us the most time, and it is why the keep first rendered inside-out.

The axis map `(x, z, -y)` is a change of handedness. The textbook reaction to a handedness change is that triangle front and back swap, so you flip triangle winding from `(a, b, c)` to `(a, c, b)` to compensate. We did that, and roughly ninety-nine percent of the keep's faces ended up inverted. Back-face culling then removed the outside surfaces and kept the inside ones, so you could see straight through the tower walls into the interior.

Flipping the winding was itself the cause of the problem. The exporter carries each vertex's real NIF normal through the same axis map and binds it as the glTF normal attribute, so the facing is already defined by the actual normal data. When you check the winding against those normals, the NIF's original `(a, b, c)` order is the one that faces outward, and the extra flip we added is what inverted the faces. The working rule we settled on is to leave the NIF winding alone during the Y-up conversion. The decision and the reasoning are commented at `src/DaocAssets/Nif/GltfExporter.cs:65`.

Two related points so they are not confused with mesh winding. Triangle strips still need their winding flipped on odd-numbered triangles during conversion to a triangle list, which is just how strips work; that is at `src/DaocAssets/Nif/NifFile.cs:659`, and it treats `0xFFFF` as a strip-restart marker. And separately, the terrain collider in Godot turns on backface collision, because the terrain's faces point downward and the ground-snap ray needs to register a hit coming from above. That is a collision setting on the terrain, unrelated to how the mesh renders, at `godot/scripts/WorldView.cs:329`.

## What the format does not tell you about texturing

The texturing lessons are where DAoC stops behaving like a tidy modern asset and you have to learn how the original client actually used the data.

The starting symptom: about two thirds of a classic keep's shapes store no texture coordinates at all. In `keep.NIF`, which is NetImmerse 4.2.2.0, 39 of its 59 geometry shapes declare zero UV sets. These are the tower cylinders and the wall bodies. The remaining 20 shapes declare one UV set and texture correctly. A renderer handed a shape with no UVs samples texel (0,0) for every pixel, which collapses the whole texture down to one averaged colour, so the towers came out a flat tan. This is not a bug in the reader. The data genuinely has no UVs for those shapes.

Our first theory was that these shapes were meant to be texture-projected, with the client generating UVs on the fly from a projection. We went looking for the block that would drive that, the `NiTextureEffect`, and probed `keep.NIF` along with three other buildings. None of them contained a single `NiTextureEffect` block, across 223, 507, 316, and 25 blocks respectively. The projection idea had no support in the data. (The block type does exist in DAoC, and a few environment and trophy models use it, so it is present in the format and absent from the keeps specifically.)

The actual mechanism is simpler, and the correlation in the file is exact. Every shape that has a `NiTexturingProperty` has one UV set and a real source texture, and those materials are white so the texture shows through unmodulated. Every shape that lacks a texturing property has no UVs and carries a `NiMaterialProperty` with a chosen diffuse colour. In `keep.NIF`, 38 of the untextured shapes share one material whose diffuse colour is RGB (233, 171, 74), a tan stone. Those towers were authored to be a solid colour, and the absence of UVs on them is deliberate rather than missing data.

So the fix is to read each shape's material colour and apply it, and to stop guessing. Our old code had fallen back, for any shape that named no texture, to the first texture in the file, then sampled it at (0,0). That produced a tan that was accidentally close to right, by the wrong mechanism, and it would have broken on any model whose intended colour differed from its first texture's average. The corrected reader carries each shape's diffuse and alpha from its `NiMaterialProperty` into the mesh, and the exporter writes it as the glTF base colour factor. A shape with no texturing property gets a null texture and renders in its material colour, with no UV generation anywhere. The material reader is at `src/DaocAssets/Nif/NifFile.cs:390`, the field it feeds is at `src/DaocAssets/Nif/NifMesh.cs:20`, and the full set of findings is written up as W3 and W3a in [`docs/open-questions.md`](https://github.com/joej/eod-client/blob/main/docs/open-questions.md).

### Two texturing cases that go further, and are not solved here

The frontiers-era keeps raise the difficulty again, and we have not finished them. `1MainKeep.NIF` and `1-outpost_tower.NIF` embed no texturing properties and no source textures at all across 507 blocks. Their shapes instead carry material tokens with a leading exclamation mark, names like `!main_wall`, `!stone_trim`, `!wood_planking`, and `!corona`, over a neutral grey diffuse. The original client resolves these names at load time to realm-specific and keep-level-specific textures, so `!main_wall` becomes something like `alb_mainwall_lvl3.dds` depending on which realm owns the keep and how far it is upgraded. Rendering these correctly needs a name-to-texture binding table keyed on realm and upgrade level, which we have deferred until the frontiers are actually in scope. One caution for anyone generalizing from the classic keeps: some frontiers shapes do have UV sets while still carrying no texturing property, so the clean "no texturing property means no UVs" rule from the classic keep does not hold here.

A second case is invisible geometry. Some shapes in a NIF are collision hulls and climb volumes for gameplay and are not meant to be drawn. We first guessed these were identifiable by a low alpha value around 0.25, which did not hold up. They are identified by node name, which is how the retail client does it. A node named `collidee` (in any case, with variants like `__patch_collidee` and numbered `collideeNN`) is wall collision, and a node whose name begins with `climb`, excluding `sclimb`, is a climb volume. That identification, including the specific `game.dll` code offsets, comes from the OfflineDAoC project's reverse engineering of the retail client. We adopt it with attribution, since our own policy is to avoid disassembling the client. For a converter, the practical step is to drop these shapes by node name when a file contains them.

These texturing cases are covered as W3b and W3c in the open-questions log.

## What the other projects taught us

We leaned on three GPL projects, and the comparison is itself instructive because they made different design choices.

OfflineDAoC takes a spec-driven approach. Its NIF reader parses the NifTools `nif.xml` schema at runtime and interprets every block generically from the schema's field and condition rules, so one reader handles every version and, importantly, gets skinning and animation for free. Ours is hand-coded, geometry-only, and carries no schema dependency, which makes it smaller and strongly validating at the cost of not yet doing skinned characters. Checking the two against each other confirmed several things for us: our MPK archive reader field for field, the Z-up to Y-up matrix, and the keep texturing mechanism. It also gave us the measured scale constant and corrected our collision-volume identification. The one place our approach is clearly ahead is that the Python NIF library OfflineDAoC uses elsewhere cannot read DAoC's older 4.x item files, so they keep a separate code path for those, while our single reader handles them. We expect to port their skinning and figure-assembly work, with attribution, when we build animated characters. The full comparison is in [`docs/offlinedaoc-asset-pipeline.md`](https://github.com/joej/eod-client/blob/main/docs/offlinedaoc-asset-pipeline.md).

DAOC_Navigator provided the per-block reference reader that validated our transform layout, as described above. We use it as a reference for now and intend to port its skinning and monster-assembly code later, since its licence is compatible with ours.

DaocClientLib is the lineage for the MPK extraction tool rather than a NIF-conversion reference; it is the library an owner runs on their own installation to unpack the archives into the NIF, texture, and animation files the converter then reads.

## Summary of the findings

If you are building something similar, these are the findings that will save you the most time. DAoC uses one scale, 0.025 metres per unit, for both its mesh units and its region units, and you should avoid applying it twice to anything your exporter has already baked. The format comes in four versions across two engine eras with different headers, and it is worth validating your transform layout against another implementation before trusting your own reading of the schema. During the Y-up conversion, keep the NIF triangle winding, because the real normals already define the correct facing. A large share of the geometry has no texture coordinates by design; those shapes are meant to render in their material colour. Some other shapes bind their textures by name at load time, and some are collision volumes that should not be drawn at all.

---

*Part of the [eve of darkness](https://eveofdarkness.org) devlog. The companion piece, on how the client assembles the world you see from several independent sources, is [here](https://eveofdarkness.org/posts/composite-world/).*
