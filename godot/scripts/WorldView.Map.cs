// SPDX-License-Identifier: GPL-3.0-or-later
//
// WorldView: the map window (M; roadmap M9, asked for by the owner 2026-11-07 to get about for testing). It
// shows the client's own map pictures from the user's install (`make maps`: ui/maps into <library>/maps): the
// zone you are in (z<NNN>.dds, the whole zone on 256 x 256), or the region (r<NNN>.dds, each zone's square
// placed by regions.dat), with an arrow where you stand and which way you face, and the zone's description.
// A click on either map runs you there (auto-run in a straight line, as a player would: the server sees
// ordinary running); a movement key, Esc on the map or another click stops or changes it.

using DaocAssets.Zone;
using DaocProtocol.World;
using Godot;

namespace EodClient;

public partial class WorldView
{
    private const int MapSize = 512;            // on screen; the pictures are 256 x 256
    private PanelContainer _mapWindow = null!;
    private TextureRect _mapPicture = null!;
    private MapMarks _mapMarks = null!;
    private Label _mapTitle = null!, _mapDesc = null!, _mapWhere = null!;

    // DAoC headings: 0 south, 1024 west, 2048 north, 3072 east (4096 a full turn).
    private static string Compass(ushort heading) =>
        new[] { "S", "SW", "W", "NW", "N", "NE", "E", "SE" }[(int)Mathf.PosMod(Mathf.Round(heading / 512f), 8)];
    private Button _mapSwitch = null!;
    private bool _mapRegion;                    // showing the region map (else the zone's)
    private IReadOnlyList<RegionMap>? _regionMaps;
    private ushort _mapShownZone = ushort.MaxValue;

    private string? MapsDir => _libraryPath is string lib && System.IO.File.Exists(System.IO.Path.Combine(lib, "maps", "regions.dat"))
        ? System.IO.Path.Combine(lib, "maps") : null;

    private void BuildMapWindow(CanvasLayer overlay)
    {
        _mapWindow = new PanelContainer { Visible = false };
        _mapWindow.AddThemeStyleboxOverride("panel", new StyleBoxFlat
        {
            BgColor = new Color(0.08f, 0.09f, 0.11f, 0.94f), BorderColor = new Color(0.55f, 0.85f, 1f, 0.5f),
            BorderWidthLeft = 1, BorderWidthTop = 1, BorderWidthRight = 1, BorderWidthBottom = 1,
            ContentMarginLeft = 12, ContentMarginTop = 10, ContentMarginRight = 12, ContentMarginBottom = 10,
            CornerRadiusTopLeft = 4, CornerRadiusTopRight = 4, CornerRadiusBottomLeft = 4, CornerRadiusBottomRight = 4,
        });
        _mapWindow.SetAnchorsPreset(Control.LayoutPreset.Center);
        _mapWindow.Position = new Vector2(-(MapSize + 24) / 2f, -(MapSize + 140) / 2f);
        var box = new VBoxContainer();
        var top = new HBoxContainer();
        _mapTitle = new Label { SizeFlagsHorizontal = Control.SizeFlags.ExpandFill };
        _mapSwitch = new Button { Text = "Region", FocusMode = Control.FocusModeEnum.None };
        _mapSwitch.Pressed += () => { _mapRegion = !_mapRegion; _mapShownZone = ushort.MaxValue; RefreshMap(); };
        var close = new Button { Text = "Close (M)", FocusMode = Control.FocusModeEnum.None };
        close.Pressed += () => _mapWindow.Visible = false;
        top.AddChild(_mapTitle);
        top.AddChild(_mapSwitch);
        top.AddChild(close);
        box.AddChild(top);
        var frame = new Control { CustomMinimumSize = new Vector2(MapSize, MapSize) };
        _mapPicture = new TextureRect
        {
            ExpandMode = TextureRect.ExpandModeEnum.IgnoreSize, StretchMode = TextureRect.StretchModeEnum.Scale,
            Size = new Vector2(MapSize, MapSize), MouseFilter = Control.MouseFilterEnum.Stop,
        };
        _mapPicture.GuiInput += OnMapClicked;
        _mapMarks = new MapMarks { Size = new Vector2(MapSize, MapSize), MouseFilter = Control.MouseFilterEnum.Ignore };
        frame.AddChild(_mapPicture);
        frame.AddChild(_mapMarks);
        box.AddChild(frame);
        _mapDesc = new Label
        {
            AutowrapMode = TextServer.AutowrapMode.WordSmart, CustomMinimumSize = new Vector2(MapSize, 0),
            Modulate = new Color(1, 1, 1, 0.75f), MaxLinesVisible = 4,
        };
        box.AddChild(_mapDesc);
        _mapWhere = new Label { Modulate = new Color(1, 1, 1, 0.75f) };
        box.AddChild(_mapWhere);
        box.AddChild(new Label
        {
            Text = "Click the map to run there; a movement key stops.", Modulate = new Color(0.55f, 0.85f, 1f, 0.8f),
        });
        _mapWindow.AddChild(box);
        overlay.AddChild(_mapWindow);
    }

