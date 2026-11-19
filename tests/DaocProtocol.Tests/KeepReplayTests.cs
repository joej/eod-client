// SPDX-License-Identifier: GPL-3.0-or-later

using DaocProtocol.Net;
using DaocProtocol.Packets;
using DaocProtocol.World;

namespace DaocProtocol.Tests;

/// <summary>The keep packets built from keep tables, as OpenDAoC would send them.</summary>
public class KeepReplayTests
{
    private static readonly KeepRow Benowyc = new(50, "Caer Benowyc", 163, 576312, 502235, 8776, 270, 1, 4, 50);
    private static readonly KeepRow Portal = new(1, "Albion Portal Keep", 165, 583575, 584548, 4896, 1988, 1, 0, 100);

    [Theory]
    [InlineData(0, 0)] [InlineData(1, 0)] [InlineData(2, 1)] [InlineData(4, 1)] [InlineData(5, 2)] [InlineData(7, 2)]
    [InlineData(8, 3)] [InlineData(10, 3)] [InlineData(11, 4)] [InlineData(16, 5)]
    public void TheBuildHeightFollowsTheLevel(int level, int height) => Assert.Equal(height, KeepReplay.HeightFromLevel(level));

    [Fact]
    public void ARegionsKeepsComeWithTheirComponentsOfOneLook()
    {
        KeepComponentRow[] parts =
        [
            new(50, 2, 31, 0, 0, 0),    // new tower
            new(50, 1, 27, -3, 4, 1),   // new climbing wall
            new(50, 3, 9, -3, 4, 1),    // the old look's wall: not loaded with new keeps
            new(50, 4, 20, 0, 0, 0),    // skin 20 loads with neither
            new(1, 1, 27, 0, 0, 0),     // another region
        ];
        List<ServerPacket> packets = KeepReplay.Build([Benowyc, Portal], parts, 163, newKeeps: true, level: 10);

        Assert.Equal([KeepInfo.Code, KeepComponentInfo.Code, KeepComponentInfo.Code], packets.Select(p => p.Code));
        Assert.Equal(new KeepInfo(50, 576312, 502235, 270, 1, 10), KeepInfo.Parse(packets[0].Payload));
        KeepComponentInfo wall = KeepComponentInfo.Parse(packets[1].Payload), tower = KeepComponentInfo.Parse(packets[2].Payload);
        Assert.Equal((1, 27, -3, 4, 1, 3, 100, KeepComponentInfo.Climbable),
            (wall.ComponentId, wall.Skin, wall.X, wall.Y, wall.Heading, wall.Height, wall.Health, wall.Flags));
        Assert.Equal((2, 0), (tower.ComponentId, tower.Flags));   // towers aren't climbable by default
    }

    [Fact]
    public void APortalKeepsWallsArentClimbable()
    {
        List<ServerPacket> packets = KeepReplay.Build([Portal], [new(1, 1, 9, 0, 0, 0)], 165, newKeeps: false);
        Assert.Equal(0, KeepComponentInfo.Parse(packets[1].Payload).Flags);
        Assert.Equal(0, KeepComponentInfo.Parse(packets[1].Payload).Height);
    }

    [Fact]
    public void DamageAndRaisedTowersCanBeAskedFor()
    {
        KeepRow tower = new(362, "Dun Ailinne Guardtower", 163, 412822, 612291, 8496, 0, 3, 10, 50);
        List<ServerPacket> broken = KeepReplay.Build([Benowyc, tower], [new(50, 1, 27, 0, 0, 0), new(362, 1, 31, 0, 0, 0)], 163, true,
            health: 0, raised: true);
        KeepComponentInfo wall = KeepComponentInfo.Parse(broken[1].Payload), top = KeepComponentInfo.Parse(broken[3].Payload);
        Assert.Equal((0, KeepComponentInfo.Broken), (wall.Health, wall.Flags));   // no climbing on a broken wall
        Assert.Equal(KeepComponentInfo.Broken | KeepComponentInfo.Raised, top.Flags);   // only the tower is raised
    }

    [Fact]
    public void TheReplayFileRoundTripsAndReadsTheTables()
    {
        List<ServerPacket> packets = KeepReplay.Build([Benowyc], [new(50, 1, 27, -3, 4, 1)], 163, true);
        List<ServerPacket> back = KeepReplay.ReadText(KeepReplay.ToText(packets, "two lines\nof header"));
        Assert.Equal(packets.Select(p => (p.Code, Convert.ToHexString(p.Payload))), back.Select(p => (p.Code, Convert.ToHexString(p.Payload))));

        KeepRow k = Assert.Single(KeepReplay.ReadKeeps("""[{"KeepID":50,"Name":"Caer Benowyc","Region":163,"X":576312,"Y":502235,"Z":8776,"Heading":270,"Realm":1,"Level":4,"BaseLevel":50,"KeepType":1}]"""));
        Assert.Equal(Benowyc, k);
        KeepComponentRow c = Assert.Single(KeepReplay.ReadComponents("""[{"KeepID":50,"ID":1,"Skin":27,"X":-3,"Y":4,"Heading":1,"Health":30000}]"""));
        Assert.Equal(new KeepComponentRow(50, 1, 27, -3, 4, 1), c);
    }
}
