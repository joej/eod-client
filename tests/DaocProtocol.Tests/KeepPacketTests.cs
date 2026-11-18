// SPDX-License-Identifier: GPL-3.0-or-later

using DaocProtocol.Net;
using DaocProtocol.Packets;

namespace DaocProtocol.Tests;

/// <summary>Keep packets, against bytes written the way OpenDAoC's writers write them.</summary>
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

    [Fact]
    public void KeepInfoReadsAsOpenDaocWritesIt()
    {
        // PacketLib1124.SendKeepInfo: keep 1029, 0, x 396519, y 618017, heading 2159, Hibernia (3), level 7, 0, model 0, 0.
        byte[] bytes = [0x04, 0x05, 0, 0, 0x00, 0x06, 0x0C, 0xE7, 0x00, 0x09, 0x6E, 0x21, 0x08, 0x6F, 3, 7, 0, 0, 0, 0];
        KeepInfo k = KeepInfo.Parse(bytes);
        Assert.Equal(new KeepInfo(1029, 396519, 618017, 2159, 3, 7), k);
        Assert.Equal(bytes, k.Payload());
    }

    [Fact]
    public void AComponentsOffsetsAreSigned()
    {
        // PacketLib170.SendKeepComponentInfo: keep 1029, component 4, object 0x1234, skin 9 (wall), x -3, y 2, a quarter
        // turn, height 2, 75 %, climbable, 0.
        byte[] bytes = [0x04, 0x05, 0, 4, 0, 0, 0x12, 0x34, 9, 0xFD, 2, 1, 2, 75, 0x02, 0];
        KeepComponentInfo c = KeepComponentInfo.Parse(bytes);
        Assert.Equal(new KeepComponentInfo(1029, 4, 0x1234, 9, -3, 2, 1, 2, 75, KeepComponentInfo.Climbable), c);
        Assert.Equal(bytes, c.Payload());
    }

    [Fact]
    public void ComponentUpdatesReadEveryComponent()
    {
        // 0x6D: keep 1029, component 4, height 3, 40 %, broken; 0x67: keep 1029, Midgard, level 9, three components.
        Assert.Equal(new KeepComponentDetail(1029, 4, 3, 40, KeepComponentInfo.Broken),
            KeepComponentDetail.Parse([0x04, 0x05, 0, 4, 3, 40, 1, 0]));
        KeepUpdate u = KeepUpdate.Parse([0x04, 0x05, 2, 9, 3, 0x83, 0x43, 0x13, 0]);
        Assert.Equal((2, 9), (u.Realm, u.Level));
        Assert.Equal(new byte[] { 0x83, 0x43, 0x13 }, u.Components);
        Assert.Equal(3, u.Components[0] & KeepUpdate.HeightMask);
    }

    [Fact]
    public void TheBridgeRaisesTheKeepEvents()
    {
        var bridge = new Session.NetworkBridge();
        var seen = new List<string>();
        bridge.KeepArrived += k => seen.Add($"keep {k.KeepId}");
        bridge.KeepComponentArrived += c => seen.Add($"component {c.ComponentId}");
        bridge.KeepRealmChanged += r => seen.Add($"realm {r.Realm}");
        bridge.KeepRemoved += (k, c) => seen.Add($"removed {k} {c?.ToString() ?? "all"}");
        bridge.Dispatch(new ServerPacket(KeepInfo.Code, new KeepInfo(7, 1, 2, 3, 1, 1).Payload()));
        bridge.Dispatch(new ServerPacket(KeepComponentInfo.Code, new KeepComponentInfo(7, 2, 9, 0, 0, 0, 0, 0, 100, 0).Payload()));
        bridge.Dispatch(new ServerPacket(KeepRealm.Code, [0, 7, 2, 1]));
        bridge.Dispatch(new ServerPacket(KeepRemove.ComponentCode, [0, 7, 0, 2]));
        bridge.Dispatch(new ServerPacket(KeepRemove.Code, [0, 7]));
        Assert.Equal(["keep 7", "component 2", "realm 2", "removed 7 2", "removed 7 all"], seen);
    }
}
