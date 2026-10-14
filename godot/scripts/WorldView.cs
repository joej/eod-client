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

    private DaocConnection _connection = null!;
    private WorldPosition _world = null!;
    private Label _status = null!;
    private Button _button = null!;
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

    /// <summary>Call before adding to the tree.</summary>
    public void Init(DaocConnection connection, WorldPosition world)
    {
        _connection = connection;
        _world = world;
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

        // Ground: 1000 × 1000 units (100,000 region units) centred under the character.
        AddChild(new MeshInstance3D
        {
            Mesh = new PlaneMesh { Size = new Vector2(1000, 1000) },
            Position = new Vector3(at.X, at.Y, at.Z),
            MaterialOverride = new StandardMaterial3D { AlbedoColor = new Color(0.35f, 0.5f, 0.3f) },
        });

        // Character: a 2-unit capsule standing on its position, with a nose showing its heading.
        var body = new Node3D { Position = at, Rotation = new Vector3(0, -HeadingRadians(p.Heading), 0) };
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

        var camera = new Camera3D { Position = at + new Vector3(0, 5, 9) };
        AddChild(camera);
        camera.LookAt(at + new Vector3(0, 1, 0));

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

    public override void _Process(double delta)
    {
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
            (_closedReason is null ? "connected" : $"DISCONNECTED: {_closedReason}") + _note;
    }
}
