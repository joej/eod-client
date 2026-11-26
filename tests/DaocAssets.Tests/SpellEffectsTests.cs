// SPDX-License-Identifier: GPL-3.0-or-later

using DaocAssets.Effects;

namespace DaocAssets.Tests;

/// <summary>The spell effect tables (spells, speffects, spnifs) read as the client reads them.</summary>
public sealed class SpellEffectsTests
{
    [Fact]
    public void ASpellNamesItsEffectsByColumn()
    {
        const string csv = "Spell,Spell (level),Spell,Caster,Caster,Caster,Caster,Caster,Caster,Caster,Projectile,Target,Buildup,Pulse,Sound,Radius,Resist,Projectile,Play,Focus,Remove\n" +
                           "ID,Name,Icon,Anim1,Anim2,Anim3,Effect1A,Effect1B,Effect2,Effect3,Effect,Effect,Effect,Effect,Set,Effect,Effect,Lag,Anims,CasterFX,Effect on Cast\n" +
                           ";,,,,,,,,,,,,,,,,,,,,\n" +
                           "301,Fire Bolt,5,12,13,14,21,22,23,24,31,32,33,34,7,35,36,250,1,0,A\n";
        SpellVisual v = SpellEffectTables.ParseSpells(csv)[301];
        Assert.Equal((21, 22, 23, 24), (v.Effect1A, v.Effect1B, v.Effect2, v.Effect3));
        Assert.Equal((31, 32, 33, 34), (v.Projectile, v.Target, v.Buildup, v.Pulse));
        Assert.Equal((7, 35, 36, 250), (v.SoundSet, v.Radius, v.Resist, v.ProjectileLagMs));
    }

    [Fact]
    public void AnEffectRowAndItsNif()
    {
        const string fx = "Effect,Effect,Effect,Effect,Effect,Effect,Track,Auto,Spin,Angle,Focus,Focus,Focus,Focus,Focus\n" +
                          "ID,Name,NIF,Node,Duration,Rate,Angle,Scale,Axis,,Effect,Freq,Speed,Timeout,TargetFX\n" +
                          "1,L Sparkle,2,Bip01 L Held,3000,100,0,1,,,,,,,\n" +
                          "434,fire1 damage pulse,9,rootnode,1500,0,2,0,3,90,435,500,800,6000,0\n";
        Dictionary<int, SpEffect> e = SpellEffectTables.ParseEffects(fx);
        Assert.Equal(("Bip01 L Held", 3000, true, 1f), (e[1].Node, e[1].DurationMs, e[1].AutoScale, e[1].Speed));
        SpEffect pulse = e[434];
        Assert.True(pulse.OnRoot);
        Assert.Equal((1f, 2, 3, 90f), (pulse.Speed, pulse.TrackAngle, pulse.SpinAxis, pulse.Angle));   // rate 0 = 100 %
        Assert.Equal((435, 500, 800f, 6000), (pulse.FocusEffect, pulse.FocusFreqMs, pulse.FocusSpeed, pulse.FocusTimeoutMs));

        const string nifs = "1500 limit,,,Expansion,Housing\nNIF ID,NIF Description,NIF Filename,Only,Only\n" +
                            "2,blue sparkles,cast1,0,0\n7,keep fire,kfire,0,2\n8,garden glow,gglow,0,1\n";
        Dictionary<int, SpNif> n = SpellEffectTables.ParseNifs(nifs);
        Assert.Equal("effects/cast1.nif", n[2].Path);
        Assert.Equal("frontiers/nifs/kfire.nif", n[7].Path);
        Assert.Equal("phousing/effects/gglow.nif", n[8].Path);
    }
}
