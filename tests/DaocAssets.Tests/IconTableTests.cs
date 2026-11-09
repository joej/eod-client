// SPDX-License-Identifier: GPL-3.0-or-later

using DaocAssets.Ui;

namespace DaocAssets.Tests;

/// <summary>
/// The server's number for a skill, style or spell, through gamedata's icons.csv, spells.csv and styles.csv, to a
/// picture. Crafted rows in the files' own layout (two header rows; spells.csv has ';' rows).
/// </summary>
public sealed class IconTableTests
{
    private const string Icons =
        "322,,0,1,2,3,4,5,6,7,8,9\r\n" +
        "ID,Name,baseIconId,borderId,up left,up,up right,right,down right,down,left,spell\r\n" +
        "409,sprint,1602,-1,-1,-1,-1,-1,-1,-1,-1,-1\r\n" +
        "654,Recenter,555,-1,1,-1,-1,-1,-1,-1,-1,-1\r\n" +
        "1000,iconname,91,-1,-1,-1,-1,-1,-1,-1,-1,114\r\n" +
        "1004,iconname,91,0,-1,-1,-1,-1,-1,-1,-1,115\r\n";
    private const string Spells =
        "Spell,Spell (level),Spell,Caster\r\nID,Name,Icon,Anim1\r\n;,,,\r\n" +
        "1,Amethyst Shield,1000,0\r\n5,Shield,1004,0\r\n";
    private const string Styles = ",,0,1\r\nID,name,Icon,Animation\r\n110,Recenter,654,42\r\n";

    [Fact]
    public void EachKindGoesThroughItsTable()
    {
        IconTable t = IconTable.Parse(Icons, Spells, Styles);
        Assert.Equal((1602, -1), t.Resolve(IconKind.Icon, 409));     // an ability: an icons.csv id itself
        Assert.Equal((555, -1), t.Resolve(IconKind.Style, 110));     // a style: styles.csv first
        Assert.Equal((91, -1), t.Resolve(IconKind.Spell, 1));        // a spell: spells.csv first
        Assert.Equal((91, 0), t.Resolve(IconKind.Spell, 5));         // with a border frame
    }

    [Fact]
    public void ANumberMissingFromATableHasNoPicture()
    {
        IconTable t = IconTable.Parse(Icons, Spells, Styles);
        Assert.Null(t.Resolve(IconKind.Spell, 409));    // 409 is no spells.csv id
        Assert.Null(t.Resolve(IconKind.Icon, 1));       // nor 1 an icons.csv id
        Assert.Null(IconTable.Empty.Resolve(IconKind.Icon, 409));
    }
}
