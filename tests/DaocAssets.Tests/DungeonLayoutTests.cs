// SPDX-License-Identifier: GPL-3.0-or-later

using System.Numerics;
using DaocAssets.Mpk;
using DaocAssets.Nif;
using DaocAssets.Zone;

namespace DaocAssets.Tests;

/// <summary>A dungeon's rooms (dungeon.chunk + dungeon.place) and props (dungeon.prop), placed as the client places them.</summary>
public sealed class DungeonLayoutTests
{
    private const string Chunk = "hall.NIF\r\nroom.NIF\r\ndoor.nif\r\n";

    [Fact]
    public void RoomsAreNumberedByRowAndNameTheirPieceByLine()
    {
        DungeonLayout d = DungeonLayout.Parse(Chunk,
            "1, 100.0, 200.0, 300.0, 0.0, 1.0, 0.0, 0.0\n" +
            "0, 0.0, 0.0, 0.0, 1.570796, 0.0, 0.0, -1.0, 1, -40.5, 4, 0, 0\n" +
            "7, 0.0, 0.0, 0.0, 0.0, 1.0, 0.0, 0.0\n",   // piece 7 isn't in dungeon.chunk
            "2, -651.0, 0.0, 12.0, 1.570796, 0.0, 0.0, 1.0, 0, 1, 1.5\n" +
            "2, 0, 0, 0, 0, 1, 0, 0, 0, 9, 1\n");          // room 9 doesn't exist

        Assert.Equal(2, d.Rooms.Count);
        Assert.Equal(new DungeonRoom(0, "room.NIF", 100, 200, 300, 0, 1, 0, 0), d.Rooms[0]);
        Assert.Equal(1, d.Rooms[1].Id);
        Assert.Equal("hall.NIF", d.Rooms[1].Piece);
        Assert.True(d.Rooms[1].Sky);
        Assert.Equal(-40.5f, d.Rooms[1].WaterHeight);
        Assert.Equal(4, d.Rooms[1].WaterType);
        DungeonProp prop = Assert.Single(d.Props);
        Assert.Equal(("door.nif", 1, 1.5f), (prop.Piece, prop.Room, prop.Scale));
        Assert.Equal(2, d.Skipped.Count);
    }

    [Fact]
    public void ALandingOffTheRoomsMovesToTheNearestFloorNotOntoARoof()
    {
        // One room: a floor 400 units square at its height and a ceiling 200 above it (NIF z is up).
        DungeonLayout d = DungeonLayout.Parse(Chunk, "0, 0, 0, 0, 0, 1, 0, 0\n", "");
        (Vector3, Vector3, Vector3)[] Square(float z) =>
            [(new(-200, -200, z), new(200, -200, z), new(200, 200, z)), (new(-200, -200, z), new(200, 200, z), new(-200, 200, z))];
        var tris = Square(0).Concat(Square(200)).ToList();

        // A doorway on the floor stays where it is.
        var onFloor = new Vector3(32000, 32050, 16040);
        Assert.Equal(onFloor, DungeonCheck.Landing(d, onFloor, _ => tris));
        // One out in the dark moves onto the floor (16000 + 40), not onto the roof (16200), which has nothing over it.
        Vector3 moved = Assert.IsType<Vector3>(DungeonCheck.Landing(d, new Vector3(34000, 32000, 16240), _ => tris));
        Assert.Equal(16040, moved.Z, 1);
        Assert.InRange(moved.X, 31800, 32200);
        // No doorway: the floor nearest the first room.
        Assert.Equal(16040, Assert.IsType<Vector3>(DungeonCheck.Landing(d, null, _ => tris)).Z, 1);
        // Without a ceiling, nothing counts as a floor to move to (a roof can't be told from one).
        Assert.Null(DungeonCheck.Landing(d, new Vector3(34000, 32000, 16240), _ => Square(0).ToList()));

        // A spot in the exit doorway (a jump point's line near the room's edge) moves into the room, clear of it: one
        // step from the doorway's middle took the character out of Muire Tomb.
        var exit = new DungeonCheck.Doorway(new(31900, 32190), new(32100, 32190), 15990, 16200);
        Vector3 inside = Assert.IsType<Vector3>(DungeonCheck.Landing(d, new Vector3(32000, 32190, 16040), _ => tris, [exit]));
        Assert.True(exit.Distance(inside) >= DungeonCheck.DoorwayClearance);
        Assert.True(inside.Y < 32190);
        // A doorway on another storey doesn't count.
        var below = exit with { ZLow = 14000, ZHigh = 14200 };
        Assert.Equal(new Vector3(32000, 32190, 16040), DungeonCheck.Landing(d, new Vector3(32000, 32190, 16040), _ => tris, [below]));
    }

