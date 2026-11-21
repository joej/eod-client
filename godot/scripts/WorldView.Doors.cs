// SPDX-License-Identifier: GPL-3.0-or-later
//
// WorldView: doors (roadmap M11, dungeon doors). A converted model's door is a glTF animation named after its node
// ("door001") that moves the door's parts (nodes "door001__<part>") from closed to open (GltfExporter); a dungeon's
// doors.csv names each door's id and the zone.csv row of the model it belongs to (DungeonDoors). How retail drives them
// (daoc-re's doors note, RE/80): S→C 0x99 opens (plays forward) or closes (plays backward) a door; a click on a door
// sends C→S 0x99 with request 1; in a dungeon a door stops blocking as soon as it starts opening and blocks again once
// fully closed. OpenDAoC opens a door it doesn't know (nearly every dungeon door) and closes it after a while.
// Keep doors (WorldView.Keeps) go through the same path by their own ids. Not built: the door sound (0x99's sound row), a
// mid-frame hold, four-state doors, outdoor doors.

using Godot;

namespace EodClient;

public partial class WorldView
{
    // How far from the character a door can be clicked, region units (ours: OpenDAoC accepts twice its pickup distance).
    private const float DoorReachUnits = 512f;

    // Doors whose collision comes back once a close has played out: the parts' bodies and when (view seconds).
    private readonly System.Collections.Generic.Dictionary<uint, double> _doorsBlockingAt = new();

    // After a zone's objects are placed: each doors.csv door's parts tagged with its id (for clicks), and kept by id.
    private static void TagDoors(ZoneParts parts, Node3D?[] placedRows)
    {
        string csv = parts.Dir.PathJoin("doors.csv");
        if (parts.Dir.Length == 0 || !Godot.FileAccess.FileExists(csv))
            return;
        foreach (DaocAssets.Zone.DungeonDoor door in DaocAssets.Zone.DungeonDoors.ReadCsv(Godot.FileAccess.GetFileAsString(csv)))
        {
            if (door.Row < 0 || door.Row >= placedRows.Length || placedRows[door.Row] is not Node3D model)
                continue;
            int tagged = 0;
            foreach (Node3D part in DoorParts(model, door.Door))
            {
                part.SetMeta("door_id", (long)door.Id);
                tagged++;
            }
            if (tagged == 0 || FindAnimationPlayer(model) is not AnimationPlayer imported || !imported.HasAnimation(door.Door))
                continue;
            // A player of its own for each door: one player runs one animation at a time, and a model may have several
            // doors (Darkspire's room 0: door001 and door002) that open apart.
            string own = DoorPlayerPrefix + door.Door;
            if (model.GetNodeOrNull<AnimationPlayer>(own) is null)
            {
                var player = new AnimationPlayer { Name = own };
                player.AddAnimationLibrary("", imported.GetAnimationLibrary(""));
                model.AddChild(player);
            }
            parts.Doors[door.Id] = (model, door.Door);
        }
        if (parts.Doors.Count > 0)
            GD.Print($"[world] zone {parts.Id}: {parts.Doors.Count} door(s)");
    }

    // A door's moving parts in a placed model: the nodes named "<door>__…".
    private static System.Collections.Generic.IEnumerable<Node3D> DoorParts(Node model, string door)
    {
        string prefix = door + DaocAssets.Nif.GltfExporter.DoorPartSeparator;
        var stack = new System.Collections.Generic.Stack<Node>();
        stack.Push(model);
        while (stack.Count > 0)
        {
            Node n = stack.Pop();
            if (n is Node3D n3 && n.Name.ToString().StartsWith(prefix, System.StringComparison.OrdinalIgnoreCase))
                yield return n3;
            foreach (Node c in n.GetChildren())
                stack.Push(c);
        }
    }

    private bool TryDoor(uint id, out Node3D model, out string door)
    {
        if (_keepDoors.TryGetValue(id, out var k) && IsInstanceValid(k.Model))
        {
            (model, door) = k;
            return true;
        }
        foreach (ZoneParts z in _zones.Values)
            if (z.Doors.TryGetValue(id, out var d))
            {
                (model, door) = d;
                return true;
            }
        (model, door) = (null!, "");
        return false;
    }