    private void ToggleMap()
    {
        _mapWindow.Visible = !_mapWindow.Visible;
        _mapShownZone = ushort.MaxValue;
        if (_mapWindow.Visible)
            RefreshMap();
    }

    // The picture for where you are; called when the window opens, when you change zone, and on the switch.
    private void RefreshMap()
    {
        string? dir = MapsDir;
        Zone zone = _world.Zone;
        ushort region = _world.Position.RegionId;
        _regionMaps ??= dir is null ? [] : MapLayout.Parse(System.IO.File.ReadAllText(System.IO.Path.Combine(dir, "regions.dat"),
            System.Text.Encoding.Latin1));
        RegionMap? layout = _regionMaps.FirstOrDefault(m => m.Region == region);
        bool hasRegion = dir is not null && layout is not null && System.IO.File.Exists(System.IO.Path.Combine(dir, $"r{region:D3}.dds"));
        _mapSwitch.Visible = hasRegion;
        if (!hasRegion)
            _mapRegion = false;
        _mapSwitch.Text = _mapRegion ? "Zone" : "Region";
        string stem = _mapRegion ? $"r{region:D3}" : $"z{zone.Id:D3}";
        _mapPicture.Texture = dir is null ? null : ModelTexturing.Load(stem, dir) ?? ModelTexturing.Load("no_map", dir);
        _mapTitle.Text = _mapRegion ? RegionName(region) : $"{zone.Name} (zone {zone.Id})";
        _mapDesc.Text = dir is null ? "No maps converted: run make maps."
            : layout?.Zones.FirstOrDefault(z => z.Zone == zone.Id)?.Description ?? "";
        _mapShownZone = zone.Id;
    }

    // The region's name as the install has it (loading.dat, converted by `make loading`: "Albion"), else its number.
    private string RegionName(ushort region)
    {
        string csv = System.IO.Path.Combine(_libraryPath ?? "", "loading", "loading.csv");
        _regionNames ??= System.IO.File.Exists(csv) ? LoadingScreens.ReadCsv(System.IO.File.ReadAllText(csv)) : [];
        return _regionNames.TryGetValue(region, out var r) && r.Name.Length > 0 ? r.Name : $"Region {region}";
    }

    private Dictionary<int, (string Name, bool Instance, string[] Stems)>? _regionNames;

    // Each frame while open: the marks, and the picture again after a zone change.
    private void UpdateMap()
    {
        if (!_mapWindow.Visible)
            return;
        if (_mapShownZone != _world.Zone.Id)
            RefreshMap();
        (float x, float y, _) = ToRegion(_body.Position);
        _mapMarks.Player = MapPixel(x, y);
        // Where the arrow is, in numbers, for checking it against the picture.
        ushort heading = HeadingFromYaw(_bodyYaw);
        string pixel = _mapMarks.Player is Vector2 px ? $"   map {px.X * 256f / MapSize:0},{px.Y * 256f / MapSize:0} of 256" : "";
        _mapWhere.Text = $"x {x:0} y {y:0}   heading {heading} ({Compass(heading)}){pixel}";
        _mapMarks.Facing = _bodyYaw;
        _mapMarks.Goal = _walkPoint is Vector3 goal ? MapPixel(ToRegion(goal).Item1, ToRegion(goal).Item2) : null;
        _mapMarks.QueueRedraw();
    }

