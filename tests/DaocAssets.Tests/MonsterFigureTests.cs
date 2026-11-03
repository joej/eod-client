// SPDX-License-Identifier: GPL-3.0-or-later

using System.Numerics;
using DaocAssets.Figures;
using DaocAssets.Nif;

namespace DaocAssets.Tests;

/// <summary>
/// Classic figures: a server model id resolved through gamedata.mpk's monsters.csv, monnifs.csv and
/// skins.csv (rows shaped like the install's, cut down), and a figure dressed with one variant per slot.
/// </summary>
public sealed class MonsterFigureTests
{
    // monsters.csv: ID, name, #, Body, Head, Arms, Gloves, Lbody, Legs, Boots, Cloak, Helm, Scale, ... Face (18).
    private const string Monsters = """
        Information,,NIF,Skins
        ID,name,#,Body,Head,Arms,Gloves,Lbody,Legs,Boots,Cloak,Helm,Scale,Tall,Shdw,Set,Kilt,Sarac.,Face
        8,Briton Male Merchant 1,206,114,1020,116,117,118,119,120,0,0,100,80,1,1,0,0,8
        9,Catacombs thing,88,1,0,0,0,0,0,0,0,0,135,80,1,1,0,0,0
        """;
    // monnifs.csv: ID, Textual Name, Name, ..., Figure Version (12).
    private const string Monnifs = """
        Information,,NIF
        ID,Textual Name,Name,Set,Run,Walk,Back,Strafe,Alternate,Only,Type,Anims,Version
        206,Briton Male,bcommonm,2,135,75,54,48,0,0,1,,0
        88,Albion Stone Minotaur,mino,357,120,90,54,48,0,4,1,,3
        """;
    private const string Skins = """
        Information,,Skin
        ID,Textual Name,Name
        1,Briton,b_cth1_body1m.tga
        114,m,m1_bodym.tga
        116,m,m1_arm1.tga
        117,m,m1_glove1.tga
        118,m,m1_tunic1.tga
        119,m,m1_legs1.tga
        120,m,m1_boot1.tga
        1020,hair,hair_dirtblonde.tga
        200,face,b_mhed4.tga
        """;

    [Fact]
    public void AModelIdResolvesToItsFigureSkinsAndFace()
    {
        var tables = MonsterTables.Parse(Monsters, Monnifs, Skins);
        FigureSpec spec = tables.Resolve(8)!;
        Assert.Equal(("bcommonm.nif", 0, 1f, 8), (spec.Nif, spec.Version, spec.Scale, spec.Face));
        Assert.Equal("m1_tunic1.tga", spec.Skins["LBody"]);
        Assert.Equal("hair_dirtblonde.tga", spec.Skins["Head"]);
        Assert.Equal("b_mhed4.tga", spec.Skins["Face"]);     // face 8 folds onto the 4 Briton male faces
        Assert.False(spec.Skins.ContainsKey("Cloak"));       // skin 0: none

        Assert.Equal(3, tables.Resolve(9)!.Version);           // a Catacombs figure, flagged for the caller
        Assert.Null(tables.Resolve(12345));
    }

    [Fact]
    public void DressingShowsVariantOneOfEachSlotInItsSkin()
    {
        static NifMesh Part(string shape) => new()
        {
            Name = shape, ShapeName = shape, Vertices = [Vector3.Zero, Vector3.UnitX, Vector3.UnitY],
            Triangles = [(0, 1, 2)],
        };
        var figure = new NifModel
        {
            Version = new NifVersion(10, 1, 0, 0),
            Meshes = [Part("Body1:0"), Part("Body2:3"), Part("Body1b:7"), Part("HeadA1:19"), Part("HeadB1:24"),
                      Part("HeadA2:20"), Part("Lod1a"), Part("Cloak1:5"), Part("LBody1:6")],
        };
        FigureSpec spec = MonsterTables.Parse(Monsters, Monnifs, Skins).Resolve(8)!;
        NifModel dressed = MonsterFigure.Dress(figure, spec);

        var shown = dressed.Meshes.Where(m => !m.Helper).ToDictionary(m => m.ShapeName, m => m.TextureFile);
        Assert.Equal(new Dictionary<string, string?>
        {
            ["Body1:0"] = "m1_bodym.tga",
            ["HeadA1:19"] = "b_mhed4.tga",          // the face
            ["HeadB1:24"] = "hair_dirtblonde.tga",  // the hair
            ["LBody1:6"] = "m1_tunic1.tga",
        }, shown);
        // Body1b (variant "1b"), other numbers, the low-detail body and the cloak (no cloak skin) are hidden.
    }

    [Fact]
    public void ACreaturesSingleShapeWearsTheBodySkin()
    {
        // Wolf.nif is one shape ("Editable Mesh"), skinned with the row's Body skin (wolf_black.tga).
        var wolf = new NifModel
        {
            Version = new NifVersion(4, 2, 2, 0),
            Meshes = [new NifMesh { Name = "mesh109", ShapeName = "Editable Mesh", Vertices = [Vector3.Zero, Vector3.UnitX, Vector3.UnitY], Triangles = [(0, 1, 2)] }],
        };
        FigureSpec spec = MonsterTables.Parse(Monsters, Monnifs, Skins).Resolve(8)!;
        Assert.Equal("m1_bodym.tga", Assert.Single(MonsterFigure.Dress(wolf, spec).Meshes).TextureFile);
    }

    [Theory]
    [InlineData("Body1b:3", "Body", "1b")]
    [InlineData("LBody2:6", "LBody", "2")]
    [InlineData("HeadA1:19", "HeadA", "1")]
    [InlineData("Lod1a", "Lod", "1a")]
    [InlineData("Editable Mesh", "", "")]
    [InlineData("Bodyguard", "", "")]
    public void PartNamesSplitIntoSlotAndVariant(string shape, string slot, string variant) =>
        Assert.Equal((slot, variant), MonsterFigure.SlotOf(shape));
}
