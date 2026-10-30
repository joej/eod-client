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
    // ----- World scale (W1, calibrated 2026-10-29) -----
    // DAoC region units and NIF mesh units turn out to share ONE scale: 0.025 m/unit. Measured by
    // re-converting Camelot Hills with the current exporter and comparing a known-size building to
    // the terrain it stands on — keep.glb came out 52 × 65 m (a real keep) and the terrain spans the
    // zone sensibly, both at 0.025. So there is a single conversion, used consistently:
    //  * GltfExporter bakes × 0.025 into every .glb it writes — models AND the terrain mesh. Those
    //    .glb files are therefore ALREADY in metres; this view must NOT rescale them for units.
    //  * Raw region coordinates that never pass through the exporter — the live 0x20/0xA9 positions
    //    and the zone.csv placement x,y,z — are converted here by RegionMetresPerUnit (= 0.025), so a
    //    placed model lands where the terrain already is.
    // Earlier this was a provisional 1/100 with the terrain re-scaled by it on top of the exporter's
    // 0.025 — a double-scale that put the terrain at 1/40th size, 5 km from the player (W1 follow-up).
    private const float RegionMetresPerUnit = 0.025f;

    private DaocConnection _connection = null!;
    private ClientSession _session = null!;
    private WorldPosition _world = null!;
    private Node3D _body = null!;        // the controlled character
    private bool _chase = true;          // chase camera (default) vs free-fly debug camera
    private float _bodyYaw;              // the body's facing, radians (Godot sense)
    // Movement sent to the server is throttled to this rate while moving (well under the 5 s
    // link-death threshold, and gentle on the TCP position channel). 0 when standing still.
    private double _sinceMoveSent;
    private bool _movedSinceSend;        // moved/turned since the last 0xA9 we sent
    private const double MoveSendInterval = 0.2; // 5 Hz
    // Ground-plane walk speed in region units/second and the matching 0xA9 "speed" value. 191 is
    // DAoC's base run; the server trusts our X/Y/Z, so this only has to look right and move at a
    // plausible rate. Metres/s = units/s × 0.025.
    private const float WalkUnitsPerSecond = 191f;
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
    public void Init(DaocConnection connection, ClientSession session, WorldPosition world,
        string? modelPath = null, string? zoneManifest = null, string? libraryPath = null, string? terrainPath = null)
    {
        _connection = connection;
        _session = session;
        _world = world;
        _modelPath = modelPath;
        _zonePath = zoneManifest;
        _libraryPath = libraryPath;
        _terrainPath = terrainPath;
    }

    /// <summary>
    /// A region-unit coordinate to Godot metres. DAoC: X east, Y south, Z up. Godot: Y up, metres.
    /// So Godot = (X, Z, Y) × RegionMetresPerUnit.
    /// </summary>
    public static Vector3 ToGodot(float x, float y, float z) =>
        new(x * RegionMetresPerUnit, z * RegionMetresPerUnit, y * RegionMetresPerUnit);

    /// <summary>
    /// The inverse of <see cref="ToGodot"/>: a Godot-metres position back to region units (X, Y, Z).
    /// Godot (gx, gy, gz) = (X, Z, Y) × 0.025, so X = gx/0.025, Y = gz/0.025, Z = gy/0.025.
    /// Movement is sending the server a new region X/Y/Z, so the player's Godot position round-trips
    /// through this every time it changes.
    /// </summary>
    public static (float X, float Y, float Z) ToRegion(Vector3 g) =>
        (g.X / RegionMetresPerUnit, g.Z / RegionMetresPerUnit, g.Y / RegionMetresPerUnit);

    /// <summary>Heading 0–4095 = a full turn. The direction of heading 0 isn't checked yet.</summary>
    public static float HeadingRadians(ushort heading) => (heading & 0x0FFF) / 4096f * Mathf.Tau;

    /// <summary>A Godot facing (yaw about Y, as the body's -rotation) back to a 0–4095 DAoC heading.</summary>
    public static ushort HeadingFromYaw(float yaw) =>
        (ushort)(Mathf.PosMod(-yaw / Mathf.Tau, 1f) * 4096f);

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
        _bodyYaw = -HeadingRadians(p.Heading);
        var body = new Node3D { Position = at, Rotation = new Vector3(0, _bodyYaw, 0) };
        _body = body;
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

        // Camera starts in chase mode behind the player (Tab switches to the free-fly debug camera,
        // which is useful because the spawn is often inside a large structure — Eodarms starts at
        // Prydwen Keep). Seed both: the chase position behind the body, and the free-fly yaw/pitch so
        // a later Tab doesn't snap.
        Vector3 chaseStart = at + _body.GlobalTransform.Basis.Z * ChaseOffset.Z + new Vector3(0, ChaseOffset.Y, 0);
        _camera = new Camera3D { Position = chaseStart, Far = 8000f };
        AddChild(_camera);
        _camera.LookAt(at + ChaseLookAt);
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

    // Loads the zone terrain .glb if given. The exporter already baked region units → metres (× 0.025)
    // into its vertices in absolute region-global position, so it drops in at the origin with NO extra
    // scale (scale 1) — matching models, which are also metres. ToGodot converts the raw live/placement
    // coords by the same 0.025, so the terrain and everything placed on it share one frame. Returns
    // false (use the flat placeholder) when no terrain was given or it can't load.
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
            // The model .glb is already in metres (GltfExporter bakes NIF units × 0.025). The only
            // remaining factor is the per-fixture art scale from the manifest (a ratio, 100% -> 1.0);
            // apply it directly. (Before GltfExporter baked in metres, this also divided by the region
            // factor to shrink raw NIF units — that is now wrong and would scale the model twice.)
            inst.Scale = new Vector3(scale, scale, scale);
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

        // The model is already in metres (GltfExporter bakes NIF units × 0.025), so trust its own
        // size instead of forcing a fixed height — that lets a character stand at its true scale next
        // to the terrain, which is what the W1 region/NIF calibration reads. Only recentre on X/Z and
        // seat the lowest point at y = 0 (feet at the body origin). Scale stays 1.
        Aabb bounds = ModelBounds(model);
        Vector3 centre = bounds.Position + bounds.Size / 2f;
        model.Position = new Vector3(-centre.X, -bounds.Position.Y, -centre.Z);
        body.AddChild(model);
        GD.Print($"[world] character model {System.IO.Path.GetFileName(_modelPath)} " +
            $"({bounds.Size.Y:0.00} m tall, textured {textured})");
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

    // Right mouse button toggles mouse-look; while held, relative motion turns the camera.
    // P saves a screenshot of the current view.
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
            if (_chase)
            {
                // Right-drag turns the character (the chase camera follows the new facing).
                _bodyYaw -= mm.Relative.X * sens;
            }
            else
            {
                _camYaw -= mm.Relative.X * sens;
                _camPitch = Mathf.Clamp(_camPitch - mm.Relative.Y * sens, -1.5f, 1.5f);
            }
        }
        else if (@event is InputEventKey { Pressed: true, Echo: false, Keycode: Key.P })
        {
            _ = SaveScreenshot();
        }
        else if (@event is InputEventKey { Pressed: true, Echo: false, Keycode: Key.Tab })
        {
            _chase = !_chase;
        }
    }

    // Saves the current frame to <progress>/shot_<timestamp>.png. The progress dir is derived from
    // the library path (its sibling "progress"), falling back to Godot's writable user:// dir. The
    // viewport image is only valid after a frame is drawn, so await the post-draw signal first.
    private async System.Threading.Tasks.Task SaveScreenshot()
    {
        await ToSignal(RenderingServer.Singleton, RenderingServer.SignalName.FramePostDraw);
        Image img = GetViewport().GetTexture().GetImage();

        string dir = ScreenshotDir();
        DirAccess.MakeDirRecursiveAbsolute(dir);
        string stamp = Time.GetDatetimeStringFromSystem().Replace(":", "-").Replace("T", "_");
        string path = dir.PathJoin($"shot_{stamp}.png");

        Error err = img.SavePng(path);
        _note = err == Error.Ok ? $"   saved {path}" : $"   screenshot failed: {err}";
        GD.Print(err == Error.Ok ? $"[world] screenshot -> {path}" : $"[world] screenshot failed: {err}");
    }

    private string ScreenshotDir()
    {
        if (!string.IsNullOrEmpty(_libraryPath))
        {
            // <...>/private/library -> <...>/private/progress
            string? parent = System.IO.Path.GetDirectoryName(_libraryPath.TrimEnd('/'));
            if (!string.IsNullOrEmpty(parent))
                return parent.PathJoin("progress");
        }
        return ProjectSettings.GlobalizePath("user://progress");
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

    // Self-driven movement: W/S walk along facing, A/D turn, mouse-look turns while held. Updates the
    // body's Godot position/yaw, then (throttled) sends the server the new region position + heading
    // through ClientSession.MoveToAsync. The server trusts the X/Y/Z we send, so this IS the movement;
    // the keep-alive goes on reporting whatever World.Position MoveToAsync last set.
    private void UpdatePlayer(double delta)
    {
        // A/D turn (keys); mouse-look also turns in chase mode, feeding _bodyYaw from _Input.
        float turnKeys = 0f;
        if (Input.IsKeyPressed(Key.A)) turnKeys += 1f;
        if (Input.IsKeyPressed(Key.D)) turnKeys -= 1f;
        const float turnRate = 2.5f; // radians/s
        _bodyYaw += turnKeys * turnRate * (float)delta;

        float forward = 0f;
        if (Input.IsKeyPressed(Key.W)) forward += 1f;
        if (Input.IsKeyPressed(Key.S)) forward -= 1f;

        _body.Rotation = new Vector3(0, _bodyYaw, 0);

        bool moving = forward != 0f;
        if (moving)
        {
            // Body faces -Z in Godot; forward is the body's -Z basis, flattened to the ground plane.
            Vector3 ahead = _body.GlobalTransform.Basis.Z * -forward;
            ahead.Y = 0;
            if (ahead.LengthSquared() > 0)
            {
                float metresPerSec = WalkUnitsPerSecond * RegionMetresPerUnit;
                _body.Position += ahead.Normalized() * metresPerSec * (float)delta;
            }
        }

        // Did the player's position or heading change this frame (keys or mouse-look)?
        ushort heading = HeadingFromYaw(_bodyYaw);
        bool changed = moving || heading != _world.Position.Heading;

        // While changing, send at MoveSendInterval; when it stops, send once more so the final
        // resting position/heading and speed 0 reach the server promptly.
        _sinceMoveSent += delta;
        if (changed && _sinceMoveSent >= MoveSendInterval)
        {
            SendMove(moving ? WalkUnitsPerSecond : 0f);
            _sinceMoveSent = 0;
        }
        else if (!changed && _movedSinceSend)
        {
            SendMove(0f);
            _sinceMoveSent = 0;
        }
        _movedSinceSend = changed;
    }

    private void SendMove(float speed)
    {
        (float rx, float ry, float rz) = ToRegion(_body.Position);
        ushort heading = HeadingFromYaw(_bodyYaw);
        // Reflect it locally at once so the overlay and keep-alive see the new position without
        // waiting for the async send; MoveToAsync sets the same values on the session.
        _world = _world with { Position = _world.Position with { X = rx, Y = ry, Z = rz, Heading = heading } };
        _ = _session.MoveToAsync(rx, ry, rz, heading, speed);
    }

    // Over-the-shoulder offset for the chase camera, in metres: close behind (+Z) and a little above
    // a ~2 m character, looking at chest height. Tight enough that the capsule fills the frame rather
    // than reading as a dot against the keep.
    private static readonly Vector3 ChaseOffset = new(0, 1.8f, 3f);
    private static readonly Vector3 ChaseLookAt = new(0, 1.2f, 0);

    // Chase camera: sits behind and above the body, looking at it. A smooth follow keeps it from
    // snapping. Tab switches to the free-fly debug camera (UpdateDebugCamera) for inspecting the zone.
    private void UpdateChaseCamera(double delta)
    {
        Vector3 target = _body.Position + ChaseLookAt;
        // Desired camera position: behind the body (its +Z) and up.
        Vector3 behind = _body.GlobalTransform.Basis.Z * ChaseOffset.Z + new Vector3(0, ChaseOffset.Y, 0);
        Vector3 want = _body.Position + behind;
        float follow = 1f - Mathf.Exp(-8f * (float)delta); // frame-rate-independent smoothing
        _camera.Position = _camera.Position.Lerp(want, follow);
        _camera.LookAt(target);
    }

    public override void _Process(double delta)
    {
        UpdatePlayer(delta);
        if (_chase)
            UpdateChaseCamera(delta);
        else
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
            $"W {(Input.IsKeyPressed(Key.W) ? 1 : 0)} S {(Input.IsKeyPressed(Key.S) ? 1 : 0)} " +
            $"A {(Input.IsKeyPressed(Key.A) ? 1 : 0)} D {(Input.IsKeyPressed(Key.D) ? 1 : 0)}   " +
            $"body {_body.Position.X:0.0},{_body.Position.Z:0.0}   mode {(_chase ? "chase" : "free")}\n" +
            (_chase
                ? "move: W/S walk, A/D or right-drag turn   ·   Tab free-fly camera, P screenshot"
                : "free-fly: WASD move, Q/E down/up, right-drag look, Shift sprint   ·   Tab chase, P screenshot");
    }
}
