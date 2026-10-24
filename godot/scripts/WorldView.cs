// SPDX-License-Identifier: GPL-3.0-or-later
//
// Gate 3 view: a capsule where the server put the character, on a plain ground, plus a status
// overlay. CharacterScreen keeps the session alive (ClientSession.KeepAliveAsync); this node
// drains incoming packets once per frame and notices when the connection closes.

using DaocProtocol.Net;
using DaocProtocol.Packets;
using DaocProtocol.Session;
using Godot;


namespace EodClient;

public partial class WorldView : Node3D
{
    /// <summary>Region units per Godot unit.</summary>
    private const float RegionUnitsPerUnit = 100f;

    /// <summary>Target height (Godot units) to scale a loaded placeholder model to, so an item mesh
    /// in NIF units reads at roughly human scale next to the ground.</summary>
    private const float ModelTargetHeight = 2f;

    private DaocConnection _connection = null!;
    private WorldPosition _world = null!;
    private string? _modelPath;
    private string? _zonePath;
    private string? _libraryPath;
    private string? _terrainPath;
    private int _zoneTexturedModels;
    private int _zoneTexturedSurfaces;
    private Label _status = null!;
    private Button _button = null!;
    private Camera3D _camera = null!;
    private float _camYaw;
    private float _camPitch;
    private bool _lookActive;
    // Free-fly debug camera speed in Godot units/second (hold Shift to move faster).
    private int _packets;
    private int _pings;
    private double _seconds;
    private string? _closedReason;
    private bool _draining = true;
    private string _note = "";

    /// <summary>Raised when the player presses Quit (connected).</summary>
    public event Action? QuitPressed;

    /// <summary>Raised when the player presses Back to login (after the connection closed).</summary>
    public event Action? BackToLoginPressed;

    public int Packets => _packets;
    public int Pings => _pings;
    public string? ClosedReason => _closedReason;

    /// <summary>
    /// Stops draining incoming packets (ClientSession.QuitAsync must see the 0xA4) and shows a
    /// note; the keep-alive keeps running until this node leaves the tree.
    /// </summary>
    public void BeginQuit()
    {
        _draining = false;
        _note = "   quitting: the server's quit timer takes about 20 s…";
    }

    /// <summary>Call before adding to the tree. <paramref name="modelPath"/> is an optional .glb to
    /// stand in for the character; null or missing falls back to the capsule (MVC needs no assets).
    /// <paramref name="zoneManifest"/> is an optional zone.csv (from `mpk zone`) whose static objects
    /// are placed around the character; null or missing just shows the flat ground.</summary>
    public void Init(DaocConnection connection, WorldPosition world,
        string? modelPath = null, string? zoneManifest = null, string? libraryPath = null, string? terrainPath = null)
    {
        _connection = connection;
        _world = world;
        _modelPath = modelPath;
        _zonePath = zoneManifest;
        _libraryPath = libraryPath;
        _terrainPath = terrainPath;
    }

    /// <summary>
    /// DAoC: X east, Y south, Z up, in region units. Godot: Y up. So Godot = (X, Z, Y) / RegionUnitsPerUnit.
    /// </summary>
    public static Vector3 ToGodot(float x, float y, float z) => new(x / RegionUnitsPerUnit, z / RegionUnitsPerUnit, y / RegionUnitsPerUnit);

    /// <summary>Heading 0–4095 = a full turn. The direction of heading 0 isn't checked yet.</summary>
    public static float HeadingRadians(ushort heading) => (heading & 0x0FFF) / 4096f * Mathf.Tau;

