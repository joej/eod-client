// SPDX-License-Identifier: GPL-3.0-or-later
//
// WorldView: One zone's static world — terrain, placed objects, water — plus the sky and view distance.
// A zone is built into its own detached root (ZoneParts) without touching the view, in steps, so a
// streamed zone is built a few milliseconds per frame and added when done (WorldView.Streaming).

using DaocProtocol.Net;
using DaocProtocol.Packets;
using DaocProtocol.Session;
using DaocProtocol.World;
using Godot;

namespace EodClient;

public partial class WorldView
{
    /// <summary>One zone's static world, under one root named zoneNNN.</summary>
    private sealed class ZoneParts
    {
        public required ushort Id;
        public required string Dir;           // the folder with zone.csv, sky.csv, water.* ("" for none)
        public required Node3D Root;
        public Node3D? Terrain, Objects, Water, JumpMarkers;
        public System.Collections.Generic.List<DaocAssets.Zone.ZoneJump> Jumps = [];
        public int Placed, Distinct, TexturedModels, TexturedSurfaces, WaterBodies;
        public double Seconds;                // how long the build took
    }

    // Builds a zone's terrain, objects and water at once into a new root that is not in the tree.
    private static ZoneParts BuildZoneParts(ushort id, string? zoneCsv, string? terrainGlb, string? libraryPath, ModelCache models)
    {
        var watch = System.Diagnostics.Stopwatch.StartNew();
        ZoneParts parts = NewZoneParts(id, zoneCsv, terrainGlb);
        foreach (bool _ in ZoneSteps(parts, zoneCsv, terrainGlb, libraryPath, models)) { }
        parts.Seconds = watch.Elapsed.TotalSeconds;
        return parts;
    }

    private static ZoneParts NewZoneParts(ushort id, string? zoneCsv, string? terrainGlb) => new()
    {
        Id = id, Dir = System.IO.Path.GetDirectoryName(zoneCsv ?? terrainGlb ?? "") ?? "", Root = new Node3D { Name = $"zone{id:D3}" },
    };

    // The build in small steps (each yield is a point where the caller may wait for the next frame). Built
    // on the main thread: from a worker thread most mesh and collider calls wait for the render thread,
    // which made a zone take 20-40 times as long (measured 2026-11-07: 18-40 s against 1-2 s). Touches no
    // view state. Any file that is absent just leaves its part out.
    private static System.Collections.Generic.IEnumerable<bool> ZoneSteps(ZoneParts parts, string? zoneCsv, string? terrainGlb, string? libraryPath, ModelCache models)
    {
        string lib = string.IsNullOrEmpty(libraryPath) ? parts.Dir : libraryPath;
        foreach (bool step in TerrainSteps(parts, terrainGlb, lib)) yield return step;
        foreach (bool step in ObjectSteps(parts, zoneCsv, lib, models)) yield return step;
        foreach (bool step in WaterSteps(parts, lib)) yield return step;
        foreach (bool step in JumpSteps(parts)) yield return step;
    }

    // The zone terrain .glb. The exporter already baked region units → metres (× 0.025) into its vertices
    // in absolute region-global position, so it drops in at the origin with NO extra scale — matching
    // models, which are also metres. ToGodot converts the raw live/placement coords by the same 0.025, so
    // the terrain and everything placed on it share one frame.
    private static System.Collections.Generic.IEnumerable<bool> TerrainSteps(ZoneParts parts, string? terrainGlb, string libraryDir)
    {
        if (string.IsNullOrEmpty(terrainGlb) || !Godot.FileAccess.FileExists(terrainGlb))
            yield break;

        // Untextured here: each ground tile is textured below.
        if (ModelTexturing.LoadGlb(terrainGlb) is not Node3D terrain)
            yield break;
        yield return true;
        // No scale: the .glb is already in metres (GltfExporter baked × 0.025). Scaling again here was
        // the double-scale that shrank the terrain to 1/40th and divorced it from the player position.
        terrain.Scale = Vector3.One;

        // Texture each ground tile (texRR-CC) from the library, like any model. A tile that doesn't
        // resolve keeps a plain green fallback so the ground is never a hole.
        // One tile per step: all 64 at once was the longest step of a zone build (61 ms).
        int textured = 0;
        foreach (MeshInstance3D tileMesh in FindMeshes(terrain).ToList())
        {
            textured += ModelTexturing.Apply(tileMesh,
                libraryDir.PathJoin("overrides"), libraryDir.PathJoin("textures"), libraryDir);
            yield return true;
        }

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
        // heightmap and the player walks on it (M4). BackfaceCollision: the ray hits either winding (terrain
        // converted before 2026-11-03 faced down). Every terrain body is on its OWN collision layer so the
        // ground ray can target terrain alone — otherwise a ray from high above hits a tall placed object
        // (tree/keep) first and snaps the player onto its roof ("spawned very high in the air").
        terrain.Name = "Terrain";
        parts.Root.AddChild(terrain);
        parts.Terrain = terrain;
        int built = 0;
        foreach (MeshInstance3D mi in FindMeshes(terrain))
        {
            if (mi.Mesh is null)
                continue;
            ConcavePolygonShape3D shape = mi.Mesh.CreateTrimeshShape();
            shape.BackfaceCollision = true;
            // Not in the tree yet, so no GlobalTransform: the mesh's transform relative to the zone root.
            var sb = new StaticBody3D { CollisionLayer = TerrainLayer, Transform = RelativeTransform(mi, parts.Root) };
            sb.SetMeta("terrain_mesh", mi);   // so an inspect pick can name the tile it hit
            sb.AddChild(new CollisionShape3D { Shape = shape });
            parts.Root.AddChild(sb);
            built++;
            yield return true;
        }
        GD.Print($"[world] zone {parts.Id}: terrain ({textured} ground tile(s) textured, {built} collider(s))");
    }

