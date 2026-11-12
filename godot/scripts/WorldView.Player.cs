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
        bool driving = _chase && !ChatInputActive && !_dead;   // the dead don't move until revived

        // A/D turn (keys); mouse-look also turns in chase mode, feeding _bodyYaw from _Input.
        float turnKeys = 0f;
        if (driving && KeyBindings.Held(ControlAction.TurnLeft)) turnKeys += 1f;
        if (driving && KeyBindings.Held(ControlAction.TurnRight)) turnKeys -= 1f;
        const float turnRate = 2.5f; // radians/s
        _bodyYaw += turnKeys * turnRate * (float)delta;

        float forward = 0f;
        bool forwardHeld = driving && KeyBindings.Held(ControlAction.Forward), backHeld = driving && KeyBindings.Held(ControlAction.Back);
        if (forwardHeld || backHeld)
        {
            _autoRun = false;   // auto-run (NumLock) runs until forward or back is pressed
            (_walkPoint, _walkTo, _follow) = (null, 0, 0);   // and a run to a map point or an NPC, or a follow, stops
        }
        if (forwardHeld || (_autoRun && driving)) forward += 1f;
        if (backHeld) forward -= 1f;

        // Q/E strafe left/right: sidestep without turning. Essential with the mouse-chase camera,
        // where you steer with the mouse and strafe to line up — forward + strafe is how you move.
        float strafe = 0f;
        if (driving && KeyBindings.Held(ControlAction.StrafeLeft)) strafe -= 1f;
        if (driving && KeyBindings.Held(ControlAction.StrafeRight)) strafe += 1f;

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
            _sitting = false;   // moving stands up (the server stands us up too: GamePlayer.OnPlayerMove)
        if (moving)
        {
            // Body faces -Z in Godot; forward is its -Z basis, strafe its +X basis (right). Both are
            // flattened to the ground plane, summed, then normalised so diagonal moves aren't faster.
            Vector3 move = _body.GlobalTransform.Basis.Z * -forward + _body.GlobalTransform.Basis.X * strafe;
            move.Y = 0;
            if (move.LengthSquared() > 0)
            {
                // Shift sprints (×4) to cross a ~15 km zone quickly, when the dev toggle allows it: only a
                // GM may (the server snaps a player back and then kicks them). The sent 0xA9 speed stays
                // the allowed run speed.
                float sprint = ClientSettings.Sprint && Input.IsKeyPressed(Key.Shift) ? 4f : 1f;
                float metresPerSec = MoveSpeed(forward) * RegionMetresPerUnit * sprint;
                // The zone's edge walls (WorldView.Bounds) slide or stop the step, as retail's do.
                Vector3 step = WallStep(_body.Position, _body.Position + move.Normalized() * metresPerSec * (float)delta);
                _body.Position = BoundedStep(_body.Position, step);
            }
        }

        // Space jumps from the ground.
        if (driving && !_airborne && _groundHit && KeyBindings.Held(ControlAction.Jump))
        {
            _verticalSpeed = JumpSpeed;
            _airborne = true;
            _jumping = true;
        }
        bool wasAirborne = _airborne;
        Ground(delta);

        // In the air, the jump clip (legs tucked, held to its end); on landing standing still, the land clip
        // (down out of the tuck) before the idle; landing on the move goes straight back to the run.
        // DAoC's normal pace is a run; backing up has its own clip.
        _landLeft -= (float)delta;
        if (wasAirborne && !_airborne && !moving)
            PlayOnce("land", LandTime);
        if (_dead)
            PlayOnce("death");   // held on its last frame: lying down
        else if (_swimming)
            PlayBodyClip(!moving ? "swim_idle" : forward < 0 ? "swim_back" : "swim");
        else if (_airborne)
            PlayOnce("jump");
        else if ((_quitting || _sitting) && !moving)
            Sit();
        else if (_landLeft <= 0 || moving)
        {
            // Sideways only: the figure's strafe clip (anims.csv 13/14), else the run.
            string side = strafe < 0 ? "strafe_left" : "strafe_right";
            PlayBodyClip(!moving ? Idle(_bodyAnim, _attacking, _ownCombatIdle) : forward < 0 ? "back"
                : forward == 0 && _bodyAnim?.HasAnimation(side) == true ? side : "run");
        }

        // Position send (the event-driven shape adapted from OfflineDAoC's _send_position, GPL-3.0; the
        // rates are retail 1.127's): every 0.2 s while anything is changing, every 2 s standing still,
        // and at once on the first change after standing still, and on a landing. (We compare against
        // what the keep-alive/World already holds, so "changed" also covers mouse-look turns from _Input.)
        _posLeft -= (float)delta;
        ushort heading = HeadingFromYaw(_bodyYaw);
        (float rx, float ry, float rz) = ToRegion(_body.Position);
        // What others see us doing (0xA9 state byte): swimming 0x04, in a jump 0x08, sitting 0x10; strafing left 0x40 or
        // right 0x80. A change goes out at once.
        byte state = (byte)((_swimming ? 0x04 : _jumping ? 0x08 : (_quitting || _sitting) && !moving ? 0x10 : 0)
            | (strafe < 0 ? 0x40 : strafe > 0 ? 0x80 : 0));
        bool changed = moving || _airborne || _landingSpeed > 0 || state != _stateSent
            || heading != _world.Position.Heading
            || Mathf.Abs(rx - _world.Position.X) > PositionEpsilon
            || Mathf.Abs(ry - _world.Position.Y) > PositionEpsilon
            || Mathf.Abs(rz - _world.Position.Z) > PositionEpsilon;

        if (_posLeft <= 0f || _landingSpeed > 0 || (changed && !_sendingChanges))
        {
            SendMove(moving ? (forward < 0 ? -MoveSpeed(forward) : MoveSpeed(forward)) : 0f, state);
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
    // Not yet: walls and obstacles (nothing stops the character sideways), the client's own
    // collision hulls ("collidee" nodes, which our converter drops), retail's gravity and jump values.
    private void Ground(double delta)
    {
        if (_dropsLeft > 0 && !_dead)
        {
            if (_airborne || !_groundHit || _dropPending > 0)
                _dropAgainAt = -1;   // the clock starts on landing, once the landing has gone out
            else if (_dropAgainAt < 0)
                _dropAgainAt = _seconds + 1;
            else if (_seconds >= _dropAgainAt)
                (_dropPending, _dropsLeft, _dropAgainAt) = (_dropAgain, _dropsLeft - 1, -1);
        }
        if (_dropPending > 0 && _groundHit)
        {
            _body.Position += new Vector3(0, _dropPending, 0);
            GD.Print($"[world] --drop: lifted {_dropPending:0.#} m");
            _dropPending = 0;
        }
        Vector3 at = _body.Position;
        float dt = (float)delta;
        float? floor = FloorBelow(at);
        if (floor is not null && Swim(at, floor.Value, dt))
            return;
        if (floor is null)
        {
            // Nothing below: under the ground? Back onto the terrain above.
            if (AnyTerrain && TerrainAbove(at) is float above)
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
            ray.CollisionMask = ~NotSolid;   // NPCs' pick shapes and water aren't floors
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
    private float _dropAgain;                 // --drops: the height of each further lift
    private int _dropsLeft;                   // --drops: lifts still to come
    private double _dropAgainAt = -1;         // when the next one goes (a second after landing)
    private bool _airborne;
    private bool _jumping;                    // airborne from a jump: 0xA9 state flag 0x08 (others see it)
    private float _verticalSpeed;             // metres per second, up positive
    private float _landingSpeed;              // a landing's impact speed (m/s), until it is reported
    private float _landLeft;                  // seconds of the land clip still to show before the idle
    private const float LandTime = 0.8f;      // Cat_JU_H_B is standing again by about 1 s (the rest holds)
    private const float StepUp = 1.5f;        // a rise walked up at once (a stair)
    private const float StepDown = 2f;        // a drop walked down at once
    private const float Gravity = 9.8f;       // ours; retail's value is unknown
    private const float JumpSpeed = 4.5f;     // about 1 m high; retail's value is unknown
    private const float MaxFallSpeed = 50f;
    private const float MaxFallSearch = 600f; // how far below a floor is looked for
    private const float MinFloorNormal = 0.6f;   // a surface up to ~53 degrees from flat is stood on

    // Region units per second for the way we're moving: running forward at the server's speed (0xB6); backing up
    // or strafing without going forward at retail's 85 of a 191 run (measured by daoc-re, 2026-11-05: backward
    // is sent as a negative speed), scaled with the server's speed.
    private float MoveSpeed(float forward) =>
        _swimming ? SwimUnitsPerSecond * (forward > 0 ? 1f : 0.85f)
        : (forward > 0 ? _runUnitsPerSecond : _runUnitsPerSecond * SlowUnits / 191f) * (_wading ? 0.5f : 1f);

    private const float SlowUnits = 85f;

    private byte _stateSent;   // the state byte of our last 0xA9
    private bool _sitting;     // the server sat us down (0xAD's sitting byte: /sit, the quit timer)

    private void SendMove(float speed, byte state)
    {
        _stateSent = state;
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
            _ = _session.MoveToAsync(rx, ry, rz, heading, speed, vertical, landed,
                stateFlags: state);
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
        // Another zone of the region: with streaming the zones there load around the new position; without
        // it (explicit zone files) the owner builds a view of that zone.
        if (moved.Zone.Id != _world.Zone.Id && !_streaming)
        {
            _draining = false;
            _note = $"   moving to {moved.Zone.Name}…";
            Relocated?.Invoke(moved);
            return;
        }
        _world = moved;
        // The camera goes with the character: a free camera left behind would look as if nothing moved.
        SetChase(true);
        PositionAndObjectId p = moved.Position;
        _body.Position = ToGodot(p.X, p.Y, p.Z);
        _lastJumpCheck = null;   // a move by the server, not a step through a doorway
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
        _camPivot.Rotation = new Vector3(_camPitch, _camOrbit, 0);
        // Lerp the arm's TARGET length toward the zoom distance; the SpringArm shortens from this
        // target on its own when it hits placed geometry. Floor the target at CamMin so even a wall
        // the arm catches can't collapse the camera into the pivot (the "inside the capsule" bug).
        float target = Mathf.Max(CamMin, _camDistance);
        _camArm.SpringLength = Mathf.Lerp(_camArm.SpringLength, target, Mathf.Min(1f, (float)delta * 10f));
    }

    // ----- Swimming (M8) -----
    // Retail's rules (daoc-re RE/58, static from 1.130; 40 units a metre, z is the feet): the character swims once
    // its feet are 0.9 m under the surface, and floats with them 1.0 m under (on ground between the two it swims
    // standing). Feet under the surface but not swimming is wading, at half the land speed. Swimming: no gravity,
    // no jump, the figure's "Swim Alt" clips, state flag 0x04 in each 0xA9; a fall into deep water takes no
    // landing (no fall damage). Swim speed: 85 units/s x (100 + w)/100 x the max speed %, w = 0xB6's water-speed
    // byte (realm ability Mastery of Water would add to it; capped at 100); backwards or sideways x 0.85.
    // Not yet: diving (W51). The surfaces are the zone's water bodies (water.glb, on WaterLayer).
    private bool _swimming, _wading;
    private ushort _speedPercent = 100;
    private byte _waterSpeed;
    private const float SwimStart = 0.9f;     // feet this far under the surface: swimming
    private const float FloatDepth = 1.0f;    // and floating with them this far under

    private float SwimUnitsPerSecond => 85f * (100 + Mathf.Min(100, (int)_waterSpeed)) / 100f * _speedPercent / 100f;

    private bool Swim(Vector3 at, float floor, float dt)
    {
        float? surface = WaterSurface(at);
        bool deep = surface is float s && floor <= s - SwimStart && at.Y <= s - SwimStart + 0.02f;
        bool wading = !deep && surface is float w && at.Y < w - 0.05f && floor < w;
        if (wading && !_wading)
            AppendChat("Wading: you move more slowly.", new Color(0.6f, 0.8f, 1f));
        _wading = wading;
        if (!deep)
        {
            if (_swimming)
                GD.Print("[world] out of the water");
            _swimming = false;
            return false;
        }
        if (!_swimming)
            GD.Print($"[world] swimming (water {surface!.Value - floor:0.0} m deep, {SwimUnitsPerSecond:0} units/s)");
        _swimming = true;
        float y = Mathf.Max(floor, surface!.Value - FloatDepth);
        // Falling in: the water takes it, no landing (and no fall damage).
        (_airborne, _jumping, _verticalSpeed, _landingSpeed) = (false, false, 0, 0);
        _body.Position = new Vector3(at.X, Mathf.MoveToward(at.Y, y, 3f * dt + 0.02f), at.Z);
        _groundHit = false;   // no jumping off the water
        return true;
    }

    // The water surface straight above or below the feet, if any.
    private float? WaterSurface(Vector3 at)
    {
        var ray = PhysicsRayQueryParameters3D.Create(at + new Vector3(0, 6f, 0), at - new Vector3(0, 30f, 0));
        ray.CollisionMask = WaterLayer;
        ray.HitBackFaces = true;
        Godot.Collections.Dictionary hit = GetWorld3D().DirectSpaceState.IntersectRay(ray);
        return hit.Count == 0 ? null : hit["position"].AsVector3().Y;
    }
}
