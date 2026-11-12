// SPDX-License-Identifier: GPL-3.0-or-later

using DaocAssets.Figures;

namespace DaocAssets.Tests;

/// <summary>models/figures.csv and catacombs.csv: what the converter writes, the client reads back.</summary>
public sealed class FigureTableTests
{
    [Fact]
    public void ReadsBackBothTables()
    {
        string classic = FigureTable.ClassicHeader + "\n" + FigureTable.Row(new(32, 1.25f, "bcommonm")) + "\n";
        string cata = FigureTable.CatacombsHeader + "\n" + FigureTable.Row(new(2032, 0.9f, "bri_m_skeleton", 3)) + "\nbad\n";

        Assert.Equal("32,1.25,bcommonm", FigureTable.Row(new(32, 1.25f, "bcommonm")));
        Assert.Equal(new FigureRow(32, 1.25f, "bcommonm"), Assert.Single(FigureTable.Read(classic)).Value);
        Assert.Equal(new FigureRow(2032, 0.9f, "bri_m_skeleton", 3), Assert.Single(FigureTable.Read(cata)).Value);
    }

    [Fact]
    public void StridesComeFromTheFiguresMonnifsRowsAndReadBack()
    {
        // Model 8's classic figure is monnifs 206 (Briton Male: run 135, walk 75, back 54, strafe 48); it has no Catacombs row.
        const string monsters = """
            ID,name,#,Body,Head,Arms,Gloves,Lbody,Legs,Boots,Cloak,Helm,Scale
            8,Briton Male Merchant 1,206,0,0,0,0,0,0,0,0,0,100
            9,No such figure,999,0,0,0,0,0,0,0,0,0,100
            """;
        const string monnifs = """
            ID,Textual Name,Name,Set,Run,Walk,Back,Strafe
            206,Briton Male,bcommonm,2,135,75,54,48
            """;
        var tables = MonsterTables.Parse(monsters, monnifs, "");
        Assert.Equal((new Strides(135, 75, 54, 48), (Strides?)null), tables.StridesOf(8));
        Assert.Equal(((Strides?)null, (Strides?)null), tables.StridesOf(9));

        string csv = FigureTable.StridesHeader + "\n" + FigureTable.StridesRow(8, new(135, 75, 54, 48), null) + "\n"
            + FigureTable.StridesRow(32, new(0, 0, 0, 0), new(158, 65, 52, 46)) + "\nbad\n";
        Assert.Equal("8,135,75,54,48,,,,", FigureTable.StridesRow(8, new(135, 75, 54, 48), null));
        var read = FigureTable.ReadStrides(csv);
        Assert.Equal((new Strides(135, 75, 54, 48), (Strides?)null), read[8]);
        Assert.Equal((new Strides(0, 0, 0, 0), new Strides(158, 65, 52, 46)), read[32]);
        Assert.Equal(2, read.Count);
    }
}
