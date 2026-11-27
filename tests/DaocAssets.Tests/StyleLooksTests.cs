// SPDX-License-Identifier: GPL-3.0-or-later

using DaocAssets.Figures;

namespace DaocAssets.Tests;

/// <summary>styles.csv read as the client reads it: a style's swing and its weapon trail.</summary>
public sealed class StyleLooksTests
{
    private const string Csv =
        ",,0,1,2,3,4,5,6,7,8,9\n" +
        "ID,name,Icon,Animation,Imagenum,Duration,Buildup,Red,Green,Blue,Hit Effect,anytime style\n" +
        "5,1H slash #1,608,44,10,933,400,74,148,224,0,\n" +
        "252,Fire Blade,727,75,11,1066,400,252,255,5,0, \n" +
        "292,Celtic Fury,792,80,10,1600,400,70,186,75,0, \n" +
        "533,\"Assault (Shadowblades, Mid)\",700,60,10,1200,400,1,2,3,255,1\n";

    [Fact]
    public void AStyleRowGivesItsSwingAndTrail()
    {
        StyleLook s = StyleLooks.Parse(Csv).Find(5, stance: 1);
        Assert.Equal((44, 10, 933, 400), (s.Animation, s.Imagenum, s.DurationMs, s.BuildupMs));
        Assert.Equal((74, 148, 224), (s.Red, s.Green, s.Blue));
        Assert.False(s.Anytime);
    }

    [Fact]
    public void AnAnytimeStyleTakesTheWeaponsGenericRow()
    {
        StyleLooks t = StyleLooks.Parse(Csv);
        Assert.Equal(4, t.Count);   // the quoted name with a comma keeps its row's columns
        Assert.Equal(252, t.Find(533, stance: 1).Id);
        Assert.Equal(292, t.Find(533, stance: 2).Id);
    }

    [Fact]
    public void AStyleWithNoRowUsesTheClientDefaults()
    {
        StyleLook s = StyleLooks.Parse(Csv).Find(9999, stance: 0);
        Assert.Equal((0, 3, 1000, 200, (byte)255), (s.Animation, s.Imagenum, s.DurationMs, s.BuildupMs, s.Red));
    }
}
