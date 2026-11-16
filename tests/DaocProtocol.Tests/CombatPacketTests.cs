// SPDX-License-Identifier: GPL-3.0-or-later

using DaocProtocol.Net;
using DaocProtocol.Packets;

namespace DaocProtocol.Tests;

/// <summary>
/// Attack mode (0x74 both ways) and how other players show it (bit 0x80 of the health byte in S→C 0xA9 and
/// 0xBA). The recorded 1.127e session has two S→C 0x74 and no S→C 0xA9 / 0xBA (it was alone in the world),
/// so those two are built here in OpenDAoC's field order (CreateOutPak1127 in PlayerPositionUpdateHandler
/// and PlayerHeadingUpdateHandler). Also the swings (S→C 0xBC).
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

    [Theory]
    [InlineData(0x00, PlayerPosture.Normal)]
    [InlineData(0x04, PlayerPosture.Swim)]
    [InlineData(0x08, PlayerPosture.Jump)]
    [InlineData(0x0C, PlayerPosture.Fly)]
    [InlineData(0x10, PlayerPosture.Sit)]
    [InlineData(0x14, PlayerPosture.Dead)]
    [InlineData(0x18, PlayerPosture.Ride)]
    [InlineData(0x1C, PlayerPosture.Climb)]
    [InlineData(0x10 | 0x80 | 0x20, PlayerPosture.Sit)]   // strafe bits don't touch it
    public void APlayersPostureIsBitsTwoToFourOfItsStateByteAndTheLowThreeOfTheHeadingPackets(int state, PlayerPosture posture)
    {
        byte[] position = new PayloadWriter()
            .F32LE(0).F32LE(0).F32LE(0).F32LE(0).F32LE(0).U16BE(7).U16BE(1234).U16BE(0)
            .U8((byte)state).U8(0).U16BE(0).U16BE(0x1000).U8(0).U8(0).U8(0).U8(100).U8(100).U8(100).U16BE(0)
            .ToArray();
        Assert.Equal(posture, PlayerPosition.Parse(position).Posture);
        // 0xBA carries the same byte shifted right by 2, strafe bits dropped (OpenDAoC PlayerHeadingUpdateHandler).
        byte[] heading = new PayloadWriter()
            .U16BE(7).U16BE(0).U16BE(0).U8(0).U8(0).U8(0).U8((byte)((state & 0x1C) >> 2)).U8(100).U8(100).U8(100).U8(0)
            .ToArray();
        Assert.Equal(posture, PlayerHeading.Parse(heading).Posture);
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

    // 0xBC: hand-built in OpenDAoC's writer layout (PacketLib186.SendCombatAnimation); the recording has none.
    [Fact]
    public void ACombatAnimationReadsTheStyleLittleEndian()
    {
        byte[] payload =
        [
            0x41, 0x67, 0x2A, 0x93,   // attacker 0x4167, defender 0x2A93
            0x00, 0x03, 0x00, 0x3B,   // weapon model 3, shield model 59
            0x9A, 0x00,               // style 154, little-endian
            0x00, 0x0B, 0x37, 0x00,   // time 0, styled hit, defender at 55 %, one zero byte
        ];
        CombatAnimation c = CombatAnimation.Parse(payload);
        Assert.Equal(new CombatAnimation(0x4167, 0x2A93, 3, 59, 154, 0, CombatAnimation.StyledHit, 55), c);
        Assert.Equal(payload, c.ToBytes());
        Assert.True(c.Swings);
        Assert.True(c.IsHit);
        Assert.Equal(154, c.StyleId);
    }

    [Theory]
    // A bleed (no attacker), a trap or area hit (0x14), a parry, a bow shot (its code isn't a style).
    [InlineData(0, 0, 0x0A, false, true, 0)]
    [InlineData(0x4167, 0, 0x14, false, true, 0)]
    [InlineData(0x4167, 0, 1, true, false, 0)]
    [InlineData(0x4167, 0x3E83, 0x0A, true, true, 0)]
    public void WhatTheClientShows(int attacker, int style, int result, bool swings, bool hit, int styleId)
    {
        var c = new CombatAnimation((ushort)attacker, 0x2A93, 0, 0, (ushort)style, 0, (byte)result, 80);
        Assert.Equal((swings, hit, styleId), (c.Swings, c.IsHit, (int)c.StyleId));
    }
}
