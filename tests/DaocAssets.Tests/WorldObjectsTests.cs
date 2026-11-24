// SPDX-License-Identifier: GPL-3.0-or-later

using DaocAssets.Figures;

namespace DaocAssets.Tests;

/// <summary>A server object's model number → its model file, through objects.csv and items.csv (crafted rows in the
/// files' own layout: two header lines, then ID, name, NIF #, ...).</summary>
public sealed class WorldObjectsTests
{
    private static readonly string Objects =
        "Information,,NIF,Icon\nID,name,#,#\n" +
        // Column 52 ("Colideable") set: solid.
        "3405,Fest_cart1,1779,1" + new string(',', 49) + "1,\n" +
        // Column 23 ("Show Name") and 64 ("Player Boat Flag") set.
        "2646,Med Boat,1,1" + new string(',', 20) + "1" + new string(',', 41) + "1\n" +
        "3408,sword,1,1,0,0\n" +
        "3406,No model,0,1\n" +
        "3407,Unknown item,99999,1\n" +
        "3405,Second row ignored,1,1\n";

    private const string Items = "ID,name,nif\n1,sword,sword01\n1779,cart,Fest_cart1\n";

    [Fact]
    public void ShowNameAndPlayerBoatComeFromTheirColumns()
    {
        var models = WorldObjects.Parse(Objects, Items);
        Assert.Equal(new ObjectModel("sword01.nif", false, ShowName: true, PlayerBoat: true), models[2646]);
        Assert.False(models[3405].ShowName || models[3405].PlayerBoat);
        string csv = "model,glb,solid,showname,boat\n" + WorldObjects.Row(2646, "boat_med", models[2646]) + "\n";
        Assert.Equal(new ObjectModel("boat_med", false, true, true), WorldObjects.ReadTable(csv)[2646]);
    }

    [Fact]
    public void ARowsModelComesThroughItemsCsv()
    {
        var models = WorldObjects.Parse(Objects, Items);
        Assert.Equal(new ObjectModel("Fest_cart1.nif", true), models[3405]);
        Assert.Equal(new ObjectModel("sword01.nif", false), models[3408]);
        Assert.False(models.ContainsKey(3406));   // NIF # 0
        Assert.False(models.ContainsKey(3407));   // no such items row
        Assert.Equal(3, models.Count);
    }

    [Fact]
    public void TheConvertersTableReadsBack()
    {
        string csv = "model,glb,solid\n" + WorldObjects.Row(3405, "Fest_cart1", new ObjectModel("", true)) + "\n"
            + WorldObjects.Row(3408, "sword01", new ObjectModel("", false))
            + "\n3406,,1\nbad,row\n";
        var table = WorldObjects.ReadTable(csv);
        Assert.Equal(new ObjectModel("Fest_cart1", true), table[3405]);
        Assert.Equal(new ObjectModel("sword01", false), table[3408]);
        Assert.Equal(2, table.Count);
    }
}
