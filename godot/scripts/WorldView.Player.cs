// SPDX-License-Identifier: GPL-3.0-or-later
//
// WorldView: The player: free-fly and chase cameras, movement, ground and gravity, and position sends.

using DaocProtocol.Net;
using DaocProtocol.Packets;
using DaocProtocol.Session;
using DaocProtocol.World;
using Godot;

namespace EodClient;

public partial class WorldView
{
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
        // Sitting out the quit timer: walking or strafing stands up and calls the quit off. The server
        // decides on a change of X/Y (OpenDAoC PlayerMovementComponent.UpdatePosition → OnPlayerMove),
        // so a jump in place or a turn doesn't cancel, here or there.
        if (_quitting && moving)
        {
            _quitting = false;
            _note = "   standing up: quit cancelled";
            QuitInterrupted?.Invoke();
        }
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

        // Space jumps from the ground.
        if (driving && !_airborne && _groundHit && Input.IsKeyPressed(Key.Space))
        {
            _verticalSpeed = JumpSpeed;
            _airborne = true;
            _jumping = true;
        }
        Ground(delta);

        // DAoC's normal pace is a run; backing up has its own clip.
        if (_quitting && !moving)
            Sit();
        else
            PlayBodyClip(!moving ? Idle(_bodyAnim, _attacking, _ownCombatIdle) : forward < 0 ? "back" : "run");

        // Position send (the event-driven shape adapted from OfflineDAoC's _send_position, GPL-3.0; the
        // rates are retail 1.127's): every 0.2 s while anything is changing, every 2 s standing still,
        // and at once on the first change after standing still, and on a landing. (We compare against
        // what the keep-alive/World already holds, so "changed" also covers mouse-look turns from _Input.)
        _posLeft -= (float)delta;
        ushort heading = HeadingFromYaw(_bodyYaw);
        (float rx, float ry, float rz) = ToRegion(_body.Position);
        bool changed = moving || _airborne || _landingSpeed > 0
            || heading != _world.Position.Heading
            || Mathf.Abs(rx - _world.Position.X) > PositionEpsilon
            || Mathf.Abs(ry - _world.Position.Y) > PositionEpsilon
            || Mathf.Abs(rz - _world.Position.Z) > PositionEpsilon;

