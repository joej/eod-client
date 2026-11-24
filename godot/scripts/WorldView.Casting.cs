// SPDX-License-Identifier: GPL-3.0-or-later
//
// WorldView: casting (roadmap M13). S→C 0x72 starts a figure's cast, 0x73 interrupts it, 0x1B lands it. On the caster
// the retail client plays the spell's spells.csv animations (daoc-re RE/78 "Cast sequence"; the figures carry them as
// clips "a<anims.csv slot>"):
// - an instant spell (cast time 0) plays none;
// - otherwise Anim1 at once; Anim2 after it when the cast is longer than 1.5 s; Anim3 when it is longer than 0.5 s and
//   the spell has no Remove letters (those play Anim3 when 0x1B arrives instead);
// - Play Anims 2: no animations when the caster is moving as the cast starts; Play Anims 1: 0x1B plays the cast again,
//   with the bolt time as its length;
// - 0x73 cancels the caster's cast animations.
// - a slot the figure's set has no clip for plays the walk (column 0), as retail's clip lookup does.
// Ours (retail's timing inside the cast isn't traced): Anim2 repeats until Anim3, and Anim3 starts 0.5 s before the end
// (where retail plays the "cast" sound). Moving takes over from a cast clip, as from a swing. Not here yet: the spell
// effects and sounds (M12). Retail has no cast bar (daoc-re RE/97): 0x72 drives effects, animations and sounds only, the
// same for our own casts as for others', and the client writes no chat line for 0x72, 0x1B or 0x73.

using DaocAssets.Figures;
using DaocProtocol.Packets;
using Godot;

namespace EodClient;

public partial class WorldView
{
    private SpellCasts? _spellCasts;
    private readonly Dictionary<ushort, int> _castRuns = [];   // per caster: bumped by each new cast or interrupt

    private void SubscribeCasting()
    {
        _bridge.SpellCastStarted += OnSpellCast;
        _bridge.SpellInterrupted += OnSpellInterrupt;
        _bridge.SpellLanded += OnSpellLanded;
        _bridge.EffectsChanged += icons => GD.Print("[effects] " + (icons.Count == 0 ? "none" : string.Join(", ",
            icons.Values.Select(i => $"{i.Slot}: {i.Name} (icon {i.Icon}, {i.SecondsLeft} s, tooltip {i.TooltipId}" +
                                     $"{(i.Negative ? ", negative" : "")})"))));
        _bridge.ConcentrationChanged += list => GD.Print($"[concentration] {list.Entries.Count} buffs, {list.Used} used: " +
            string.Join(", ", list.Entries.Select(e => $"{e.Index}: {e.Name} on {e.Owner} ({e.Concentration})")));
    }

    private SpellCastLook? CastLook(ushort spell)
    {
        if (_spellCasts is null)
        {
            string path = (_libraryPath ?? "").PathJoin("icons").PathJoin("spells.csv");
            _spellCasts = SpellCasts.Parse(Godot.FileAccess.FileExists(path) ? Godot.FileAccess.GetFileAsString(path) : "");
        }
        return _spellCasts.Find(spell);
    }

    private void OnSpellCast(SpellCast c)
    {
        GD.Print($"[cast] #{c.CasterId} starts spell {c.SpellId}, {c.CastMilliseconds} ms");
        if (c.SpellId == 0)
            return;
        PlayCast(c.CasterId, CastLook(c.SpellId), c.CastMilliseconds / 1000.0, anim3: true);
    }

    private void OnSpellInterrupt(SpellInterrupt s)
    {
        GD.Print($"[cast] #{s.CasterId} interrupted");
        _castRuns[s.CasterId] = _castRuns.GetValueOrDefault(s.CasterId) + 1;
        StopCastClip(s.CasterId);
    }

    private void OnSpellLanded(SpellEffect fx)
    {
        GD.Print($"[cast] #{fx.CasterId} spell {fx.SpellId} on #{fx.TargetId}: result {fx.Result}" +
                 $"{(fx.Pulse ? ", pulse" : "")}, bolt {fx.BoltTenths * 100} ms");
        if (fx.Pulse || CastLook(fx.SpellId) is not { } look)
            return;
        if (look.CastOnLanding)
            PlayCast(fx.CasterId, look, fx.BoltTenths / 10.0, anim3: true);
        else if (look.Anim3OnLanding && look.Anim3 > 0)
            PlayCastClip(fx.CasterId, look.Anim3);
    }

    // The cast sequence on one caster: Anim1 now, Anim2 after it (repeated) when the cast is over 1.5 s, Anim3 0.5 s
    // before the end when it is over 0.5 s.
    private void PlayCast(ushort caster, SpellCastLook? look, double seconds, bool anim3)
    {
        int run = _castRuns[caster] = _castRuns.GetValueOrDefault(caster) + 1;
        if (look is null || seconds <= 0 || (look.StillOnly && CasterMoving(caster)))
            return;
        bool Current() => _castRuns.GetValueOrDefault(caster) == run;
        void After(double delay, Action action) => GetTree().CreateTimer(Math.Max(0, delay)).Timeout += () =>
        {
            if (Current() && IsInstanceValid(this))
                action();
        };

        double start = _seconds, anim3At = seconds - 0.5;
        float first = look.Anim1 > 0 ? PlayCastClip(caster, look.Anim1) : 0;
        if (look.Anim2 > 0 && seconds > 1.5)
        {
            void Mid()
            {
                if (_seconds - start >= anim3At)
                    return;
                float length = PlayCastClip(caster, look.Anim2);
                if (length > 0)
                    After(Math.Min(length, anim3At - (_seconds - start)), Mid);
            }
            After(first, Mid);
        }
        if (anim3 && look.Anim3 > 0 && seconds > 0.5 && !look.Anim3OnLanding)
            After(anim3At, () => PlayCastClip(caster, look.Anim3));
    }

    private bool CasterMoving(ushort id) =>
        id == SelfId ? _runningForward : _entities.TryGetValue(id, out Entity? e) && e.Moved;

    // One cast clip once on the caster ("a<slot>"); its length, 0 when nothing played. A figure whose animation set has no
    // clip in that slot plays the set's first column, the walk, as retail falls back to column 0 of the row (daoc-re
    // RE/83, corrected 2026-11-11).
    private float PlayCastClip(ushort id, int slot)
    {
        string played = PlayCombatClip(id, $"a{slot}", "walk");
        if (played == "")
            return 0;
        AnimationPlayer? player = id == SelfId ? _bodyAnim : _entities.GetValueOrDefault(id)?.Player;
        return (float)(player?.GetAnimation(played)?.Length ?? 0);
    }

    private static bool IsCastClip(string clip) =>
        clip == "walk" || (clip.Length > 1 && clip[0] == 'a' && char.IsDigit(clip[1]));

    // 0x73: a cast clip still showing gives way to the motion clips at once.
    private void StopCastClip(ushort id)
    {
        // (A held walk is only ever the cast fallback: the motion clips don't hold.)
        if (id == SelfId)
        {
            if (IsCastClip(_bodyClip))
                _landLeft = 0;
        }
        else if (_entities.TryGetValue(id, out Entity? e) && IsCastClip(e.Clip))
            e.HoldUntil = 0;
    }
}
