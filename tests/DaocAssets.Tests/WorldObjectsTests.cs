// SPDX-License-Identifier: GPL-3.0-or-later

using DaocAssets.Figures;

namespace DaocAssets.Tests;

/// <summary>A server object's model number → its model file, through objects.csv and items.csv (crafted rows in the
/// files' own layout: two header lines, then ID, name, NIF #, ...).</summary>
public sealed class WorldObjectsTests
{
    private const string Objects =
        "Information,,NIF,Icon\nID,name,#,#\n" +
        "3405,Fest_cart1,1779,1,0,0\n" +
        "3406,No model,0,1\n" +
        "3407,Unknown item,99999,1\n" +
        "3405,Second row ignored,1,1\n";

    private const string Items = "ID,name,nif\n1,sword,sword01\n1779,cart,Fest_cart1\n";

    [Fact]
    public void ARowsModelComesThroughItemsCsv()
    {
        var models = WorldObjects.Parse(Objects, Items);
        Assert.Equal("Fest_cart1.nif", models[3405]);
        Assert.False(models.ContainsKey(3406));   // NIF # 0
        Assert.False(models.ContainsKey(3407));   // no such items row
        Assert.Single(models);
    }

    [Fact]
    public void TheConvertersTableReadsBack()
    {
        var table = WorldObjects.ReadTable("model,glb\n3405,items/fest_cart1\n3406,\nbad,row\n");
        Assert.Equal("items/fest_cart1", table[3405]);
        Assert.Single(table);
    }
}