    public override void _Ready()
    {
        PositionAndObjectId p = _world.Position;
        Vector3 at = ToGodot(p.X, p.Y, p.Z);

        AddChild(new WorldEnvironment
        {
            Environment = new Godot.Environment
            {
                BackgroundMode = Godot.Environment.BGMode.Color,
                BackgroundColor = new Color(0.55f, 0.7f, 0.85f),
                AmbientLightSource = Godot.Environment.AmbientSource.Color,
                AmbientLightColor = new Color(0.6f, 0.6f, 0.6f),
            },
        });
        AddChild(new DirectionalLight3D { RotationDegrees = new Vector3(-50, 30, 0) });

        // Ground: the zone's real terrain mesh if given (its vertices are absolute region coords, so
        // it loads at the origin with the usual region→Godot scale), otherwise a flat placeholder.
        if (!TryAddTerrain())
            AddChild(new MeshInstance3D
            {
                Mesh = new PlaneMesh { Size = new Vector2(1000, 1000) },
                Position = new Vector3(at.X, at.Y, at.Z),
                MaterialOverride = new StandardMaterial3D { AlbedoColor = new Color(0.35f, 0.5f, 0.3f) },
            });

        // Character: at its world position, facing its heading, with a nose showing facing. The body
        // is a loaded .glb when one was supplied (first meeting of the asset pipeline and the live
        // world), otherwise the MVC capsule — the client still runs with no assets at all.
        var body = new Node3D { Position = at, Rotation = new Vector3(0, -HeadingRadians(p.Heading), 0) };
        if (!TryAddModel(body))
            body.AddChild(new MeshInstance3D
            {
                Mesh = new CapsuleMesh { Radius = 0.4f, Height = 2f },
                Position = new Vector3(0, 1, 0),
                MaterialOverride = new StandardMaterial3D { AlbedoColor = new Color(0.8f, 0.2f, 0.2f) },
            });
        body.AddChild(new MeshInstance3D
        {
            Mesh = new BoxMesh { Size = new Vector3(0.2f, 0.2f, 0.6f) },
            Position = new Vector3(0, 1.6f, 0.5f),
        });
        AddChild(body);

        int zoneObjects = BuildZone();
        if (zoneObjects > 0)
            GD.Print($"[world] placed {zoneObjects} zone object(s)");

        // Free-fly debug camera (WASD + Q/E, right-drag to look, Shift to sprint): the spawn is often
        // inside a large structure (Eodarms starts at Prydwen Keep), so no fixed chase offset reliably
        // sees the scene. Start high and back, looking down at the player, then fly from there.
        _camera = new Camera3D { Position = at + new Vector3(0, 45, 65), Far = 8000f };
        AddChild(_camera);
        _camera.LookAt(at + new Vector3(0, 1, 0));
        Vector3 e = _camera.Rotation;          // seed yaw/pitch from the initial LookAt
        _camPitch = e.X;
        _camYaw = e.Y;

        var overlay = new CanvasLayer();
        _status = new Label { Position = new Vector2(16, 16) };
        overlay.AddChild(_status);
        _button = new Button { Text = "Quit to character screen", Position = new Vector2(16, 72) };
        _button.Pressed += () =>
        {
            _button.Disabled = true;
            if (_closedReason is null)
                QuitPressed?.Invoke();
            else
                BackToLoginPressed?.Invoke();
        };
        overlay.AddChild(_button);
        AddChild(overlay);
    }

