// SPDX-License-Identifier: GPL-3.0-or-later
//
// WorldView: The zone around the player: terrain, placed objects, water, sky and view distance.

using DaocProtocol.Net;
using DaocProtocol.Packets;
using DaocProtocol.Session;
using DaocProtocol.World;
using Godot;

namespace EodClient;

public partial class WorldView
{
    // Loads the zone terrain .glb if given. The exporter already baked region units → metres (× 0.025)
    // into its vertices in absolute region-global position, so it drops in at the origin with NO extra
    // scale (scale 1) — matching models, which are also metres. ToGodot converts the raw live/placement
    // coords by the same 0.025, so the terrain and everything placed on it share one frame. Returns
    // false (use the flat placeholder) when no terrain was given or it can't load.
    private bool TryAddTerrain()
    {
        if (string.IsNullOrEmpty(_terrainPath) || !Godot.FileAccess.FileExists(_terrainPath))
            return false;

        // Untextured here: each ground tile is textured below.
        if (ModelTexturing.LoadGlb(_terrainPath) is not Node3D terrain)
            return false;
        // No scale: the .glb is already in metres (GltfExporter baked × 0.025). Scaling again here was
        // the double-scale that shrank the terrain to 1/40th and divorced it from the player position.
        terrain.Scale = Vector3.One;

        // Texture each ground tile (texRR-CC) from the library, like any model. A tile that doesn't
        // resolve keeps a plain green fallback so the ground is never a hole.
        string libraryDir = string.IsNullOrEmpty(_libraryPath)
            ? System.IO.Path.GetDirectoryName(_terrainPath) ?? ""
            : _libraryPath;
        int textured = ModelTexturing.Apply(terrain,
            libraryDir.PathJoin("overrides"), libraryDir.PathJoin("textures"), libraryDir);

        var green = new StandardMaterial3D
        {
            AlbedoColor = new Color(0.33f, 0.46f, 0.28f),
            CullMode = BaseMaterial3D.CullModeEnum.Disabled,
        };
        foreach (MeshInstance3D mi in FindMeshes(terrain))
        {
            Mesh? mesh = mi.Mesh;
            if (mesh is null)
                continue;
            for (int s = 0; s < mesh.GetSurfaceCount(); s++)
            {
                // Both sides: a camera that dips under a slope still sees ground (the exported
                // triangles face up, with normals from the heightmap, since 2026-11-03). Green only
                // where no tile was applied.
                if (mi.GetSurfaceOverrideMaterial(s) is BaseMaterial3D tile)
                {
                    tile.CullMode = BaseMaterial3D.CullModeEnum.Disabled;
                    // Each tile's UVs run exactly 0..1, edge to edge. With the default repeat, filtering
                    // and mipmaps at a tile's edge blend in the texture's OPPOSITE edge: thin lines
                    // along every tile border, clearest at low angles (W21). Clamp instead.
                    tile.TextureRepeat = false;
                }
                else
                    mi.SetSurfaceOverrideMaterial(s, green);
            }
        }
        // Trimesh static collision on every terrain surface, so the ground raycast finds the real
        // heightmap and the player walks on it (M4). Until 2026-11-03 the terrain .glb's triangles
        // faced DOWN, so a plain trimesh collider only reported hits from below and the downward ground
        // ray passed through it (ground --, player floating); BackfaceCollision = true made the ray hit
        // either winding. The triangles face up now, and the flag stays so terrain converted before
        // that still works. Build the shape explicitly (not MeshInstance3D.CreateTrimeshCollision) only
        // so we can set that flag. Put every terrain body on its OWN collision layer so the ground ray can target
        // terrain alone — otherwise a ray from high above hits a tall placed object (tree/keep) first
        // and snaps the player onto its roof ("spawned very high in the air").
        AddChild(terrain);
        _terrain = terrain;
        int built = 0;
        foreach (MeshInstance3D mi in FindMeshes(terrain))
        {
            if (mi.Mesh is null)
                continue;
            ConcavePolygonShape3D shape = mi.Mesh.CreateTrimeshShape();
            shape.BackfaceCollision = true;   // hit from either side (see above)
            var sb = new StaticBody3D { CollisionLayer = TerrainLayer, Transform = mi.GlobalTransform };
            sb.SetMeta("terrain_mesh", mi);   // so an inspect pick can name the tile it hit
            sb.AddChild(new CollisionShape3D { Shape = shape });
            AddChild(sb);
            built++;
        }
        GD.Print($"[world] terrain loaded ({textured} ground tile(s) textured, {built} collider(s))");
        return true;
    }