        if (_posLeft <= 0f || _landingSpeed > 0 || (changed && !_sendingChanges))
        {
            SendMove(moving ? WalkUnitsPerSecond : 0f);
            _posLeft = changed ? MovingSendInterval : IdleSendInterval;
            _sendingChanges = changed;
        }
    }

    // Gravity (roadmap M8 movement). The character stands on the highest floor below a point a step above
    // its feet: terrain or a model's surface (street, stair, bridge, keep wall, roof) that faces up enough to
    // stand on, never leaves (alpha cut-out) or a wall. A floor within a step (up 1.5 m, down 2 m) is stood on
    // at once, walking up stairs and down slopes; one further below is fallen to under gravity and landed on,
    // and Space jumps. The landing is reported to the server with the impact speed, which charges fall
    // damage (0xA9). With no floor below (under the terrain after a bad position, or off the models) the
    // character is put back on the terrain above it, or keeps its height.
    // Not yet: walls and obstacles (nothing stops the character sideways), swimming, the client's own
    // collision hulls ("collidee" nodes, which our converter drops), retail's gravity and jump values.
    private void Ground(double delta)
    {
        if (_dropPending > 0 && _groundHit)
        {
            _body.Position += new Vector3(0, _dropPending, 0);
            GD.Print($"[world] --drop: lifted {_dropPending:0.#} m");
            _dropPending = 0;
        }
        Vector3 at = _body.Position;
        float dt = (float)delta;
        float? floor = FloorBelow(at);
        if (floor is null)
        {
            // Nothing below: under the ground? Back onto the terrain above.
            if (_terrain is not null && TerrainAbove(at) is float above)
                _body.Position = new Vector3(at.X, above, at.Z);
            _airborne = false;
            _verticalSpeed = 0;
            _groundHit = false;
            return;
        }
        if (_airborne || _verticalSpeed > 0 || at.Y - floor.Value > StepDown)
        {
            _airborne = true;
            _verticalSpeed = Mathf.Max(_verticalSpeed - Gravity * dt, -MaxFallSpeed);
            float y = at.Y + _verticalSpeed * dt;
            if (y <= floor.Value)
            {
                y = floor.Value;
                _landingSpeed = -_verticalSpeed;     // reported on the next position send
                if (_landingSpeed > 5f)
                    GD.Print($"[world] landed at {_landingSpeed:0.0} m/s ({_landingSpeed / RegionMetresPerUnit:0} units/s)");
                _verticalSpeed = 0;
                _airborne = false;
                _jumping = false;
            }
            _body.Position = new Vector3(at.X, y, at.Z);
        }
        else
        {
            _body.Position = new Vector3(at.X, floor.Value, at.Z);
            _verticalSpeed = 0;
        }
        _groundHit = !_airborne;
    }

    // The highest standable surface below a point a step above the feet: a ray down that passes walls,
    // steep faces and alpha cut-out surfaces (leaves) to the first floor.
    private float? FloorBelow(Vector3 at)
    {
        var exclude = new Godot.Collections.Array<Rid>();
        foreach (Node n in _body.FindChildren("*", "CollisionObject3D", true, false))
            exclude.Add(((CollisionObject3D)n).GetRid());
        Vector3 from = at + new Vector3(0, StepUp, 0), to = at - new Vector3(0, MaxFallSearch, 0);
        for (int tries = 0; tries < 12; tries++)
        {
            var ray = PhysicsRayQueryParameters3D.Create(from, to);
            ray.Exclude = exclude;
            ray.HitBackFaces = true;
            Godot.Collections.Dictionary hit = GetWorld3D().DirectSpaceState.IntersectRay(ray);
            if (hit.Count == 0)
                return null;
            Vector3 point = hit["position"].AsVector3();
            Vector3 normal = hit["normal"].AsVector3();
            GodotObject collider = hit["collider"].AsGodotObject();
            bool terrain = collider is CollisionObject3D co && (co.CollisionLayer & TerrainLayer) != 0;
            if (terrain || (Mathf.Abs(normal.Y) >= MinFloorNormal && !CutOut(collider)))
                return point.Y;
            exclude.Add(hit["rid"].AsRid());
        }
        return null;
    }

    // A model surface drawn with an alpha cut-out (foliage): its collider's mesh uses alpha scissor.
    private static bool CutOut(GodotObject collider) =>
        collider is Node n && n.GetParent() is MeshInstance3D mi
        && Enumerable.Range(0, mi.GetSurfaceOverrideMaterialCount()).Any(i =>
            (mi.GetActiveMaterial(i) as BaseMaterial3D)?.Transparency is BaseMaterial3D.TransparencyEnum.AlphaScissor
                or BaseMaterial3D.TransparencyEnum.Alpha);

    // The terrain surface above a point (the character got under the ground), or null.
    private float? TerrainAbove(Vector3 at)
    {
        var ray = PhysicsRayQueryParameters3D.Create(new Vector3(at.X, 400f, at.Z), at);
        ray.CollisionMask = TerrainLayer;
        Godot.Collections.Dictionary hit = GetWorld3D().DirectSpaceState.IntersectRay(ray);
        return hit.Count > 0 ? hit["position"].AsVector3().Y : null;
    }

    private float _dropPending;               // --drop: metres to lift the character after spawning
    private bool _airborne;
    private bool _jumping;                    // airborne from a jump: 0xA9 state flag 0x08 (others see it)
    private float _verticalSpeed;             // metres per second, up positive
    private float _landingSpeed;              // a landing's impact speed (m/s), until it is reported
    private const float StepUp = 1.5f;        // a rise walked up at once (a stair)
    private const float StepDown = 2f;        // a drop walked down at once
    private const float Gravity = 9.8f;       // ours; retail's value is unknown
    private const float JumpSpeed = 4.5f;     // about 1 m high; retail's value is unknown
    private const float MaxFallSpeed = 50f;
    private const float MaxFallSearch = 600f; // how far below a floor is looked for
    private const float MinFloorNormal = 0.6f;   // a surface up to ~53 degrees from flat is stood on

    private void SendMove(float speed)
    {
        (float rx, float ry, float rz) = ToRegion(_body.Position);
        ushort heading = HeadingFromYaw(_bodyYaw);
        // Reflect it locally at once so the overlay and keep-alive see the new position without
        // waiting for the async send; MoveToAsync sets the same values on the session.
        _world = _world with { Position = _world.Position with { X = rx, Y = ry, Z = rz, Heading = heading } };
        // While airborne the vertical speed goes along; the landing update carries the impact speed and
        // the landed bit, from which the server charges fall damage. Region units per second, falling negative.
        bool landed = _landingSpeed > 0;
        float vertical = (landed ? -_landingSpeed : _airborne ? _verticalSpeed : 0f) / RegionMetresPerUnit;
        _landingSpeed = 0;
        if (_session is not null)
            _ = _session.MoveToAsync(rx, ry, rz, heading, speed, vertical, landed, stateFlags: _jumping ? (byte)0x08 : (byte)0);
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
}
