// SPDX-License-Identifier: GPL-3.0-or-later
//
// WorldView: swings and hit reactions from S→C 0xBC (roadmap M10), as the retail client plays them (daoc-re RE/75 "Combat
// animation from 0xBC", RE/83 "Combat reaction timing"):
// - the attacker swings, unless there is none or the result is 0x14 or more; our own swing shows only when the defender is
//   our current target. The weapon's Combat Stance gives a base canims slot, plus a random 0-2; the dual-wield codes pick
//   the left-hand or both-hands swings.
// - at T the defender reacts: a parry, block or evade by its own weapon, or on a hit (0x0A and up) a flinch (the run
//   flinch on the move), unless it is busy in another clip. T is the swing clip's hit time, 400 ms when that is 0, as it
//   is for most clips; ours is always 400 ms (the hit time isn't carried into the figures yet), and a parry starts at T
//   rather than T minus its own hit time.
// - the defender's health % updates its target window; when we are hit with no target, the attacker becomes it
//   (retail: inferred), and it is our last attacker (Shift+Tab).
// The figures carry the clips as "c<slot>" (MonsterTables.CombatSlots); an empty slot falls back to slot 0, as retail's
// does. Not here yet: styles (styles.csv Animation replaces the swing; the styled-hit trail), ranged (ready, aim, fire,
// the arrow), hit sounds, the set's No Flinch flag.

using DaocProtocol.Packets;
using Godot;

namespace EodClient;

public partial class WorldView
{
    private const double HitSeconds = 0.4;

    private ushort _lastAttacker;
    private bool _runningForward;
    private bool _holdMoving;   // our body's clip (the run flinch) keeps playing on the move

    private void OnCombatAnimated(CombatAnimation c)
    {
        if (_entities.TryGetValue(c.DefenderId, out Entity? defender))
            defender.Health = c.DefenderHealth;
        if (c.DefenderId == SelfId && c.AttackerId != 0)
        {
            _lastAttacker = c.AttackerId;
            if (_target == 0 && _entities.ContainsKey(c.AttackerId))
                SetTarget(c.AttackerId, examine: false);
        }

        string swing = "";
        if (c.Swings && !c.IsRanged && (c.AttackerId != SelfId || c.DefenderId == _target))
        {
            int slot = c.Style switch
            {
                CombatAnimation.LeftHand => 25,
                CombatAnimation.BothHands => 28,
                _ => SwingBase(FigureLook.Stance(GearPath, c.WeaponModel)),
            } + (int)(GD.Randi() % 3);
            swing = PlayCombatClip(c.AttackerId, slot);
        }
        GD.Print($"[combat] #{c.AttackerId} -> #{c.DefenderId}: result {c.Result}, weapon {c.WeaponModel}, style {c.Style}, " +
                $"health {c.DefenderHealth}%, swing {(swing == "" ? "-" : swing)}");

        if (c.Result is CombatAnimation.Parried or CombatAnimation.Blocked or CombatAnimation.Evaded || c.IsHit)
            GetTree().CreateTimer(HitSeconds).Timeout += () => React(c.DefenderId, c.Result);
    }

    private string GearPath => (_libraryPath ?? "").PathJoin("models").PathJoin("cata_gear.json");

    // objects.csv Combat Stance -> the first of its three swings (canims slots).
    private static int SwingBase(int stance) => stance switch
    {
        7 => 57, 2 => 8, 3 => 11, 4 => 18, 9 => 91, 10 or 12 => 94, 11 => 97, 13 => 105, 14 => 114, _ => 0,
    };

    // The defender's own weapon stance: ours from what we wear, another's from its last 0x15.
    private int DefenderStance(ushort id) =>
        id == SelfId ? FigureLook.ActiveStance(GearPath, PlayerWorn, PlayerActiveWeapons)
        : _equipment.TryGetValue(id, out EquipmentUpdate? g) ? FigureLook.ActiveStance(GearPath, g.Items, g.ActiveWeapons) : 0;

    private void React(ushort id, byte result)
    {
        int stance = DefenderStance(id);
        int? slot = result switch
        {
            CombatAnimation.Parried => stance switch { 2 => 38, 3 => 35, 9 => 100, 10 => 101, 11 => 102, 12 => 103, _ => 32 },
            CombatAnimation.Blocked => 41,   // always, whatever the defender holds (retail doesn't check for a shield)
            CombatAnimation.Evaded => stance switch { 2 => 39, 3 => 36, _ => 33 },
            _ => null,
        };
        if (slot is { } s)
        {
            PlayCombatClip(id, s);
            return;
        }
        // A hit: the flinch, unless busy in another clip (a swing, a parry, landing).
        Entity? e = null;
        if (id != SelfId && !_entities.TryGetValue(id, out e))
            return;
        if (e is null ? _landLeft > 0 : _seconds < e.HoldUntil)
            return;
        bool running = e is null ? _runningForward : e.Moved && e.MoveSpeed > 0;
        if (!(running && PlayCombatClip(id, "run_flinch", holdMoving: true) != ""))
            PlayCombatClip(id, 3);
    }

    private string PlayCombatClip(ushort id, int slot) => PlayCombatClip(id, $"c{slot}", "c0");

    // Plays a clip once from its start on us or a figure in view, holding off the motion clips until it ends. Moving takes
    // over, except from the run flinch (ours: retail's handover isn't traced). Not while dead, sitting, swimming or in the
    // air. Returns the clip played, or "".
    private string PlayCombatClip(ushort id, string clip, string? fallback = null, bool holdMoving = false)
    {
        AnimationPlayer? player;
        Entity? e = null;
        if (id == SelfId)
        {
            if (_dead || _sitting || _quitting || _swimming || _airborne)
                return "";
            player = _bodyAnim;
        }
        else if (_entities.TryGetValue(id, out e) && !e.Dead && e.Posture == PlayerPosture.Normal)
            player = e.Player;
        else
            return "";
        if (player is null)
            return "";
        if (!player.HasAnimation(clip))
        {
            if (fallback is null || !player.HasAnimation(fallback))
                return "";
            clip = fallback;
        }
        player.SpeedScale = 1f;
        float length = StartOnce(player, clip);
        if (e is null)
            (_bodyClip, _landLeft, _holdMoving) = (clip, length, holdMoving);
        else
            (e.Clip, e.HoldUntil, e.HoldMoving) = (clip, _seconds + length, holdMoving);
        return clip;
    }

    // Plays a clip once (no loop) from its start, blending in unless it is already the current clip; its length.
    // Shared by the one-shots: swings and reactions here, jump and land (PlayOnce, PlayHeld).
    private static float StartOnce(AnimationPlayer player, string clip)
    {
        Animation a = player.GetAnimation(clip);
        a.LoopMode = Animation.LoopModeEnum.None;
        if (player.CurrentAnimation == clip)
            player.Seek(0, true);
        else
            player.Play(clip, customBlend: 0.1);
        return (float)a.Length;
    }

    // Last Attacker (Shift+Tab): whoever last attacked us, if still in view and in sight (RE/76). "In range" is ours: the
    // server still shows it to us.
    private void TargetLastAttacker()
    {
        if (_lastAttacker == 0 || !_entities.TryGetValue(_lastAttacker, out Entity? e) || e.Dead)
            AppendChat("Your last attacker is not in range.", LocalReply);
        else if (!InSight(e))
            AppendChat("Your last attacker is not visible.", LocalReply);
        else
            SetTarget(_lastAttacker, examine: false);
    }
}