    // Places the zone's static objects from a zone.csv manifest (from `mpk zone`): each row is
    // "glb,x,y,z,angle,scale" in region-global coordinates. Each distinct .glb is loaded and textured
    // once, then instanced per placement. Returns how many objects were placed. No manifest -> 0.
    private int BuildZone()
    {
        if (string.IsNullOrEmpty(_zonePath) || !Godot.FileAccess.FileExists(_zonePath))
            return 0;

        // Models/textures come from the shared library (overrides/ win, then models/ + textures/);
        // fall back to the manifest's own folder if no library was given.
        string libraryDir = string.IsNullOrEmpty(_libraryPath)
            ? System.IO.Path.GetDirectoryName(_zonePath) ?? ""
            : _libraryPath;
        var placements = DaocAssets.Zone.ZoneCheck.ReadManifest(Godot.FileAccess.GetFileAsString(_zonePath));
        var loaded = new System.Collections.Generic.Dictionary<string, Node3D?>();
        var zoneRoot = new Node3D { Name = "Zone" };
        _zoneRoot = zoneRoot;
        int placed = 0;

        foreach ((string glb, float x, float y, float z, float angleDeg, float scale) in placements)
        {
            Node3D? template = LoadZoneModel(glb, libraryDir, loaded);
            if (template is null)
                continue;

            var inst = (Node3D)template.Duplicate();
            inst.Position = ToGodot(x, y, z);
            // DAoC angle is degrees about the vertical axis; negate for Godot's handedness (as heading).
            inst.Rotation = new Vector3(0, -Mathf.DegToRad(angleDeg), 0);
            // The model .glb is already in metres (GltfExporter bakes NIF units × 0.025). The only
            // remaining factor is the per-fixture art scale from the manifest (a ratio, 100% -> 1.0);
            // apply it directly. (Before GltfExporter baked in metres, this also divided by the region
            // factor to shrink raw NIF units — that is now wrong and would scale the model twice.)
            inst.Scale = new Vector3(scale, scale, scale);
            // What the inspect readout reports for this placement (raw zone.csv values).
            inst.SetMeta("glb", glb);
            inst.SetMeta("placement", $"x {x:0} y {y:0} z {z:0}  angle {angleDeg:0}°  scale {scale:0.##}");
            zoneRoot.AddChild(inst);
            // Trimesh collision per placed object, so the SpringArm chase camera pulls in off
            // buildings/keep walls (not just terrain). Generated after AddChild so the static bodies
            // sit in the live tree. ~1,500 static bodies for a full zone; revisit if load time bites.
            foreach (MeshInstance3D mi in FindMeshes(inst))
                mi.CreateTrimeshCollision();
            placed++;
        }

        if (placed > 0)
            AddChild(zoneRoot);
        int distinct = 0;
        foreach (Node3D? m in loaded.Values) if (m is not null) distinct++;
        GD.Print($"[world] zone: {placed} object(s), {distinct} distinct model(s), " +
            $"{_zoneTexturedModels} textured ({_zoneTexturedSurfaces} surface(s))");
        // The templates were only duplicated, never added to the tree, so nothing else frees them
        // (the copies keep the shared meshes and materials alive).
        foreach (Node3D? m in loaded.Values)
            m?.Free();
        return placed;
    }

