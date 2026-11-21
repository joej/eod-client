// SPDX-License-Identifier: GPL-3.0-or-later

using DaocProtocol.Net;
using DaocProtocol.Packets;
using DaocProtocol.Session;

namespace DaocProtocol.Tests;

/// <summary>The server's sound packets, as OpenDAoC writes them (PacketLib168).</summary>
public sealed class SoundPacketTests
{
    [Fact]
    public void RegionSoundIsThePlayerTheTypeAndTheValue()
    {
        // SendRegionEnterSound(6): player object id, 2 (region enter), the cc_music row.
        RegionSound s = RegionSound.Parse([0x01, 0x2C, 0x02, 0x06]);
        Assert.Equal(new RegionSound(300, RegionSound.AreaMusic, 6), s);
    }

    [Fact]
    public void PlaySoundIsTheTypeAndIdThenPadding()
    {
        // SendPlaySound(Craft, 4): type, id, 8 zero bytes.
        PlaySound p = PlaySound.Parse([0x00, 0x01, 0x00, 0x04, 0, 0, 0, 0, 0, 0, 0, 0]);
        Assert.Equal(new PlaySound(PlaySound.Craft, 4), p);
        Assert.Equal((ushort)925, PlaySound.CraftSound(p.Id));   // masterpiece
        Assert.Null(PlaySound.CraftSound(0));
    }

    [Fact]
    public void SoundEffectIsTheIdZonePositionAndRadius()
    {
        // SendSoundEffect(412, 0, 0, 0, 0, 0), as /cast sound sends it: 2D.
        SoundEffect flat = SoundEffect.Parse([0x01, 0x9C, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0]);
        Assert.Equal((ushort)412, flat.SoundId);
        Assert.False(flat.Positional);
        SoundEffect at = SoundEffect.Parse([0x00, 0x13, 0x00, 0x01, 0x10, 0x00, 0x20, 0x00, 0x01, 0x00, 0x03, 0xE8]);
        Assert.Equal(new SoundEffect(19, 1, 0x1000, 0x2000, 0x100, 1000), at);
        Assert.True(at.Positional);
    }

    [Fact]
    public void TheBridgeRaisesEachOne()
    {
        var bridge = new NetworkBridge();
        RegionSound? region = null;
        PlaySound? play = null;
        SoundEffect? effect = null;
        bridge.RegionSoundArrived += s => region = s;
        bridge.SoundPlayed += s => play = s;
        bridge.SoundEffectArrived += s => effect = s;
        bridge.Dispatch(new ServerPacket(RegionSound.Code, [0, 1, 1, 2]));
        bridge.Dispatch(new ServerPacket(PlaySound.Code, [0, 2, 0, 19, 0, 0, 0, 0, 0, 0, 0, 0]));
        bridge.Dispatch(new ServerPacket(SoundEffect.Code, [0, 19, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0]));
        Assert.Equal(new RegionSound(1, RegionSound.LevelUp, 2), region);
        Assert.Equal(new PlaySound(PlaySound.Sound2D, 19), play);
        Assert.Equal((ushort)19, effect?.SoundId);
    }
}