    // Loads the zone terrain .glb if given. Its vertices are absolute region coordinates (already
    // Y-up from the exporter), so it goes at the origin scaled by 1/RegionUnitsPerUnit to match the
    // rest of the scene (which places everything via ToGodot, i.e. region units / 100). Returns false
    // (use the flat placeholder) when no terrain was given or it can't load.
    private bool TryAddTerrain()
    {
        if (string.IsNullOrEmpty(_terrainPath) || !Godot.FileAccess.FileExists(_terrainPath))
            return false;

        var doc = new GltfDocument();
        var state = new GltfState();
        if (doc.AppendFromFile(_terrainPath, state) != Error.Ok || doc.GenerateScene(state) is not Node3D terrain)
        {
            GD.PrintErr($"[world] terrain failed to load: {_terrainPath}");
            return false;
        }
        terrain.Scale = new Vector3(1f / RegionUnitsPerUnit, 1f / RegionUnitsPerUnit, 1f / RegionUnitsPerUnit);

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
                // Render both sides regardless of winding; keep green only where no tile was applied.
                if (mi.GetSurfaceOverrideMaterial(s) is BaseMaterial3D tile)
                    tile.CullMode = BaseMaterial3D.CullModeEnum.Disabled;
                else
                    mi.SetSurfaceOverrideMaterial(s, green);
            }
        }
        AddChild(terrain);
        GD.Print($"[world] terrain loaded ({textured} ground tile(s) textured)");
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
        string[] lines = Godot.FileAccess.GetFileAsString(_zonePath).Split('\n');
        var loaded = new System.Collections.Generic.Dictionary<string, Node3D?>();
        var zoneRoot = new Node3D { Name = "Zone" };
        int placed = 0;

        foreach (string raw in lines)
        {
            string line = raw.Trim();
            if (line.Length == 0 || line.StartsWith("glb,")) // skip header/blank
                continue;
            string[] c = line.Split(',');
            if (c.Length < 6
                || !float.TryParse(c[1], out float x) || !float.TryParse(c[2], out float y)
                || !float.TryParse(c[3], out float z) || !float.TryParse(c[4], out float angleDeg)
                || !float.TryParse(c[5], out float scale))
                continue;

            Node3D? template = LoadZoneModel(c[0], libraryDir, loaded);
            if (template is null)
                continue;

            var inst = (Node3D)template.Duplicate();
            inst.Position = ToGodot(x, y, z);
            // DAoC angle is degrees about the vertical axis; negate for Godot's handedness (as heading).
            inst.Rotation = new Vector3(0, -Mathf.DegToRad(angleDeg), 0);
            // The model's vertices are in region/NIF units (exporter leaves scale at 1), the same units
            // the placement coords and terrain use, so bring them into Godot space by the same factor
            // as everything else (ToGodot and the terrain both divide by RegionUnitsPerUnit). Without
            // this the buildings render 100x oversized — the keep alone fills the whole zone.
            float s = scale / RegionUnitsPerUnit;
            inst.Scale = new Vector3(s, s, s);
            zoneRoot.AddChild(inst);
            placed++;
        }

        if (placed > 0)
            AddChild(zoneRoot);
        int distinct = 0;
        foreach (Node3D? m in loaded.Values) if (m is not null) distinct++;
        GD.Print($"[world] zone: {placed} object(s), {distinct} distinct model(s), " +
            $"{_zoneTexturedModels} textured ({_zoneTexturedSurfaces} surface(s))");
        return placed;
    }

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
        if (path is not null)
        {
            var doc = new GltfDocument();
            var state = new GltfState();
            if (doc.AppendFromFile(path, state) == Error.Ok && doc.GenerateScene(state) is Node3D m)
            {
                // Texture search dirs, in priority order: overrides, textures, library root.
                int t = ModelTexturing.Apply(m, libraryDir.PathJoin("overrides"), libraryDir.PathJoin("textures"), libraryDir);
                _zoneTexturedModels += t > 0 ? 1 : 0;
                _zoneTexturedSurfaces += t;
                // Render every surface double-sided. The exporter now winds faces outward (walls are
                // solid), but DAoC authors thin architectural pieces — battlement caps, parapet edges,
                // banners — as single-sided surfaces meant to be seen from one side; back-face culling
                // makes them vanish (and reappear) as the camera moves around. Disabling culling costs
                // a little overdraw and removes the artifact without hiding any real winding error.
                MakeDoubleSided(m);
                model = m;
            }
            else
            {
                GD.PrintErr($"[world] zone model failed to load: {glb}");
            }
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

    // Loads the .glb at _modelPath as the character body: scaled to a human-ish height and seated on
    // the ground (feet at the body origin). Returns false (keep the capsule) if no usable model.
    private bool TryAddModel(Node3D body)
    {
        if (string.IsNullOrEmpty(_modelPath) || !Godot.FileAccess.FileExists(_modelPath))
            return false;

        var doc = new GltfDocument();
        var state = new GltfState();
        if (doc.AppendFromFile(_modelPath, state) != Error.Ok)
        {
            GD.PrintErr($"[world] could not load model {_modelPath}; using the capsule.");
            return false;
        }
        Node scene = doc.GenerateScene(state);
        if (scene is not Node3D model)
            return false;

        // Texture from the extracted .dds beside the model (where tools/pipeline.sh puts them).
        string textureDir = System.IO.Path.GetDirectoryName(_modelPath) ?? "";
        int textured = ModelTexturing.Apply(model, textureDir);

        Aabb bounds = ModelBounds(model);
        float height = Mathf.Max(bounds.Size.Y, 0.001f);
        float scale = ModelTargetHeight / height;
        model.Scale = new Vector3(scale, scale, scale);
        // Recentre on X/Z and lift so the model's lowest point sits at y = 0 after scaling.
        Vector3 centre = bounds.Position + bounds.Size / 2f;
        model.Position = new Vector3(-centre.X * scale, -bounds.Position.Y * scale, -centre.Z * scale);
        body.AddChild(model);
        GD.Print($"[world] character model {System.IO.Path.GetFileName(_modelPath)} " +
            $"(NIF bounds {bounds.Size.Y:0} u, scaled x{scale:0.###}, textured {textured})");
        return true;
    }

    private static Aabb ModelBounds(Node node)
    {
        Aabb total = default;
        bool any = false;
        foreach (MeshInstance3D mi in FindMeshes(node))
        {
            Aabb a = mi.Transform * mi.GetAabb();
            total = any ? total.Merge(a) : a;
            any = true;
        }
        return any ? total : new Aabb(Vector3.Zero, Vector3.One);
    }

    private static System.Collections.Generic.IEnumerable<MeshInstance3D> FindMeshes(Node node)
    {
        if (node is MeshInstance3D mi)
            yield return mi;
        foreach (Node child in node.GetChildren())
            foreach (MeshInstance3D m in FindMeshes(child))
                yield return m;
    }

    // Disables back-face culling on every surface of a model so single-sided art (thin caps, banners)
    // doesn't vanish from the back. Works on whichever material the surface already carries.
    private static void MakeDoubleSided(Node root)
    {
        foreach (MeshInstance3D mi in FindMeshes(root))
        {
            Mesh? mesh = mi.Mesh;
            if (mesh is null)
                continue;
            for (int s = 0; s < mesh.GetSurfaceCount(); s++)
                if (mi.GetActiveMaterial(s) is BaseMaterial3D mat)
                    mat.CullMode = BaseMaterial3D.CullModeEnum.Disabled;
        }
    }

    // Right mouse button toggles mouse-look; while held, relative motion turns the camera.
    public override void _Input(InputEvent @event)
    {
        if (@event is InputEventMouseButton mb && mb.ButtonIndex == MouseButton.Right)
        {
            _lookActive = mb.Pressed;
            Input.MouseMode = mb.Pressed ? Input.MouseModeEnum.Captured : Input.MouseModeEnum.Visible;
        }
        else if (@event is InputEventMouseMotion mm && _lookActive)
        {
            const float sens = 0.005f;
            _camYaw -= mm.Relative.X * sens;
            _camPitch = Mathf.Clamp(_camPitch - mm.Relative.Y * sens, -1.5f, 1.5f);
        }
    }

    // Free-fly movement: WASD plane, Q/E down/up, Shift sprint. Camera-relative.
    private void UpdateDebugCamera(double delta)
    {
        _camera.Rotation = new Vector3(_camPitch, _camYaw, 0);

        var dir = Vector3.Zero;
        if (Input.IsKeyPressed(Key.W)) dir -= _camera.GlobalTransform.Basis.Z;
        if (Input.IsKeyPressed(Key.S)) dir += _camera.GlobalTransform.Basis.Z;
        if (Input.IsKeyPressed(Key.A)) dir -= _camera.GlobalTransform.Basis.X;
        if (Input.IsKeyPressed(Key.D)) dir += _camera.GlobalTransform.Basis.X;
        if (Input.IsKeyPressed(Key.E)) dir += Vector3.Up;
        if (Input.IsKeyPressed(Key.Q)) dir += Vector3.Down;
        if (dir != Vector3.Zero)
        {
            float speed = Input.IsKeyPressed(Key.Shift) ? 120f : 30f;
            _camera.Position += dir.Normalized() * speed * (float)delta;
        }
    }

    public override void _Process(double delta)
    {
        UpdateDebugCamera(delta);
        _seconds += delta;
        while (_draining && _connection.TryReceive(out ServerPacket packet))
        {
            _packets++;
            if (packet.Code == PingRequest.ReplyCode)
                _pings++;
        }

        if (_closedReason is null && _connection.Completion.IsCompleted)
        {
            _closedReason = _connection.Completion.Exception?.InnerException?.Message ?? "connection closed";
            _note = "";
            _button.Text = "Back to login";
            _button.Disabled = false;
        }

        PositionAndObjectId p = _world.Position;
        _status.Text =
            $"{_world.Zone.Name} (zone {_world.Zone.Id}, region {p.RegionId})   x {p.X:0} y {p.Y:0} z {p.Z:0}   heading {p.Heading & 0x0FFF}\n" +
            $"{_seconds:0} s in world   {_packets} packets   {_pings} ping replies   " +
            (_closedReason is null ? "connected" : $"DISCONNECTED: {_closedReason}") + _note + "\n" +
            "camera: WASD move, Q/E down/up, right-drag look, Shift sprint";
    }
}