    // Loads the zone's rivers, lakes and swamps: water.glb (one waterNN mesh per body, already in
    // region-global metres like the terrain) and water.csv (texture, flow, type per body), both written
    // by `mpk water` beside zone.csv. Each surface gets the water shader with its texture and flow.
    // Returns how many bodies were placed; none if the files aren't there.
    private int BuildWater()
    {
        if (string.IsNullOrEmpty(_zonePath))
            return 0;
        string dir = System.IO.Path.GetDirectoryName(_zonePath) ?? "";
        string glb = dir.PathJoin("water.glb"), csv = dir.PathJoin("water.csv");
        if (!Godot.FileAccess.FileExists(glb) || !Godot.FileAccess.FileExists(csv))
            return 0;

        var rows = DaocAssets.Zone.ZoneWater.ReadCsv(Godot.FileAccess.GetFileAsString(csv));

        if (ModelTexturing.LoadGlb(glb) is not Node3D water)   // the water shader textures it below
            return 0;
        string lib = string.IsNullOrEmpty(_libraryPath) ? dir : _libraryPath;
        var shader = GD.Load<Shader>("res://shaders/water.gdshader");
        int bodies = 0;
        foreach (MeshInstance3D mi in FindMeshes(water))
        {
            // The node is named after the body (waterNN); the mesh instance may be it or its child.
            string name = rows.ContainsKey(mi.Name) ? mi.Name : mi.GetParent()?.Name ?? "";
            if (!rows.TryGetValue(name, out DaocAssets.Zone.WaterRow? row))
                continue;
            var mat = new ShaderMaterial { Shader = shader };
            if (ModelTexturing.Load(row.Texture, lib.PathJoin("overrides"), lib.PathJoin("textures"), lib) is { } tex)
                mat.SetShaderParameter("albedo_tex", tex);
            // DAoC's flow is about -50..50; as texture repeats per second, /1000 reads as a slow drift.
            mat.SetShaderParameter("flow", row.Flow / 1000f);
            mi.MaterialOverride = mat;
            bodies++;
        }
        water.Name = "Water";
        AddChild(water);
        _waterRoot = water;
        GD.Print($"[world] water: {bodies} bod{(bodies == 1 ? "y" : "ies")}");
        return bodies;
    }

    // Applies the zone's sky from sky.csv (written by `mpk sky` beside zone.csv) for one weather condition
    // and time of day: the sky shader's canopy and cloud gradients, cloud layers and sun disk; ambient and
    // sun light from the file; and depth fog in the file's distance-fog colour. Fog distances come from
    // SECTOR.DAT's [fog]: clip is the client's far plane (view distance) and amount the percentage of
    // that distance, measured back from it, that is fogged (Camelot Hills: 15% of 16000 units → fog
    // from 340 to 400 m); amount 0 turns fog off. Light energies are scaled by eye. Returns the sky's name,
    // or null (plain colour background) when there is no sky.csv.
    private string? ApplySky(string condition, string time)
    {
        if (string.IsNullOrEmpty(_zonePath))
            return null;
        string dir = System.IO.Path.GetDirectoryName(_zonePath) ?? "";
        string csv = dir.PathJoin("sky.csv");
        if (!Godot.FileAccess.FileExists(csv))
            return null;
        var kv = new System.Collections.Generic.Dictionary<string, string>();
        foreach (string line in Godot.FileAccess.GetFileAsString(csv).Split('\n').Skip(1))
        {
            int comma = line.IndexOf(',');
            if (comma > 0)
                kv[line[..comma]] = line[(comma + 1)..].Trim();
        }
        string k = $"{condition}.{time}";
        Color Col(string key) => kv.TryGetValue(key, out string? v) && Floats(v) is { Length: >= 3 } f
            ? new Color(f[0], f[1], f[2], f.Length > 3 ? f[3] : 1f) : Colors.White;
        float Num(string key, float fallback) =>
            kv.TryGetValue(key, out string? v) && Floats(v) is { Length: >= 1 } f ? f[0] : fallback;

        string lib = string.IsNullOrEmpty(_libraryPath) ? dir : _libraryPath;
        string[] texDirs = [lib.PathJoin("overrides"), lib.PathJoin("textures"), lib];
        var mat = new ShaderMaterial { Shader = GD.Load<Shader>("res://shaders/sky.gdshader") };
        foreach (string band in new[] { "zenith", "east1", "east2", "east3", "east4", "west1", "west2", "west3", "west4", "nadir" })
        {
            mat.SetShaderParameter(band, Col($"{k}.canopy.{band}"));
            if (band != "nadir")
                mat.SetShaderParameter("c_" + band, Col($"{k}.clouds.{band}"));
        }
        // cloud0 = top, cloud1 = bottom (the first two layers; the "2" layers are weather variants).
        foreach (var (key, uniform) in new[] { ("cloud0", "top"), ("cloud1", "bottom") })
        {
            if (!kv.TryGetValue(key, out string? layer))
                continue;
            string[] p = layer.Split(' ');
            if (ModelTexturing.Load(p[0], texDirs) is not { } tex)
                continue;
            mat.SetShaderParameter($"cloud_{uniform}", tex);
            mat.SetShaderParameter($"{uniform}_tile", Floats(p[1])[0]);
            mat.SetShaderParameter($"{uniform}_speed", Floats(p[2])[0]);
            mat.SetShaderParameter("has_clouds", true);
        }
        if (kv.TryGetValue("sun", out string? sun) && ModelTexturing.Load(sun.Split(' ')[0], texDirs) is { } disk)
        {
            mat.SetShaderParameter("sun_disk", disk);
            mat.SetShaderParameter("has_sun", true);
            mat.SetShaderParameter("sun_color", Col("sun.noon_color"));
        }
        _environment.BackgroundMode = Godot.Environment.BGMode.Sky;
        _environment.Sky = new Sky { SkyMaterial = mat };

        _environment.AmbientLightSource = Godot.Environment.AmbientSource.Color;
        _environment.AmbientLightColor = Col($"{k}.ambient");
        _environment.AmbientLightEnergy = Num($"{k}.ambient_amount", 0.5f) * 1.4f;
        _sun.LightColor = Col($"{k}.sun");
        _sun.LightEnergy = Num($"{k}.sun_amount", 0.6f) * 1.7f;

        _zoneClip = Num("fog.clip", 0) * RegionMetresPerUnit;
        _fogShare = Mathf.Clamp(Num("fog.amount", 0) / 100f, 0f, 1f);
        _environment.FogLightColor = Col($"{k}.fog");
        float clip = _zoneClip, amount = _fogShare;
        string name = kv.GetValueOrDefault("sky", "?");
        GD.Print($"[world] sky: sky_{name} {k}, zone view {clip:0} m, fogged share {amount:0.00}");
        return name;
    }

