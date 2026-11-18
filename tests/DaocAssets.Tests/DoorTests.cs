// SPDX-License-Identifier: GPL-3.0-or-later

using System.Numerics;
using DaocAssets.Nif;
using DaocAssets.Zone;

namespace DaocAssets.Tests;

/// <summary>Doors: which nodes are doors, how long they open, how their parts move, their ids, and their glTF form.</summary>
public sealed class DoorTests
{
    [Theory]
    [InlineData("door001", true)]
    [InlineData("door001H", true)]        // Galladoria's ROOTRoom03: the client reads the leading digits
    [InlineData("Door001S", true)]        // a four-state door
    [InlineData("door001-15", true)]
    [InlineData("door1:5", true)]         // 7 characters, n = 1 (inside a door it is that door's geometry)
    [InlineData("door0012", true)]
    [InlineData("door123456789", true)]
    [InlineData("door", false)]           // too short (bdungdoor01's mesh node: door001 / visible / door)
    [InlineData("door1", false)]          // too short (InconnuDoor01's leaves under its door001)
    [InlineData("door12", false)]         // too short
    [InlineData("doorway", false)]        // n = 0
    [InlineData("door000", false)]        // n = 0
    public void ADoorNodeIsAsTheClientTestsIt(string name, bool door) => Assert.Equal(door, NifFile.IsDoorNode(name));

    [Fact]
    public void ADoorsLengthComesFromItsNameItsExtraDataOrFifteenFrames()
    {
        // The install's dungeon doors: a UserPropBuffer "maxframes=45" (several keys, one per line).
        Assert.Equal((45f, (float?)null), NifFile.DoorFrames("door001", ["zMode10\r\nmaxframes=45\r\n"]));
        Assert.Equal((60f, (float?)20f), NifFile.DoorFrames("door001-F60-M20", ["maxframes=45"]));   // the name wins
        Assert.Equal((15f, (float?)8f), NifFile.DoorFrames("door002", ["midframe=8"]));
        Assert.Equal((15f, (float?)null), NifFile.DoorFrames("door003", []));
    }

    [Fact]
    public void APartMovesFromRestByItsKeysAboutItsOwnPivot()
    {
        // A leaf hinged at (100, 0, 0) under a parent at (0, 50, 0): it turns a quarter about +z over a second.
        Matrix4x4 parent = Matrix4x4.CreateTranslation(0, 50, 0);
        Matrix4x4 restLocal = Matrix4x4.CreateTranslation(100, 0, 0);
        var keys = new KfaTrack("leaf", [(0f, Quaternion.Identity), (1f, Quaternion.CreateFromAxisAngle(Vector3.UnitZ, MathF.PI / 2))], [], []);
        var mover = new NifMover("door001", "leaf", restLocal * parent, parent, restLocal, keys);

        // At rest nothing moves.
        Assert.True(Matrix4x4.Identity.Equals(mover.Delta(0)) || Near(mover.Delta(0), Matrix4x4.Identity));
        // At the end a point 10 along the leaf (baked at (110, 50, 0)) has swung about the hinge (100, 50, 0).
        Vector3 moved = Vector3.Transform(new Vector3(110, 50, 0), mover.Delta(1));
        Assert.True(Vector3.Distance(moved, Vector3.Transform(new Vector3(10, 0, 0), Matrix4x4.CreateFromAxisAngle(Vector3.UnitZ, MathF.PI / 2)) + new Vector3(100, 50, 0)) < 1e-3f,
            $"{moved}");
        // The hinge itself stays.
        Assert.True(Vector3.Distance(Vector3.Transform(new Vector3(100, 50, 0), mover.Delta(0.5f)), new Vector3(100, 50, 0)) < 1e-3f);
    }

    [Fact]
    public void AStaticModelsDoorBecomesNamedPartsAndAnAnimation()
    {
        var keys = new KfaTrack("leaf", [(0f, Quaternion.Identity), (1.5f, Quaternion.CreateFromAxisAngle(Vector3.UnitZ, 1f))], [], []);
        var model = new NifModel
        {
            Version = new NifVersion(10, 1, 0, 0),
            Meshes =
            [
                Tri("wall", -1),
                Tri("leaf", 0),
                Tri("leafcollide", 0) with { Helper = true, Collision = true },
            ],
            Doors = [new NifDoor("door001", 1.5f)],
            Movers = [new NifMover("door001", "leaf", Matrix4x4.Identity, Matrix4x4.Identity, Matrix4x4.Identity, keys)],
        };
        var gltf = GltfExporter.BuildScene(model).ToGltf2();
        var part = Assert.Single(gltf.LogicalNodes, n => n.Name == "door001__leaf");
        Assert.Equal(["eodcollide_leafcollide", "leaf"], part.VisualChildren.Select(c => c.Name).Order());
        var anim = Assert.Single(gltf.LogicalAnimations);
        Assert.Equal("door001", anim.Name);
        Assert.Equal(1.5f, anim.Duration, 3);
        Assert.Null(gltf.LogicalNodes.Single(n => n.Name == "wall").VisualParent);   // the rest stays where it was
    }

