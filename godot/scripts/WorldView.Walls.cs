// SPDX-License-Identifier: GPL-3.0-or-later
//
// WorldView: walls (roadmap M9, solid world). The models' collision volumes ("collidee", "collider", "collision"
// nodes, exported as eodcollide_ meshes) are bodies on the wall layer, never drawn. Each horizontal step of the
// character is cast against them as retail's player box (daoc-re RE/72, W55): 28 x 28 units wide, turning with the
// character's facing, 48 units tall, from 0.8 to 2.0 m above the feet. It stops at a wall and slides along it with
// v - 1.02 (v.n) n on the normal's horizontal part, as at a zone's edge walls (WorldView.Bounds). Anything under 0.8 m
// never touches the box: kerbs, steps and low rocks are the ground-following's (Ground). A surface that faces up enough
// to stand on (a ramp) doesn't block (ours). Creatures and other players don't block.
// Retail's rule is for outdoor zones; cities and dungeons use another system, not traced.

using Godot;

namespace EodClient;

public partial class WorldView
{
    private const float WallHalfWidth = 14 * RegionMetresPerUnit;   // 28 x 28 units
    private const float WallClearance = 0.8f;                       // the box's bottom above the feet (32 units)
    private const float WallHeight = 48 * RegionMetresPerUnit;      // 1.2 m
    private const float SlideFactor = 1.02f;                        // retail's slide: v - 1.02 (v.n) n
    private BoxShape3D? _wallShape;
    private double _wallLogAt;

    // Where a step from `from` to `to` ends against the walls: the whole step, as far as the first wall then along it,
    // or (at a corner) the start.
    private Vector3 WallStep(Vector3 from, Vector3 to)
    {
        Vector3 motion = to - from;
        motion.Y = 0;
        if (motion.LengthSquared() < 1e-8f)
            return to;
        _wallShape ??= new BoxShape3D { Size = new Vector3(2 * WallHalfWidth, WallHeight, 2 * WallHalfWidth) };
        var facing = new Basis(Vector3.Up, _bodyYaw);
        var query = new PhysicsShapeQueryParameters3D
        {
            Shape = _wallShape, CollisionMask = WallLayer, Margin = 0.02f,
        };
        PhysicsDirectSpaceState3D space = GetWorld3D().DirectSpaceState;
        Vector3 at = from, lift = new(0, WallClearance + WallHeight / 2, 0);
        for (int pass = 0; pass < 3 && motion.LengthSquared() > 1e-8f; pass++)
        {
            query.Transform = new Transform3D(facing, at + lift);
            query.Motion = motion;
            float[] cast = space.CastMotion(query);
            if (cast.Length < 2 || cast[0] >= 1f)
            {
                at += motion;
                break;
            }
            // The wall's normal where the box would touch it.
            query.Transform = new Transform3D(facing, at + lift + motion * cast[1]);
            query.Motion = Vector3.Zero;
            Godot.Collections.Dictionary rest = space.GetRestInfo(query);
            Vector3 normal;
            if (rest.Count > 0)
                normal = rest["normal"].AsVector3();
            else
            {
                // No resting contact at the touching point (it sits just inside the cast's margin): the contact
                // pairs (a point on the box, then one on the wall) give the normal, pointing back at us.
                query.Margin = 0.06f;
                Godot.Collections.Array<Vector3> pts = space.CollideShape(query, 4);
                query.Margin = 0.02f;
                normal = pts.Count >= 2 ? (pts[0] - pts[1]) : -motion;
                if (normal.Dot(motion) > 0)
                    normal = -normal;
                normal = normal.LengthSquared() > 1e-8f ? normal.Normalized() : -motion.Normalized();
            }
            if (normal.Dot(motion) >= 0)
            {
                at += motion;   // moving away from it (the turning box can start against a wall): only contacts you move into count
                break;
            }
            if (normal.Y >= MinFloorNormal)
            {
                at += motion;   // a ramp or a step's top, not a wall: the ground step-up takes it
                break;
            }
            at += motion * cast[0];
            normal.Y = 0;
            if (normal.LengthSquared() < 1e-6f)
                break;
            normal = normal.Normalized();
            Vector3 rest_ = motion * (1 - cast[0]);
            motion = rest_ - normal * (SlideFactor * rest_.Dot(normal));   // slide along the wall, a little off it
            if (_seconds >= _wallLogAt)
            {
                _wallLogAt = _seconds + 2;
                // What it touches: the box where it stopped, a little grown (it rests just off the wall).
                query.Transform = new Transform3D(facing, at + lift);
                query.Margin = 0.1f;
                var hits = space.IntersectShape(query, 1);
                query.Margin = 0.02f;
                string what = hits.Count > 0 && hits[0]["collider"].AsGodotObject() is Node n ? ModelOf(n) : "a wall";
                GD.Print($"[world] walked into {what} at {ToRegion(at)}");
            }
        }
        return new Vector3(at.X, to.Y, at.Z);
    }

    /// <summary>
    /// Gives a placed model its collision. Solid: each collision volume (the converter's "eodcollide_" nodes, never drawn)
    /// becomes a body on the wall layer, which only the character's movement meets (WallStep); each drawn surface a body
    /// to stand on and for the camera. <paramref name="drawnWalls"/>: a solid model without collision volumes is walled
    /// by its drawn surfaces instead. Not solid: nothing (its volumes stay hidden).
    /// </summary>
    private static void MakeSolid(Node3D model, bool solid, bool drawnWalls = false)
    {
        var meshes = FindMeshes(model).ToList();
        bool hasVolumes = meshes.Any(IsCollisionVolume);
        foreach (MeshInstance3D mi in meshes)
        {
            bool volume = IsCollisionVolume(mi);
            if (volume)
            {
                mi.Visible = solid && _showWalls;
                if (mi.Visible)
                    mi.MaterialOverride = WallLook;
            }
            if (!solid)
                continue;
            mi.CreateTrimeshCollision();
            foreach (Node c in mi.GetChildren())
                if (c is StaticBody3D body)
                {
                    if (volume)
                        (body.CollisionLayer, body.CollisionMask) = (WallLayer, 0);
                    else if (drawnWalls && !hasVolumes)
                        body.CollisionLayer |= WallLayer;
                }
        }
    }

    private static bool IsCollisionVolume(MeshInstance3D mi) =>
        mi.Name.ToString().StartsWith(DaocAssets.Nif.GltfExporter.CollisionPrefix, System.StringComparison.Ordinal);

    // --show-walls (dev): the collision volumes drawn, translucent red, to see where they are.
    private static readonly bool _showWalls = System.Array.IndexOf(OS.GetCmdlineUserArgs(), "--show-walls") >= 0;
    private static readonly StandardMaterial3D WallLook = new()
    {
        AlbedoColor = new Color(1f, 0.1f, 0.1f, 0.35f), Transparency = BaseMaterial3D.TransparencyEnum.Alpha,
        ShadingMode = BaseMaterial3D.ShadingModeEnum.Unshaded, CullMode = BaseMaterial3D.CullModeEnum.Disabled,
    };

    // The placed model a collision body belongs to (its "glb" meta, set when placed), for the log.
    private static string ModelOf(Node n)
    {
        for (Node? p = n; p is not null; p = p.GetParent())
            if (p.HasMeta("glb"))
                return p.GetMeta("glb").AsString();
        return n.GetPath().ToString();
    }
}