    // A node's transform relative to an ancestor, from the local transforms (works outside the tree).
    private static Transform3D RelativeTransform(Node3D node, Node ancestor)
    {
        Transform3D t = node.Transform;
        for (Node? p = node.GetParent(); p is not null && p != ancestor; p = p.GetParent())
            if (p is Node3D p3)
                t = p3.Transform * t;
        return t;
    }

    /// <summary>Zone models by .glb name, loaded and textured once for all the zones a view shows: a
    /// neighbouring zone reuses the trees and buildings already loaded (null: the model didn't load). The
    /// templates are never in the tree; the view frees them when it leaves (copies keep the meshes).</summary>
    private sealed class ModelCache : System.Collections.Generic.Dictionary<string, Node3D?>
    {
        public void FreeAll()
        {
            foreach (Node3D? m in Values)
                m?.Free();
            Clear();
        }
    }

    // The zone's static objects from a zone.csv manifest (from `mpk zone`): each row is
    // "glb,x,y,z,angle,scale" in region-global coordinates. Each distinct .glb is loaded and textured
    // once (for every zone of the view: <paramref name="loaded"/>), then instanced per placement.
    private static System.Collections.Generic.IEnumerable<bool> ObjectSteps(ZoneParts parts, string? zoneCsv, string libraryDir, ModelCache loaded)
    {
        if (string.IsNullOrEmpty(zoneCsv) || !Godot.FileAccess.FileExists(zoneCsv))
            yield break;

        var placements = DaocAssets.Zone.ZoneCheck.ReadManifest(Godot.FileAccess.GetFileAsString(zoneCsv));
        var zoneRoot = new Node3D { Name = "Objects" };
        var used = new System.Collections.Generic.HashSet<string>();

        foreach ((string glb, float x, float y, float z, float angleDeg, float scale) in placements)
        {
            bool fresh = !loaded.ContainsKey(glb);
            Node3D? template = LoadZoneModel(parts, glb, libraryDir, loaded);
            if (fresh)
                yield return true;   // a model load is the biggest single step
            if (template is null)
                continue;
            used.Add(glb);

            var inst = (Node3D)template.Duplicate();
            inst.Position = ToGodot(x, y, z);
            // DAoC angle is degrees about the vertical axis; negate for Godot's handedness (as heading).
            inst.Rotation = new Vector3(0, -Mathf.DegToRad(angleDeg), 0);
            // The model .glb is already in metres; the manifest's scale is the per-fixture art ratio.
            inst.Scale = new Vector3(scale, scale, scale);
            // What the inspect readout reports for this placement (raw zone.csv values).
            inst.SetMeta("glb", glb);
            inst.SetMeta("placement", $"x {x:0} y {y:0} z {z:0}  angle {angleDeg:0}°  scale {scale:0.##}");
            zoneRoot.AddChild(inst);
            // Trimesh collision per placed object: the chase camera pulls in off buildings and the
            // character stands on floors. ~1,500 static bodies for a full zone.
            foreach (MeshInstance3D mi in FindMeshes(inst))
                mi.CreateTrimeshCollision();
            parts.Placed++;
            yield return true;
        }

        if (parts.Placed > 0)
        {
            parts.Root.AddChild(zoneRoot);
            parts.Objects = zoneRoot;
        }
        else
            zoneRoot.Free();
        parts.Distinct = used.Count;
        GD.Print($"[world] zone {parts.Id}: {parts.Placed} object(s), {parts.Distinct} distinct model(s), " +
            $"{parts.TexturedModels} textured ({parts.TexturedSurfaces} surface(s))");
    }