    [Fact]
    public void AnUnturnedRoomIsPlacedByTheCitiesRuleAtHeight16000()
    {
        // The capitals' pieces, turned 180°, land at (32000 − x, 32000 + y, 8000 + z) (ZoneLayout.ParseCity); a dungeon's
        // rooms hang under the same node at height 16000.
        DungeonLayout d = DungeonLayout.Parse(Chunk, "0, 100, 200, 300, 0, 1, 0, 0\n", "");
        Assert.Equal(new Vector3(31900, 32200, 16300), DungeonLayout.RegionPoint(d.Rooms[0], Vector3.Zero));
        Assert.Equal(new Vector3(31890, 32220, 16330), DungeonLayout.RegionPoint(d.Rooms[0], new Vector3(10, 20, 30)));

        ZoneFixture f = Assert.Single(d.ToZoneLayout().Fixtures);
        Assert.Equal((31900f, 32200f, 16300f), (f.X, f.Y, f.Z));
        // The manifest's turn is the city's: angle 180 about the vertical.
        var city = new ZonePlacement("x", 0, 0, 0, 180, 1);
        AssertSameTurn(city.Rotation, Placement(f).Rotation);
    }

    [Fact]
    public void ARoomsAngleTurnsItAsMeasuredByItsDoorwaysAndTheServersNpcs()
    {
        // Measured 2026-11-17 (DungeonLayout's notes): +π/2 about +z takes the piece's +x, which an unturned room puts at
        // region −x, to region −y.
        DungeonLayout d = DungeonLayout.Parse(Chunk, "0, 0, 0, 0, 1.5707964, 0, 0, 1\n", "");
        Vector3 at = DungeonLayout.RegionPoint(d.Rooms[0], new Vector3(100, 0, 0)) - DungeonLayout.RegionPoint(d.Rooms[0], Vector3.Zero);
        Assert.Equal(0, at.X, 3);
        Assert.Equal(-100, at.Y, 3);
    }

    [Fact]
    public void TheManifestPlacesRoomsAndPropsWhereTheChecksDo()
    {
        // The client turns a placement by ZonePlacement.Rotation (the manifest's tilt and axis); the checks place points by
        // DungeonLayout.RegionPoint. Both must agree, for rooms and for a prop turned and moved inside its room.
        DungeonLayout d = DungeonLayout.Parse(Chunk,
            "0, 1500.0, 3000.0, 13.0, 1.0, 0.0, 0.0, -1.0\n1, -730.0, 0.0, -4.0, 2.4, 0.0, 0.0, 1.0\n",
            "2, -651.0, 40.0, 387.0, 1.570796, 0.0, 0.0, 1.0, 0, 0, 1.0\n");
        IReadOnlyList<ZoneFixture> fixtures = d.ToZoneLayout().Fixtures;
        Assert.Equal(3, fixtures.Count);
        var point = new Vector3(37, -120, 55);
        for (int i = 0; i < 2; i++)
            AssertNear(DungeonLayout.RegionPoint(d.Rooms[i], point), Place(fixtures[i], point));

        // The prop: its own turn and offset inside room 0 (a piece placed at the prop's spot within the room).
        DungeonRoom room = d.Rooms[0];
        Vector3 propOrigin = DungeonLayout.RegionPoint(room, new Vector3(-651, 40, 387));
        AssertNear(propOrigin, new Vector3(fixtures[2].X, fixtures[2].Y, fixtures[2].Z));
        // A point 100 along the prop's +x: the prop's quarter turn about +z takes it to the room's −y (the angle turns
        // clockwise seen from above, as a room's does).
        AssertNear(DungeonLayout.RegionPoint(room, new Vector3(-651, -60, 387)), Place(fixtures[2], new Vector3(100, 0, 0)));
    }

