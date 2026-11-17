// SPDX-License-Identifier: GPL-3.0-or-later

using DaocProtocol.Net;
using DaocProtocol.Packets;
using DaocProtocol.Session;

namespace DaocProtocol.Tests;

/// <summary>0x99 both ways: the server's door state and the client's request to open a door.</summary>
public sealed class DoorPacketTests
{
    [Fact]
    public void TheServersDoorStateIsReadAsOpenDaocWritesIt()
    {
        // PacketLib168.SendDoorState for Darkspire's door 98000001 (zone 98, room 0, door001), opened, in a dungeon
        // (no default sound): id u32 BE, state, flag, 0xFF, 0.
        DoorState d = DoorState.Parse([0x05, 0xD7, 0x5C, 0x81, 0x01, 0x00, 0xFF, 0x00]);
        Assert.Equal(new DoorState(98000001, DoorState.Open, 0, 0xFF), d);
        Assert.True(d.IsOpen);
        Assert.False(DoorState.Parse([0x05, 0xD7, 0x5C, 0x81, 0x00, 0x01, 0xFF, 0x00]).IsOpen);
    }

    [Fact]
    public void ARequestCarriesTheIdBigEndianThenOpenOrClose()
    {
        // DoorRequestHandler: ReadInt (the id), ReadByte (1 opens, anything else closes); retail pads to 8 bytes.
        Assert.Equal(0x99, DoorRequest.Code);
        Assert.Equal(new byte[] { 0x05, 0xD7, 0x5C, 0x81, 0x01, 0, 0, 0 }, DoorRequest.Payload(98000001));
        Assert.Equal(new byte[] { 0x05, 0xD7, 0x5C, 0x81, 0x00, 0, 0, 0 }, DoorRequest.Payload(98000001, open: false));
    }

    [Fact]
    public void TheBridgeReportsADoorChange()
    {
        var bridge = new NetworkBridge();
        DoorState? seen = null;
        bridge.DoorChanged += d => seen = d;
        bridge.Dispatch(new ServerPacket(DoorState.Code, [0x0C, 0x2F, 0x5E, 0x75, 0x01, 0x00, 0xFF, 0x00]));
        Assert.Equal(204430965u, seen?.DoorId);
    }
}
