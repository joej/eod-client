// SPDX-License-Identifier: GPL-3.0-or-later
//
// Line-of-sight answers (roadmap M13). OpenDAoC has no world geometry of its own, so it asks a nearby player's client
// whether one figure can see another (S→C 0xD0) before a mob aggroes, an NPC swings, shoots or casts, or the player's own
// spell lands, and waits up to 1.5 s for the answer (then: not in sight, and NPCs ask again). We answer as retail 1.130
// does (daoc-re RE/93, 2026-11-22): at once, with a ray from 4/5 of the source's height to 4/5 of the target's through
// world geometry (figures, water and the never-drawn collision volumes don't block; climb surfaces do).
//   - Either id not a figure we have (players, NPCs, ourselves; not ground items, doors or keep pieces): no answer.
//   - A figure with no scene node yet: in sight, without a ray.
//   - The world not built (loading): not in sight.
// Trees never block, solid or not: retail keeps them out of the tested geometry (RE/93, updated 2026-11-22); a solid tree
// (fixtures.csv Collide set; library/trees.txt, `make trees`) still stops the player walking, so the ray passes through
// it. Fixtures with Collide 0 have no collider at all, as retail leaves them untested too. Retail decides per hit by node
// name (RE/34's click rules); ours is the collision layers.
// Rays are cast in the physics frame only: from _Process (where packets are drained) IntersectRay sometimes finds no hit,
// which would answer "in sight" through a wall. So a question waits for the next physics frame (ours; retail answers inside
// the packet handler), at most 1/60 s.
// The same ray sets the "target in view" bit the client sends in each 0xA9 and use request, which OpenDAoC trusts for a
// player's spells and range attacks (SpellHandler, RangeAttackComponent): retail sets it only while the target is in view.

using Godot;
using DaocProtocol.Packets;

namespace EodClient;

public partial class WorldView
{
    private int _losAnswered, _losBlocked, _losTrees;

    /// <summary>For the headless summary: line-of-sight questions answered, how many "not in sight", trees passed.</summary>
    public string Sight => $"{_losAnswered} line-of-sight answer(s) ({_losBlocked} not in sight, {_losTrees} tree(s) passed)";

    private readonly System.Collections.Generic.List<LosCheck> _losAsked = [];
    private double _targetViewAt;

    private void OnLosCheckAsked(LosCheck check) => _losAsked.Add(check);

    // Physics frame: answer the questions asked since the last one.
    private void AnswerLosChecks()
    {
        if (_losAsked.Count == 0)
            return;
        LosCheck[] asked = [.. _losAsked];
        _losAsked.Clear();
        if (_session is not { } session)
            return;
        foreach (LosCheck check in asked)
        {
            if (!IsFigure(check.Source) || !IsFigure(check.Target))
                continue;
            bool inSight = IsInsideTree() && Clear(EyeOf(check.Source), EyeOf(check.Target));
            _losAnswered++;
            _losBlocked += inSight ? 0 : 1;
            _ = Send(() => session.AnswerLosCheckAsync(check.Source, check.Target, inSight), "line of sight");
        }
    }

    // Physics frame, ten times a second: is the current target in view? Ourselves, or a target that isn't a figure we have
    // (ours): yes. Read by the session for every 0xA9 and use request.
    private void UpdateTargetInView()
    {
        if (_session is not { } session || _seconds - _targetViewAt < TargetViewInterval)
            return;
        _targetViewAt = _seconds;
        session.TargetInView = _target == 0 || _target == SelfId || !_entities.TryGetValue(_target, out Entity? e) || InSight(e);
    }

    private const double TargetViewInterval = 0.1;

    private bool IsFigure(ushort id) => id == SelfId || _entities.ContainsKey(id);

    // Nothing solid between a and b. Either missing (a figure with no scene node): yes.
    private bool Clear(Vector3? a, Vector3? b)
    {
        if (a is not Vector3 from || b is not Vector3 to)
            return true;
        var ray = PhysicsRayQueryParameters3D.Create(from, to);
        ray.CollisionMask = ~NotSolid | ClimbLayer;
        var passed = new Godot.Collections.Array<Rid>();
        for (int i = 0; i < MaxTreesPassed; i++)
        {
            Godot.Collections.Dictionary hit = GetWorld3D().DirectSpaceState.IntersectRay(ray);
            if (hit.Count == 0)
                return true;
            if (hit["collider"].AsGodotObject() is not Node n || !n.HasMeta(TreeMeta))
                return false;
            passed.Add(hit["rid"].AsRid());
            _losTrees++;
            ray.Exclude = passed;
        }
        return true;
    }

    private const string TreeMeta = "eod_tree";
    private const int MaxTreesPassed = 16;
    private static System.Collections.Generic.HashSet<string>? _treeModels;

    // The install's tree model names (library/trees.txt), read once; none without the file.
    private static System.Collections.Generic.HashSet<string> TreeModels(string libraryDir)
    {
        if (_treeModels is null)
        {
            string path = libraryDir.PathJoin(DaocAssets.Zone.TreeMap.OutFile);
            _treeModels = Godot.FileAccess.FileExists(path)
                ? [.. Godot.FileAccess.GetFileAsString(path).Split('\n', System.StringSplitOptions.RemoveEmptyEntries | System.StringSplitOptions.TrimEntries)]
                : [];
            GD.Print($"[world] {_treeModels.Count} tree model(s) line of sight passes through{(_treeModels.Count == 0 ? $" (no {path}: make trees)" : "")}");
        }
        return _treeModels;
    }

    // Tag a solid tree's collision bodies so the line-of-sight ray goes through them.
    private static void MarkTree(Node model)
    {
        foreach (Node c in model.GetChildren())
        {
            if (c is CollisionObject3D)
                c.SetMeta(TreeMeta, true);
            MarkTree(c);
        }
    }

    // 4/5 of the figure's height above its feet (RE/76), or null when it has no node in the scene.
    private Vector3? EyeOf(ushort id)
    {
        if (id == SelfId)
            return _body.IsInsideTree() ? _body.GlobalPosition + Vector3.Up * EyeUnits * RegionMetresPerUnit : null;
        return _entities.TryGetValue(id, out Entity? e) ? EyeOf(e) : null;
    }

    private static Vector3? EyeOf(Entity e) =>
        e.Node.IsInsideTree() ? e.Node.GlobalPosition + Vector3.Up * EyeUnits * e.SizeScale * RegionMetresPerUnit : null;
}