    // View distance: the zone's own (classic) times the player's setting. The camera's far plane and the
    // fog scale together, keeping the zone's fogged share, so a longer view still fades out the way the
    // zone does. Zones without sky.csv keep a long fixed far plane and no fog. (Beyond the loaded zone
    // there is nothing to see until neighbouring zones load, roadmap M8.)
    private void ApplyViewDistance(int setting)
    {
        if (_zoneClip <= 0)
            return;
        float view = _zoneClip * ClientSettings.ViewDistances[setting].Multiple;
        _camera.Far = view * 1.02f;
        _environment.FogEnabled = _fogShare > 0;
        _environment.FogMode = Godot.Environment.FogModeEnum.Depth;
        _environment.FogDensity = 1f;
        _environment.FogSkyAffect = 0f;
        _environment.FogDepthEnd = view;
        _environment.FogDepthBegin = view * (1f - _fogShare);
        GD.Print($"[world] view distance {ClientSettings.ViewDistances[setting].Label}: {view:0} m" +
            (_fogShare > 0 ? $", fog {_environment.FogDepthBegin:0}-{view:0} m" : ""));
    }

    private static float[] Floats(string s) => s.Split(' ', System.StringSplitOptions.RemoveEmptyEntries)
        .Select(x => float.TryParse(x, System.Globalization.NumberStyles.Float, System.Globalization.CultureInfo.InvariantCulture, out float f) ? f : 0f)
        .ToArray();

    // Loads a zone .glb once (cached). Resolves the library layout: an overrides/<glb> wins, else
    // models/<glb>; a flat <lib>/<glb> is also accepted (manifest-folder fallback). Textures are
    // applied from the same layers (overrides win over textures/). Null if it can't load.
    private Node3D? LoadZoneModel(string glb, string libraryDir, System.Collections.Generic.Dictionary<string, Node3D?> cache)
    {
        if (cache.TryGetValue(glb, out Node3D? cached))
            return cached;

        string? path = FirstExisting(
            libraryDir.PathJoin("overrides").PathJoin(glb),
            libraryDir.PathJoin("models").PathJoin(glb),
            libraryDir.PathJoin(glb));

        Node3D? model = null;
        // Texture search dirs, in priority order: overrides, textures, library root.
        if (path is not null
            && ModelTexturing.LoadGlb(path, out int t, libraryDir.PathJoin("overrides"), libraryDir.PathJoin("textures"), libraryDir) is Node3D m)
        {
            _zoneTexturedModels += t > 0 ? 1 : 0;
            _zoneTexturedSurfaces += t;
            model = m;
        }
        cache[glb] = model;
        return model;
    }

    // First of the given paths that exists, or null.
    private static string? FirstExisting(params string[] paths)
    {
        foreach (string p in paths)
            if (Godot.FileAccess.FileExists(p))
                return p;
        return null;
    }
}
