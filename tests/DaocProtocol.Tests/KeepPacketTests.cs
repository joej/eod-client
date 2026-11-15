// SPDX-License-Identifier: GPL-3.0-or-later

using DaocProtocol.Net;
using DaocProtocol.Packets;

namespace DaocProtocol.Tests;

/// <summary>Keep hookpoint packets, against bytes written the way OpenDAoC's writers write them.</summary>
public class KeepPacketTests
{
    [Fact]
    public void TheInteractRequestIsFourBigEndianShorts()
    {
        byte[] payload = KeepComponentInteract.Payload(9, 2, KeepComponentInteract.SelectHookpoint, 0x41);
        Assert.Equal(new byte[] { 0, 9, 0, 2, 0, 2, 0, 0x41 }, payload);
    }

    [Fact]
    public void HookpointsListTheFreeIds()
    {
        // PacketLib170.SendKeepComponentHookPoint: keep 9, component 2, 3 free, selected 0, ids 1, 0x41, 0x81.
        KeepHookpoints h = KeepHookpoints.Parse([0, 9, 0, 2, 3, 0, 0x01, 0x41, 0x81]);
        Assert.Equal((ushort)9, h.KeepId);
        Assert.Equal((ushort)2, h.ComponentId);
        Assert.Equal(new byte[] { 0x01, 0x41, 0x81 }, h.Free);
    }

    [Fact]
    public void ASelectionAnswerHasNoHookpoints()
    {
        // PacketLib170.SendClearKeepComponentHookPoint.
        KeepHookpoints h = KeepHookpoints.Parse([0, 9, 0, 2, 0, 0x41]);
        Assert.Empty(h.Free);
        Assert.Equal((byte)0x41, h.Selected);
    }

    [Fact]
    public void TheStoreReadsEveryRow()
    {
        // PacketLib1127.SendHookPointStore: two rows (flag 0x0102, then 0), prices 500 and 2000.
        byte[] payload = new PayloadWriter()
            .U8(2).U8(0).U8(1).U8(1).U8(1).U16LE(9).U16LE(2).U16LE(0x41)
            .U8(0).U8(1).U8(2).Zeros(4).U16LE(0).U32LE(500).U16LE(0x1234).StringIntLE("Ballista")
            .U8(1).Zeros(6).U16LE(0).U32LE(2000).U16LE(0x0F00).StringIntLE("Trebuchet")
            .ToArray();
        HookpointStore s = HookpointStore.Parse(payload);
        Assert.Equal(((ushort)9, (ushort)2, (ushort)0x41), (s.KeepId, s.ComponentId, s.HookpointId));
        Assert.Equal(["Ballista", "Trebuchet"], s.Items.Select(i => i.Name));
        Assert.Equal([500u, 2000u], s.Items.Select(i => i.Price));
        Assert.Equal((ushort)0x1234, s.Items[0].Model);
    }

    [Fact]
    public void AnEmptyStoreHasNoRows()
    {
        HookpointStore s = HookpointStore.Parse(new PayloadWriter().U8(0).U8(0).U8(1).U8(1).U8(1).U16LE(9).U16LE(2).U16LE(0x21).ToArray());
        Assert.Empty(s.Items);
    }
}
