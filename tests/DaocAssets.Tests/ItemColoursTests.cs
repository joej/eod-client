// SPDX-License-Identifier: GPL-3.0-or-later

using DaocAssets.Figures;

namespace DaocAssets.Tests;

/// <summary>Dyes (tints.csv: the Catacombs colour, columns 8-10) and emblem colours and logos (patterns.csv,
/// insignia.csv), in rows shaped like the install's.</summary>
public sealed class ItemColoursTests
{
    [Fact]
    public void DyesTakeTheCatacombsColourAndEmblemsTheirTwoPalettesAndLogos()
    {
        ItemColours c = ItemColours.Parse(
            "Information,, ,,,Expansion,Expansion,Expansion,Cata,Cata,Cata\nID,Textual Name,Red,Green,Blue,Red,Green,Blue,Red,Green,Blue\n"
                + "0,White,255,255,255,255,255,255,255,255,255\n1,Old Red,255,128,64,250,120,60,240,110,50",
            ",,0,1,,\nSet,ID,name,red,green,blue\n1,0,Dk_Red,169,0,3\n1,15,Dk_Maroon,116,0,68\n2,0,Lt_Kak,205,200,158\n2,7,Lt_Purp,152,136,165",
            ",,0,1\nID,name,Filename,spare\n1,Grail,B_emblem_001.dds,0\n5,Castle Tower,B_emblem_005.dds,0\n6,,,0");
        Assert.Equal(((byte)240, (byte)110, (byte)50), c.Dyes[1]);
        Assert.Equal(2, c.Dyes.Count);
        Assert.Equal(((byte)169, (byte)0, (byte)3), c.Primary[0]);
        Assert.Equal(((byte)116, (byte)0, (byte)68), c.Primary[15]);
        Assert.Equal(((byte)152, (byte)136, (byte)165), c.Secondary[7]);
        Assert.Equal(new Dictionary<int, string> { [1] = "B_emblem_001.dds", [5] = "B_emblem_005.dds" }, c.Logos);
        Assert.All(ItemColours.PatternMasks, m => Assert.Equal(8, m.Length));
    }
}
