// SPDX-License-Identifier: GPL-3.0-or-later

using DaocProtocol.Net;
using DaocProtocol.Packets;

namespace DaocProtocol.Tests;

/// <summary>
/// Attack mode (0x74 both ways) and how other players show it (bit 0x80 of the health byte in S→C 0xA9 and
/// 0xBA). The recorded 1.127e session has two S→C 0x74 and no S→C 0xA9 / 0xBA (it was alone in the world),
/// so those two are built here in OpenDAoC's field order (CreateOutPak1127 in PlayerPositionUpdateHandler
/// and PlayerHeadingUpdateHandler).
/// </summary>
public class CombatPacketTests
{
    [Fact]
    public void TheRecordedAttackModePacketsSayOff()
    {
        var modes = CaptureFile.Login1127e().ServerPackets().Where(p => p.Code == AttackMode.Code).ToList();
        Assert.Equal(2, modes.Count);
        Assert.All(modes, p => Assert.Equal(new byte[] { 0, 0, 0, 0 }, p.Payload));
        Assert.All(modes, p => Assert.False(AttackMode.IsOn(p.Payload)));
        Assert.True(AttackMode.IsOn(new byte[] { 1, 0, 0, 0 }));
    }

    [Fact]
    public void AnAttackRequestSaysStartOrStopAndThatThePlayerAsked()
    {
        Assert.Equal(new byte[] { 1, 0 }, AttackRequest.Payload(start: true));
        Assert.Equal(new byte[] { 0, 0 }, AttackRequest.Payload(start: false));
        Assert.Equal(new byte[] { 0, 1 }, AttackRequest.Payload(start: false, byPlayer: false));
    }

    [Fact]
    public void AnotherPlayersPositionCarriesItsAttackModeInTheHealthByte()
    {
        byte[] payload = new PayloadWriter()
            .F32LE(574797).F32LE(530161).F32LE(2896).F32LE(191).F32LE(0)
            .U16BE(7).U16BE(1234).U16BE(0)          // session, object, zone
            .U8(0x08).U8(0).U16BE(0)                 // state flags, 0, steed seat
            .U16BE(0x1000 | 3116).U8(0x80).U8(1).U8(0)   // heading (with the client's 0x1000), action, RP flag, 0
            .U8(0x80 | 87).U8(100).U8(95).U16BE(0)  // health 87 % in attack mode, mana, endurance, 0
            .ToArray();
        Assert.Equal(40, payload.Length);
        PlayerPosition p = PlayerPosition.Parse(payload);
        Assert.Equal((574797f, 530161f, 2896f, 191f), (p.X, p.Y, p.Z, p.Speed));
        Assert.Equal((7, 1234, 0), (p.SessionId, p.ObjectId, p.ZoneId));
        Assert.Equal((0x08, 3116, 0x80), (p.StateFlags, p.HeadingOnly, p.ActionFlags));
        Assert.True(p.InCombat);
        Assert.Equal(87, p.HealthPercent);
        payload[35] = 87;
        Assert.False(PlayerPosition.Parse(payload).InCombat);
    }

    [Fact]
    public void AnotherPlayersHeadingNamesItBySessionAndCarriesItsAttackMode()
    {
        byte[] payload = new PayloadWriter()
            .U16BE(7).U16BE(0).U16BE(2048)          // session, target, heading
            .U8(0).U8(0x80).U8(0).U8(0x02)           // steed seat, action, 0, state
            .U8(0x80 | 100).U8(100).U8(100).U8(0)   // health in attack mode, mana, endurance, unknown
            .ToArray();
        PlayerHeading h = PlayerHeading.Parse(payload);
        Assert.Equal((7, 2048, 0x80, 0x02), (h.SessionId, h.HeadingOnly, h.ActionFlags, h.StateFlags));
        Assert.True(h.InCombat);
        payload[10] = 100;
        Assert.False(PlayerHeading.Parse(payload).InCombat);
    }
}
