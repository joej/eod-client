// SPDX-License-Identifier: GPL-3.0-or-later

using DaocProtocol.Net;
using DaocProtocol.Packets;
using DaocProtocol.Session;

namespace DaocProtocol.Tests;

/// <summary>S→C 0x12, laid out as OpenDAoC @f0aaa1e's SendMovingObjectCreate writes it and read as retail 1.130 reads it
/// (daoc-re RE/98).</summary>
public sealed class MovingObjectPacketTests
{
    // OpenDAoC's oaken ram as sent live (2026-11-24): oid 19388, heading 0x0800, z 2712, x 559421, y 510761, model 2601,
    // flags word 0x9612 (type 0x9602 | Albion), emblem 0, then 0, 0, the name and a trailing 0.
    private static readonly byte[] Ram =
    [
        0x4B, 0xBC, 0, 0, 0x08, 0x00, 0x0A, 0x98,
        0x00, 0x08, 0x89, 0x3D, 0x00, 0x07, 0xCB, 0x29,
        0x0A, 0x29, 0x96, 0x12, 0x00, 0x00, 0, 0, 0, 0, 0, 0,
        9, (byte)'o', (byte)'a', (byte)'k', (byte)'e', (byte)'n', (byte)' ', (byte)'r', (byte)'a', (byte)'m', 0,
    ];

    [Fact]
    public void ReadsOpenDaocsRamAsRetailDoes()
    {
        MovingObjectCreate o = MovingObjectCreate.Parse(Ram);
        Assert.Equal(new MovingObjectCreate(19388, 0x0800, 2712, 559421, 510761, 2601, 0x12, 0, "oaken ram") { Unread = 0x96 }, o);
        Assert.True(o.Vehicle);
        Assert.False(o.Floating);
        Assert.False(o.Lootable);
        Assert.Equal(1, o.Realm);             // byte 19's 0x10; byte 18 (0x96: OpenDAoC's type) isn't read
        Assert.Null(o.ExtraId);
        Assert.Equal(Ram, o.ToBytes());
        Assert.Equal(39, Ram.Length);         // the live packet: 42 bytes with its 3-byte header
    }

    [Fact]
    public void RealmBitsLastSetWinsAndModelLimits()
    {
        var o = new MovingObjectCreate(1, 0, 0, 0, 0, 7000, 0x02 | 0x10 | 0x40, 0, "");
        Assert.Equal(3, o.Realm);
        Assert.Equal(2, (o with { Flags = 0x30 }).Realm);
        Assert.Equal(0, (o with { Flags = 0x02 }).Realm);
        Assert.Equal(0, o.ModelRow);                                  // above 5499
        Assert.Equal(0xFFFF, (o with { Model = 0xFFFF }).ModelRow);   // none
        Assert.True((o with { Flags = 0x0C }).Lootable);
    }

    [Fact]
    public void LookBytesEmblemHighBitAndTheTrailingId()
    {
        var boat = new MovingObjectCreate(5, 0, -10, 1, 2, 2646, 0x03, 0x10005, "Med Boat")
        {
            ObjectTint = 300, Figurehead = 4, HullTint = 12, TrimTint = 34, ExtraId = 700_000_101,
        };
        byte[] bytes = boat.ToBytes();
        Assert.Equal(1, bytes[27] & 1);                               // the emblem's 0x10000
        MovingObjectCreate back = MovingObjectCreate.Parse(bytes);
        Assert.Equal(0x10005u, back.Emblem);
        Assert.Equal((short)-10, back.Z);
        Assert.Equal((4, 12, 34, 300), (back.Figurehead, back.HullTint, back.TrimTint, back.ObjectTint));
        Assert.Equal(700_000_101u, back.ExtraId);
        Assert.True(back.Floating);
    }

    [Fact]
    public void ANameIsCutTo47Bytes()
    {
        var o = new MovingObjectCreate(1, 0, 0, 0, 0, 1, 0x02, 0, new string('x', 60));
        Assert.Equal(47, MovingObjectCreate.Parse(o.ToBytes()).Name.Length);
    }

    [Fact]
    public void TheBridgeRaisesIt()
    {
        var bridge = new NetworkBridge();
        MovingObjectCreate? seen = null;
        bridge.MovingObjectSpawned += o => seen = o;
        bridge.Dispatch(new ServerPacket(MovingObjectCreate.Code, Ram));
        Assert.Equal("oaken ram", seen?.Name);
        Assert.Empty(bridge.UnhandledCounts);
    }
}
