// SPDX-License-Identifier: GPL-3.0-or-later
//
// WorldView: Zone streaming (roadmap M8). The zones of the region near the player are loaded and dropped
// as the player moves, so walking over a zone border needs no loading screen: within a region the server
// sends nothing at a border (the client reports its zone in 0xA9, ClientSession.MoveToAsync), so the
// client loads the next zone on its own. A zone is built in steps on the main thread, a few milliseconds
// per frame (ZoneSteps), and added to the tree when done. Region changes still rebuild the view.

using DaocProtocol.World;
using Godot;

namespace EodClient;

public partial class WorldView
{
    private readonly System.Collections.Generic.Dictionary<ushort, ZoneParts> _zones = new();
    private readonly ModelCache _zoneModels = new();
    private bool _streaming;          // zones come from ZonesDir/zoneNNN, so neighbours can be found
    // The zone being built: its parts, the steps still to run, and the time spent so far.
    private (ZoneParts Parts, System.Collections.Generic.IEnumerator<bool> Steps, System.Diagnostics.Stopwatch Spent)? _building;
    // Milliseconds of building per frame: a frame at 60 fps is about 16.
    private const double BuildBudgetMs = 5;
    private const double LoadingBudgetMs = 40;   // behind a loading screen nobody sees the frame rate

    /// <summary>Set while a loading screen covers the view: zones build in bigger slices, one after another.</summary>
    public bool BehindLoadingScreen { get; set; }

    /// <summary>How far the zones around the player are built (0 to 1), and whether all of them are.</summary>
    public double ZoneLoadProgress
    {
        get
        {
            int pending = _wanted + (Building ? 1 : 0);
            return _zones.Count + pending == 0 ? 1 : (double)_zones.Count / (_zones.Count + pending);
        }
    }

    public bool ZonesReady => _wanted == 0 && !Building;
    private double _nextStreamCheck;
    private ushort _skyZone;          // the zone whose sky and fog are shown

    // Load a zone when it is this close beyond the view distance, drop it this far beyond (metres): the
    // gap stops a zone at the edge from loading and dropping as the player walks to and fro.
    private const float StreamAhead = 150f, StreamDrop = 400f;
    // Without a zone view distance (no sky.csv), how far to stream (metres).
    private const float StreamDefaultView = 600f;

    // Zones that are wanted but not yet in the tree (shots wait for none, WaitForZones).
    private int _wanted;
    private bool Building => _building is not null;
    private double _worstStepMs;      // the longest single build step of the zone being built (a stutter)

    /// <summary>The converted folder of zone <paramref name="id"/>, or null when it isn't converted.</summary>
    private string? ConvertedZoneDir(ushort id)
    {
        if (string.IsNullOrEmpty(ZonesDir))
            return null;
        string dir = System.IO.Path.Combine(ZonesDir, $"zone{id:D3}");
        return System.IO.File.Exists(System.IO.Path.Combine(dir, "zone.csv")) ? dir : null;
    }

    // Adds a built zone to the view, applying the dev overlay's current layer switches.
    private void AddZone(ZoneParts parts)
    {
        if (parts.Terrain is not null) parts.Terrain.Visible = _terrainOn;
        if (parts.Objects is not null) parts.Objects.Visible = _fixturesOn;
        if (parts.Water is not null) parts.Water.Visible = _waterOn;
        if (parts.JumpMarkers is not null) parts.JumpMarkers.Visible = _jumpsVisible;
        _zones[parts.Id] = parts;
        var watch = System.Diagnostics.Stopwatch.StartNew();
        AddChild(parts.Root);
        GD.Print($"[stream] zone {parts.Id} in: {parts.Seconds:0.0} s of building, added in {watch.Elapsed.TotalMilliseconds:0} ms " +
            $"({parts.Placed} objects, {_zones.Count} zone(s) loaded)");
    }

    // The first zone (where the view opens) is built at once; streaming starts from it when it is a
    // zone of the converted zones folder.
    private ZoneParts AddFirstZone()
    {
        ZoneParts first = BuildZoneParts(_world.Zone.Id, _zonePath, _terrainPath, _libraryPath, _zoneModels);
        AddZone(first);
        _skyZone = first.Id;
        _streaming = first.Dir.Length > 0 && ConvertedZoneDir(first.Id) is string dir
            && System.IO.Path.GetFullPath(dir) == System.IO.Path.GetFullPath(first.Dir);
        return first;
    }

    // Metres from a zone to the nearer of the character and, while the free camera flies, the camera: zones
    // stream in around both, so a move by the server isn't lost on a camera left behind (2026-11-08: jumps in
    // the tutorial loaded nothing, perhaps for that reason).
    private float StreamDistance(Zone zone) =>
        _chase ? DistanceTo(zone, _body.Position) : Mathf.Min(DistanceTo(zone, _body.Position), DistanceTo(zone, _camera.GlobalPosition));

    private ushort _streamHere;

    // Metres from a point (Godot X/Z) to a zone's rectangle; 0 inside.
    private static float DistanceTo(Zone zone, Vector3 at)
    {
        (float x, float y, _) = ToRegion(at);
        float dx = Mathf.Max(Mathf.Max(zone.X - x, 0), x - (zone.X + zone.Width));
        float dy = Mathf.Max(Mathf.Max(zone.Y - y, 0), y - (zone.Y + zone.Height));
        return Mathf.Sqrt(dx * dx + dy * dy) * RegionMetresPerUnit;
    }

