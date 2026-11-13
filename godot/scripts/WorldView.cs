// SPDX-License-Identifier: GPL-3.0-or-later
//
// The world view: the character where the server put it, in its zone, with the dev overlay
// (DevOverlay). CharacterScreen keeps the session alive (ClientSession.KeepAliveAsync); this node
// drains incoming packets once per frame and notices when the connection closes.
// This file holds the state, setup, input and per-frame loop; the rest is split by subject:
// WorldView.Zone (terrain, objects, water, sky), .Player (cameras, movement, ground), .Entities
// (other players and NPCs), .Chat, .Inspect and .Shots (development arguments, screenshots).

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
    private bool _chase = true;          // chase camera (default) vs free-fly debug camera
    private float _bodyYaw;              // the body's facing, radians (Godot sense)
    // Position sends follow retail 1.127: every 0.2 s while moving, turning or in the air, every
    // 2 s standing still (well under OpenDAoC's 5 s link-death threshold).
    private float _posLeft;              // countdown to the next position send, seconds
    private bool _sendingChanges;        // the last send was of a change (moving/turning)
    private const float MovingSendInterval = 0.2f;
    private const float IdleSendInterval = 2.0f;
    private const float PositionEpsilon = 1.0f;      // region units; ~0.025 m, below a frame's step
    // Ground-plane run speed in region units/second and the matching 0xA9 "speed" value: the base run
    // (191) until the server's 0xB6 says otherwise (buffs, encumbrance, mounts). Its speed check allows
    // 1.15 × that over x/y, snaps a faster player back and disconnects one snapped back six times in a
    // row, so we never run faster (except the GM-only dev sprint). Metres/s = units/s × 0.025.
    private float _runUnitsPerSecond = DaocProtocol.Packets.MaxSpeed.BaseUnitsPerSecond;
    private string? _modelPath;
    private string? _zonePath;
    private string? _libraryPath;
    private string? _terrainPath;
    private DevOverlay _dev = null!;
    private Button _button = null!;
    private Godot.Environment _environment = null!;
    private DirectionalLight3D _sun = null!;
    private string? _skyName;
    // The zone's own view distance (SECTOR.DAT [fog] clip) and fogged share, in metres / 0..1; 0 when
    // the zone has no sky.csv. The player's view-distance setting multiplies the distance.
    private float _zoneClip;
    private float _fogShare;
    private System.Action? _nextViewDistance;
    private readonly Node3D _entityRoot = new() { Name = "Entities" };
    private bool _fixturesOn = true;
    private bool _terrainOn = true;
    private bool _waterOn = true;
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
    private bool ChatInputActive => _chatInput is not null && _chatInput.HasFocus();
    // Chase rig (adapted from OfflineDAoC's SpringArm3D camera, GPL-3.0; see
    // docs/offlinedaoc-asset-pipeline.md): a pivot at shoulder height on the body carries a
    // SpringArm3D, which pulls the camera in when it would clip terrain/walls — the thing a fixed
    // manual offset gets wrong. The camera is the arm's child, so it always frames the pivot.
    private Node3D _camPivot = null!;
    private SpringArm3D _camArm = null!;
    private float _camYaw;               // chase: orbit yaw (free-fly: camera yaw)
    private float _camPitch;
    private float _camDistance = 5.5f;    // spring length target, metres; set from the camera home on entering (WorldView.Camera)
    private float _camOrbit;              // a left-drag pan round the character (radians); the Reset Camera key resets it
    private float _camHeight;             // the camera home's height: the pivot raised (metres, +-50 units)
    private bool _panActive, _panMoved;   // a left press on the world, and whether it has become a drag
    private double _leftDownAt, _rightDownAt;   // when each button went down: a release within 250 ms is a click (retail)
    private double _lastInteractAt = -10;       // right-click interact at most once a second (retail)
    // Retail's mouse rates (camera note "Mouse and keys"): a pixel turns 1/4096 of a turn and tilts 0.1°; tilt ±80°.
    private const float YawPerPixel = Mathf.Tau / 4096f, PitchPerPixel = Mathf.Pi / 1800f, MaxTilt = 80f * Mathf.Pi / 180f;
    private Vector2 _panFrom;
    private bool _autoRun;                // NumLock auto-run
    private const float CamMin = 1.25f;  // 50 DAoC units; game.dll cameradistance clamp
    private const float CamMax = 12.5f;  // 500 DAoC units
    private bool _lookActive;
    private bool _freshCapture;           // true right after mouse capture, to drop the first delta
    private bool _groundHit;              // did the last ground raycast find terrain? (debug overlay)
    private const uint TerrainLayer = 1 << 1;  // collision layer 2: terrain only, for the ground ray
    private const uint WaterLayer = 1 << 3;    // collision layer 4: water surfaces, for the swim check only
    // Layers nothing stands on, sees through or is stopped by: NPCs' pick shapes and water surfaces.
    private const uint WallLayer = 1 << 4;     // collision layer 5: the models' collision volumes (eodcollide_), for walking into
    // Not a floor, nothing the camera pulls in off, nothing that hides a pick: creatures' pick shapes, water, and the
    // invisible collision volumes (only the character's movement meets those: WorldView.Walls).
    private const uint NotSolid = EntityLayer | WaterLayer | WallLayer;
    // Free-fly debug camera speed in Godot units/second (hold Shift to move faster).
    private int _packets;
    private int _pings;
    private double _seconds;
    private double _nextStatus;
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
        public float ServerY;         // the height the server last sent (Godot metres)
        public int Realm;             // 0 none (monsters), 1 Albion, 2 Midgard, 3 Hibernia
        public bool IsPlayer;
        public bool Grounded;         // stands on the ground under it, whatever height the server sends
        public bool GroundedAtSpawn;  // its create said neither flying nor swimming (a 0xA1 flag may still lift it)
        public float? GroundY;        // that ground's height, probed for ProbedAt
        public Vector3 ProbedAt = new(float.NaN, 0, 0);
        public float TargetYaw;       // the heading last sent; the figure turns to it over TurnFrom..TurnStart + 0.5 s
        public float TurnFrom;
        public double TurnStart = -1; // when that turn began (-1: none)
        public double Age;            // seconds since spawn, for a brief "is this real" grace period
        public AnimationPlayer? Player;   // a DAoC figure's clips (idle, walk, run), or null for a capsule
        public string Clip = "";
        // Between updates the client moves it itself (daoc-re RE/77 "Between updates"):
        public float MoveSpeed;       // units/s along its heading as sent (negative: backwards); 0 once it arrives
        public float ZSpeed;          // units/s up or down (a flying or swimming NPC without a destination)
        public int Strafe;            // a player strafing: -1 left, 1 right (110 units/s sideways)
        public Vector3? Destination;  // where an NPC walks to (0xA1); it stops on arrival
        public ushort Follow;         // the object an NPC faces and walks to (0xA1 word 9; 0 / 0xFFFF none)
        public double LastUpdate;     // when its last position update came (_seconds)
        public bool Moved;            // it moved this frame (picks a moving clip)
        public DaocAssets.Figures.Strides? Strides;   // its figure's strides (clip playback rate), null: defaults
        public float SizeScale = 1f;  // its figure's size against 100 (the table's scale times the NPC's size / 50)
        public Node3D? Figure;        // a player's Catacombs figure, dressed by 0x15 (null otherwise)
        public ushort Model;
        public CharacterLook Look;
        public bool InCombat;         // attack mode (the health byte's 0x80 in its 0xA9 / 0xBA): weapons drawn
        public string CombatIdle = "combat_idle";   // the clip for standing in attack mode, by the weapon in use
        public Node3D? QuestGlyph;    // the glyph over a quest giver's head (null: none)
        public string Name = "";
        public string Guild = "";     // a player's guild (its plate turns the guild colour when it is ours)
        public byte Health = 100;     // percent, from its position updates (0xA1, 0xA9)
        public int Level;             // an NPC's level (0 for players: not sent)
        public Color NameColor = new(0.92f, 0.90f, 0.82f);   // con or realm colour, as its nameplate
        public bool Dead;            // died (0xAE) and not yet revived: lies in its Death clip
        public PlayerPosture Posture; // a player's posture from its 0xA9 / 0xBA state byte (sit, swim, jump, climb, ...)
        public double HoldUntil;      // a one-off clip (land) plays until then unless it moves
        public Label3D? Nameplate;    // its name (and role or guild) over its head
    }

    /// <summary>Raised when the player presses Quit (connected).</summary>
    public event Action? QuitPressed;

    /// <summary>Raised when the player presses Back to login (after the connection closed).</summary>
    public event Action? BackToLoginPressed;

    /// <summary>
    /// The server moved the player into another zone of this region (0x04) and this view doesn't stream
    /// zones (explicit zone files): its terrain and objects are the wrong ones, so the owner replaces it
    /// with a view of the new zone. A streaming view loads the zones there itself. The session
    /// already holds the new position. Draining has stopped.
    /// </summary>
    public event Action<WorldPosition>? Relocated;

    /// <summary>
    /// The server moved the player to another region (0xB7). Draining has stopped; the owner runs
    /// ClientSession.ChangeRegionAsync (which must see the packets that follow) and builds a new view.
    /// The argument is the destination region (for its loading screen).
    /// </summary>
    public event Action<ushort>? RegionChanging;

    /// <summary>The player's own model id (the character list's model; monsters.csv row, e.g. 32 =
    /// Briton male): the body is drawn as that figure when one was converted. 0 = none.</summary>
    public ushort PlayerModel { get; set; }

    /// <summary>The player's face and hair (the character list's appearance bytes), applied to a
    /// Catacombs figure.</summary>
    public CharacterLook PlayerLook { get; set; }

    /// <summary>The player's armour (the character list's; the server never sends our own 0x15) and
    /// whether the cloak's hood is up.</summary>
    public IReadOnlyList<WornItem> PlayerWorn { get; set; } = [];

    /// <summary>The converted zones folder (private/zones), for the travel window.</summary>
    public string? ZonesDir { get; set; }
    public bool PlayerHoodUp { get; set; }
    /// <summary>The player's weapon slots in use (the character list's, as 0x15's byte).</summary>
    public byte PlayerActiveWeapons { get; set; } = 0xFF;

    private AnimationPlayer? _bodyAnim;
    private string _bodyClip = "";
    private Node3D? _ownFigure;               // the character's Catacombs figure, re-geared when attack mode changes
    private bool _attacking;                  // attack mode as the server last told us (S→C 0x74)
    private string _ownCombatIdle = "combat_idle";

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
    /// note; the keep-alive keeps running until this node leaves the tree. The server sits the
    /// character down; moving raises <see cref="QuitInterrupted"/>.
    /// </summary>
    public void BeginQuit()
    {
        _draining = false;
        _quitting = true;
        _note = "   quitting: sit still for the server's quit timer (about 20 s); moving cancels it";
    }

    /// <summary>The server ended the game (0xA4) for a quit the owner wasn't waiting on: finish it.</summary>
    public event Action<ServerPacket>? QuitArrived;

    /// <summary>The player moved during the quit timer: the owner stands up and calls the quit off.</summary>
    public event Action? QuitInterrupted;

    /// <summary>The quit was called off: back to playing (draining again, the Quit button usable).</summary>
    public void EndQuit()
    {
        _quitting = false;
        _draining = true;
        _note = "";
        _button.Disabled = false;
        AppendChat("Quit cancelled: you moved.", new Color(0.6f, 0.8f, 1f));
    }

    private bool _quitting;

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

        // The zone the view opens in: its terrain (vertices in absolute region coords, so it loads at the
        // origin), objects and water; neighbours stream in after (WorldView.Streaming). Without terrain a
        // flat placeholder, except in a capital city, which is all models: no plane cutting through it.
        ZoneParts first = AddFirstZone();
        _skyName = ApplySky(first.Dir, "clear");
        bool modelsOnly = first.Placed > 0;
        if (first.Terrain is null && !modelsOnly)
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


        // Chase rig: pivot at shoulder height on the body → SpringArm3D → camera. The arm pulls the
        // camera in off terrain/walls/scenery (mask below) so it never clips into the ground — the
        // thing the old fixed-offset camera got wrong. Tab detaches it to a free-fly debug camera
        // (handy: the spawn is often inside a large structure — Eodarms starts at Prydwen Keep).
        ApplyCameraHome(CameraHomeNow);
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
            CollisionMask = ~NotSolid,   // not NPCs' pick shapes or water
        };
        _camPivot.AddChild(_camArm);
        foreach (Node n in _body.GetChildren())
            if (n is CollisionObject3D co)
                _camArm.AddExcludedObject(co.GetRid());
        // Retail's camera: the field of view is across (Fov keeps the width), near 4 units.
        _camera = new Camera3D { Far = 8000f, Fov = ClientSettings.FieldOfView, KeepAspect = Camera3D.KeepAspectEnum.Width, Near = 4 * RegionMetresPerUnit };
        ClientSettings.Changed += OnSettingsChanged;
        _camArm.AddChild(_camera);
        // The SpringArm extends the camera along the pivot's +Z, so to sit BEHIND a body that faces
        // -Z the pivot yaw is the body yaw turned 180°. Matching them put the camera in front, looking
        // at the nose (so walking read as "backwards toward the camera").
        _camYaw = _bodyYaw + Mathf.Pi;          // start behind the body

        // Dispatcher: route drained packets to entity handlers (M5). Subscribe before the first
        // drain in _Process so no world packet is missed.
        _bridge = new NetworkBridge(message => GD.PrintErr(message), _session?.Delves);
        _bridge.ObjectMoved += OnObjectMoved;
        _bridge.NpcSpawned += OnNpcSpawned;
        _bridge.PlayerSpawned += OnPlayerSpawned;
        _bridge.EquipmentChanged += OnEquipmentChanged;
        _bridge.PlayerMoved += OnPlayerMoved;
        _bridge.PlayerTurned += OnPlayerTurned;
        _bridge.AttackModeChanged += OnAttackModeChanged;
        _bridge.QuestChanged += (id, quest) =>
        {
            if (_entities.TryGetValue(id, out Entity? e))
                SetQuestGlyph(e, quest);
        };
        _bridge.MaxSpeedChanged += speed =>
        {
            _runUnitsPerSecond = speed.UnitsPerSecond;
            (_speedPercent, _waterSpeed) = (speed.Percent, speed.WaterSpeed);
            GD.Print($"[world] max speed {speed.Percent}% ({speed.UnitsPerSecond:0} units/s)");
        };
        _bridge.ObjectRemoved += OnObjectRemoved;
        _bridge.ObjectSpawned += OnWorldObject;
        _bridge.ChatReceived += OnChatReceived;
        SubscribeDeath();
        SubscribeCharacter();
        SubscribeStats();
        _bridge.PingReplied += OnPingReplied;
        _bridge.Jumped += OnJumped;
        _bridge.QuitArrived += reply =>
        {
            _draining = false;   // the character has left the world; what follows is the listing's
            _quitting = false;
            _note = "   quit: back to the character screen…";
            QuitArrived?.Invoke(reply);
        };
        _bridge.RegionChanged += change =>
        {
            _draining = false;   // what follows is the re-entry's, not ours
            _note = "   changing region…";
            RegionChanging?.Invoke(change.RegionId);
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
        var travel = new Button { Text = "Travel (F6)", FocusMode = Control.FocusModeEnum.None, SizeFlagsHorizontal = Control.SizeFlags.ShrinkBegin };
        travel.Pressed += () => OpenTravel();
        _dev.AddToStatus(travel);
        BuildChatWindow(overlay);
        BuildNpcWindow(overlay);
        BuildDeathPanel(overlay);
        BuildMapWindow(overlay);
        BuildHud(overlay);
        BuildQuickbars(overlay);
        BuildCompass(overlay);
        BuildXpWindow(overlay);
        BuildMenuBar(overlay);
        BuildClock(overlay);
        BuildStatsPanel(overlay);
        BuildItemWindows(overlay);
        BuildDialog(overlay);
        _builtScale = UiScale;
        ScaleChatAndNpc(_builtScale / ChatDesignScale);
        NameSelf();
        AddChild(overlay);
        _viewSetting = ViewOverride() ?? ClientSettings.ViewDistance;
        ApplyViewDistance(_viewSetting);
        BuildDevToggles(first);
        ApplyDevArgs();
        // Released into another region: the session is still dead until the revive in the re-entry.
        if (_session?.IsDead == true)
            Die();
    }

    // Layer switches for the dev overlay. Hidden fixtures/terrain are also left out of inspect picks.
    private void BuildDevToggles(ZoneParts first)
    {
        _nextViewDistance = _dev.AddChoice("F3 view", [.. ClientSettings.ViewDistances.Select(v => v.Label)],
            _viewSetting, i => { _viewSetting = i; ClientSettings.ViewDistance = i; ApplyViewDistance(i); });
        _dev.AddToggle("Sun shadows", ClientSettings.SunShadows, on =>
        {
            ClientSettings.SunShadows = on;
            _sun.ShadowEnabled = on;
        });
        _dev.AddToggle("Terrain", true, on =>
        {
            _terrainOn = on;
            foreach (ZoneParts z in _zones.Values) if (z.Terrain is not null) z.Terrain.Visible = on;
        });
        _dev.AddToggle("Fixtures", true, on =>
        {
            _fixturesOn = on;
            foreach (ZoneParts z in _zones.Values) if (z.Objects is not null) z.Objects.Visible = on;
        });
        _dev.AddToggle("Water", true, on =>
        {
            _waterOn = on;
            foreach (ZoneParts z in _zones.Values) if (z.Water is not null) z.Water.Visible = on;
        });
        _flipJumpsToggle = _dev.AddToggle("Jump points", _jumpsVisible, ShowJumpMarkers);
        _flipBordersToggle = _dev.AddToggle("Zone borders", _bordersVisible, ShowZoneBorders);
        if (_bordersVisible)
            ShowZoneBorders(true);   // on before a region change: draw this region's at once
        _dev.AddToggle("Entities", true, on => _entityRoot.Visible = on);
        _dev.AddToggle("Own character", true, on => { _ownCharacterShown = on; ShowOwnBody(); });
        _dev.AddToggle("Names", _namesVisible, ShowNames);
        _dev.AddToggle("Chat", true, on => { if (_chatWindow is not null) _chatWindow.Visible = on; });
        // Off by default: the server's speed check exempts GMs only, and kicks a player who sprints.
        _dev.AddToggle("Shift fast move ×4 (GM only)", ClientSettings.Sprint, on => ClientSettings.Sprint = on);
        if (PacketLogging is var (getLog, setLog))
            _togglePacketLog = _dev.AddToggle("F4 packet log", getLog(), setLog);
        _dev.AddInfo($"{first.Placed} fixtures · {first.TexturedModels} textured models · {first.WaterBodies} water\n" +
            $"{first.TexturedSurfaces} textured surfaces" + (first.Terrain is null ? " · flat ground" : "")
            + (_streaming ? " · neighbouring zones stream in" : ""));
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
        string look = Outfit(inst, PlayerModel, PlayerLook, PlayerWorn, PlayerHoodUp, false, false, PlayerActiveWeapons, _attacking);
        _ownFigure = inst;
        _ownCombatIdle = CombatIdle(PlayerWorn, PlayerActiveWeapons);
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

    // A clip played once and held on its last frame (jump: the tucked pose in the air; land), for
    // <paramref name="hold"/> seconds before the frame loop may change it. A figure without it keeps its clip.
    private void PlayOnce(string clip, float hold = 0)
    {
        if (_bodyAnim is null || _bodyClip == clip || !_bodyAnim.HasAnimation(clip))
            return;
        _bodyAnim.GetAnimation(clip).LoopMode = Animation.LoopModeEnum.None;
        _bodyAnim.Play(clip, customBlend: 0.1);
        _bodyClip = clip;
        _landLeft = hold;
    }

    // Sitting (/sit, or the quit timer: the server sits the character down and says so in 0xAD): sit down once, then
    // hold the sit idle; others see it from the 0x10 in our 0xA9. Moving stands up (and cancels a quit), and the
    // walk/run clip takes over. A figure converted before the sit
    // clips existed keeps its idle.
    private void Sit()
    {
        if (_bodyAnim is null || _bodyClip == "sit")
            return;
        if (!_bodyAnim.HasAnimation("sit_down") || !_bodyAnim.HasAnimation("sit_idle"))
        {
            PlayBodyClip("idle");
            return;
        }
        _bodyAnim.GetAnimation("sit_down").LoopMode = Animation.LoopModeEnum.None;
        _bodyAnim.GetAnimation("sit_idle").LoopMode = Animation.LoopModeEnum.Linear;
        _bodyAnim.Play("sit_down", customBlend: 0.2);
        _bodyAnim.Queue("sit_idle");
        _bodyClip = "sit";
    }

    private bool TryAddModel(Node3D body)
    {
        if (string.IsNullOrEmpty(_modelPath) || !Godot.FileAccess.FileExists(_modelPath))
            return false;

        // Texture from the extracted .dds beside the model (where tools/pipeline.sh puts them).
        string textureDir = System.IO.Path.GetDirectoryName(_modelPath) ?? "";
        if (ModelTexturing.LoadGlb(_modelPath, out int textured, textureDir) is not Node3D model)
            return false;   // keep the capsule

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

    // An action's keys for the help line ("-" when it has none), from the current layout and the player's own keys.
    private static string Keys(ControlAction a) =>
        KeyBindings.KeysFor(a) is { Length: > 0 } k ? string.Join("+", System.Linq.Enumerable.Select(k, OS.GetKeycodeString)) : "-";

    // Zooms the chase camera by wheel notches: positive moves away, 50 units a notch at once (retail). In past 50 units
    // is first person; zooming out of first person comes back at 50 (WorldView.Camera).
    private void Zoom(float notches) => ZoomTo(_camDistance + notches * ZoomStep);

    // Right mouse button toggles mouse-look; while held, relative motion turns the camera.
    // P saves a screenshot of the current view.
    public override void _Input(InputEvent @event)
    {
        if (InterfaceScaleDrag(@event))
        {
            GetViewport().SetInputAsHandled();
            return;
        }
        // Enter focuses the chat box so you can type; Escape drops focus back to the game. While the
        // box has focus the LineEdit consumes keys itself, so none of the game hotkeys below fire.
        if (@event is InputEventKey { Pressed: true, Echo: false, Keycode: Key.Enter or Key.KpEnter }
            && !ChatInputActive)
        {
            _chatInput.GrabFocus();
            GetViewport().SetInputAsHandled();
            return;
        }
        // Typing '/' starts a command at once, as in retail: the box takes focus with the '/' already in it.
        if (@event is InputEventKey { Pressed: true, Echo: false, Unicode: '/' } && !ChatInputActive)
        {
            _chatInput.GrabFocus();
            _chatInput.Text = "/";
            _chatInput.CaretColumn = 1;
            GetViewport().SetInputAsHandled();
            return;
        }
        if (ChatInputActive)
        {
            if (@event is InputEventKey { Pressed: true, Echo: false, Keycode: Key.Escape })
                _chatInput.ReleaseFocus();
            return;   // typing: don't drive the camera or trigger P/Tab while the box has focus
        }
        if (QuickbarKey(@event))
        {
            GetViewport().SetInputAsHandled();
            return;
        }
        // A click on a game window (quickbar, summary, target) is the window's, not the world's: no mouse-look.
        if (@event is InputEventMouseButton { Pressed: true } over && OverHud(over.Position) && !_lookActive)
            return;

        // Left-drag pans the chase camera round the character (retail); a left click without a drag still inspects.
        if (@event is InputEventMouseButton { ButtonIndex: MouseButton.Left, Pressed: false } up && _panActive)
        {
            // A click is a release within 250 ms of the press; held longer it was a pan (retail).
            if (_seconds - _leftDownAt <= 0.25 && !_lookActive && !ChatInputActive)
                _pendingClick = up.Position;
            _panActive = false;
        }
        else if (@event is InputEventMouseMotion pm && _panActive && !_lookActive)
        {
            _panMoved |= pm.Position.DistanceTo(_panFrom) > 4;
            if (_panMoved && _chase)
            {
                _camOrbit -= Mathf.Clamp(pm.Relative.X, -80, 80) * YawPerPixel;
                _camPitch = Mathf.Clamp(_camPitch - Mathf.Clamp(pm.Relative.Y, -80, 80) * PitchPerPixel, -MaxTilt, MaxTilt);
            }
        }
        else if (@event is InputEventMouseButton mb && mb.ButtonIndex == MouseButton.Right)
        {
            // A right click without a drag interacts with what is under the cursor (resolved in the physics
            // frame); a drag is mouse-look. In the classic layout the right button walks instead.
            if (mb.Pressed)
                (_rightFrom, _rightMoved, _rightDownAt) = (mb.Position, 0, _seconds);
            else if (_lookActive && _seconds - _rightDownAt <= 0.25 && !KeyBindings.Moves(MouseButton.Right))
                _pendingInteract = _rightFrom;   // released within 250 ms: a click (retail), not mouselook
            _lookActive = mb.Pressed;
            // Mouselook turns the character and keeps the camera's orbit angle (retail; snapback is off by default).
            Input.MouseMode = mb.Pressed ? Input.MouseModeEnum.Captured : Input.MouseModeEnum.Visible;
            _freshCapture = mb.Pressed;   // swallow the first (often huge) motion delta after capture
        }
        else if (@event is InputEventMouseButton { Pressed: true } wheel && _chase)
        {
            // Mouse wheel zooms the chase camera (retail: 50 units a notch, 50-500, first person in past 50).
            // Smooth-scrolling wheels report fractional notches in Factor (0 = a plain notch).
            float notches = wheel.Factor > 0 ? wheel.Factor : 1f;
            if (wheel.ButtonIndex == MouseButton.WheelUp)
                Zoom(-notches);
            else if (wheel.ButtonIndex == MouseButton.WheelDown)
                Zoom(notches);
        }
        // macOS trackpads and the Magic Mouse scroll with pan gestures, not wheel buttons; a pinch magnifies.
        else if (@event is InputEventPanGesture pan && _chase)
            Zoom(pan.Delta.Y * TrackpadNotches);
        else if (@event is InputEventMagnifyGesture pinch && _chase && pinch.Factor > 0)
            ZoomTo(_firstPerson && pinch.Factor < 1 ? CamMin : _camDistance / pinch.Factor);
        else if (@event is InputEventMouseMotion mm && _lookActive)
        {
            _rightMoved += mm.Relative.Length();
            if (_freshCapture)
            {
                _freshCapture = false;   // drop the first post-capture delta (a large recenter spike)
                return;
            }
            // Clamp per-event delta so an occasional spike can't snap the view.
            Vector2 d = new(Mathf.Clamp(mm.Relative.X, -80, 80), Mathf.Clamp(mm.Relative.Y, -80, 80));
            mm = new InputEventMouseMotion { Relative = d };
            if (_chase)
            {
                // Right-drag steers: the character turns with the camera yaw, and pitch tilts the view.
                // Camera stays behind the -Z-facing body, so its yaw trails bodyYaw by 180°.
                _bodyYaw -= mm.Relative.X * YawPerPixel;
                _camYaw = _bodyYaw + Mathf.Pi;
                _camPitch = Mathf.Clamp(_camPitch - mm.Relative.Y * PitchPerPixel, -MaxTilt, MaxTilt);
            }
            else
            {
                _camYaw -= mm.Relative.X * YawPerPixel * 3;   // the free-fly dev camera: a little quicker
                _camPitch = Mathf.Clamp(_camPitch - mm.Relative.Y * PitchPerPixel * 3, -1.5f, 1.5f);
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
        else if (@event is InputEventKey { Pressed: true, Echo: false, Keycode: Key.F6 } && !ChatInputActive)
        {
            OpenTravel();
        }
        else if (KeyBindings.Pressed(@event, ControlAction.AutoRun) && !ChatInputActive && _chase)
        {
            _autoRun = !_autoRun;   // NumLock: run forward until it is pressed again, or forward or back
        }
        else if (KeyBindings.Pressed(@event, ControlAction.Sprint) && !ChatInputActive)
        {
            // V toggles sprint (retail's default key, darkageofcamelot.com "Control and Movement"). The
            // server does the rest: /sprint gives 1.3x speed while endurance lasts, drains it while moving
            // and ends sprint itself, each time sending 0xB6, which sets how fast we run.
            if (_session is not null && _closedReason is null)
                _ = SendTravel("/sprint");
        }
        else if (KeyBindings.Pressed(@event, ControlAction.Map) && !ChatInputActive)
        {
            ToggleMap();
        }
        else if (KeyBindings.Pressed(@event, ControlAction.ResetCamera) && !ChatInputActive && _chase)
        {
            ApplyCameraHome(CameraHomeNow);
        }
        else if (KeyBindings.Pressed(@event, ControlAction.Torch) && !ChatInputActive)
        {
            ToggleTorch();
        }
        else if (KeyBindings.Pressed(@event, ControlAction.AttackMode) && !ChatInputActive)
        {
            // H toggles attack mode: our binding, a key the install's RPG preset leaves free (F is retail's face
            // target; retail's attack key is unconfirmed, open question W34). The server answers with 0x74, and
            // that draws or sheathes the weapons.
            _ = SendAttack(!_attacking);
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
            // Esc closes a window, else clears the target and stops attacking, else opens the main menu (retail).
            if (_mainMenu is { Visible: true })
                _mainMenu.Visible = false;
            else if (_infoWindow is { Visible: true })
                _infoWindow.Visible = false;
            else if (_miniInfo is { Visible: true })
                CloseMiniInfo();
            else if (_statsPanel is { Visible: true })
                _statsPanel.Visible = false;
            else if (_mapWindow.Visible)
                _mapWindow.Visible = false;
            else if (_npcWindow.Visible)
                _npcWindow.Visible = false;
            else if (_target != 0 || _attacking)
            {
                // Retail: Esc clears the target and stops attacking; with nothing left to clear it opens the main menu.
                ClearInspect();
                SetTarget(0);
                if (_attacking)
                    _ = SendAttack(false);
            }
            else
            {
                ClearInspect();
                ToggleMainMenu();
            }
        }
    }

    // Settings window changes, live: the field of view, and the window mode while in the world.
    private void OnSettingsChanged(bool display)
    {
        _camera.Fov = ClientSettings.FieldOfView;
        RestyleNames();
        ApplyInterfaceScale();
        if (display)
            ClientSettings.ApplyWindowMode();
    }

    public override void _ExitTree()
    {
        ClientSettings.Changed -= OnSettingsChanged;
        // A zone still being built isn't in the tree, so nothing else frees it.
        if (_building is var (unfinished, _, _))
        {
            _building = null;
            unfinished.Root.Free();
        }
        _zoneModels.FreeAll();
        // Figure templates live outside the tree (only their copies are added), so freeing this view
        // doesn't reach them; a new view is built on every region change.
        foreach (Node3D? template in _figureFiles.Values)
            template?.Free();
        _figureFiles.Clear();
        _figures.Clear();
    }

    // Left-click on the world (not on a panel: panels stop their clicks) queues an inspect pick.
    public override void _UnhandledInput(InputEvent @event)
    {
        // A left press on the world starts a camera pan or, released without a drag, an inspect pick (in _Input).
        // In the classic layout the left button walks backward instead.
        if (@event is InputEventMouseButton { Pressed: true, ButtonIndex: MouseButton.Left } mb
            && !_lookActive && !ChatInputActive && !KeyBindings.Moves(MouseButton.Left))
            (_panActive, _panMoved, _panFrom, _leftDownAt) = (true, false, mb.Position, _seconds);
    }

    // Switches between the chase rig and a free-fly debug camera by reparenting the one Camera3D:
    // under the spring arm for chase (so the arm drives it), or directly under WorldView for free-fly
    // (so UpdateDebugCamera can set its global transform). Reparent keeps the camera where it is.
    private void SetChase(bool chase)
    {
        if (chase == _chase)
            return;
        _chase = chase;
        GD.Print($"[world] camera {(chase ? "follows the character" : "flies free (Tab)")}");
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
            ApplyCameraHome(CameraHomeNow);   // back at the home settings, as retail's death camera returns
            _camPivot.Rotation = new Vector3(_camPitch, _camOrbit, 0f);
        }
        else
        {
            AddChild(_camera);
            _camera.GlobalTransform = worldXform; // stay put, then fly from here
            _camYaw = _camera.Rotation.Y;
            _camPitch = _camera.Rotation.X;
        }
    }

    // Movement + the ground raycast run in the PHYSICS frame: DirectSpaceState.IntersectRay is only
    // valid there, and from _Process it intermittently returns no hit — which left the body walking
    // horizontally off a downslope instead of following the ground (the symptom on real terrain).
    public override void _PhysicsProcess(double delta)
    {
        WalkToTarget();
        FollowStep();
        GroundEntities();
        UpdatePlayer(delta);
        if (_chase)
            CheckJumpPoints();
        if (_pendingClick is Vector2 click)
        {
            _pendingClick = null;
            // A creature or player under the cursor is targeted; empty ground or scenery clears the target (retail), and
            // the dev inspect readout describes what was hit.
            if (PickEntity(click) is ushort picked)
                SetTarget(picked);
            else
            {
                SetTarget(0);
                Inspect(click);
            }
        }
        if (_pendingInteract is Vector2 at)
        {
            _pendingInteract = null;
            // Retail: a right click on a player does nothing; on an NPC or object it targets and interacts, once a second.
            if (PickEntity(at) is ushort other && !(_entities.TryGetValue(other, out Entity? oe) && oe.IsPlayer))
            {
                if (_seconds - _lastInteractAt < 1)
                    AppendChat("You must wait one more second to interact again!", new Color(1f, 1f, 1f));
                else
                {
                    _lastInteractAt = _seconds;
                    Interact(other);
                }
            }
        }
    }

    public override void _Process(double delta)
    {
        UpdateHud();
        UpdateQuickbars();
        UpdateCompass();
        UpdateXp();
        UpdateMenuBar();
        UpdateClock();
        UpdateStatsPanel();
        UpdateSkyTime();
        UpdateLamps(delta);
        UpdateChat();
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
        Stream(delta);
        // First what the session passed over while entering the world or moving us (the NPCs announced
        // then), then the connection's own queue.
        if (_draining && _session is not null)
            foreach (ServerPacket skipped in _session.TakeSkipped())
                _bridge.Dispatch(skipped);
        // A session method waiting for its reply (a jump, a quit) reads the queue itself.
        while (_draining && _session?.AwaitingReply != true && _connection is not null && _connection.TryReceive(out ServerPacket packet))
            _bridge.Dispatch(packet);
        _packets = _bridge.PacketCount;
        InterpolateEntities(delta);
        UpdateDeathPanel();
        UpdateBorderLabels();
        UpdateMap();

        if (_closedReason is null && _connection is not null && _connection.Completion.IsCompleted)
        {
            _closedReason = _connection.Completion.Exception?.InnerException?.Message ?? "connection closed";
            _note = "";
            _button.Text = "Back to login";
            _button.Disabled = false;
        }

        // The status text is for reading, not animation: rebuilt 4 times a second, and not while F1 hides it.
        if (!_dev.PanelsVisible || _seconds < _nextStatus)
            return;
        _nextStatus = _seconds + 0.25;
        PositionAndObjectId p = _world.Position;
        _dev.Status =
            $"{_world.Zone.Name} (zone {_world.Zone.Id}, region {p.RegionId})   x {p.X:0} y {p.Y:0} z {p.Z:0}   heading {p.Heading & 0x0FFF}\n" +
            (AssetsNote.Length > 0 ? AssetsNote + "\n" : "") +
            $"{Engine.GetFramesPerSecond():0} fps   {_seconds:0} s in world   {_packets} packets ({_connection?.Pending ?? 0} queued)   {_pings} ping replies   {_entities.Count} entities ({_entities.Values.Count(e => e.Player is not null)} figures)   {_chatReceived} chat   " +
            (_closedReason is null ? "connected" : $"DISCONNECTED: {_closedReason}") + _note + TargetLine + VitalsLine + CharacterLine + "\n" +
            $"W {(Input.IsKeyPressed(Key.W) ? 1 : 0)} S {(Input.IsKeyPressed(Key.S) ? 1 : 0)} " +
            $"A {(Input.IsKeyPressed(Key.A) ? 1 : 0)} D {(Input.IsKeyPressed(Key.D) ? 1 : 0)}   " +
            $"body {_body.Position.X:0.0},{_body.Position.Y:0.0},{_body.Position.Z:0.0}   " +
            $"ground {(_groundHit ? "ON" : "--")}   mode {(_chase ? "chase" : "free")}\n" +
            (_chase
                ? $"move: {Keys(ControlAction.Forward)}/{Keys(ControlAction.Back)} walk, {Keys(ControlAction.TurnLeft)}/{Keys(ControlAction.TurnRight)} or right-drag turn, " +
                  $"{Keys(ControlAction.StrafeLeft)}/{Keys(ControlAction.StrafeRight)} strafe, {Keys(ControlAction.Jump)} jump, {Keys(ControlAction.Sprint)} sprint, " +
                  $"{Keys(ControlAction.AutoRun)} auto-run, wheel zoom, left-drag pan   ·   {Keys(ControlAction.AttackMode)} attack   ·   Tab free-fly, P shot"
                : "free-fly: WASD move, Q/E down/up, right-drag look, Shift sprint   ·   Tab chase, P screenshot")
            + $"\nF1 panels · F2 render mode · F3 view distance · left-click inspect (Esc clears) · {Keys(ControlAction.Map)} map";
    }
}