    [Theory]
    [InlineData(98, 0, "door001", 98000001u)]       // Hibernia's Darkspire: OpenDAoC's database row
    [InlineData(191, 5, "door001H", 191000501u)]    // Galladoria's room 5: its other row
    [InlineData(221, 57, "door002", 221005702u)]
    [InlineData(221, 57, "door0012", 221005712u)]
    [InlineData(5, 3, "door123456789", 123456789u)]
    public void ADungeonDoorsIdIsZoneRoomAndNumber(int zone, int room, string door, uint id) =>
        Assert.Equal(id, DungeonDoors.Id(zone, room, door));

    [Fact]
    public void ADoorWithoutANumberOrAboveNinetyNineHasNoId()
    {
        Assert.Null(DungeonDoors.Id(98, 0, "door"));
        Assert.Null(DungeonDoors.Id(98, 0, "gate01"));
        Assert.Null(DungeonDoors.Id(98, 0, "door0100"));   // n 100: the click's 2 digits can't hold it
    }

    [Fact]
    public void DoorsCsvRoundTripsAndDoorsAreReadFromAGlb()
    {
        var doors = new[] { new DungeonDoor(98000001, 0, "door001", 32000, 38912, 16000) };
        Assert.Equal(doors, DungeonDoors.ReadCsv(DungeonDoors.ToCsv(doors)));

        var keys = new KfaTrack("leaf", [(0f, Quaternion.Identity), (1f, Quaternion.CreateFromAxisAngle(Vector3.UnitZ, 1f))], [], []);
        var model = new NifModel
        {
            Version = new NifVersion(10, 1, 0, 0),
            Meshes = [Tri("leaf", 0)],
            Doors = [new NifDoor("door001", 1f)],
            Movers = [new NifMover("door001", "leaf", Matrix4x4.Identity, Matrix4x4.Identity, Matrix4x4.Identity, keys)],
        };
        string path = Path.Combine(Path.GetTempPath(), $"door-{Guid.NewGuid():N}.glb");
        try
        {
            GltfExporter.SaveGlb(model, path);
            Assert.Equal(["door001"], DungeonDoors.InGlb(path));
        }
        finally { File.Delete(path); }
    }

    [Fact]
    public void ADungeonsRenamedTexturesKeepThePiecesDoors()
    {
        // Zones 060, 079 and 423 convert their pieces through texproxy.csv (AssetLibrary.Renamed): the doors must come too.
        var keys = new KfaTrack("leaf", [(0f, Quaternion.Identity), (1f, Quaternion.CreateFromAxisAngle(Vector3.UnitZ, 1f))], [], []);
        var model = new NifModel
        {
            Version = new NifVersion(4, 2, 1, 0),
            Meshes = [Tri("leaf", 0)],
            Doors = [new NifDoor("door001", 1f)],
            Movers = [new NifMover("door001", "leaf", Matrix4x4.Identity, Matrix4x4.Identity, Matrix4x4.Identity, keys)],
        };
        NifModel renamed = DaocAssets.Library.AssetLibrary.Renamed(model, DaocAssets.Library.TextureProxy.Parse("stone,granite,0\n"));
        Assert.Equal("granite.dds", renamed.Meshes[0].TextureFile);
        Assert.Equal(model.Doors, renamed.Doors);
        Assert.Equal(model.Movers, renamed.Movers);
    }

    [LiveFact("live-nif/gatehouse_lvl4.nif")]
    public void AKeepGatesLeavesSwingWithItsDoor()
    {
        // A frontier gatehouse: door001's leaves (vis1, vis2 and their _damaged twins) each carry a NiVisController and
        // then the NiKeyframeController that swings them; the chain must be followed past the first to find the keys.
        NifModel model = NifFile.Read(File.ReadAllBytes(Path.Combine(AppContext.BaseDirectory, "live-nif", "gatehouse_lvl4.nif")));
        Assert.Equal(["door001"], model.Doors.Select(d => d.Name));
        Assert.Equal(["collision01", "collision02", "vis1", "vis1_damaged", "vis2", "vis2_damaged"], model.Movers.Select(m => m.Node).Order());
        // The visible leaves (the "!nf_door" surfaces that aren't hidden) move with it.
        Assert.Equal(2, model.Meshes.Count(m => m.KeepMaterial == "!nf_door" && !m.Helper && m.Mover >= 0));
    }

    private static NifMesh Tri(string name, int mover) => new()
    {
        Name = name,
        Vertices = [new(0, 0, 0), new(10, 0, 0), new(0, 10, 0)],
        Triangles = [(0, 1, 2)],
        TextureFile = "stone.dds",
        Mover = mover,
    };

    private static bool Near(Matrix4x4 a, Matrix4x4 b)
    {
        for (int r = 0; r < 4; r++)
            for (int c = 0; c < 4; c++)
                if (MathF.Abs(a[r, c] - b[r, c]) > 1e-4f)
                    return false;
        return true;
    }
}
