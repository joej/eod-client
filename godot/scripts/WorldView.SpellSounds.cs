// SPDX-License-Identifier: GPL-3.0-or-later
//
// WorldView: spell sounds (roadmap M12, docs/sound.md "Spells"), as the retail client plays them (daoc-re RE/78 "Cast
// sequence" and "Landing", RE/60, RE/82). spells.csv column 14 "Sound Set" is a cc_spells row: 0 gather, 1 shape, 2 cast,
// 3 travel, 4 hit, 5 miss, 6 resisted, 7 fizzle; 3, 6 and 7 are never played.
// - 0x72 (cast, T = its cast time): T = 0, "cast" at once. Otherwise "gather" at once; "shape" 1 s later when the spell has
//   an Anim2 and T is over 1.5 s; "cast" at T - 0.5 s when it has an Anim3, T is over 0.5 s and it has no Remove letters.
//   Play Anims 1 runs the same again on landing, with the bolt time as T.
// - 0x1B (landing), unless the pulse flag is set: "cast" at the caster for a bolt, or for a spell with Remove letters (its
//   Anim3 plays then too); "hit" (results 1, 2, 3) or "miss" (0, 4) at the target at once, not after the bolt.
// - 0x73 stops the caster's "shape" and "cast" sounds, those playing and those still to come.
// Each sound plays where its figure is when it starts (ours: it doesn't follow a moving caster).

using DaocAssets.Figures;
using DaocProtocol.Packets;
using Godot;

namespace EodClient;

public partial class WorldView
{
    private const int SpellGather = 0, SpellShape = 1, SpellCastSound = 2, SpellHit = 4, SpellMiss = 5;

    // Per caster: its "shape" and "cast" sounds still playing, which 0x73 stops.
    private readonly Dictionary<ushort, List<GameAudio.Voice>> _castVoices = [];

    // The cast sequence's sounds, after PlayCast has started this cast's run (an interrupt or a new cast ends it).
    private void CastSounds(ushort caster, ushort spell, double seconds)
    {
        if (SpellVisualOf(spell) is not { SoundSet: > 0 } v)
            return;
        if (seconds <= 0)
        {
            SpellSound(caster, v.SoundSet, SpellCastSound, stoppable: true);
            return;
        }
        SpellCastLook? look = CastLook(spell);
        int run = _castRuns.GetValueOrDefault(caster);
        void Due(double delay, int column) => Later(delay, () =>
        {
            if (_castRuns.GetValueOrDefault(caster) == run)
                SpellSound(caster, v.SoundSet, column, stoppable: true);
        });
        SpellSound(caster, v.SoundSet, SpellGather, stoppable: false);
        if (look is { Anim2: > 0 } && seconds > 1.5)
            Due(1, SpellShape);
        if (look is { Anim3: > 0, Anim3OnLanding: false } && seconds > 0.5)
            Due(seconds - 0.5, SpellCastSound);
    }

    private void LandSounds(SpellEffect fx)
    {
        if (fx.Pulse || SpellVisualOf(fx.SpellId) is not { SoundSet: > 0 } v)
            return;
        bool bolt = v.Projectile > 0 && FxOwnerNode(fx.CasterId) is not null;
        if (bolt || CastLook(fx.SpellId) is { Anim3OnLanding: true })
            SpellSound(fx.CasterId, v.SoundSet, SpellCastSound, stoppable: true);
        SpellSound(fx.TargetId, v.SoundSet, fx.Result is 1 or 2 or 3 ? SpellHit : SpellMiss, stoppable: false);
    }

    private void InterruptSounds(ushort caster)
    {
        if (!_castVoices.Remove(caster, out List<GameAudio.Voice>? voices))
            return;
        foreach (GameAudio.Voice voice in voices)
            voice.Stop();
    }

    // One cc_spells column at a figure (ours or one in view); nothing when the row names none.
    private void SpellSound(ushort at, int set, int column, bool stoppable)
    {
        if (Audio is not { } audio || audio.Tables.Spells.Get(set, column) is not string name || FxOwnerNode(at) is not Node3D node)
            return;
        if (audio.Play(name, node.GlobalPosition) is not { } voice || !stoppable)
            return;
        if (!_castVoices.TryGetValue(at, out List<GameAudio.Voice>? list))
            _castVoices[at] = list = [];
        list.RemoveAll(x => x.Finished);
        list.Add(voice);
    }
}
