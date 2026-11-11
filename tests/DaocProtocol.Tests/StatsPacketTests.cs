// SPDX-License-Identifier: GPL-3.0-or-later

using DaocProtocol.Net;
using DaocProtocol.Packets;
using DaocProtocol.Session;

namespace DaocProtocol.Tests;

/// <summary>
/// 0xFB StatsUpdate from the RustDAoC 1.127e capture (a level 50 Briton Paladin): the stats kind and the resists
/// kind both decode to the server's numbers and rebuild byte for byte.
/// </summary>
public class StatsPacketTests
{
    private static readonly List<ServerPacket> Fb =
        CaptureFile.Login1127e().ServerPackets().Where(p => p.Code == StatsUpdate.Code).ToList();

    [Fact]
    public void EveryRecordedOneRebuilds()
    {
        Assert.True(Fb.Count >= 2);
        foreach (ServerPacket p in Fb)
            Assert.Equal(p.Payload, StatsUpdate.Parse(p.Payload).ToBytes());
    }

    [Fact]
    public void StatsReadBaseItemsCapsAndHealth()
    {
        StatsUpdate s = StatsUpdate.Parse(Fb.First(p => p.Payload[72] == 0).Payload);
        Assert.False(s.IsResists);
        // Strength, dexterity, constitution, quickness, intelligence, piety, empathy, charisma.
        Assert.Equal(new short[] { 95, 50, 125, 50, 60, 93, 60, 60, 0 }, s.Base);
        Assert.Equal(new short[] { 101, 101, 101, 101, 0, 101, 0, 0, 0 }, s.Item);
        // The item cap: 75 at level 50, + 26 cap bonus (OpenDAoC caps the bonus at level / 2 + 1); intelligence has none.
        Assert.Equal(new byte[] { 101, 101, 101, 101, 75, 101, 75, 75, 0 }, s.ItemCap);
        Assert.Equal((ushort)1848, s.MaxHealth);
        Assert.Equal(95 + 101, s.Total(0));
        Assert.Equal(60, s.Total(4));
    }

    [Fact]
    public void ResistsReadRacialAndItems()
    {
        StatsUpdate r = StatsUpdate.Parse(Fb.First(p => p.Payload[72] == 0xFF).Payload);
        Assert.True(r.IsResists);
        // A Briton's racial resists, in the packet's order: crush 3, slash 2, cold 5 … (thrust, heat 0).
        Assert.Equal(new short[] { 3, 2, 0, 0, 5, 0, 0, 0, 0 }, r.Base);
        Assert.All(r.Item, v => Assert.Equal(26, v));
        Assert.Equal(3 + 26, r.Total(0));
        Assert.Equal((ushort)0, r.MaxHealth);
    }

    [Fact]
    public void ItemsAboveTheCapCountOnlyToTheCapAndDebuffsSubtract()
    {
        var s = new StatsUpdate(false, [50, 0, 0, 0, 0, 0, 0, 0, 0], [-10, 0, 0, 0, 0, 0, 0, 0, 0], [90, 0, 0, 0, 0, 0, 0, 0, 0],
            [75, 0, 0, 0, 0, 0, 0, 0, 0], [3, 0, 0, 0, 0, 0, 0, 0, 0], 0, 100);
        Assert.Equal(50 - 10 + 75 + 3, s.Total(0));
        Assert.Equal(-10, StatsUpdate.Parse(s.ToBytes()).Buff[0]);
    }

    [Fact]
    public void TheBridgeRaisesBothKinds()
    {
        var bridge = new NetworkBridge();
        var seen = new List<bool>();
        bridge.StatsChanged += s => seen.Add(s.IsResists);
        foreach (ServerPacket p in Fb)
            bridge.Dispatch(p);
        Assert.Contains(false, seen);
        Assert.Contains(true, seen);
        Assert.False(bridge.UnhandledCounts.ContainsKey(StatsUpdate.Code));
    }
}
