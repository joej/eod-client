// SPDX-License-Identifier: GPL-3.0-or-later

using System.Numerics;
using DaocAssets.Nif;
using DaocAssets.Zone;

namespace DaocAssets.Tests;

/// <summary>A dungeon's water planes (dungeon.water, dungeon.water.csv) and the meshes they are drawn as.</summary>
public sealed class DungeonWaterTests
{
    // Zone 226's first plane, as the install writes it: four corners around a position in the dungeon's frame, type −1.
    private const string Row =
        "0, -100.000000, 100.000000, 0.000000, 900.000000, 100.000000, 0.000000, 900.000000, -800.000000, 0.000000, " +
        "-100.000000, -800.000000, 0.000000, 7798.107422, -3863.587402, 809.343018, -1\r\n";

    [Fact]
    public void APlaneIsReadWithItsCornersPositionAndType()
    {
        DungeonWaterPlane p = Assert.Single(DungeonWater.Parse(Row + "\r\nshort, 1, 2\r\n"));
        Assert.Equal(0, p.Id);
        Assert.Equal(new Vector3(900, -800, 0), p.Corners[2]);
        Assert.Equal(new Vector3(7798.107422f, -3863.587402f, 809.343018f), p.At);
        Assert.Equal(("RIVER", "water00"), (p.Kind, p.MeshName));
        // In the dungeon's frame, as its rooms: height 16000 + 809, where zone 226's rooms put their water (16810).
        Assert.Equal(16809.34f, p.Height, 2);
    }

    [Fact]
    public void APlaneWithoutATypeIsRiverStyleWater()
    {
        // Cetus' Pit (078) writes its only plane without the type column.
        DungeonWaterPlane p = Assert.Single(DungeonWater.Parse(Row[..Row.LastIndexOf(',')]));
        Assert.Equal((0, "RIVER"), (p.Type, p.Kind));
    }

    [Theory]
    [InlineData(4, "LAKE")]
    [InlineData(6, "LAVA")]
    [InlineData(0, "RIVER")]
    public void TheTypeIsTheClients(int type, string kind) =>
        Assert.Equal(kind, new DungeonWaterPlane(0, new Vector3[4], Vector3.Zero, type).Kind);

    [Fact]
    public void APlaneIsATenByTenGridOverItsCornersPlacedLikeTheRooms()
    {
        DungeonWaterPlane p = DungeonWater.Parse(Row)[0];
        Vector3[] grid = DungeonWater.RegionGrid(p);
        Assert.Equal(121, grid.Length);
        Assert.Equal(DungeonLayout.FramePoint(p.At + p.Corners[0]), grid[0]);
        Assert.Equal(DungeonLayout.FramePoint(p.At + p.Corners[2]), grid[120]);
        // The dungeon's 180° node: frame (x, y) lands at region (32000 − x, 32000 + y).
        Assert.Equal(32000 - (7798.107422f - 100), grid[0].X, 2);
        Assert.Equal(32000 + (-3863.587402f + 100), grid[0].Y, 2);

        NifMesh mesh = DungeonWater.BuildMesh(p);
        Assert.Equal(("water00", 121, 200), (mesh.Name, mesh.Vertices.Length, mesh.Triangles.Length));
        Assert.Equal(new Vector3(grid[0].X, -grid[0].Y, grid[0].Z), mesh.Vertices[0]);   // water.glb's frame (ZoneWater)
        Assert.Equal(("watery.dds", SurfaceAlpha.Blend, true), (mesh.TextureFile, mesh.Alpha, mesh.DoubleSided));
        // Facing up, the texture once per 512 units: 1000 × 900 units.
        foreach ((ushort a, ushort b, ushort c) in mesh.Triangles)
            Assert.True(Vector3.Cross(mesh.Vertices[b] - mesh.Vertices[a], mesh.Vertices[c] - mesh.Vertices[a]).Z > 0);
        Assert.Equal(1000 / 512f, mesh.TexCoords![10].X, 3);
        Assert.Equal(900 / 512f, mesh.TexCoords![110].Y, 3);
    }

    [Fact]
    public void ThePlanesRoomsAreReadAndWrittenToWaterCsv()
    {
        Dictionary<int, List<int>> rooms = DungeonWater.ParseRooms("146, 0\r\n149, 0\r\n256, 1\r\n");
        Assert.Equal([146, 149], rooms[0]);
        string row = DungeonWater.CsvRow(DungeonWater.Parse(Row)[0], rooms[0]);
        Assert.Equal("water00,plane 0 rooms 146 149,RIVER,16809.34,watery,,0,0", row);
        WaterRow back = ZoneWater.ReadCsv(ZoneWater.CsvHeader + "\n" + row)["water00"];
        Assert.Equal(("watery", 0f, "RIVER"), (back.Texture, back.Flow, back.Type));
    }

    [Theory]
    [InlineData("anim001", true)]
    [InlineData("Anim_torch", true)]
    [InlineData("anim01", false)]     // under 7 characters
    [InlineData("door001s", true)]
    [InlineData("DOORabcS", true)]
    [InlineData("door001", false)]
    [InlineData("door0001s", false)]
    [InlineData("visible", false)]
    public void ADungeonsAnimatedPartsAndDoorsKeepTheirMaterial(string name, bool keeps) =>
        Assert.Equal(keeps, NifFile.KeepsMaterialName(name));
}
