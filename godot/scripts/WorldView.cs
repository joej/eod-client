// SPDX-License-Identifier: GPL-3.0-or-later
//
// Gate 3 view: a capsule where the server put the character, on a plain ground, plus the dev
// overlay (DevOverlay). CharacterScreen keeps the session alive (ClientSession.KeepAliveAsync); this node
// drains incoming packets once per frame and notices when the connection closes.

using DaocProtocol.Net;
using DaocProtocol.Packets;
using DaocProtocol.Session;
using DaocProtocol.World;
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

    private DaocConnection? _connection;
    private ClientSession? _session;   // null offline (--offline): no server, nothing sent
    private WorldPosition _world = null!;
    private Node3D _body = null!;        // the controlled character
    private Node3D? _terrain;            // terrain root with trimesh collision, for ground raycasts
    private bool _chase = true;          // chase camera (default) vs free-fly debug camera
    private float _bodyYaw;              // the body's facing, radians (Godot sense)
    // Movement sent to the server is throttled to this rate while moving (well under the 5 s
    // link-death threshold, and gentle on the TCP position channel). 0 when standing still.
    private float _posLeft;              // countdown to the next position send, seconds
    private const float MovingSendInterval = 0.1f;   // 10 Hz while moving/turning (OfflineDAoC)
    private const float IdleSendInterval = 1.0f;     // 1 Hz standing still
    private const float PositionEpsilon = 1.0f;      // region units; ~0.025 m, below a frame's step
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
    private DevOverlay _dev = null!;
    private Button _button = null!;
    // Layer roots the dev overlay toggles; the terrain is _terrain.
    private Node3D? _zoneRoot;
    private Node3D? _waterRoot;
    private Godot.Environment _environment = null!;
    private DirectionalLight3D _sun = null!;
    private string? _skyName;
    // The zone's own view distance (SECTOR.DAT [fog] clip) and fogged share, in metres / 0..1; 0 when
    // the zone has no sky.csv. The player's view-distance setting multiplies the distance.
    private float _zoneClip;
    private float _fogShare;
    private System.Action? _nextViewDistance;
    private int _waterBodies;
    private readonly Node3D _entityRoot = new() { Name = "Entities" };
    private bool _fixturesOn = true;
    private bool _terrainOn = true;
    // Click-to-inspect: the click is queued from input and resolved in the physics frame (ray queries
    // are only reliable there). The hit mesh gets a translucent accent overlay until the next pick.
    private Vector2? _pendingClick;
    private MeshInstance3D? _selected;
    // The inspect selection: a pulsing magenta wash, so it can't be taken for a surface's own colour
    // (a steady green one on a whole cobbled square was, open question W26).
    private readonly StandardMaterial3D _highlight = new()
    {
        AlbedoColor = new Color(1f, 0f, 1f, 0.3f),
        Transparency = BaseMaterial3D.TransparencyEnum.Alpha,
        ShadingMode = BaseMaterial3D.ShadingModeEnum.Unshaded,
        CullMode = BaseMaterial3D.CullModeEnum.Disabled,
    };
    private Camera3D _camera = null!;

    // ----- Chat (M6) -----
    // A scrolling log of incoming/outgoing lines and a text input. The input only grabs keys while
    // it has focus (ChatInputActive); otherwise Enter focuses it. While focused, movement and the
    // game hotkeys (P, Tab, mouse-look) are suppressed so typing never drives the character.
    private RichTextLabel _chatLog = null!;
    private LineEdit _chatInput = null!;
    private const int ChatMaxLines = 200;
    private int _chatLines;
    private bool ChatInputActive => _chatInput is not null && _chatInput.HasFocus();
    // Chase rig (adapted from OfflineDAoC's SpringArm3D camera, GPL-3.0; see
    // docs/offlinedaoc-asset-pipeline.md): a pivot at shoulder height on the body carries a
    // SpringArm3D, which pulls the camera in when it would clip terrain/walls — the thing a fixed
    // manual offset gets wrong. The camera is the arm's child, so it always frames the pivot.
    private Node3D _camPivot = null!;
    private SpringArm3D _camArm = null!;
    private float _camYaw;               // chase: orbit yaw (free-fly: camera yaw)
    private float _camPitch;
    private float _camDistance = 5.5f;   // spring length target, metres (OfflineDAoC default)
    private const float CamMin = 1.25f;  // 50 DAoC units; game.dll cameradistance clamp
    private const float CamMax = 12.5f;  // 500 DAoC units
    private bool _lookActive;
    private bool _freshCapture;           // true right after mouse capture, to drop the first delta
    private bool _groundHit;              // did the last ground raycast find terrain? (debug overlay)
    private const uint TerrainLayer = 1 << 1;  // collision layer 2: terrain only, for the ground ray
    // Free-fly debug camera speed in Godot units/second (hold Shift to move faster).
    private int _packets;
    private int _pings;
    private double _seconds;
    private string? _closedReason;
    private bool _draining = true;
    private string _note = "";

    // ----- Live entities (M5) -----
    // The dispatcher that turns drained packets into typed events, and the scene nodes it drives.
    // Keyed by the server's object id. Each entity lerps from its last position to the newest one
    // the server sent (Target*), so movement reads smooth between the ~1 Hz-per-NPC updates rather
    // than teleporting. The body (our own character) is driven by input, not by these, so it is
    // never added here even though the server also sends 0xA1 for us.
    private NetworkBridge _bridge = null!;
    private readonly System.Collections.Generic.Dictionary<ushort, Entity> _entities = new();
    private sealed class Entity
    {
        public required Node3D Node;
        public Vector3 TargetPosition;
        public float TargetYaw;
        public double Age;            // seconds since spawn, for a brief "is this real" grace period
        public AnimationPlayer? Player;   // a DAoC figure's clips (idle, walk, run), or null for a capsule
        public string Clip = "";
        public Vector3 LastPosition;
        public float Speed;           // smoothed metres per second, to pick the clip
    }

    /// <summary>Raised when the player presses Quit (connected).</summary>
    public event Action? QuitPressed;

    /// <summary>Raised when the player presses Back to login (after the connection closed).</summary>
    public event Action? BackToLoginPressed;

    /// <summary>
    /// The server moved the player into another zone of this region (0x04): this view's terrain and
    /// objects are the wrong ones, so the owner replaces it with a view of the new zone. The session
    /// already holds the new position. Draining has stopped.
    /// </summary>
    public event Action<WorldPosition>? Relocated;

    /// <summary>
    /// The server moved the player to another region (0xB7). Draining has stopped; the owner runs
    /// ClientSession.ChangeRegionAsync (which must see the packets that follow) and builds a new view.
    /// </summary>
    public event Action? RegionChanging;

    /// <summary>The player's own model id (the character list's model; monsters.csv row, e.g. 32 =
    /// Briton male): the body is drawn as that figure when one was converted. 0 = none.</summary>
    public ushort PlayerModel { get; set; }

    /// <summary>The player's face and hair (the character list's appearance bytes), applied to a
    /// Catacombs figure.</summary>
    public CharacterLook PlayerLook { get; set; }

    private AnimationPlayer? _bodyAnim;
    private string _bodyClip = "";

    /// <summary>
    /// One line on where this view's zone assets came from, for the status panel (e.g. that the
    /// server's zone isn't converted). Set before adding to the tree.
    /// </summary>
    public string AssetsNote { get; set; } = "";

    /// <summary>Reads and switches the packet log (F4); set by CharacterScreen, which owns it.</summary>
    public (Func<bool> Get, Action<bool> Set)? PacketLogging { get; set; }
    private Action? _togglePacketLog;

    public int Packets => _packets;
    public int Pings => _pings;
    public int Entities => _entities.Count;
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
    public void Init(DaocConnection? connection, ClientSession? session, WorldPosition world,
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

    /// <summary>
    /// A DAoC heading (0–4095; 0 faces south, 1024 west: <see cref="Heading"/>) to the yaw of a body that
    /// faces its local -Z. Godot X/Z are region X/Y, and yaw θ turns -Z to (-sin θ, -cos θ), so
    /// θ = atan2(-dx, -dy). (Until 2026-11-03 this was -heading, half a turn off: NPCs faced away
    /// from where the server points them, guards at the walls.)
    /// </summary>
    public static float YawFromHeading(ushort heading)
    {
        (double dx, double dy) = Heading.ToDirection(heading);
        return (float)Math.Atan2(-dx, -dy);
    }

    /// <summary>The inverse of <see cref="YawFromHeading"/>: the heading a body with this yaw faces.</summary>
    public static ushort HeadingFromYaw(float yaw) => Heading.FromDirection(-Math.Sin(yaw), -Math.Cos(yaw));

    public override void _Ready()
    {
        PositionAndObjectId p = _world.Position;
        Vector3 at = ToGodot(p.X, p.Y, p.Z);

        // Added first: its _Ready turns on wireframe generation, which must precede mesh loading.
        _dev = new DevOverlay();
        AddChild(_dev);
        AddChild(_entityRoot);

        _environment = new Godot.Environment
        {
            BackgroundMode = Godot.Environment.BGMode.Color,
            BackgroundColor = new Color(0.55f, 0.7f, 0.85f),
            AmbientLightSource = Godot.Environment.AmbientSource.Color,
            AmbientLightColor = new Color(0.6f, 0.6f, 0.6f),
        };
        AddChild(new WorldEnvironment { Environment = _environment });
        _sun = new DirectionalLight3D
        {
            RotationDegrees = new Vector3(-50, 30, 0),
            ShadowEnabled = ClientSettings.SunShadows,
            // Shadows out to 250 m in four cascades: crisp near the player, cheap beyond.
            DirectionalShadowMaxDistance = 250f,
            DirectionalShadowMode = DirectionalLight3D.ShadowMode.Parallel4Splits,
        };
        AddChild(_sun);
        _skyName = ApplySky("clear", "day");

        // Ground: the zone's real terrain mesh if given (its vertices are absolute region coords, so
        // it loads at the origin with the usual region→Godot scale), otherwise a flat placeholder.
        // A capital city has no terrain (it is all models): no placeholder plane cutting through it.
        bool modelsOnly = !string.IsNullOrEmpty(_zonePath) && Godot.FileAccess.FileExists(_zonePath)
            && (string.IsNullOrEmpty(_terrainPath) || !Godot.FileAccess.FileExists(_terrainPath));
        if (!TryAddTerrain() && !modelsOnly)
            AddChild(new MeshInstance3D
            {
                Mesh = new PlaneMesh { Size = new Vector2(1000, 1000) },
                Position = new Vector3(at.X, at.Y, at.Z),
                MaterialOverride = new StandardMaterial3D { AlbedoColor = new Color(0.35f, 0.5f, 0.3f) },
            });

        // Character: at its world position, facing its heading, with a nose showing facing. The body
        // is a loaded .glb when one was supplied (first meeting of the asset pipeline and the live
        // world), otherwise the MVC capsule — the client still runs with no assets at all.
        _bodyYaw = YawFromHeading(p.Heading);
        var body = new Node3D { Position = at, Rotation = new Vector3(0, _bodyYaw, 0) };
        _body = body;
        // The body: a --model override, else the character's own DAoC figure, else the MVC capsule
        // with a nose and a back stripe to show its facing.
        if (!TryAddModel(body) && !TryAddOwnFigure(body))
        {
            body.AddChild(new MeshInstance3D
            {
                Mesh = new CapsuleMesh { Radius = 0.4f, Height = 2f },
                Position = new Vector3(0, 1, 0),
                MaterialOverride = new StandardMaterial3D { AlbedoColor = new Color(0.8f, 0.2f, 0.2f) },
            });
            // Facing marker on the FRONT of the body. The body walks along -Z (ahead = Basis.Z * -forward),
            // so -Z is forward; the nose must sit at -Z or it marks the back and "forward" walks away from it.
            body.AddChild(new MeshInstance3D
            {
                Mesh = new BoxMesh { Size = new Vector3(0.2f, 0.2f, 0.6f) },
                Position = new Vector3(0, 1.6f, -0.5f),
                MaterialOverride = new StandardMaterial3D { AlbedoColor = new Color(1f, 0.9f, 0.2f) },
            });
            // Vertical stripe down the BACK (+Z): a fixed reference for the chase camera, which looks at
            // the character's back. Bright so it reads against terrain and the keep.
            body.AddChild(new MeshInstance3D
            {
                Mesh = new BoxMesh { Size = new Vector3(0.12f, 1.9f, 0.12f) },
                Position = new Vector3(0, 1f, 0.42f),
                MaterialOverride = new StandardMaterial3D { AlbedoColor = new Color(0.2f, 0.55f, 1f) },
            });
        }
        AddChild(body);

        int zoneObjects = BuildZone();
        _waterBodies = BuildWater();
        if (zoneObjects > 0)
            GD.Print($"[world] placed {zoneObjects} zone object(s)");

        // Chase rig: pivot at shoulder height on the body → SpringArm3D → camera. The arm pulls the
        // camera in off terrain/walls/scenery (mask below) so it never clips into the ground — the
        // thing the old fixed-offset camera got wrong. Tab detaches it to a free-fly debug camera
        // (handy: the spawn is often inside a large structure — Eodarms starts at Prydwen Keep).
        _camPivot = new Node3D { Position = PivotHeight };
        _body.AddChild(_camPivot);
        _camArm = new SpringArm3D
        {
            SpringLength = _camDistance,
            // A larger margin keeps the camera a bit further off whatever it hits, which also damps
            // the brief flicker of seeing under a slope as the arm catches and releases the terrain
            // edge frame to frame when walking over undulating ground.
            Margin = 0.5f,
            // Pull in off ALL world geometry, terrain included: walking up/down slopes otherwise put
            // the camera under a hill behind the player (seeing up through the ground), because the
            // arm couldn't see terrain to pull off it. The arm raycasts from the pivot (shoulder
            // height) up-and-back, so it won't jam against the ground directly under the player; the
            // body's own collider is excluded below, and CamMin floors the length so it can't collapse
            // into the pivot even against a wall or the keep floor at the Prydwen Keep spawn.
            CollisionMask = uint.MaxValue,
        };
        _camPivot.AddChild(_camArm);
        foreach (Node n in _body.GetChildren())
            if (n is CollisionObject3D co)
                _camArm.AddExcludedObject(co.GetRid());
        _camera = new Camera3D { Far = 8000f, Fov = ClientSettings.FieldOfView };
        ClientSettings.Changed += OnSettingsChanged;
        _camArm.AddChild(_camera);
        // The SpringArm extends the camera along the pivot's +Z, so to sit BEHIND a body that faces
        // -Z the pivot yaw is the body yaw turned 180°. Matching them put the camera in front, looking
        // at the nose (so walking read as "backwards toward the camera").
        _camYaw = _bodyYaw + Mathf.Pi;          // start behind the body
        _camPitch = -0.22f;                     // ~-12.6°, OfflineDAoC default

        // Dispatcher: route drained packets to entity handlers (M5). Subscribe before the first
        // drain in _Process so no world packet is missed.
        _bridge = new NetworkBridge();
        _bridge.ObjectMoved += OnObjectMoved;
        _bridge.NpcSpawned += OnNpcSpawned;
        _bridge.PlayerSpawned += OnPlayerSpawned;
        _bridge.ObjectRemoved += OnObjectRemoved;
        _bridge.ChatReceived += OnChatReceived;
        _bridge.PingReplied += () => _pings++;
        _bridge.Jumped += OnJumped;
        _bridge.RegionChanged += _ =>
        {
            _draining = false;   // what follows is the re-entry's, not ours
            _note = "   changing region…";
            RegionChanging?.Invoke();
        };

        var overlay = new CanvasLayer();
        // Click-only: don't let the button take keyboard focus, or Enter/Space could "press" it and
        // it would compete with the chat box for focus.
        _button = new Button { Text = "Quit to character screen", FocusMode = Control.FocusModeEnum.None, SizeFlagsHorizontal = Control.SizeFlags.ShrinkBegin };
        _button.Pressed += () =>
        {
            _button.Disabled = true;
            if (_closedReason is null)
                QuitPressed?.Invoke();
            else
                BackToLoginPressed?.Invoke();
        };
        _dev.AddToStatus(_button);
        var settings = new Button { Text = "Settings (F10)", FocusMode = Control.FocusModeEnum.None, SizeFlagsHorizontal = Control.SizeFlags.ShrinkBegin };
        settings.Pressed += () => SettingsDialog.Open(this);
        _dev.AddToStatus(settings);
        BuildChatUi(overlay);
        AddChild(overlay);
        _viewSetting = ViewOverride() ?? ClientSettings.ViewDistance;
        ApplyViewDistance(_viewSetting);
        BuildDevToggles(zoneObjects);
        ApplyDevArgs();
    }

    // Layer switches for the dev overlay. Hidden fixtures/terrain are also left out of inspect picks.
    private void BuildDevToggles(int zoneObjects)
    {
        _nextViewDistance = _dev.AddChoice("F3 view", [.. ClientSettings.ViewDistances.Select(v => v.Label)],
            _viewSetting, i => { ClientSettings.ViewDistance = i; ApplyViewDistance(i); });
        _dev.AddToggle("Sun shadows", ClientSettings.SunShadows, on =>
        {
            ClientSettings.SunShadows = on;
            _sun.ShadowEnabled = on;
        });
        _dev.AddToggle("Terrain", true, on =>
        {
            _terrainOn = on;
            if (_terrain is not null) _terrain.Visible = on;
        });
        _dev.AddToggle("Fixtures", true, on =>
        {
            _fixturesOn = on;
            if (_zoneRoot is not null) _zoneRoot.Visible = on;
        });
        _dev.AddToggle("Water", true, on => { if (_waterRoot is not null) _waterRoot.Visible = on; });
        _dev.AddToggle("Entities", true, on => _entityRoot.Visible = on);
        _dev.AddToggle("Own character", true, on => _body.Visible = on);
        _dev.AddToggle("Chat", true, on => _chatLog.Visible = _chatInput.Visible = on);
        if (PacketLogging is var (getLog, setLog))
            _togglePacketLog = _dev.AddToggle("F4 packet log", getLog(), setLog);
        _dev.AddInfo($"{zoneObjects} fixtures · {_zoneTexturedModels} textured models · {_waterBodies} water\n" +
            $"{_zoneTexturedSurfaces} textured surfaces" + (_terrain is null ? " · flat ground" : ""));
    }

    // The chat log sits in the lower-left; the input is a thin bar beneath it. Both are anchored to
    // the bottom so they follow the window as it resizes. BBCode is on so each line can be coloured
    // by its chat type without any per-glyph work.
    private void BuildChatUi(CanvasLayer overlay)
    {
        _chatLog = new RichTextLabel
        {
            BbcodeEnabled = true,
            ScrollFollowing = true,
            // Don't steal clicks/keys from the 3D view; only the input line is interactive.
            MouseFilter = Control.MouseFilterEnum.Ignore,
            FocusMode = Control.FocusModeEnum.None,
            AnchorLeft = 0f,
            AnchorRight = 0f,
            AnchorTop = 1f,
            AnchorBottom = 1f,
            OffsetLeft = 16,
            OffsetRight = 520,
            OffsetTop = -236,
            OffsetBottom = -52,
        };
        overlay.AddChild(_chatLog);

        _chatInput = new LineEdit
        {
            PlaceholderText = "Press Enter to chat (/say by default; /who, /gu … work too)",
            AnchorLeft = 0f,
            AnchorRight = 0f,
            AnchorTop = 1f,
            AnchorBottom = 1f,
            OffsetLeft = 16,
            OffsetRight = 520,
            OffsetTop = -44,
            OffsetBottom = -16,
        };
        _chatInput.TextSubmitted += OnChatSubmitted;
        overlay.AddChild(_chatInput);
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
        string[] lines = Godot.FileAccess.GetFileAsString(_zonePath).Split('\n');
        var loaded = new System.Collections.Generic.Dictionary<string, Node3D?>();
        var zoneRoot = new Node3D { Name = "Zone" };
        _zoneRoot = zoneRoot;
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
            // What the inspect readout reports for this placement (raw zone.csv values).
            inst.SetMeta("glb", c[0]);
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

        var rows = new System.Collections.Generic.Dictionary<string, string[]>();
        foreach (string line in Godot.FileAccess.GetFileAsString(csv).Split('\n').Skip(1))
        {
            string[] c = line.Trim().Split(',');
            if (c.Length >= 8)
                rows[c[0]] = c;
        }

        var doc = new GltfDocument();
        var state = new GltfState();
        if (doc.AppendFromFile(glb, state) != Error.Ok || doc.GenerateScene(state) is not Node3D water)
        {
            GD.PrintErr($"[world] water failed to load: {glb}");
            return 0;
        }
        string lib = string.IsNullOrEmpty(_libraryPath) ? dir : _libraryPath;
        var shader = GD.Load<Shader>("res://shaders/water.gdshader");
        int bodies = 0;
        foreach (MeshInstance3D mi in FindMeshes(water))
        {
            // The node is named after the body (waterNN); the mesh instance may be it or its child.
            string name = rows.ContainsKey(mi.Name) ? mi.Name : mi.GetParent()?.Name ?? "";
            if (!rows.TryGetValue(name, out string[]? row))
                continue;
            var mat = new ShaderMaterial { Shader = shader };
            if (ModelTexturing.Load(row[4], lib.PathJoin("overrides"), lib.PathJoin("textures"), lib) is { } tex)
                mat.SetShaderParameter("albedo_tex", tex);
            // DAoC's flow is about -50..50; as texture repeats per second, /1000 reads as a slow drift.
            float flow = float.TryParse(row[6], System.Globalization.NumberStyles.Float,
                System.Globalization.CultureInfo.InvariantCulture, out float f) ? f / 1000f : 0f;
            mat.SetShaderParameter("flow", flow);
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
        if (path is not null)
        {
            var doc = new GltfDocument();
            var state = new GltfState();
            if (doc.AppendFromFile(path, state) == Error.Ok && doc.GenerateScene(state) is Node3D m)
            {
                ModelTexturing.TagLayers(state);
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
    // The character's own figure: the Catacombs version of its model id (models/cata<PlayerModel>.glb,
    // how retail draws players; W13), else the classic one (models/model<PlayerModel>.glb). It stands
    // on its origin and faces +Z, so it is turned to the body's -Z.
    private bool TryAddOwnFigure(Node3D body)
    {
        if (PlayerModel == 0 || (Figure(PlayerModel, catacombs: true) ?? Figure(PlayerModel)) is not { } figure)
            return false;
        var inst = (Node3D)figure.Template.Duplicate();
        inst.Scale = new Vector3(figure.Scale, figure.Scale, figure.Scale);
        string look = ApplyLook(inst, PlayerModel, PlayerLook);
        // A figure faces +Z (seen live: the chase camera, behind the body, met the face); the body
        // walks along -Z, so turn it half round.
        inst.Rotation = new Vector3(0, Mathf.Pi, 0);
        body.AddChild(inst);
        _bodyAnim = inst.FindChildren("*", "AnimationPlayer", true, false).FirstOrDefault() as AnimationPlayer;
        PlayBodyClip("idle");
        GD.Print($"[world] character drawn as figure {figure.File}{look}");
        return true;
    }

    private void PlayBodyClip(string clip)
    {
        if (_bodyAnim is null || _bodyClip == clip || !_bodyAnim.HasAnimation(clip))
            return;
        _bodyAnim.GetAnimation(clip).LoopMode = Animation.LoopModeEnum.Linear;
        _bodyAnim.Play(clip, customBlend: 0.2);
        _bodyClip = clip;
    }

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
        // Enter focuses the chat box so you can type; Escape drops focus back to the game. While the
        // box has focus the LineEdit consumes keys itself, so none of the game hotkeys below fire.
        if (@event is InputEventKey { Pressed: true, Echo: false, Keycode: Key.Enter or Key.KpEnter }
            && !ChatInputActive)
        {
            _chatInput.GrabFocus();
            GetViewport().SetInputAsHandled();
            return;
        }
        if (ChatInputActive)
        {
            if (@event is InputEventKey { Pressed: true, Echo: false, Keycode: Key.Escape })
                _chatInput.ReleaseFocus();
            return;   // typing: don't drive the camera or trigger P/Tab while the box has focus
        }

        if (@event is InputEventMouseButton mb && mb.ButtonIndex == MouseButton.Right)
        {
            _lookActive = mb.Pressed;
            Input.MouseMode = mb.Pressed ? Input.MouseModeEnum.Captured : Input.MouseModeEnum.Visible;
            _freshCapture = mb.Pressed;   // swallow the first (often huge) motion delta after capture
        }
        else if (@event is InputEventMouseButton { Pressed: true } wheel && _chase)
        {
            // Mouse wheel zooms the chase camera (OfflineDAoC: ×0.88 per notch, clamped 1.25–12.5 m).
            if (wheel.ButtonIndex == MouseButton.WheelUp)
                _camDistance = Mathf.Clamp(_camDistance * 0.88f, CamMin, CamMax);
            else if (wheel.ButtonIndex == MouseButton.WheelDown)
                _camDistance = Mathf.Clamp(_camDistance / 0.88f, CamMin, CamMax);
        }
        else if (@event is InputEventMouseMotion mm && _lookActive)
        {
            if (_freshCapture)
            {
                _freshCapture = false;   // drop the first post-capture delta (a large recenter spike)
                return;
            }
            const float sens = 0.005f;
            // Clamp per-event delta so an occasional spike can't snap the view.
            Vector2 d = new(Mathf.Clamp(mm.Relative.X, -80, 80), Mathf.Clamp(mm.Relative.Y, -80, 80));
            mm = new InputEventMouseMotion { Relative = d };
            if (_chase)
            {
                // Right-drag steers: the character turns with the camera yaw, and pitch tilts the view.
                // Camera stays behind the -Z-facing body, so its yaw trails bodyYaw by 180°.
                _bodyYaw -= mm.Relative.X * sens;
                _camYaw = _bodyYaw + Mathf.Pi;
                _camPitch = Mathf.Clamp(_camPitch - mm.Relative.Y * sens, -0.87f, 0.87f); // ±50°
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
            SetChase(!_chase);
        }
        else if (@event is InputEventKey { Pressed: true, Echo: false, Keycode: Key.F1 })
        {
            _dev.PanelsVisible = !_dev.PanelsVisible;
        }
        else if (@event is InputEventKey { Pressed: true, Echo: false, Keycode: Key.F3 })
        {
            _nextViewDistance?.Invoke();
        }
        else if (@event is InputEventKey { Pressed: true, Echo: false, Keycode: Key.F10 })
        {
            SettingsDialog.Open(this);
        }
        else if (@event is InputEventKey { Pressed: true, Echo: false, Keycode: Key.F4 })
        {
            _togglePacketLog?.Invoke();
        }
        else if (@event is InputEventKey { Pressed: true, Echo: false, Keycode: Key.F2 })
        {
            _dev.CycleRenderMode();
        }
        else if (@event is InputEventKey { Pressed: true, Echo: false, Keycode: Key.Escape })
        {
            ClearInspect();
        }
    }

    // Settings window changes, live: the field of view, and the window mode while in the world.
    private void OnSettingsChanged(bool display)
    {
        _camera.Fov = ClientSettings.FieldOfView;
        if (display)
            ClientSettings.ApplyWindowMode();
    }

    public override void _ExitTree() => ClientSettings.Changed -= OnSettingsChanged;

    // Left-click on the world (not on a panel: panels stop their clicks) queues an inspect pick.
    public override void _UnhandledInput(InputEvent @event)
    {
        if (@event is InputEventMouseButton { Pressed: true, ButtonIndex: MouseButton.Left } mb
            && !_lookActive && !ChatInputActive)
            _pendingClick = mb.Position;
    }

    // Drops the inspect highlight and readout (Esc, or clicking the selected object again).
    private void ClearInspect()
    {
        if (_selected is not null && IsInstanceValid(_selected))
            _selected.MaterialOverlay = null;
        _selected = null;
        _dev.SetInspect("Left-click a surface to inspect it.", false);
    }

    // Casts from the camera through the clicked pixel and reports what it hit: for a fixture, its
    // .glb and zone.csv placement, the mesh node, and every surface's material (texture stem,
    // textured or not, UVs or not, colour, transparency) with the hit surface marked. That is the
    // question M7a keeps asking about magenta walls, flat-tan stone and opaque foliage.
    // What stands between a point and the sun: a ray from just above the point toward the light,
    // against everything that has collision (zone models, terrain). Names the first model it meets, or
    // says the point is in sunlight. Figures cast shadows too but have no collision, so they don't show.
    private string ShadowCaster(Vector3 point)
    {
        Vector3 towardSun = _sun.GlobalTransform.Basis.Z.Normalized();   // a DirectionalLight3D shines along -Z
        var ray = PhysicsRayQueryParameters3D.Create(point + towardSun * 0.05f, point + towardSun * 400f);
        ray.HitBackFaces = true;
        Godot.Collections.Dictionary hit = GetWorld3D().DirectSpaceState.IntersectRay(ray);
        if (hit.Count == 0 || hit["collider"].AsGodotObject() is not Node collider)
            return "shadow  none (in sunlight)";
        Node? n = collider.HasMeta("terrain_mesh") ? null : collider.GetParent();
        while (n is not null && !n.HasMeta("glb"))
            n = n.GetParent();
        float distance = point.DistanceTo(hit["position"].AsVector3());
        (float x, float y, float z) = ToRegion(hit["position"].AsVector3());
        string what = n is not null ? n.GetMeta("glb").AsString() : collider.HasMeta("terrain_mesh") ? "terrain" : collider.Name;
        string mesh = collider.GetParent() is MeshInstance3D mi ? $" {mi.Name}" : "";
        return $"shadow  from {what}{mesh}, {distance:0} m toward the sun (x {x:0} y {y:0} z {z:0})";
    }

    private void Inspect(Vector2 screen)
    {
        Vector3 from = _camera.ProjectRayOrigin(screen);
        var ray = PhysicsRayQueryParameters3D.Create(from, from + _camera.ProjectRayNormal(screen) * 4000f);
        ray.HitBackFaces = true;
        uint mask = 0;
        if (_fixturesOn) mask |= ~TerrainLayer;
        if (_terrainOn) mask |= TerrainLayer;
        ray.CollisionMask = mask;
        Godot.Collections.Dictionary hit = mask == 0
            ? new Godot.Collections.Dictionary()
            : GetWorld3D().DirectSpaceState.IntersectRay(ray);

        MeshInstance3D? previous = _selected;
        ClearInspect();

        if (hit.Count == 0 || hit["collider"].AsGodotObject() is not Node collider)
        {
            _dev.SetInspect("Nothing hit (sky, or a hidden layer).", false);
            return;
        }

        MeshInstance3D? mesh = collider.HasMeta("terrain_mesh")
            ? collider.GetMeta("terrain_mesh").AsGodotObject() as MeshInstance3D
            : collider.GetParent() as MeshInstance3D;
        (float hx, float hy, float hz) = ToRegion(hit["position"].AsVector3());
        int face = hit.ContainsKey("face_index") ? hit["face_index"].AsInt32() : -1;

        // Clicking the highlighted object again deselects it.
        if (mesh is not null && mesh == previous)
            return;

        var sb = new System.Text.StringBuilder();
        Node? placed = mesh;
        while (placed is not null && !placed.HasMeta("glb"))
            placed = placed.GetParent();
        if (placed is not null)
        {
            sb.AppendLine($"model   {placed.GetMeta("glb").AsString()}");
            sb.AppendLine($"placed  {placed.GetMeta("placement").AsString()}");
        }
        else if (collider.HasMeta("terrain_mesh"))
        {
            sb.AppendLine("model   terrain");
        }
        sb.AppendLine($"hit     x {hx:0} y {hy:0} z {hz:0}" + (face >= 0 ? $"  face {face}" : ""));
        sb.AppendLine(ShadowCaster(hit["position"].AsVector3()));

        if (mesh is null || mesh.Mesh is null)
        {
            sb.Append("(no mesh behind this collider)");
        }
        else
        {
            sb.AppendLine($"mesh    {mesh.Name}");
            int hitSurface = SurfaceForFace(mesh.Mesh, face);
            for (int s = 0; s < mesh.Mesh.GetSurfaceCount(); s++)
                sb.AppendLine((s == hitSurface ? "> " : "  ") + DescribeSurface(mesh, s));
            mesh.MaterialOverlay = _highlight;
            _selected = mesh;
        }

        string text = sb.ToString().TrimEnd();
        _dev.SetInspect(text, true);
        GD.Print("[inspect]\n" + text);
    }

    // The trimesh collider lists triangles surface by surface, in order, so a face index maps back
    // to its surface by cumulative triangle counts. -1 when unknown (no index, not an ArrayMesh).
    private static int SurfaceForFace(Mesh mesh, int face)
    {
        if (face < 0 || mesh is not ArrayMesh am)
            return -1;
        int start = 0;
        for (int s = 0; s < am.GetSurfaceCount(); s++)
        {
            int indices = am.SurfaceGetArrayIndexLen(s);
            int tris = (indices > 0 ? indices : am.SurfaceGetArrayLen(s)) / 3;
            if (face < start + tris)
                return s;
            start += tris;
        }
        return -1;
    }

    private static string DescribeSurface(MeshInstance3D mi, int s)
    {
        bool uv = mi.Mesh is ArrayMesh am && (am.SurfaceGetFormat(s) & Mesh.ArrayFormat.FormatTexUV) != 0;
        if (mi.GetActiveMaterial(s) is not BaseMaterial3D m)
            return $"s{s}  (no material)  {(uv ? "uv" : "NO UV")}";
        string name = string.IsNullOrEmpty(m.ResourceName) ? "(unnamed)" : m.ResourceName;
        string tex = m.AlbedoTexture is null ? "NO TEXTURE" : "textured";
        string alpha = m.Transparency == BaseMaterial3D.TransparencyEnum.Disabled ? "" : $"  {m.Transparency}".ToLowerInvariant();
        return $"s{s}  {name}  {tex}  {(uv ? "uv" : "NO UV")}  #{m.AlbedoColor.ToHtml(false)}{alpha}";
    }

    // Switches between the chase rig and a free-fly debug camera by reparenting the one Camera3D:
    // under the spring arm for chase (so the arm drives it), or directly under WorldView for free-fly
    // (so UpdateDebugCamera can set its global transform). Reparent keeps the camera where it is.
    private void SetChase(bool chase)
    {
        if (chase == _chase)
            return;
        _chase = chase;
        Transform3D worldXform = _camera.GlobalTransform;
        _camera.GetParent().RemoveChild(_camera);
        if (chase)
        {
            _camArm.AddChild(_camera);
            // Reset the camera's local transform fully: the arm + pivot own chase orientation, but
            // the camera node still carries the free-fly tilt/roll it was flown with. Leaving that in
            // put the chase view at a skewed, buried angle on the way back. Zero it and re-seat the
            // chase pitch behind the body so it snaps cleanly back to "over the shoulder".
            _camera.Transform = Transform3D.Identity;
            _camPitch = -0.22f;
            _camPivot.Rotation = new Vector3(_camPitch, 0f, 0f);
        }
        else
        {
            AddChild(_camera);
            _camera.GlobalTransform = worldXform; // stay put, then fly from here
            _camYaw = _camera.Rotation.Y;
            _camPitch = _camera.Rotation.X;
        }
    }

    // Saves the current frame to <progress>/shot_<timestamp>.png. The progress dir is the repo's
    // git-ignored private/progress (where the roadmap/eveofdarkness workflow expects shots), resolved
    // from the library path if given, else from the project directory; only if neither is under the
    // repo does it fall back to Godot's writable user:// dir. The viewport image is only valid after a
    // frame is drawn, so await the post-draw signal first.
    // Developer options for repeatable views of the real client (rendered comparisons):
    //   --campose X Y Z YAW PITCH  start in free-fly with the camera at region coordinates X Y Z,
    //                              looking along YAW (degrees, 0 = north, 90 = east) and PITCH (degrees)
    //   --shot SECONDS             save a screenshot (as P does) after that many seconds in the world
    //   --view LABEL               view distance for this run (classic, x1.5, x2, x4), not saved
    private double _shotAt = -1;
    private int _viewSetting;

    // --view LABEL: a view distance for this run only (the saved setting is untouched).
    private static int? ViewOverride()
    {
        string[] args = OS.GetCmdlineUserArgs();
        int v = System.Array.IndexOf(args, "--view");
        if (v < 0 || v + 1 >= args.Length)
            return null;
        int idx = System.Array.FindIndex(ClientSettings.ViewDistances, x => x.Label == args[v + 1]);
        return idx >= 0 ? idx : null;
    }
    private void ApplyDevArgs()
    {
        string[] args = OS.GetCmdlineUserArgs();
        int i = System.Array.IndexOf(args, "--campose");
        if (i >= 0 && i + 5 < args.Length && Floats(string.Join(' ', args[(i + 1)..(i + 6)])) is { Length: 5 } c)
        {
            SetChase(false);
            _camera.GlobalPosition = ToGodot(c[0], c[1], c[2]);
            // Godot yaw 0 looks along -Z (north, since DAoC y runs south); DAoC-style yaw turns clockwise.
            _camYaw = -Mathf.DegToRad(c[3]);
            _camPitch = Mathf.DegToRad(c[4]);
        }
        int set = System.Array.IndexOf(args, "--shots");
        int dir = System.Array.IndexOf(args, "--shots-out");
        if (set >= 0 && set + 1 < args.Length && dir >= 0 && dir + 1 < args.Length)
            _ = RunShotSet(ResolveArgPath(args[set + 1]), ResolveArgPath(args[dir + 1]));
        int s = System.Array.IndexOf(args, "--shot");
        if (s >= 0 && s + 1 < args.Length && double.TryParse(args[s + 1], System.Globalization.NumberStyles.Float,
                System.Globalization.CultureInfo.InvariantCulture, out double at))
            _shotAt = at;
    }

    // A fixed shot set (--shots FILE --shots-out DIR): each line of FILE is "name X Y Z YAW PITCH"
    // (region units, degrees; '#' starts a comment). The camera visits each pose in turn, the frame
    // is saved as DIR/name.png, and the client quits when the set is done. Run the same set before and
    // after an asset change and compare: the world checked the same way every time, not by walking.
    private async System.Threading.Tasks.Task RunShotSet(string file, string dir)
    {
        DirAccess.MakeDirRecursiveAbsolute(dir);
        var poses = new System.Collections.Generic.List<(string Name, float[] P)>();
        foreach (string raw in System.IO.File.ReadAllLines(file))
        {
            string line = raw.Split('#')[0].Trim();
            if (line.Length == 0) continue;
            string[] f = line.Split((char[])[' ', '\t'], System.StringSplitOptions.RemoveEmptyEntries);
            if (f.Length == 6 && Floats(string.Join(' ', f[1..])) is { Length: 5 } c)
                poses.Add((f[0], c));
            else
                GD.PrintErr($"[shots] skipped line: {raw}");
        }
        // Let the zone finish loading and settle for a moment first.
        for (int k = 0; k < 10; k++)
            await ToSignal(GetTree(), SceneTree.SignalName.ProcessFrame);
        foreach ((string name, float[] c) in poses)
        {
            SetChase(false);
            _camera.GlobalPosition = ToGodot(c[0], c[1], c[2]);
            _camYaw = -Mathf.DegToRad(c[3]);
            _camPitch = Mathf.DegToRad(c[4]);
            for (int k = 0; k < 4; k++)
                await ToSignal(GetTree(), SceneTree.SignalName.ProcessFrame);
            await ToSignal(RenderingServer.Singleton, RenderingServer.SignalName.FramePostDraw);
            Image img = GetViewport().GetTexture().GetImage();
            string path = dir.PathJoin(name + ".png");
            Error err = img.SavePng(path);
            GD.Print(err == Error.Ok ? $"[shots] {name} -> {path}" : $"[shots] {name} failed: {err}");
        }
        GD.Print($"[shots] {poses.Count} shot(s) done");
        if (_session is null)
            GetTree().Quit();
        else
            QuitPressed?.Invoke();
    }

    // Like CharacterScreen's paths: relative to the shell's working directory, not godot/.
    private static string ResolveArgPath(string path)
    {
        if (System.IO.Path.IsPathRooted(path)) return path;
        string cwd = OS.GetEnvironment("PWD");
        return System.IO.Path.GetFullPath(System.IO.Path.Combine(string.IsNullOrEmpty(cwd) ? System.IO.Directory.GetCurrentDirectory() : cwd, path));
    }

    private async System.Threading.Tasks.Task SaveScreenshot()
    {
        await ToSignal(RenderingServer.Singleton, RenderingServer.SignalName.FramePostDraw);
        Image img = GetViewport().GetTexture().GetImage();

        string dir = ScreenshotDir();
        DirAccess.MakeDirRecursiveAbsolute(dir);
        string stamp = Time.GetDatetimeStringFromSystem().Replace(":", "-").Replace("T", "_");
        string path = dir.PathJoin($"shot_{stamp}.png");

        Error err = img.SavePng(path);
        // Show only the file NAME in the overlay, never the full local path: these shots are
        // captured for the eveofdarkness.org panel, and an on-screen "/Users/…" path would have to
        // be cropped out before any could be published. The full path still goes to the console log.
        _note = err == Error.Ok ? $"   saved {path.GetFile()}" : $"   screenshot failed: {err}";
        GD.Print(err == Error.Ok ? $"[world] screenshot -> {path}" : $"[world] screenshot failed: {err}");
    }

    private string ScreenshotDir()
    {
        // 1. Sibling of the library path: <...>/private/library -> <...>/private/progress.
        if (!string.IsNullOrEmpty(_libraryPath))
        {
            string? parent = System.IO.Path.GetDirectoryName(_libraryPath.TrimEnd('/'));
            if (!string.IsNullOrEmpty(parent))
                return parent.PathJoin("progress");
        }

        // 2. Repo's private/progress, found by walking up from the Godot project dir (res://). This is
        //    the usual case for a no-assets launch (no --model): shots still land where the roadmap
        //    and eveofdarkness workflow look for them, not in Godot's hidden user:// dir.
        string projectDir = ProjectSettings.GlobalizePath("res://");
        for (var d = new System.IO.DirectoryInfo(projectDir); d is not null; d = d.Parent)
        {
            string progress = System.IO.Path.Combine(d.FullName, "private", "progress");
            if (System.IO.Directory.Exists(progress))
                return progress;
        }

        // 3. Last resort: Godot's writable user data dir.
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
        // Only chase mode drives the character from WASD. In free-fly the same keys fly the debug
        // camera (UpdateDebugCamera), and if they ALSO walked the body the character would wander —
        // into mobs, off cliffs — while you're only trying to look around. So free-fly freezes the
        // body's input; it still ground-snaps and keeps sending its (unchanged) position below, so it
        // stays grounded and link-alive where you left it. Likewise freeze while the chat box has
        // focus, so typing "wade" doesn't walk the character.
        bool driving = _chase && !ChatInputActive;

        // A/D turn (keys); mouse-look also turns in chase mode, feeding _bodyYaw from _Input.
        float turnKeys = 0f;
        if (driving && Input.IsKeyPressed(Key.A)) turnKeys += 1f;
        if (driving && Input.IsKeyPressed(Key.D)) turnKeys -= 1f;
        const float turnRate = 2.5f; // radians/s
        _bodyYaw += turnKeys * turnRate * (float)delta;

        float forward = 0f;
        if (driving && Input.IsKeyPressed(Key.W)) forward += 1f;
        if (driving && Input.IsKeyPressed(Key.S)) forward -= 1f;

        // Q/E strafe left/right: sidestep without turning. Essential with the mouse-chase camera,
        // where you steer with the mouse and strafe to line up — forward + strafe is how you move.
        float strafe = 0f;
        if (driving && Input.IsKeyPressed(Key.Q)) strafe -= 1f;
        if (driving && Input.IsKeyPressed(Key.E)) strafe += 1f;

        _body.Rotation = new Vector3(0, _bodyYaw, 0);

        bool moving = forward != 0f || strafe != 0f;
        if (moving)
        {
            // Body faces -Z in Godot; forward is its -Z basis, strafe its +X basis (right). Both are
            // flattened to the ground plane, summed, then normalised so diagonal moves aren't faster.
            Vector3 move = _body.GlobalTransform.Basis.Z * -forward + _body.GlobalTransform.Basis.X * strafe;
            move.Y = 0;
            if (move.LengthSquared() > 0)
            {
                // Shift sprints (×4) so you can cross a ~15 km zone to reach distant terrain/keep for
                // testing; base is DAoC run (191 units/s). The sent 0xA9 speed stays the base value.
                float sprint = Input.IsKeyPressed(Key.Shift) ? 4f : 1f;
                float metresPerSec = WalkUnitsPerSecond * RegionMetresPerUnit * sprint;
                _body.Position += move.Normalized() * metresPerSec * (float)delta;
            }
        }

        SnapToGround();

        // DAoC's normal pace is a run; backing up has its own clip.
        PlayBodyClip(!moving ? "idle" : forward < 0 ? "back" : "run");

        // Event-driven position send (adapted from OfflineDAoC's _send_position, GPL-3.0): ~10 Hz
        // while anything is changing, ~1 Hz standing still, and an immediate send the moment the
        // position or heading changes even if the timer hasn't elapsed — crisp under motion, quiet at
        // rest, and well under OpenDAoC's 5 s link-death threshold. (We compare against what the
        // keep-alive/World already holds, so "changed" also covers mouse-look turns from _Input.)
        _posLeft -= (float)delta;
        ushort heading = HeadingFromYaw(_bodyYaw);
        (float rx, float ry, float rz) = ToRegion(_body.Position);
        bool changed = moving
            || heading != _world.Position.Heading
            || Mathf.Abs(rx - _world.Position.X) > PositionEpsilon
            || Mathf.Abs(ry - _world.Position.Y) > PositionEpsilon;

        // Send when the timer runs out, or at once on a change (unless we just sent this tick).
        if (_posLeft <= 0f || (changed && _posLeft < MovingSendInterval - 0.02))
        {
            SendMove(moving ? WalkUnitsPerSecond : 0f);
            _posLeft = changed ? MovingSendInterval : IdleSendInterval;
        }
    }

    // Keeps the body on the terrain: cast a ray straight down the body's column against the terrain
    // trimesh collider and set Y to the hit. No gravity/stepping yet — the character is pinned to the
    // ground surface, which is all M4 needs ("walk on the ground"). No terrain (flat placeholder) or
    // no hit (over a gap) leaves Y where it is.
    private void SnapToGround()
    {
        if (_terrain is null)
        {
            SnapToFloor();
            return;
        }

        // Cast from above the terrain's whole height range straight down past its bottom, so a steep
        // descent is always found (a ray only ±100 m of the body's current Y missed the ground once
        // the body walked out past a downslope and kept its old height). Terrain Y here spans ~1–150 m.
        Vector3 col = _body.Position;
        var ray = PhysicsRayQueryParameters3D.Create(
            new Vector3(col.X, 400f, col.Z),
            new Vector3(col.X, -400f, col.Z));
        ray.CollisionMask = TerrainLayer;   // terrain only — never a tree/keep roof
        Godot.Collections.Dictionary hit = GetWorld3D().DirectSpaceState.IntersectRay(ray);
        if (hit.Count > 0 && hit["position"].AsVector3() is { } p)
        {
            _body.Position = new Vector3(col.X, p.Y, col.Z);
            _groundHit = true;
        }
        else
        {
            _groundHit = false;   // over a gap / no terrain under this column
        }
    }

    // With no terrain (a capital city, all models) the character stands on the models: a ray from a
    // little above the body down to whatever floor, street or stair is below it. Starting just above
    // the body (not high up, as for terrain) keeps it from landing on a roof or an arch overhead, and
    // only a step's drop (2 m) is followed: walking into a wall (nothing stops the player yet) no
    // longer drops it to whatever lies far below; it keeps its height instead.
    private void SnapToFloor()
    {
        if (_zoneRoot is null)
            return;
        Vector3 at = _body.Position;
        var ray = PhysicsRayQueryParameters3D.Create(at + new Vector3(0, 1.5f, 0), at - new Vector3(0, 2f, 0));
        var exclude = new Godot.Collections.Array<Rid>();
        foreach (Node n in _body.FindChildren("*", "CollisionObject3D", true, false))
            exclude.Add(((CollisionObject3D)n).GetRid());
        ray.Exclude = exclude;
        Godot.Collections.Dictionary hit = GetWorld3D().DirectSpaceState.IntersectRay(ray);
        _groundHit = hit.Count > 0;
        if (_groundHit)
            _body.Position = new Vector3(at.X, hit["position"].AsVector3().Y, at.Z);
    }

    private void SendMove(float speed)
    {
        (float rx, float ry, float rz) = ToRegion(_body.Position);
        ushort heading = HeadingFromYaw(_bodyYaw);
        // Reflect it locally at once so the overlay and keep-alive see the new position without
        // waiting for the async send; MoveToAsync sets the same values on the session.
        _world = _world with { Position = _world.Position with { X = rx, Y = ry, Z = rz, Heading = heading } };
        if (_session is not null)
            _ = _session.MoveToAsync(rx, ry, rz, heading, speed);
    }

    // The server moved us within the region (0x04). Take the position over at once — the next 0xA9
    // must carry it, or the server moves us back — then either move the body here or, in another
    // zone, hand over to a view of that zone.
    private void OnJumped(CharacterJump jump)
    {
        if (_session is null)
            return;
        WorldPosition moved;
        try
        {
            moved = _session.ApplyJump(jump);
        }
        catch (Exception e) when (e is InvalidDataException or InvalidOperationException)
        {
            GD.PrintErr($"[world] jump not applied: {e.Message}");
            return;
        }
        GD.Print($"[world] moved by the server to {moved.Zone.Name} (zone {moved.Zone.Id}) x {moved.Position.X:0} y {moved.Position.Y:0} z {moved.Position.Z:0}");
        if (moved.Zone.Id != _world.Zone.Id)
        {
            _draining = false;
            _note = $"   moving to {moved.Zone.Name}…";
            Relocated?.Invoke(moved);
            return;
        }
        _world = moved;
        PositionAndObjectId p = moved.Position;
        _body.Position = ToGodot(p.X, p.Y, p.Z);
        _bodyYaw = YawFromHeading(p.Heading);
        _body.Rotation = new Vector3(0, _bodyYaw, 0);
    }

    // Pivot height on the body (shoulder/eye), metres — OfflineDAoC uses 1.55.
    private static readonly Vector3 PivotHeight = new(0, 1.55f, 0);

    // Chase camera: orient the pivot (pitch, yaw) and lerp the spring length toward the zoom target.
    // The SpringArm3D itself does the position + collision pull-in; the camera, as its child, always
    // frames the pivot. The only smoothing is on zoom (spring length), like OfflineDAoC.
    private void UpdateChaseCamera(double delta)
    {
        // The pivot is a CHILD of the body, so its rotation is relative to the body — the body's
        // yaw is already inherited. The SpringArm extends the camera along the pivot's +Z, and the
        // body faces -Z, so a pivot yaw of 0 puts the camera straight out the body's back (behind,
        // looking at the blue stripe). Only pitch varies (look up/down); the camera is rigidly
        // behind in chase mode and right-drag turns the body, not the camera. (Adding _bodyYaw here
        // double-counted the body's rotation and skewed the camera off to the side.)
        _camPivot.Rotation = new Vector3(_camPitch, 0f, 0);
        // Lerp the arm's TARGET length toward the zoom distance; the SpringArm shortens from this
        // target on its own when it hits placed geometry. Floor the target at CamMin so even a wall
        // the arm catches can't collapse the camera into the pivot (the "inside the capsule" bug).
        float target = Mathf.Max(CamMin, _camDistance);
        _camArm.SpringLength = Mathf.Lerp(_camArm.SpringLength, target, Mathf.Min(1f, (float)delta * 10f));
    }

    // ----- Live entities (M5): spawn, move, remove -----

    /// <summary>
    /// 0xA1 carries ZONE-LOCAL coordinates and only the low 8 bits of the entity's zone-skin id, so
    /// resolve the full zone within the player's region (all in-view entities share it) and add that
    /// zone's region-coordinate offset to recover a region-global position. Falls back to the
    /// player's own zone when the skin byte matches nothing (the common case: everyone in one zone).
    /// </summary>
    private Vector3 ZoneLocalToGodot(ushort xOffset, ushort yOffset, ushort z, byte zoneSkin)
    {
        ushort region = _world.Position.RegionId;
        Zone zone = Zones.All.FirstOrDefault(zz => zz.RegionId == region && (zz.Id & 0xFF) == zoneSkin)
            ?? _world.Zone;
        return ToGodot(zone.X + xOffset, zone.Y + yOffset, z);
    }

    private void OnObjectMoved(ObjectUpdate u)
    {
        // The server sends 0xA1 for our own character too; we drive that from input, so skip it.
        if (u.ObjectId == _world.Position.ObjectId)
            return;
        Vector3 pos = ZoneLocalToGodot(u.XOffsetInZone, u.YOffsetInZone, u.Z, u.ZoneSkinId);
        float yaw = YawFromHeading(u.HeadingOnly);
        if (_entities.TryGetValue(u.ObjectId, out Entity? e))
        {
            e.TargetPosition = pos;
            e.TargetYaw = yaw;
        }
        else
        {
            // An update for something we never saw spawn (we entered mid-session, or the create
            // packet is one we don't parse yet): place a plain capsule so the world isn't empty.
            SpawnEntity(u.ObjectId, pos, yaw, new Color(0.55f, 0.55f, 0.6f));
        }
    }

    private void OnNpcSpawned(NpcCreate npc)
    {
        Vector3 pos = ToGodot(npc.X, npc.Y, npc.Z);
        SpawnEntity(npc.ObjectId, pos, YawFromHeading(npc.HeadingOnly), RealmColor(npc.Flags >> 6), npc.Model, npc.Size);
    }

    private void OnPlayerSpawned(PlayerCreate p)
    {
        Vector3 pos = ToGodot(p.X, p.Y, p.Z);
        // Drawn like your own character (its model id's Catacombs figure); without one, a capsule in a
        // brighter tint so other players read as players, not mobs.
        SpawnEntity(p.ObjectId, pos, YawFromHeading(p.HeadingOnly), new Color(0.95f, 0.85f, 0.3f), p.Model, look: p.Look);
    }

    private void OnObjectRemoved(ushort objectId)
    {
        if (_entities.Remove(objectId, out Entity? e))
            e.Node.QueueFree();
    }

    // ----- Chat (M6) -----

    // A 0xAF Message arrived. Show the real chat/system lines; drop the UI-control types
    // (SocialInterface structured strings, the centred ScreenCenter banner) that aren't text.
    private void OnChatReceived(ChatMessage msg)
    {
        _chatReceived++;
        if (msg.IsPlayerVisibleChat)
            AppendChat(msg.Text, ChatColor(msg.Type));
    }

    /// <summary>Count of 0xAF Message packets seen, for the headless summary and overlay.</summary>
    public int ChatMessages => _chatReceived;
    private int _chatReceived;

    // The player submitted the input line. Treat a leading '/' as a command and anything else as
    // /say. The server echoes it back as a 0xAF, so we don't print our own copy (avoids doubling).
    // Release focus so control returns to the game after each line (as DAoC does): press Enter again
    // to chat again. Keeping focus here trapped the player — the box looked empty but still ate all
    // input, so WASD and mouse-look went dead with no obvious way back.
    private async void OnChatSubmitted(string text)
    {
        string line = text.Trim();
        _chatInput.Clear();
        _chatInput.ReleaseFocus();
        if (line.Length == 0 || _closedReason is not null)
            return;

        if (_session is null)
        {
            AppendChat("(offline: nothing is sent)", new Color(0.8f, 0.5f, 0.5f));
            return;
        }
        try
        {
            if (line.StartsWith('/'))
                await _session.SendCommandAsync(line);
            else
                await _session.SayAsync(line);
        }
        catch (System.Exception e)
        {
            AppendChat($"(could not send: {e.Message})", new Color(0.8f, 0.5f, 0.5f));
        }
    }

    private void AppendChat(string text, Color color)
    {
        // Cap the backlog so a long session doesn't grow the label without bound.
        if (_chatLines >= ChatMaxLines)
        {
            _chatLog.Clear();
            _chatLines = 0;
        }
        _chatLog.PushColor(color);
        _chatLog.AddText(text);
        _chatLog.Pop();
        _chatLog.Newline();
        _chatLines++;
    }

    // Colour by channel/category, roughly as DAoC's default chat colours read: say white, tells and
    // group/guild channels their own hues, system/important yellow-ish, combat/spell muted.
    private static Color ChatColor(ChatType type) => type switch
    {
        ChatType.Say => new Color(0.95f, 0.95f, 0.95f),
        ChatType.Send => new Color(0.7f, 0.7f, 1.0f),
        ChatType.Group => new Color(0.5f, 0.85f, 0.95f),
        ChatType.Guild => new Color(0.5f, 0.95f, 0.5f),
        ChatType.Officer => new Color(0.4f, 0.8f, 0.4f),
        ChatType.Alliance => new Color(0.6f, 0.9f, 0.7f),
        ChatType.Broadcast => new Color(0.95f, 0.75f, 0.4f),
        ChatType.Emote => new Color(0.85f, 0.7f, 0.95f),
        ChatType.Trade or ChatType.LookingForGroup => new Color(0.9f, 0.85f, 0.5f),
        ChatType.Help or ChatType.Advise => new Color(0.6f, 0.85f, 0.9f),
        ChatType.Important or ChatType.Staff => new Color(1.0f, 0.85f, 0.3f),
        ChatType.Spell or ChatType.SpellExpires or ChatType.SpellPulse or ChatType.SpellResisted
            => new Color(0.7f, 0.75f, 0.85f),
        ChatType.Loot or ChatType.Items or ChatType.Merchant => new Color(0.8f, 0.8f, 0.6f),
        ChatType.YourDeath or ChatType.OthersDeath or ChatType.Damaged or ChatType.YouHit
            or ChatType.OthersCombat => new Color(0.9f, 0.6f, 0.55f),
        _ => new Color(0.75f, 0.78f, 0.72f),  // Unspecified/System and the rest: muted grey-green
    };

    private static Color RealmColor(int realm) => realm switch
    {
        1 => new Color(0.6f, 0.2f, 0.2f),   // Albion red
        2 => new Color(0.2f, 0.4f, 0.75f),  // Midgard blue
        3 => new Color(0.3f, 0.6f, 0.3f),   // Hibernia green
        _ => new Color(0.55f, 0.55f, 0.6f), // neutral
    };

    // look: a player (drawn with its Catacombs figure, dressed in its face and hair); null for an NPC.
    private void SpawnEntity(ushort objectId, Vector3 pos, float yaw, Color color, ushort model = 0, byte size = 50,
        CharacterLook? look = null)
    {
        // Free any node we already have for this id before replacing it. This happens normally: an
        // entity first seen via a bare 0xA1 update spawns grey (OnObjectMoved), then its 0xDA/0x4B
        // create arrives and re-spawns it realm-coloured. Without freeing the old node it is orphaned
        // in the scene tree — the NPC is drawn twice (a grey ghost left at the old spot, z-fighting
        // the coloured one) until it next moves. Also covers a repeated create for a re-seen id.
        if (_entities.Remove(objectId, out Entity? existing))
            existing.Node.QueueFree();

        var node = new Node3D { Position = pos, Rotation = new Vector3(0, yaw, 0) };
        AnimationPlayer? anim = null;
        if (model != 0 && ((look is not null ? Figure(model, catacombs: true) : null) ?? Figure(model)) is { } figure)
        {
            // The server's model id is the client's monsters.csv row; mpk figures converted it to
            // models/model<id>.glb. Size 50 is normal; the table's own scale applies on top.
            var inst = (Node3D)figure.Template.Duplicate();
            float scale = figure.Scale * Mathf.Max(size, 1) / 50f;
            inst.Scale = new Vector3(scale, scale, scale);
            inst.Rotation = new Vector3(0, Mathf.Pi, 0);   // figures face +Z; entities face -Z
            if (look is { } l)
                ApplyLook(inst, model, l);
            node.AddChild(inst);
            anim = inst.FindChildren("*", "AnimationPlayer", true, false).FirstOrDefault() as AnimationPlayer;
        }
        else
        {
            node.AddChild(new MeshInstance3D
            {
                Mesh = new CapsuleMesh { Radius = 0.4f, Height = 2f },
                Position = new Vector3(0, 1, 0),
                MaterialOverride = new StandardMaterial3D { AlbedoColor = color },
            });
        }
        _entityRoot.AddChild(node);
        var entity = new Entity { Node = node, TargetPosition = pos, TargetYaw = yaw, Player = anim, LastPosition = pos };
        _entities[objectId] = entity;
        PlayClip(entity, "idle");
    }

    // A figure's clip, looping; a no-op for a capsule or a clip the figure lacks.
    private static void PlayClip(Entity e, string clip)
    {
        if (e.Player is null || e.Clip == clip || !e.Player.HasAnimation(clip))
            return;
        e.Player.GetAnimation(clip).LoopMode = Animation.LoopModeEnum.Linear;
        e.Player.Play(clip, customBlend: 0.2);
        e.Clip = clip;
    }

    // Converted figures by model id (null: none for that id), loaded once each and duplicated per
    // entity: classic ones (models/model<id>.glb, listed in figures.csv) and Catacombs ones
    // (models/cata<id>.glb, catacombs.csv), both written by mpk figures.
    private readonly Dictionary<(ushort, bool), (Node3D Template, float Scale, string File)?> _figures = new();
    private Dictionary<int, float>? _figureScales, _cataScales;

    private (Node3D Template, float Scale, string File)? Figure(ushort model, bool catacombs = false)
    {
        if (_figures.TryGetValue((model, catacombs), out var cached))
            return cached;
        (Node3D, float, string)? result = null;
        string lib = string.IsNullOrEmpty(_libraryPath) ? "" : _libraryPath;
        Dictionary<int, float> scales = catacombs
            ? _cataScales ??= ReadFigureScales(lib.PathJoin("models").PathJoin("catacombs.csv"))
            : _figureScales ??= ReadFigureScales(lib.PathJoin("models").PathJoin("figures.csv"));
        string file = catacombs ? $"cata{model}.glb" : $"model{model}.glb";
        string path = lib.PathJoin("models").PathJoin(file);
        if (lib.Length > 0 && scales.TryGetValue(model, out float scale) && Godot.FileAccess.FileExists(path))
        {
            var doc = new GltfDocument();
            var state = new GltfState();
            if (doc.AppendFromFile(path, state) == Error.Ok && doc.GenerateScene(state) is Node3D m)
            {
                ModelTexturing.TagLayers(state);
                ModelTexturing.Apply(m, lib.PathJoin("overrides"), lib.PathJoin("textures"), lib);
                result = (m, scale, file);
            }
        }
        _figures[(model, catacombs)] = result;
        return result;
    }

    // ----- Catacombs appearance -----

    // fig3 figure number per Catacombs model id (catacombs.csv column 4), read once.
    private Dictionary<int, int>? _cataFig3;

    // Dresses a player's Catacombs figure in its appearance (FigureLook); a description for the log.
    private string ApplyLook(Node3D figure, ushort model, CharacterLook look)
    {
        string lib = _libraryPath ?? "";
        _cataFig3 ??= ReadCataFigures(lib.PathJoin("models").PathJoin("catacombs.csv"));
        return _cataFig3.TryGetValue(model, out int fig3)
            ? FigureLook.Apply(figure, lib.PathJoin("models").PathJoin($"fig3_{fig3}.json"), look,
                lib.PathJoin("overrides"), lib.PathJoin("textures"), lib)
            : "";
    }

    // models/catacombs.csv: "id,scale,figure,fig3"; fig3 is the race's options file number.
    internal static Dictionary<int, int> ReadCataFigures(string path)
    {
        var figures = new Dictionary<int, int>();
        if (!Godot.FileAccess.FileExists(path))
            return figures;
        foreach (string line in Godot.FileAccess.GetFileAsString(path).Split('\n'))
        {
            string[] c = line.Split(',');
            if (c.Length >= 4 && int.TryParse(c[0], out int id) && int.TryParse(c[3], out int fig3))
                figures[id] = fig3;
        }
        return figures;
    }

    // models/figures.csv and catacombs.csv (written by mpk figures): "id,scale,figure" per converted model id.
    private static Dictionary<int, float> ReadFigureScales(string path)
    {
        var scales = new Dictionary<int, float>();
        if (!Godot.FileAccess.FileExists(path))
            return scales;
        foreach (string line in Godot.FileAccess.GetFileAsString(path).Split('\n'))
        {
            string[] c = line.Split(',');
            if (c.Length >= 2 && int.TryParse(c[0], out int id)
                && float.TryParse(c[1], System.Globalization.NumberStyles.Float, System.Globalization.CultureInfo.InvariantCulture, out float s))
                scales[id] = s;
        }
        return scales;
    }

    // Ease each entity toward the last position/facing the server reported. The per-NPC update rate
    // is low (~1 Hz in the capture), so without this they teleport; with it they glide. The rate is
    // frame-rate independent (exponential smoothing). Yaw lerps the short way around the circle.
    private void InterpolateEntities(double delta)
    {
        float t = Mathf.Min(1f, (float)delta * 8f);
        foreach (Entity e in _entities.Values)
        {
            e.Age += delta;
            e.Node.Position = e.Node.Position.Lerp(e.TargetPosition, t);
            float yaw = e.Node.Rotation.Y;
            e.Node.Rotation = new Vector3(0, yaw + Mathf.AngleDifference(yaw, e.TargetYaw) * t, 0);

            // The clip follows how fast the figure is gliding (metres per second, smoothed): DAoC's
            // walk is about 2 m/s, its run about 4.8.
            if (e.Player is not null && delta > 0)
            {
                Vector3 step = e.Node.Position - e.LastPosition;
                step.Y = 0;
                e.Speed = Mathf.Lerp(e.Speed, step.Length() / (float)delta, Mathf.Min(1f, (float)delta * 4f));
                e.LastPosition = e.Node.Position;
                PlayClip(e, e.Speed > 3.2f ? "run" : e.Speed > 0.4f ? "walk" : "idle");
            }
        }
    }

    // Movement + the ground raycast run in the PHYSICS frame: DirectSpaceState.IntersectRay is only
    // valid there, and from _Process it intermittently returns no hit — which left the body walking
    // horizontally off a downslope instead of following the ground (the symptom on real terrain).
    public override void _PhysicsProcess(double delta)
    {
        UpdatePlayer(delta);
        if (_pendingClick is Vector2 click)
        {
            _pendingClick = null;
            Inspect(click);
        }
    }

    public override void _Process(double delta)
    {
        _highlight.AlbedoColor = new Color(1f, 0f, 1f, 0.15f + 0.15f * Mathf.Sin((float)_seconds * 4f));
        if (_shotAt >= 0 && _seconds >= _shotAt)
        {
            _shotAt = -1;
            _ = SaveScreenshot();
        }
        if (_chase)
            UpdateChaseCamera(delta);
        else
            UpdateDebugCamera(delta);
        _seconds += delta;
        // First what the session passed over while entering the world or moving us (the NPCs announced
        // then), then the connection's own queue.
        if (_draining && _session is not null)
            foreach (ServerPacket skipped in _session.TakeSkipped())
                _bridge.Dispatch(skipped);
        while (_draining && _connection is not null && _connection.TryReceive(out ServerPacket packet))
            _bridge.Dispatch(packet);
        _packets = _bridge.PacketCount;
        InterpolateEntities(delta);

        if (_closedReason is null && _connection is not null && _connection.Completion.IsCompleted)
        {
            _closedReason = _connection.Completion.Exception?.InnerException?.Message ?? "connection closed";
            _note = "";
            _button.Text = "Back to login";
            _button.Disabled = false;
        }

        PositionAndObjectId p = _world.Position;
        _dev.Status =
            $"{_world.Zone.Name} (zone {_world.Zone.Id}, region {p.RegionId})   x {p.X:0} y {p.Y:0} z {p.Z:0}   heading {p.Heading & 0x0FFF}\n" +
            (AssetsNote.Length > 0 ? AssetsNote + "\n" : "") +
            $"{Engine.GetFramesPerSecond():0} fps   {_seconds:0} s in world   {_packets} packets   {_pings} ping replies   {_entities.Count} entities ({_entities.Values.Count(e => e.Player is not null)} figures)   {_chatReceived} chat   " +
            (_closedReason is null ? "connected" : $"DISCONNECTED: {_closedReason}") + _note + "\n" +
            $"W {(Input.IsKeyPressed(Key.W) ? 1 : 0)} S {(Input.IsKeyPressed(Key.S) ? 1 : 0)} " +
            $"A {(Input.IsKeyPressed(Key.A) ? 1 : 0)} D {(Input.IsKeyPressed(Key.D) ? 1 : 0)}   " +
            $"body {_body.Position.X:0.0},{_body.Position.Y:0.0},{_body.Position.Z:0.0}   " +
            $"ground {(_groundHit ? "ON" : "--")}   mode {(_chase ? "chase" : "free")}\n" +
            (_chase
                ? "move: W/S walk, A/D or right-drag turn, Q/E strafe, Shift sprint, wheel zoom   ·   Tab free-fly, P shot"
                : "free-fly: WASD move, Q/E down/up, right-drag look, Shift sprint   ·   Tab chase, P screenshot")
            + "\nF1 panels · F2 render mode · F3 view distance · left-click inspect (Esc clears)";
    }
}