    // Called every frame: builds for up to BuildBudgetMs, and twice a second (or when forced) decides which
    // zones to load and drop, and follows the player into another zone's sky.
    private void Stream(double delta, bool force = false)
    {
        BuildSome();
        FollowZone();
        if (!_streaming)
            return;
        _nextStreamCheck -= delta;
        if (_nextStreamCheck > 0 && !force && !(BehindLoadingScreen && !Building))
            return;
        _nextStreamCheck = 0.5;

        Vector3 at = _body.Position;
        float view = _zoneClip > 0 ? _zoneClip * ClientSettings.ViewDistances[_viewSetting].Multiple : StreamDefaultView;
        ushort region = _world.Position.RegionId;
        (float rx, float ry, _) = ToRegion(at);
        ushort here = Zones.Find(region, rx, ry)?.Id ?? _world.Zone.Id;
        if (here != _streamHere)
        {
            _streamHere = here;
            GD.Print($"[stream] character in zone {here} (camera {(_chase ? "following" : "free")})");
        }
        int wanted = 0;
        // Nearest first, so the zone the player is walking into comes before one further off.
        foreach (Zone zone in Zones.All.Where(z => z.RegionId == region).OrderBy(StreamDistance))
        {
            float d = StreamDistance(zone);
            bool loaded = _zones.ContainsKey(zone.Id) || _building?.Parts.Id == zone.Id;
            if (!loaded && d <= view + StreamAhead && ConvertedZoneDir(zone.Id) is string dir)
            {
                wanted++;
                if (!Building)   // one build at a time
                    StartLoad(zone.Id, dir);
            }
            else if (_zones.ContainsKey(zone.Id) && zone.Id != here && zone.Id != _world.Zone.Id && d > view + StreamDrop)
                DropZone(zone.Id);
        }
        _wanted = wanted;
    }

    private void StartLoad(ushort id, string dir)
    {
        string csv = System.IO.Path.Combine(dir, "zone.csv"), terrain = System.IO.Path.Combine(dir, "terrain.glb");
        GD.Print($"[stream] zone {id} loading");
        ZoneParts parts = NewZoneParts(id, csv, terrain);
        _building = (parts, ZoneSteps(parts, csv, terrain, _libraryPath, _zoneModels).GetEnumerator(), new System.Diagnostics.Stopwatch());
    }

    // Runs the zone build for up to BuildBudgetMs; adds the zone when its last step is done.
    private void BuildSome()
    {
        if (_building is not var (parts, steps, spent))
            return;
        var frame = System.Diagnostics.Stopwatch.StartNew();
        spent.Start();
        bool more;
        try
        {
            double before = 0;
            while ((more = steps.MoveNext()))
            {
                double now = frame.Elapsed.TotalMilliseconds;
                _worstStepMs = System.Math.Max(_worstStepMs, now - before);
                before = now;
                if (now >= (BehindLoadingScreen ? LoadingBudgetMs : BuildBudgetMs))
                    break;
            }
        }
        catch (System.Exception e)
        {
            // An empty zone goes in instead, so it isn't retried every half second.
            GD.PrintErr($"[stream] zone {parts.Id} failed: {e.Message}");
            parts.Root.Free();
            parts = NewZoneParts(parts.Id, null, null);
            more = false;
        }
        spent.Stop();
        if (more)
            return;
        steps.Dispose();
        _building = null;
        parts.Seconds = spent.Elapsed.TotalSeconds;
        GD.Print($"[stream] zone {parts.Id}: longest step {_worstStepMs:0} ms");
        _worstStepMs = 0;
        AddZone(parts);
    }

    private void DropZone(ushort id)
    {
        if (!_zones.Remove(id, out ZoneParts? parts))
            return;
        parts.Root.QueueFree();
        GD.Print($"[stream] zone {id} dropped ({_zones.Count} zone(s) loaded)");
    }

    // The zone the player is in decides the sky and fog. Online the session knows it (the zone it reports
    // in 0xA9); offline it is the zone under the character.
    private void FollowZone()
    {
        Zone? zone = _session?.World?.Zone;
        if (_session is null)
        {
            (float x, float y, _) = ToRegion(_body.Position);
            zone = Zones.Find(_world.Position.RegionId, x, y);
        }
        if (zone is null || zone.RegionId != _world.Position.RegionId)
            return;
        if (zone.Id != _world.Zone.Id)
        {
            GD.Print($"[stream] entered {zone.Name} (zone {zone.Id})");
            _world = _session?.World ?? _world with { Zone = zone };
        }
        if (zone.Id != _skyZone && _zones.TryGetValue(zone.Id, out ZoneParts? parts) && parts.Dir.Length > 0)
        {
            _skyZone = zone.Id;
            if (ApplySky(parts.Dir, "clear", "day") is string sky)
            {
                _skyName = sky;
                ApplyViewDistance(_viewSetting);
            }
        }
    }

    /// <summary>Waits (frames) until every wanted zone near the stream centre is in, for shots.</summary>
    private async System.Threading.Tasks.Task WaitForZones(double timeoutSeconds = 60)
    {
        double until = _seconds + timeoutSeconds;
        Stream(0, force: true);
        while ((_wanted > 0 || Building) && _seconds < until)
        {
            await ToSignal(GetTree(), SceneTree.SignalName.ProcessFrame);
            Stream(0, force: true);
        }
    }

    private bool AnyTerrain => _zones.Values.Any(z => z.Terrain is not null);
}