    // The zone's rivers, lakes and swamps: water.glb (one waterNN mesh per body, already in region-global
    // metres like the terrain) and water.csv (texture, flow, type per body), both written by `mpk water`
    // beside zone.csv. Each surface gets the water shader with its texture and flow.
    private static System.Collections.Generic.IEnumerable<bool> WaterSteps(ZoneParts parts, string libraryDir)
    {
        if (parts.Dir.Length == 0)
            yield break;
        string glb = parts.Dir.PathJoin("water.glb"), csv = parts.Dir.PathJoin("water.csv");
        if (!Godot.FileAccess.FileExists(glb) || !Godot.FileAccess.FileExists(csv))
            yield break;

        var rows = DaocAssets.Zone.ZoneWater.ReadCsv(Godot.FileAccess.GetFileAsString(csv));
        if (ModelTexturing.LoadGlb(glb) is not Node3D water)   // the water shader textures it below
            yield break;
        yield return true;
        var shader = GD.Load<Shader>("res://shaders/water.gdshader");
        foreach (MeshInstance3D mi in FindMeshes(water))
        {
            // The node is named after the body (waterNN); the mesh instance may be it or its child.
            string name = rows.ContainsKey(mi.Name) ? mi.Name : mi.GetParent()?.Name ?? "";
            if (!rows.TryGetValue(name, out DaocAssets.Zone.WaterRow? row))
                continue;
            var mat = new ShaderMaterial { Shader = shader };
            if (ModelTexturing.Load(row.Texture, libraryDir.PathJoin("overrides"), libraryDir.PathJoin("textures"), libraryDir) is { } tex)
                mat.SetShaderParameter("albedo_tex", tex);
            // DAoC's flow is about -50..50; as texture repeats per second, /1000 reads as a slow drift.
            mat.SetShaderParameter("flow", row.Flow / 1000f);
            mi.MaterialOverride = mat;
            parts.WaterBodies++;
            // Its surface, for the swim check (WaterLayer only: nothing stands on it).
            if (mi.Mesh is not null)
            {
                var body = new StaticBody3D { CollisionLayer = WaterLayer, CollisionMask = 0, Transform = RelativeTransform(mi, water) };
                body.AddChild(new CollisionShape3D { Shape = mi.Mesh.CreateTrimeshShape() });
                water.AddChild(body);
            }
        }
        water.Name = "Water";
        parts.Root.AddChild(water);
        parts.Water = water;
        GD.Print($"[world] zone {parts.Id}: water {parts.WaterBodies} bod{(parts.WaterBodies == 1 ? "y" : "ies")}");
    }

    // Applies a zone's sky from sky.csv (written by `mpk sky` beside zone.csv) for one weather condition
    // and time of day: the sky shader's canopy and cloud gradients, cloud layers and sun disk; ambient and
    // sun light from the file; and depth fog in the file's distance-fog colour. Fog distances come from
    // SECTOR.DAT's [fog]: clip is the client's far plane (view distance) and amount the percentage of
    // that distance, measured back from it, that is fogged (Camelot Hills: 15% of 16000 units → fog
    // from 340 to 400 m); amount 0 turns fog off. Light energies are scaled by eye. Returns the sky's name,
    // or null (the current sky stays) when the zone has no sky.csv.
    private string? ApplySky(string dir, string condition, string time)
    {
        if (string.IsNullOrEmpty(dir))
            return null;
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
    // zone does. Zones without sky.csv keep a long fixed far plane and no fog.
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

    // Loads a zone .glb once (cached). Resolves the library layout: an overrides/<glb>
    // wins, else models/<glb>; a flat <lib>/<glb> is also accepted (manifest-folder fallback). Textures are
    // applied from the same layers (overrides win over textures/). Null if it can't load.
    private static Node3D? LoadZoneModel(ZoneParts parts, string glb, string libraryDir, System.Collections.Generic.Dictionary<string, Node3D?> cache)
    {
        if (cache.TryGetValue(glb, out Node3D? cached))
            return cached;

        string? path = FirstExisting(
            libraryDir.PathJoin("overrides").PathJoin(glb),
            libraryDir.PathJoin("models").PathJoin(glb),
            libraryDir.PathJoin(glb));

        // A scope's own copy ("tutorial/ochpier.glb": the tutorial's version of a same-name model) takes its
        // textures from that scope's folder first (textures/tutorial/).
        string scope = glb.Contains('/') ? glb[..glb.LastIndexOf('/')] : "";
        string[] textureDirs = scope.Length > 0
            ? [libraryDir.PathJoin("overrides"), libraryDir.PathJoin("textures").PathJoin(scope), libraryDir.PathJoin("textures"), libraryDir]
            : [libraryDir.PathJoin("overrides"), libraryDir.PathJoin("textures"), libraryDir];
        Node3D? model = null;
        if (path is not null
            && ModelTexturing.LoadGlb(path, out int t, textureDirs) is Node3D m)
        {
            parts.TexturedModels += t > 0 ? 1 : 0;
            parts.TexturedSurfaces += t;
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
