// SPDX-License-Identifier: GPL-3.0-or-later

using DaocProtocol.Net;
using DaocProtocol.Packets;
using DaocProtocol.Session;

namespace DaocProtocol.Tests;

/// <summary>Vitals, death and release packets: hand-built bytes in OpenDAoC's writer layouts, and the retail
/// release timer's opening bytes (daoc-re, 2026-11-05).</summary>
public class VitalsPacketTests
{
    [Fact]
    public void AStatusUpdateReadsPercentagesThenBigEndianValues()
    {
        byte[] payload =
        [
            40, 75, 2, 90, 0, 0,                 // health 40%, mana 75%, sitting, endurance 90%, concentration 0, unknown
            0x00, 0xC8, 0x00, 0x64, 0x00, 0x00,  // max mana 200, max endurance 100, max concentration 0
            0x01, 0x2C, 0x00, 0x78,              // max health 300, health 120
            0x00, 0x5A, 0x00, 0x96, 0x00, 0x00,  // endurance 90, mana 150, concentration 0
        ];
        StatusUpdate s = StatusUpdate.Parse(payload);
        Assert.Equal((byte)40, s.HealthPercent);
        Assert.Equal((byte)75, s.ManaPercent);
        Assert.True(s.IsSitting);
        Assert.Equal((byte)90, s.EndurancePercent);
        Assert.Equal((ushort)200, s.MaxMana);
        Assert.Equal((ushort)100, s.MaxEndurance);
        Assert.Equal((ushort)300, s.MaxHealth);
        Assert.Equal((ushort)120, s.Health);
        Assert.Equal((ushort)90, s.Endurance);
        Assert.Equal((ushort)150, s.Mana);
        Assert.Equal(payload, s.ToBytes());
    }

    [Fact]
    public void DeathAndReviveCarryTheObjectIds()
    {
        PlayerDied died = PlayerDied.Parse([0x01, 0x02, 0x0A, 0x0B, 0, 0, 0, 0]);
        Assert.Equal((ushort)0x0102, died.VictimId);
        Assert.Equal((ushort)0x0A0B, died.KillerId);
        Assert.Equal(new byte[] { 0x01, 0x02, 0x0A, 0x0B, 0, 0, 0, 0 }, died.ToBytes());
        Assert.Equal((ushort)0x0102, PlayerRevived.Parse([0x01, 0x02, 0, 0]).ObjectId);
    }

    [Fact]
    public void TheReleaseTimerOpensForNineHundredSecondsAndAnEmptyOneCloses()
    {
        byte[] retail = [0x03, 0x84, 0x0D, 0x01, .. "Release Timer"u8];
        TimerWindow open = TimerWindow.Parse(retail);
        Assert.Equal((ushort)900, open.Seconds);
        Assert.Equal("Release Timer", open.Title);
        Assert.False(open.IsClose);
        Assert.Equal(retail, open.ToBytes());

        TimerWindow close = TimerWindow.Parse([0, 0, 0, 0]);
        Assert.True(close.IsClose);
        Assert.Equal(new byte[] { 0, 0, 0, 0 }, close.ToBytes());
    }

    [Fact]
    public void TheBridgeRaisesEachOne()
    {
        var bridge = new NetworkBridge();
        StatusUpdate? status = null;
        PlayerDied? died = null;
        PlayerRevived? revived = null;
        TimerWindow? timer = null;
        bridge.StatusChanged += s => status = s;
        bridge.PlayerDied += d => died = d;
        bridge.PlayerRevived += r => revived = r;
        bridge.TimerChanged += t => timer = t;

        bridge.Dispatch(new ServerPacket(0xAD, new StatusUpdate(0, 100, 0, 100, 0, 0, 100, 0, 300, 0, 100, 0, 0).ToBytes()));
        bridge.Dispatch(new ServerPacket(0xAE, new PlayerDied(7, 9).ToBytes()));
        bridge.Dispatch(new ServerPacket(0x89, new PlayerRevived(7).ToBytes()));
        bridge.Dispatch(new ServerPacket(0xF3, new TimerWindow(900, "Release Timer").ToBytes()));

        Assert.Equal((ushort)300, status?.MaxHealth);
        Assert.Equal(new PlayerDied(7, 9), died);
        Assert.Equal(new PlayerRevived(7), revived);
        Assert.Equal((ushort)900, timer?.Seconds);
        Assert.Empty(bridge.UnhandledCounts);
    }
}
