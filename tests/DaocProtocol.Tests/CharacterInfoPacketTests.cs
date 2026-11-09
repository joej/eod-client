// SPDX-License-Identifier: GPL-3.0-or-later

using DaocProtocol.Net;
using DaocProtocol.Packets;
using DaocProtocol.Session;

namespace DaocProtocol.Tests;

/// <summary>
/// The character's points, money and identity (0x91, 0xFA, 0x16 subcode 3), read from the RustDAoC 1.127e
/// capture: a level 50 Paladin. Each decodes to sensible values and rebuilds byte for byte.
/// </summary>
public class CharacterInfoPacketTests
{
    private static readonly List<ServerPacket> Server = CaptureFile.Login1127e().ServerPackets();

    private static ServerPacket First(byte code, Func<ServerPacket, bool>? also = null) =>
        Server.First(p => p.Code == code && (also is null || also(p)));

    [Fact]
    public void PointsReadXpAndRealmPoints()
    {
        ServerPacket p = First(PointsUpdate.Code);
        PointsUpdate u = PointsUpdate.Parse(p.Payload);
        Assert.Equal(513_500u, u.RealmPoints);
        Assert.Equal((ushort)91, u.SpecPoints);
        Assert.Equal((ushort)40, u.RealmSpecPoints);
        Assert.Equal(0u, u.BountyPoints);
        // Little-endian u64s: 50 short of 1.7e11 and of 1e12 (the level-50 cap). Read big-endian they'd be absurd.
        Assert.Equal(169_999_999_950ul, u.Experience);
        Assert.Equal(999_999_999_950ul, u.ExperienceForNextLevel);
        Assert.Equal(p.Payload, u.ToBytes());
    }

    [Fact]
    public void MoneyReadsEachCoin()
    {
        ServerPacket p = First(MoneyUpdate.Code);
        MoneyUpdate m = MoneyUpdate.Parse(p.Payload);
        Assert.Equal((byte)0, m.Copper);
        Assert.Equal((ushort)20, m.Gold);
        Assert.Equal(20L * 100 * 100, m.TotalCopper);
        Assert.Equal(p.Payload, m.ToBytes());

        // Each coin in its place, and the total across all five.
        MoneyUpdate all = MoneyUpdate.Parse([3, 2, 0x00, 0x01, 0x00, 0x05, 0x00, 0x04]);
        Assert.Equal((3, 2, 1, 5, 4), (all.Copper, all.Silver, all.Gold, all.Mithril, all.Platinum));
        Assert.Equal((((5L * 1000 + 4) * 1000 + 1) * 100 + 2) * 100 + 3, all.TotalCopper);
    }

    [Fact]
    public void PlayerInfoReadsWhoTheCharacterIs()
    {
        ServerPacket p = First(PlayerInfo.Code, q => PlayerInfo.Matches(q.Payload));
        PlayerInfo i = PlayerInfo.Parse(p.Payload);
        Assert.Equal((byte)50, i.Level);
        Assert.Equal("Lilillyn", i.Name);
        Assert.Equal("Paladin", i.ClassName);
        Assert.Equal("Fighter", i.BaseClass);
        Assert.Equal("Highlander", i.RaceName);
        Assert.Equal("Clan Cotswold", i.GuildName);
        Assert.Equal((ushort)0x0738, i.MaxHealth);   // its two bytes sit apart, around the class name
        Assert.Equal((byte)40, i.RealmLevel);
        Assert.Equal("5L0", i.RealmRank);
        Assert.Equal("Eagle Knight", i.RealmTitle);
        Assert.Equal("", PlayerInfo.Shown(i.CustomTitle));   // the server's "None"
        Assert.Equal(p.Payload, i.ToBytes());
    }

    [Fact]
    public void TheBridgeRaisesEachAndLeavesOther0x16ListsUnhandled()
    {
        var bridge = new NetworkBridge(_ => { });
        PointsUpdate? points = null; MoneyUpdate? money = null; PlayerInfo? info = null;
        bridge.PointsChanged += u => points = u;
        bridge.MoneyChanged += m => money = m;
        bridge.PlayerInfoChanged += i => info = i;
        foreach (ServerPacket p in Server.Where(p => p.Code is PointsUpdate.Code or MoneyUpdate.Code or PlayerInfo.Code))
            bridge.Dispatch(p);
        Assert.Equal(513_500u, points?.RealmPoints);
        Assert.Equal((ushort)20, money?.Gold);
        Assert.Equal("Lilillyn", info?.Name);
        // The skill and spell lists (subcodes 1, 2, 5, 8) aren't read yet.
        Assert.True(bridge.UnhandledCounts.GetValueOrDefault(PlayerInfo.Code) > 0);
    }
}
