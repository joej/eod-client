// SPDX-License-Identifier: GPL-3.0-or-later
//
// WorldView: the targeting keys (roadmap M10), as the retail client has them (daoc-re RE/76, "Keys"; keyboard slots 24, 25,
// 43, 45-52): Target Enemy (Tab) and Target Friend (Ctrl+Tab in RPG/MMO, backtick in FPS) pick the nearest candidate in
// line of sight within a horizontal range, and the same key again within 1.5 s steps to the next farther one; Consider
// (F6 in RPG) asks the server to examine the target again; the group keys (F1-F4, Shift+F1-F4 in RPG) target that member.
// Not here yet: Last Attacker (Shift+Tab) waits for the attack packets, Target Object and Nearest Loot for pickable
// ground objects.

using System.Collections.Generic;
using DaocProtocol.Packets;
using Godot;

namespace EodClient;

public partial class WorldView
{
    private const float EnemyRange = 1200, FriendRange = 1500;   // region units, horizontal
    private const double CycleSeconds = 1.5;
    private const float EyeUnits = 64 * 0.8f;   // line of sight runs between 4/5 of each figure's height (64 if unknown)

    private ControlAction? _lastPickKey;
    private double _lastPickAt;

    /// <summary>Our own object id: Target Friend picks it (distance 0), and a group key can.</summary>
    private ushort SelfId => _world.Position.ObjectId;

    // Handles a targeting key; true when it was one.
    private bool TargetKey(InputEvent e)
    {
        if (KeyBindings.Pressed(e, ControlAction.TargetEnemy))
            TargetNearest(ControlAction.TargetEnemy);
        else if (KeyBindings.Pressed(e, ControlAction.TargetFriend))
            TargetNearest(ControlAction.TargetFriend);
        else if (KeyBindings.Pressed(e, ControlAction.Consider))
            Consider();
        else
        {
            for (ControlAction a = ControlAction.TargetGroup1; a <= ControlAction.TargetGroup8; a++)
                if (KeyBindings.Pressed(e, a))
                {
                    TargetGroupMember(a - ControlAction.TargetGroup1);
                    return true;
                }
            return false;
        }
        return true;
    }

    // Enemy: not dead; another realm (monsters count as one). Friend: our realm, dead ones included (for a resurrection).
    // Normal realm rules only: PvP and PvE servers pick differently (RE/76), not handled.
    // Our realm unknown: monsters are enemies and every realm's people friends (as the target window's buttons take it).
    // A peaceful NPC (0xDA flags 0x10) is never an enemy: ours, retail's use of that bit isn't traced.
    private bool IsEnemy(Entity e) =>
        !e.Dead && !e.Peaceful && (PlayerRealm == Realm.None ? e.Realm == 0 : e.Realm != (int)PlayerRealm);
    private bool IsFriend(Entity e) => e.Realm != 0 && (PlayerRealm == Realm.None || e.Realm == (int)PlayerRealm);

    private void TargetNearest(ControlAction key)
    {
        bool enemy = key == ControlAction.TargetEnemy;
        float range = enemy ? EnemyRange : FriendRange;
        var found = new List<(float Distance, ushort Id)>();
        if (!enemy)
            found.Add((0, SelfId));   // retail's Target Friend includes you; there is no target-self key
        foreach ((ushort id, Entity e) in _entities)
        {
            if (!e.Pickable || (enemy ? !IsEnemy(e) : !IsFriend(e)))
                continue;
            Vector3 d = e.Node.Position - _body.Position;
            d.Y = 0;
            float units = d.Length() / RegionMetresPerUnit;
            if (units <= range && InSight(e))
                found.Add((units, id));
        }
        found.Sort((a, b) => a.Distance.CompareTo(b.Distance));
        bool cycling = _lastPickKey == key && _seconds - _lastPickAt < CycleSeconds;
        (_lastPickKey, _lastPickAt) = (key, _seconds);
        if (found.Count == 0)
        {
            GD.Print($"[target] {(enemy ? "enemy" : "friend")} key: none in range");
            SetTarget(0);
            AppendChat(enemy ? "No enemy target in range." : "No friendly target in range.", LocalReply);
            return;
        }
        int at = 0;
        if (cycling)
        {
            int now = found.FindIndex(c => c.Id == _target);
            if (now >= 0)
                at = (now + 1) % found.Count;   // past the farthest: back to the nearest (ours; retail's wrap isn't traced)
        }
        ushort pick = found[at].Id;
        GD.Print($"[target] {(enemy ? "enemy" : "friend")} key: {(pick == SelfId ? PlayerName : _entities[pick].Name)} (#{pick}), " +
            $"{found[at].Distance:0} units, {at + 1} of {found.Count}");
        SetTarget(pick, examine: false);
    }

    // Retail resends 0xB0 with the examine flag, and the server answers with the con text.
    private void Consider()
    {
        if (_target == 0)
        {
            AppendChat("You must select a target to Consider!", LocalReply);
            return;
        }
        SendTarget(_target, examine: true);
    }

    // A ray through the solid world from 4/5 of our height to 4/5 of the other's (RE/76 "Line of sight").
    private bool InSight(Entity e)
    {
        if (!IsInsideTree() || !e.Node.IsInsideTree())
            return true;
        Vector3 from = _body.GlobalPosition + Vector3.Up * EyeUnits * RegionMetresPerUnit;
        Vector3 to = e.Node.GlobalPosition + Vector3.Up * EyeUnits * e.SizeScale * RegionMetresPerUnit;
        var ray = PhysicsRayQueryParameters3D.Create(from, to);
        ray.CollisionMask = ~NotSolid;
        return GetWorld3D().DirectSpaceState.IntersectRay(ray).Count == 0;
    }
}