    [Fact]
    public void TwoRoomsJoinedAtTheirDoorwaysMeet()
    {
        // Two copies of a hall with doorways at its two ends, ±256 along x: side by side, 512 apart.
        DungeonLayout d = DungeonLayout.Parse("hall.nif\n",
            "0, 0, 0, 0, 0, 1, 0, 0\n0, 512, 0, 0, 0, 1, 0, 0\n0, 100, 900, 0, 0, 1, 0, 0\n", "");
        var portals = new Dictionary<string, Vector3> { ["portal01"] = new(256, 0, 128), ["portal02"] = new(-256, 0, 128) };
        List<PortalLink> links = DungeonCheck.Check(d,
            DungeonCheck.ParsePortals("3\n0, 1, 1, 1\n1, 2, 0, 1\n0, 2, 2, 1\n"), _ => portals);
        Assert.Equal(0f, links[0].Gap!.Value, 3);
        Assert.Equal(0f, links[1].Gap!.Value, 3);
        Assert.True(links[2].Gap > 500);   // room 2 is elsewhere
    }

    [Fact]
    public void ADungeonPiecesRoomVolumeAttachPointsAndDoorwaysAreNotDrawn()
    {
        Assert.True(NifFile.IsDungeonHelper("dungswitch", "bounding"));
        Assert.True(NifFile.IsDungeonHelper("DungSwitch", "Attachpoints"));
        Assert.True(NifFile.IsDungeonHelper("dungswitch", "portal01"));
        Assert.True(NifFile.IsDungeonHelper("dungswitch", "waterproxy"));
        Assert.False(NifFile.IsDungeonHelper("dungswitch", "visible"));
        Assert.False(NifFile.IsDungeonHelper("dungswitch", "portal01a"));
        Assert.False(NifFile.IsDungeonHelper("scene root", "bounding"));   // only a dungeon piece's
    }

    [LiveTheory("live-mpk/dat060.mpk", "live-mpk/dat079.mpk")]
    [InlineData("dat060", 161, 0)]
    [InlineData("dat079", 169, 75)]
    public void ADungeonsRoomsAndPropsAreReadFromItsZoneArchive(string dat, int rooms, int props)
    {
        using MpkArchive mpk = MpkArchive.Open(Path.Combine(AppContext.BaseDirectory, $"live-mpk/{dat}.mpk"));
        Assert.True(DungeonLayout.IsDungeon(mpk));
        DungeonLayout d = DungeonLayout.Read(mpk);
        Assert.Equal((rooms, props, 0), (d.Rooms.Count, d.Props.Count, d.Skipped.Count));
        Assert.Equal(rooms + props, ZoneLayout.Read(mpk, 8192, 8192).Fixtures.Count);
    }

    [LiveFact("live-mpk/dat012.mpk")]
    public void AnOutdoorZoneIsNoDungeon()
    {
        using MpkArchive mpk = MpkArchive.Open(Path.Combine(AppContext.BaseDirectory, "live-mpk/dat012.mpk"));
        Assert.False(DungeonLayout.IsDungeon(mpk));
    }

    private static ZonePlacement Placement(ZoneFixture f) =>
        new("x", f.X, f.Y, f.Z, f.AngleDeg, f.Scale, 1, f.Tilt, f.AxisX, f.AxisY, f.AxisZ);

    // Where the client draws a NIF point of a placed model: glTF (x, z, −y), turned, then the frame offset back to region
    // axes (WorldView.ToGodot), as ZoneCheck places a model's box.
    private static Vector3 Place(ZoneFixture f, Vector3 nif)
    {
        Vector3 g = Vector3.Transform(new Vector3(nif.X, nif.Z, -nif.Y), Placement(f).Rotation) * f.Scale;
        return new Vector3(f.X + g.X, f.Y + g.Z, f.Z + g.Y);
    }

    private static void AssertNear(Vector3 want, Vector3 got)
    {
        Assert.True(Vector3.Distance(want, got) < 0.05f, $"want {want}, got {got}");
    }

    private static void AssertSameTurn(Quaternion a, Quaternion b) =>
        Assert.True(MathF.Abs(Quaternion.Dot(Quaternion.Normalize(a), Quaternion.Normalize(b))) > 0.9999f, $"{a} vs {b}");
}
