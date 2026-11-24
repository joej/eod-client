// SPDX-License-Identifier: GPL-3.0-or-later

using DaocProtocol.Net;
using DaocProtocol.Packets;
using DaocProtocol.Session;

namespace DaocProtocol.Tests;

/// <summary>Casting and effect packets, against bytes laid out as OpenDAoC @f0aaa1e writes them.</summary>
public sealed class CastingPacketTests
{
    [Fact]
    public void CastInterruptAndLandingReadBigEndian()
    {
        SpellCast cast = SpellCast.Parse([0x01, 0x02, 0x00, 0x2A, 0x00, 0x1E, 0x00, 0x00]);
        Assert.Equal(new SpellCast(0x0102, 42, 30), cast);
        Assert.Equal(3000, cast.CastMilliseconds);

        Assert.Equal(new SpellInterrupt(0x0102, 1), SpellInterrupt.Parse([0x01, 0x02, 0x00, 0x01]));

        SpellEffect fx = SpellEffect.Parse([0x01, 0x02, 0x00, 0x2A, 0x03, 0x04, 0x00, 0x07, 0x00, 0x01, 0xFF, 0xBF]);
        Assert.Equal(new SpellEffect(0x0102, 42, 0x0304, 7, false, SpellEffect.Hit), fx);
        Assert.True(fx.Landed);
        Assert.Equal(fx, SpellEffect.Parse(fx.ToBytes()));
        Assert.False(SpellEffect.Parse(new SpellEffect(1, 2, 3, 0, true, SpellEffect.Resisted).ToBytes()).Landed);
    }

    [Fact]
    public void ConcentrationListReadsEachBuff()
    {
        byte[] payload =
        [
            2, 0, 0, 0,
            0, 0, 4, 0x01, 0x2C, 4, (byte)'S', (byte)'k', (byte)'i', (byte)'n', 2, (byte)'M', (byte)'e',
            1, 0, 6, 0x00, 0x10, 1, (byte)'X', 0,
        ];
        ConcentrationList list = ConcentrationList.Parse(payload);
        Assert.Equal(2, list.Entries.Count);
        Assert.Equal(new ConcentrationEntry(0, 4, 300, "Skin", "Me"), list.Entries[0]);
        Assert.Equal("", list.Entries[1].Owner);
        Assert.Equal(10, list.Used);
        Assert.Equal(payload, list.ToBytes());
        Assert.Empty(ConcentrationList.Parse([0, 0, 0, 0]).Entries);
    }

    [Fact]
    public void EffectIconsMergeChangedSlotsAndClearTheTail()
    {
        var bridge = new NetworkBridge();
        int raised = 0;
        bridge.EffectsChanged += _ => raised++;
        var a = new EffectIcon(0, 0, 0, 0x0123, 60, 501, false, "Strength");
        var b = new EffectIcon(1, 1, 0, 0x0456, 0, 502, true, "Snare");
        bridge.Dispatch(new ServerPacket(EffectIcons.Code, new EffectIcons([a, b]).ToBytes()));
        Assert.Equal([a, b], bridge.Effects.Values);

        // OpenDAoC after the first one ends: slot 0 is now Snare, slot 1 is cleared as a slot byte and 10 zero bytes.
        var moved = b with { Slot = 0, Source = 0 };
        byte[] update = [.. new EffectIcons([moved]).ToBytes(), 1, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0];
        update[0] = 2;
        bridge.Dispatch(new ServerPacket(EffectIcons.Code, update));
        Assert.Equal([moved], bridge.Effects.Values);
        Assert.Equal(2, raised);

        // Redraw flag 0: the slot is kept, nothing is raised.
        bridge.Dispatch(new ServerPacket(EffectIcons.Code, new EffectIcons([a with { Slot = 5 }], Redraw: false).ToBytes()));
        Assert.Equal(2, raised);
        Assert.Equal(2, bridge.Effects.Count);
    }

    [Fact]
    public void DropAndCancelRequests()
    {
        Assert.Equal([3, 0, 0, 0], ConcentrationDrop.Payload(3));
        Assert.Equal([0x01, 0xF5, 0, 0], EffectCancel.Payload(501));
    }

    [Fact]
    public void BridgeRaisesCastEvents()
    {
        var bridge = new NetworkBridge();
        SpellCast? cast = null;
        SpellInterrupt? stop = null;
        ConcentrationList? conc = null;
        bridge.SpellCastStarted += c => cast = c;
        bridge.SpellInterrupted += s => stop = s;
        bridge.ConcentrationChanged += l => conc = l;
        bridge.Dispatch(new ServerPacket(SpellCast.Code, new SpellCast(5, 6, 20).ToBytes()));
        bridge.Dispatch(new ServerPacket(SpellInterrupt.Code, new SpellInterrupt(5, 1).ToBytes()));
        bridge.Dispatch(new ServerPacket(ConcentrationList.Code, [0, 0, 0, 0]));
        Assert.Equal(new SpellCast(5, 6, 20), cast);
        Assert.Equal(new SpellInterrupt(5, 1), stop);
        Assert.NotNull(conc);
    }
}
