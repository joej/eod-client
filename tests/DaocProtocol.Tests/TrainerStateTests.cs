// SPDX-License-Identifier: GPL-3.0-or-later

using System.Buffers.Binary;
using DaocProtocol.Packets;
using DaocProtocol.Session;

namespace DaocProtocol.Tests;

/// <summary>The trainer window's sums and its 0x53, by retail's rules (daoc-re, 2026-11-24).</summary>
public sealed class TrainerStateTests
{
    // Slash 4 (free to 5? no: autotrain 2), Parry 1, Riding without a slider; Slash's skills at 5 and 7, Parry's at 0 (= 50).
    private static TrainerState Sample()
    {
        var t = new TrainerState();
        Assert.True(t.Apply(new TrainerWindow(TrainerWindow.Code0Specs, 0)
        {
            Specs = [new TrainerSpec(0, 0, 5, "Slash"), new TrainerSpec(1, 0, 0, "Riding"), new TrainerSpec(2, 0, 2, "Parry")],
        }));
        Assert.False(t.Apply(new TrainerWindow(TrainerWindow.CodeNames, 0) { NameStart = 0, Names = ["Bleeder", "Gash", "Riposte"] }));
        byte[] table = new byte[50];
        for (int l = 1; l < 50; l++)
            table[l] = 10;                              // 10 points a level from level 2
        Assert.False(t.Apply(new TrainerWindow(TrainerWindow.CodeSkills, 0)
        {
            LevelTable = table,
            SpecSkills =
            [
                new TrainerSpecSkills(7, 2, [new TrainerSkill(5, 1, 2, 0, 0, 10), new TrainerSkill(7, 2, 2, 0, 0, 11)]),
                new TrainerSpecSkills(9, 0, [new TrainerSkill(0, 3, 5, 0, 0xFD, 12)]),
            ],
        }));
        t.Apply(new TrainerWindow(TrainerWindow.CodeRealmCosts, 0)
        {
            RealmCosts = [new TrainerRealmCosts(1, 3, [1, 3, 6], "Toughness")],
        });
        return t;
    }

    [Fact]
    public void LinesWithoutASliderAreLeftOutAndStartAtTheirLevel()
    {
        TrainerState t = Sample();
        Assert.Equal(["Slash", "Parry"], t.Lines.Select(l => l.Name));
        Assert.Equal(4, t.Targets[0]);
        Assert.Equal(1, t.Targets[1]);
        Assert.Equal("Gash", t.NameOf(0, 1));
        Assert.Equal("Riposte", t.NameOf(1, 0));
    }

    [Fact]
    public void CostsNeededAndNewSkills()
    {
        TrainerState t = Sample();
        t.Targets[0] = 7;                               // Slash 4 → 7: 5 + 6 + 7
        t.Targets[1] = 3;                               // Parry 1 → 3: 2 + 3
        Assert.Equal(18, t.CostOf(0, 4, 7));
        Assert.Equal(23, t.Needed);
        Assert.Equal(3, t.CostOf(0, 0, 3));             // levels 1 and 2 free (autotrain 2)
        Assert.Equal([10, 11], t.AllNewSkills().Select(s => (int)s.Info.Id));
        t.Targets[1] = 50;
        Assert.Contains(t.AllNewSkills(), s => s.Info.Id == 12);   // level 0 means 50
    }

    [Fact]
    public void MinimumLevelIsTheHigherOfTargetAndPoints()
    {
        TrainerState t = Sample();
        t.Targets[0] = 7;                               // from free base: 3+4+5+6+7 = 25
        t.Targets[1] = 3;                               // 1+2+3 = 6; total 31 → 10 a level from level 2: level 5 (40)
        Assert.Equal(7, t.MinimumLevel);                // the target 7 is higher
        t.Targets[0] = 10;                              // 3..10 = 52, +6 = 58 → level 7 (60); target 10 higher
        Assert.Equal(10, t.MinimumLevel);
    }

    [Fact]
    public void RealmCostsRunFromCurrentToTarget()
    {
        TrainerState t = Sample();
        Assert.Equal(0, t.RealmNeeded);
        t.RealmTargets[0] = 3;                          // 1 → 3: costs[1] + costs[2]
        Assert.Equal(9, t.RealmNeeded);
    }

    [Fact]
    public void TrainSendsEveryLineAndResetPutsThemBack()
    {
        TrainerState t = Sample();
        t.Targets[0] = 6;
        byte[] p = t.Request();
        uint U(int at) => BinaryPrimitives.ReadUInt32BigEndian(p.AsSpan(at));
        Assert.Equal(7u, U(0));                         // code 3 block 0's index
        Assert.Equal(9u, U(4));
        Assert.Equal(0xFFFFFFFFu, U(8));
        Assert.Equal(6u, U(32));
        Assert.Equal(1u, U(36));                        // unchanged: its current level
        Assert.Equal(1u, U(64));                        // the realm ability at its current level
        t.Reset();
        Assert.Equal(4, t.Targets[0]);
    }

    [Fact]
    public void ANamesSectionClearsTheSpecListButNotTheBuiltWindow()
    {
        TrainerState t = Sample();
        t.Apply(new TrainerWindow(TrainerWindow.CodeNames, 0) { NameStart = 3, Names = ["X"] });
        Assert.Equal(2, t.Lines.Count);                 // built at code 0
        t.Apply(new TrainerWindow(TrainerWindow.CodeRealm, 0));
        Assert.Empty(t.Lines);                          // retail: a code 1 after a code 4 shows an empty spec list
    }
}