    // S→C 0x99: play the door's animation forward (open) or backward (close); 0xFF toggles.
    private void OnDoorChanged(DaocProtocol.Packets.DoorState state)
    {
        if (!TryDoor(state.DoorId, out Node3D model, out string door))
        {
            GD.Print($"[world] door {state.DoorId} {(state.IsOpen ? "opens" : "closes")}: not one of ours");
            return;
        }
        if (DoorPlayer(model, door) is not AnimationPlayer player || !player.HasAnimation(door))
            return;
        bool open = state.State == DaocProtocol.Packets.DoorState.Toggle ? !IsDoorOpen(player, door) : state.IsOpen;
        if (_keepDoors.ContainsKey(state.DoorId))
        {
            if (open)
                _keepDoorsOpen.Add(state.DoorId);   // kept so a rebuilt piece stands open again (WorldView.Keeps)
            else
                _keepDoorsOpen.Remove(state.DoorId);
        }
        if (open)
        {
            player.Play(door);
            SetDoorBlocking(model, door, false);   // it stops blocking as soon as it starts opening
            _doorsBlockingAt.Remove(state.DoorId);
        }
        else
        {
            player.PlayBackwards(door);
            Animation anim = player.GetAnimation(door);
            double left = player.CurrentAnimation == door ? player.CurrentAnimationPosition : anim.Length;
            _doorsBlockingAt[state.DoorId] = _seconds + left;   // and blocks again once fully closed
        }
        DoorSound(state, model, open);
        GD.Print($"[world] door {state.DoorId} ({door}) {(open ? "opens" : "closes")}");
    }

    private static bool IsDoorOpen(AnimationPlayer player, string door) =>
        player.CurrentAnimation == door && player.GetPlayingSpeed() >= 0 && player.CurrentAnimationPosition > 0;

    // Closed doors block again (called every frame).
    private void UpdateDoors()
    {
        if (_doorsBlockingAt.Count == 0)
            return;
        foreach ((uint id, double at) in _doorsBlockingAt.ToArray())
            if (_seconds >= at)
            {
                _doorsBlockingAt.Remove(id);
                if (TryDoor(id, out Node3D model, out string door))
                    SetDoorBlocking(model, door, true);
            }
    }

    // A door's parts' collision on or off (the bodies MakeSolid gave them keep their layers in metadata).
    private static void SetDoorBlocking(Node3D model, string door, bool blocking)
    {
        foreach (Node3D part in DoorParts(model, door))
        {
            var stack = new System.Collections.Generic.Stack<Node>();
            stack.Push(part);
            while (stack.Count > 0)
            {
                Node n = stack.Pop();
                if (n is CollisionObject3D body)
                {
                    if (!body.HasMeta("door_layer"))
                        body.SetMeta("door_layer", (long)body.CollisionLayer);
                    body.CollisionLayer = blocking ? (uint)body.GetMeta("door_layer").AsInt64() : 0;
                }
                foreach (Node c in n.GetChildren())
                    stack.Push(c);
            }
        }
    }

    private const string DoorPlayerPrefix = "door_player_";

    // A door's own player (TagDoors), else the model's imported one.
    private static AnimationPlayer? DoorPlayer(Node model, string door) =>
        model.GetNodeOrNull<AnimationPlayer>(DoorPlayerPrefix + door) ?? FindAnimationPlayer(model);

    private static AnimationPlayer? FindAnimationPlayer(Node model)
    {
        foreach (Node c in model.GetChildren())
            if (c is AnimationPlayer p)
                return p;
        return null;
    }

    // The door under a screen point, if it is within reach and nothing solid stands in front of it.
    private uint? PickDoor(Vector2 screen)
    {
        Vector3 from = _camera.ProjectRayOrigin(screen);
        var ray = PhysicsRayQueryParameters3D.Create(from, from + _camera.ProjectRayNormal(screen) * 400f);
        ray.CollisionMask = ~NotSolid | WallLayer;
        ray.HitBackFaces = true;
        Godot.Collections.Dictionary hit = GetWorld3D().DirectSpaceState.IntersectRay(ray);
        if (hit.Count == 0 || hit["collider"].AsGodotObject() is not Node collider)
            return null;
        if (hit["position"].AsVector3().DistanceTo(_body.Position) > DoorReachUnits * RegionMetresPerUnit)
            return null;
        for (Node? n = collider; n is not null; n = n.GetParent())
            if (n.HasMeta("door_id"))
                return (uint)n.GetMeta("door_id").AsInt64();
        return null;
    }

    // C→S 0x99 for a clicked door (the server answers with 0x99 to everyone near it).
    private void RequestDoor(uint id)
    {
        GD.Print($"[world] door {id}: asking to open it");
        if (_session is null)
        {
            OnDoorChanged(new DaocProtocol.Packets.DoorState(id, DaocProtocol.Packets.DoorState.Toggle, 0, 0xFF));   // offline: just play it
            return;
        }
        _ = Send(() => _session.RequestDoorAsync(id), "door");
    }
}
