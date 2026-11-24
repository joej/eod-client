// SPDX-License-Identifier: GPL-3.0-or-later

using DaocProtocol.Packets;

namespace DaocProtocol.Tests;

/// <summary>Loot requests as the retail 1.130 client writes them (daoc-re RE/99).</summary>
public sealed class LootPacketTests
{
    [Fact]
    public void PickUpIsOneByte() => Assert.Equal([0], PickUpRequest.Payload());

    [Fact]
    public void DestroyCarriesTheSessionAndTheSlot()
    {
        // OpenDAoC's DestroyItemRequestHandler skips 4 bytes, then reads the slot.
        Assert.Equal([0x01, 0x2C, 0, 0, 0, 45, 0, 0], DestroyItemRequest.Payload(300, 45));
        Assert.True(DestroyItemRequest.Destroyable(10));
        Assert.True(DestroyItemRequest.Destroyable(36));
        Assert.True(DestroyItemRequest.Destroyable(79));
        Assert.False(DestroyItemRequest.Destroyable(14));
        Assert.False(DestroyItemRequest.Destroyable(37));
        Assert.False(DestroyItemRequest.Destroyable(0));
    }

    [Fact]
    public void DroppingIsAMoveToTheGround() =>
        Assert.Equal([0x01, 0x2C, 0, 1, 0, 45, 0, 0], MoveItemRequest.Payload(MoveItemRequest.Ground, 45, 0, 300));

    [Fact]
    public void ObjectCreateKeepsItsFlagsAndDoorId()
    {
        var drop = new ObjectCreate(7, 0, 100, 1, 2, 3, "a bag") { Flags = 0x0014 };
        Assert.Equal(drop, ObjectCreate.Parse(drop.ToBytes()));
        var door = drop with { ExtraId = 700_000_101 };
        Assert.Equal(700_000_101u, ObjectCreate.Parse(door.ToBytes()).ExtraId);
    }
}
