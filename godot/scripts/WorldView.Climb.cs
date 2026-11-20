// SPDX-License-Identifier: GPL-3.0-or-later
//
// WorldView: climbing (roadmap M9; daoc-re's climbing note, 2026-11-17). A model's climb volumes (shapes under its
// "climb…" nodes; GltfExporter.ClimbPrefix) are walls on the climb layer too. Walking into one, or catching one while
// falling faster than 150 units/s, starts a climb; each frame a level ray from 8 units above the feet along the facing
// must hit a climb surface within 31 units (the nearest wall, or a climb surface up to 4 units behind it), and holds the
// character 18 units off it; otherwise the climb ends (the top edge passes under the ray, turning away, strafing off).
// Forward climbs up and back down at 85 × min(1, speed%/100) units/s; with neither the climb runs down to a stop at
// 550 units/s². No gravity and no jump. Strafing slides along the surface. Reaching a floor ends it only with back held.
// Others see posture 7 (0xA9 state 0x1C) and the vertical speed. "sclimb…" volumes need the Climb Walls ability or
// effect 7092, neither of which a 1.65 server gives, so they stay plain walls here.
// Ours: the walk-in start also asks for the facing ray, so a box brushing a ladder sideways doesn't start and end a climb
// in one frame; strafing moves at once (retail accelerates at 62 units/s² while climbing).

using DaocProtocol.Packets;
using Godot;

namespace EodClient;

public partial class WorldView
{
    private const uint ClimbLayer = 1 << 5;   // collision layer 6: climb volumes (also on the wall layer)
    private const float ClimbSpeed = 85 * RegionMetresPerUnit;
    private const float ClimbStop = 550 * RegionMetresPerUnit;           // the climb's run-down with no key held, per s²
    private const float ClimbCatchSpeed = 150 * RegionMetresPerUnit;     // a fall faster than this catches a climb surface
    private const float ClimbRayHeight = 8 * RegionMetresPerUnit;
    private const float ClimbReach = 31 * RegionMetresPerUnit;
    private const float ClimbHold = 18 * RegionMetresPerUnit;
    private const float ClimbBehind = 4 * RegionMetresPerUnit;
    private const byte ClimbState = 0x1C;   // swim + jump + sit: posture 7, climbing

    private bool _climbing;
    private bool _wallBlocked;   // the last WallStep stopped at a wall

    private float ClimbUnitsFactor => Mathf.Min(1f, _speedPercent / 100f);

    // How far ahead along the facing the climb surface is, from 8 units above the feet, or null when the ray's nearest
    // wall within 31 units isn't one (and no climb surface lies within 4 units behind it).
    private float? ClimbAhead()
    {
        Vector3 facing = -_body.GlobalTransform.Basis.Z;
        facing.Y = 0;
        if (facing.LengthSquared() < 1e-6f)
            return null;
        facing = facing.Normalized();
        Vector3 from = _body.Position + new Vector3(0, ClimbRayHeight, 0);
        PhysicsDirectSpaceState3D space = GetWorld3D().DirectSpaceState;
        var ray = PhysicsRayQueryParameters3D.Create(from, from + facing * ClimbReach, WallLayer);
        ray.HitBackFaces = true;
        Godot.Collections.Dictionary hit = space.IntersectRay(ray);
        if (hit.Count == 0)
            return null;
        float d = (hit["position"].AsVector3() - from).Length();
        if (IsClimb(hit["collider"].AsGodotObject()))
            return d;
        var behind = PhysicsRayQueryParameters3D.Create(from, from + facing * Mathf.Min(ClimbReach, d + ClimbBehind), ClimbLayer);
        behind.HitBackFaces = true;
        hit = space.IntersectRay(behind);
        return hit.Count > 0 ? (hit["position"].AsVector3() - from).Length() : null;
    }

    private static bool IsClimb(GodotObject? o) => o is CollisionObject3D c && (c.CollisionLayer & ClimbLayer) != 0;

    private void StartClimb(string how)
    {
        _climbing = true;
        (_airborne, _jumping, _verticalSpeed, _landingSpeed, _groundHit) = (false, false, 0, 0, false);
        AppendChat("You start climbing.", Colors.White);
        GD.Print($"[world] climbing ({how}) at {ToRegion(_body.Position)}");
    }

    private void EndClimb(string why)
    {
        _climbing = false;
        _verticalSpeed = 0;
        if (_bodyAnim is not null)
            _bodyAnim.SpeedScale = 1f;
        AppendChat("You are no longer climbing.", Colors.White);
        GD.Print($"[world] no longer climbing ({why}) at {ToRegion(_body.Position)}");
    }

    // Starts a climb when the step was stopped by a climb surface ahead, or a fast fall passes one.
    private void TryStartClimb()
    {
        if (_climbing || _dead || _swimming)
            return;
        bool walkedIn = _wallBlocked;
        bool caught = _airborne && -_verticalSpeed > ClimbCatchSpeed;
        if ((walkedIn || caught) && ClimbAhead() is not null)
            StartClimb(walkedIn ? "walked into it" : "caught it falling");
    }

    // One frame of climbing, in place of the ground-following: up or down by the keys, held off the surface.
    private void Climb(double delta, bool forwardHeld, bool backHeld)
    {
        float dt = (float)delta;
        if (_dead)
        {
            EndClimb("dead");
            return;
        }
        if (ClimbAhead() is not float ahead)
        {
            EndClimb(forwardHeld ? "over the top" : "nothing to climb ahead");
            return;
        }
        float f = ClimbUnitsFactor;
        _verticalSpeed = forwardHeld && !backHeld ? ClimbSpeed * f
            : backHeld && !forwardHeld ? -ClimbSpeed * f
            : Mathf.MoveToward(_verticalSpeed, 0, ClimbStop * dt);
        Vector3 facing = -_body.GlobalTransform.Basis.Z;
        facing.Y = 0;
        Vector3 at = _body.Position + facing.Normalized() * (ahead - ClimbHold);
        Vector3 to = at + new Vector3(0, _verticalSpeed * dt, 0);
        if (_verticalSpeed > 0 && CeilingBlocks(at, to))
            (to, _verticalSpeed) = (at, 0);
        if (_verticalSpeed <= 0 && FloorBelow(at) is float floor && to.Y <= floor)
        {
            to.Y = floor;
            _verticalSpeed = 0;
            if (backHeld)
            {
                _body.Position = to;
                EndClimb("at the bottom");
                _groundHit = true;
                _lastStood = to;
                return;
            }
        }
        _body.Position = to;
        _groundHit = false;   // no jumping off a climb
    }

    // The climb clip (anims slot 10, "ladder"), at its own rate while moving up or down and held while still (daoc-re's
    // stand-in: the client's rate isn't known).
    private void PlayClimbClip()
    {
        if (_bodyAnim is null)
            return;
        PlayBodyClip(_bodyAnim.HasAnimation("ladder") ? "ladder" : "idle");
        _bodyAnim.SpeedScale = _verticalSpeed != 0 ? 1f : 0f;
    }
}