    // A region point on the shown picture (window pixels), or null when it is off this picture.
    private Vector2? MapPixel(float x, float y)
    {
        float scale = MapSize / 256f;
        if (!_mapRegion)
        {
            Zone z = _world.Zone;
            return new Vector2((x - z.X) / z.Width, (y - z.Y) / z.Height) * MapSize;
        }
        if (Zones.Find(_world.Position.RegionId, x, y) is not Zone zone
            || _regionMaps?.FirstOrDefault(m => m.Region == _world.Position.RegionId) is not RegionMap map
            || MapLayout.OnRegionMap(map, zone.Id, (x - zone.X) / zone.Width, (y - zone.Y) / zone.Height) is not var (px, py))
            return null;
        return new Vector2(px, py) * scale;
    }

    // A click on the map: the region point under it, then run there.
    private void OnMapClicked(InputEvent e)
    {
        if (e is not InputEventMouseButton { ButtonIndex: MouseButton.Left, Pressed: true } click)
            return;
        (float X, float Y)? target = null;
        if (!_mapRegion)
        {
            Zone z = _world.Zone;
            target = (z.X + click.Position.X / MapSize * z.Width, z.Y + click.Position.Y / MapSize * z.Height);
        }
        else if (_regionMaps?.FirstOrDefault(m => m.Region == _world.Position.RegionId) is RegionMap map)
        {
            Vector2 p = click.Position * (256f / MapSize);
            foreach (MapZone mz in map.Zones)
                if (p.X >= mz.Left && p.X < mz.Left + map.ZoneWidth && p.Y >= mz.Top && p.Y < mz.Top + map.ZoneHeight
                    && Zones.All.FirstOrDefault(z => z.Id == mz.Zone) is Zone zone)
                    target = (zone.X + (p.X - mz.Left) / map.ZoneWidth * zone.Width, zone.Y + (p.Y - mz.Top) / map.ZoneHeight * zone.Height);
        }
        _mapPicture.AcceptEvent();
        if (target is not var (tx, ty) || _dead || !_chase)
            return;
        GD.Print($"[map] run to x {tx:0} y {ty:0}");
        (_walkPoint, _walkUntil, _shotOnArrival, _walkTo) = (ToGodot(tx, ty, 0), _seconds + 600, false, 0);
    }

    // The arrow (you, pointing the way you face) and the goal's cross, drawn over the picture.
    private sealed partial class MapMarks : Control
    {
        public Vector2? Player, Goal;
        public float Facing;

        public override void _Draw()
        {
            if (Goal is Vector2 g)
            {
                var gold = new Color(1f, 0.85f, 0.25f);
                DrawLine(g + new Vector2(-6, -6), g + new Vector2(6, 6), gold, 3);
                DrawLine(g + new Vector2(-6, 6), g + new Vector2(6, -6), gold, 3);
                if (Player is Vector2 from)
                    DrawDashedLine(from, g, new Color(gold, 0.6f), 2, 6);
            }
            if (Player is not Vector2 p)
                return;
            // The body faces its -Z: Godot -Z is region -Y, up on the map; yaw turns it toward -X (left).
            Vector2 dir = new(-Mathf.Sin(Facing), -Mathf.Cos(Facing));
            Vector2 side = new(-dir.Y, dir.X);
            Vector2[] arrow = [p + dir * 11, p - dir * 7 + side * 7, p - dir * 3, p - dir * 7 - side * 7];
            DrawColoredPolygon(arrow, new Color(1f, 0.25f, 0.2f));
            DrawPolyline([.. arrow, arrow[0]], Colors.Black, 1.5f);
        }
    }
}
