// SPDX-License-Identifier: GPL-3.0-or-later

using DaocAssets.Figures;

namespace DaocAssets.Tests;

/// <summary>
/// Worn items' Catacombs looks from objects.csv's own columns (rows shaped like the install's, cut
/// down to the columns read: 11 classic cloak skin, 15 "Lbody #", 65 Cata Status, 66-72 Catacombs skins,
/// 74/75/78 glove, boot and cloak meshes) and pskins.csv's texture names.
/// </summary>
public sealed class CataItemsTests
{
    private static string Row(int id, string name, (int Col, string Value)[] cells)
    {
        var r = Enumerable.Repeat("", 98).ToArray();
        (r[0], r[1]) = (id.ToString(), name);
        foreach ((int col, string value) in cells) r[col] = value;
        return string.Join(',', r);
    }

    private static Dictionary<int, CataItem> Items() => CataItems.Parse(
        "Information,,NIF\nID,name,#\n" + string.Join('\n',
            Row(41, "Chain Hauberk", [(65, "1"), (66, "4030"), (67, "4031"), (73, "6"), (76, "1"), (77, "1")]),
            Row(85, "Studded Gloves", [(65, "1"), (69, "4027"), (74, "3")]),
            Row(84, "Studded Boots", [(65, "1"), (71, "4029")]),
            Row(58, "Robe 1", [(15, "3"), (65, "1"), (66, "4040")]),
            Row(57, "Cloak", [(11, "362"), (72, "4972")]),
            Row(1722, "Old Cloak", [(11, "362"), (78, "2")]),
            Row(824, "Albion Studded Helm", [(79, "1242")]),
            Row(99, "No Status", [(66, "4030")])),
        "Information,,Skin\nID,Textual Name,Name\n4030,Cata Chain,chnBody05_02_m.dds\n4031,Cata Chain,chnBody05_02_f.dds\n" +
        "4027,Cata Studded,leaGloves01_std02.dds\n4029,Cata Studded,leaboots01_std02.dds\n4040,Cata Robe,robBody01_02_m.dds\n" +
        "4972,Cata Cloak,Cat_cloak.dds\n362,New Cloak,cloak.tga");

    [Fact]
    public void ABodyItemNamesItsTexturesAndLeavesItsMeshToTheClient()
    {
        CataItem hauberk = Items()[41];
        Assert.False(hauberk.Robe);
        Assert.Equal(5, hauberk.NameVariant);                       // chnBody05: Body# (6) is the material, not the mesh
        Assert.Equal(new CataPiece(0, "chnBody05_02_m.dds"), hauberk.Male["body"]);
        Assert.Equal(new CataPiece(0, "chnBody05_02_f.dds"), hauberk.Female["body"]);
        Assert.Equal(["body"], hauberk.Male.Keys);                  // Arms#/Legs# without a skin dress nothing
    }

    [Fact]
    public void GlovesAndBootsTakeTheirMeshColumnElseTheirNamesNumber()
    {
        Assert.Equal(new CataPiece(3, "leaGloves01_std02.dds"), Items()[85].Male["gloves"]);
        Assert.Equal(new CataPiece(1, "leaboots01_std02.dds"), Items()[84].Male["boots"]);
    }

    [Fact]
    public void ARobeIsMarkedAndAClassicCloakWearsThePlainCatacombsOne()
    {
        Assert.True(Items()[58].Robe);
        Assert.Equal(new CataPiece(1, "Cat_cloak.dds"), Items()[57].Male["cloak"]);
        Assert.Equal(new CataPiece(2, "Cat_cloak.dds"), Items()[1722].Female["cloak"]);   // no Cata skin; its mesh column
    }

    [Fact]
    public void ItemsWithoutACatacombsLookAreLeftOut()
    {
        Assert.DoesNotContain(824, Items().Keys);   // a helm: an item model, not a figure part
        Assert.DoesNotContain(99, Items().Keys);    // skins but Cata Status off
    }
}
