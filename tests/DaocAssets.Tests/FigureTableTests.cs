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
}
