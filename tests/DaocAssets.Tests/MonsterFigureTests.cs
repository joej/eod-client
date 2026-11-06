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
        8,Briton Male Head,b_mhed1.tga
        9,Briton Male Head,b_mhed2.tga
        """;

    [Fact]
    public void AModelIdResolvesToItsFigureSkinsAndFace()
    {
        var tables = MonsterTables.Parse(Monsters, Monnifs, Skins);
        FigureSpec spec = tables.Resolve(8)!;
        Assert.Equal(("bcommonm.nif", 0, 1f, 8), (spec.Nif, spec.Version, spec.Scale, spec.Face));
        Assert.Equal("m1_tunic1.tga", spec.Skins["LBody"]);
        Assert.Equal("hair_dirtblonde.tga", spec.Skins["Head"]);
        Assert.Equal("b_mhed1.tga", spec.Skins["Face"]);     // Face is a skin id: 8 = b_mhed1
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
            ["HeadA1:19"] = "hair_dirtblonde.tga",  // head parts: the hair skin, the face over it (below)
            ["HeadB1:24"] = "hair_dirtblonde.tga",
            ["LBody1:6"] = "m1_tunic1.tga",
        }, shown);
        // Body1b (variant "1b"), other numbers, the low-detail body and the cloak (no cloak skin) are hidden.
    }

    [Fact]
    public void AHeadWithNoSkinIsLeftOff()
    {
        // "Headless Elf": Head and Face are 0, and the head shapes have no texture of their own.
        const string headless = """
            ID,name,#,Body,Head,Arms,Gloves,Lbody,Legs,Boots,Cloak,Helm,Scale,Tall,Shdw,Set,Kilt,Sarac.,Face
            2222,Headless Elf,206,114,0,116,117,118,119,120,0,0,100,80,1,1,0,0,0
            """;
        static NifMesh Part(string shape) => new()
        {
            Name = shape, ShapeName = shape, Vertices = [Vector3.Zero, Vector3.UnitX, Vector3.UnitY], Triangles = [(0, 1, 2)],
        };
        var figure = new NifModel { Version = new NifVersion(10, 1, 0, 0), Meshes = [Part("Body1:0"), Part("HeadA1:19"), Part("HeadB1:24")] };
        NifModel dressed = MonsterFigure.Dress(figure, MonsterTables.Parse(headless, Monnifs, Skins).Resolve(2222)!);
        Assert.Equal(["Body1:0"], dressed.Meshes.Where(m => !m.Helper).Select(m => m.ShapeName));
    }

    [Fact]
    public void HeadPartsWearTheHairWithTheFaceOverItOnTheSecondUvSet()
    {
        // Each head part: the hair skin on UV set 0 and the face skin as a decal on set 1 (its alpha shows
        // the hair through the brows and rim). Drawn on set 0 the face wrapped round the head.
        Vector2[] set0 = [Vector2.Zero, Vector2.UnitX, Vector2.UnitY], set1 = [Vector2.One, Vector2.UnitY, Vector2.UnitX];
        NifMesh Head(string shape) => new()
        {
            Name = shape, ShapeName = shape, Vertices = [Vector3.UnitX, Vector3.UnitY, Vector3.UnitZ],
            Normals = [Vector3.UnitX, Vector3.UnitY, Vector3.UnitZ], Triangles = [(0, 1, 2)], TexCoords = set0, UvSets = [set0, set1],
        };
        var head = new NifModel { Version = new NifVersion(10, 1, 0, 0), Meshes = [Head("HeadA1:19"), Head("HeadB1:24")] };
        NifModel dressed = MonsterFigure.Dress(head, MonsterTables.Parse(Monsters, Monnifs, Skins).Resolve(8)!);
        foreach (NifMesh part in dressed.Meshes)
        {
            Assert.Equal(("hair_dirtblonde.tga", "b_mhed1.tga"), (part.TextureFile, part.DecalTextureFile));
            Assert.Same(set0, part.TexCoords);
            Assert.Same(set1, part.DecalTexCoords);
            Assert.Equal(Vector3.UnitX, part.Vertices[0]);       // not moved
        }
    }

    [Fact]
    public void ASkinnedHeadPartExportsTheFaceDecalOnTexcoord1()
    {
        var model = new NifModel
        {
            Version = new NifVersion(10, 1, 0, 0),
            Joints = [new NifJoint { Name = "Bip01 Head", Parent = -1, Local = Matrix4x4.Identity }],
            Meshes =
            [
                new NifMesh
                {
                    Name = "head", ShapeName = "HeadA1:19", Vertices = [Vector3.Zero, Vector3.UnitX, Vector3.UnitY], Triangles = [(0, 1, 2)],
                    TexCoords = [Vector2.Zero, Vector2.UnitX, Vector2.UnitY], TextureFile = "hair_dirtblonde.tga",
                    DecalTextureFile = "b_mhed1.tga", DecalTexCoords = [Vector2.One, Vector2.UnitY, Vector2.UnitX],
                    SkinJoints = [(0, Matrix4x4.Identity)], Skin = [[(0, 1f)], [(0, 1f)], [(0, 1f)]],
                },
            ],
        };
        var gltf = GltfExporter.BuildScene(model).ToGltf2();
        var prim = Assert.Single(Assert.Single(gltf.LogicalMeshes).Primitives);
        Assert.NotNull(prim.GetVertexAccessor("TEXCOORD_1"));
        Assert.Equal(new Vector2(1, 1), prim.GetVertexAccessor("TEXCOORD_1").AsVector2Array()[0]);
        Assert.Contains("\"decal\":\"b_mhed1\"", prim.Material.Extras.ToJsonString());
    }

    [Fact]
    public void AFullFiguresUnnamedBodiesAreItsLods()
    {
        // HSharF: the parts are "Figure:<id>" and the four low-detail bodies are "Editable Mesh".
        static NifMesh Shape(string shape) => new()
        {
            Name = shape, ShapeName = shape, Vertices = [Vector3.Zero, Vector3.UnitX, Vector3.UnitY], Triangles = [(0, 1, 2)],
        };
        var parts = Enumerable.Range(0, 40).Where(i => i != 17 && i != 18 && i != 29).Select(i => Shape($"Figure:{i}"));
        var shar = new NifModel
        {
            Version = new NifVersion(10, 1, 0, 0),
            Meshes = [Shape("Editable Mesh"), Shape("Editable Mesh"), .. parts],
        };
        NifModel dressed = MonsterFigure.Dress(shar, MonsterTables.Parse(Monsters, Monnifs, Skins).Resolve(8)!);
        Assert.Equal(["Figure:0", "Figure:6", "Figure:9", "Figure:13", "Figure:14", "Figure:15", "Figure:19", "Figure:24"],
            dressed.Meshes.Where(m => !m.Helper).Select(m => m.ShapeName));
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
    [InlineData("Lbody3:8", "LBody", "3")]
    [InlineData("Figure:6", "LBody", "1")]
    [InlineData("Figure:19", "HeadA", "1")]
    [InlineData("Figure:41", "Lod", "1b")]
    [InlineData("Figure:99", "", "")]
    [InlineData("Editable Mesh", "", "")]
    [InlineData("Bodyguard", "", "")]
    public void PartNamesSplitIntoSlotAndVariant(string shape, string slot, string variant) =>
        Assert.Equal((slot, variant), MonsterFigure.SlotOf(shape));

    [Fact]
    public void AModelsEffectSetResolvesToItsEffectFilesAndAttachNodes()
    {
        // Rows as in the install: model 1923 "Catacombs Teleport NPC" has effect set 90 in column 40 ("Mon
        // Effect"); set 90 is effect 88, which is teleporter_ground.nif on Bip01, drawn within 2500 units.
        string monsters = "ID,name\n1923,Catacombs Teleport  NPC,399" + string.Concat(Enumerable.Repeat(",0", 37)) + ",90\n"
            + "8,Plain,206" + string.Concat(Enumerable.Repeat(",0", 37)) + ",0\n";
        var tables = MonsterTables.Parse(monsters, Monnifs, Skins).WithEffects(
            "Effect,Desc\n90,teleporter_ground,88,0,0,0,0,0,,\n",
            "Effect,Desc\n88,teleporter_ground,83,Bip01,2500,100,1,0,0,0,0,,\n",
            "Effect,Desc\n83,teleporter_ground,teleporter_ground,1,0,2500\n");
        Assert.Equal([new MonsterEffect("teleporter_ground.nif", "Bip01", 2500)], tables.Effects(1923));
        Assert.Empty(tables.Effects(8));
        Assert.Empty(tables.Effects(4242));
    }
}
