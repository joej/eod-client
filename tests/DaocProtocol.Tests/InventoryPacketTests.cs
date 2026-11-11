// SPDX-License-Identifier: GPL-3.0-or-later

using DaocProtocol.Net;
using DaocProtocol.Packets;
using DaocProtocol.Session;

namespace DaocProtocol.Tests;

/// <summary>
/// 0x02 InventoryUpdate and 0xBD Encumbrance from the RustDAoC 1.127e capture (a level 50 Paladin), and the 0xDD move
/// we send. Both recorded 0x02 packets decode to their last byte and rebuild byte for byte.
/// </summary>
public class InventoryPacketTests
{
    private static readonly List<ServerPacket> Server = CaptureFile.Login1127e().ServerPackets();
    private static readonly List<ServerPacket> Updates = Server.Where(p => p.Code == InventoryUpdate.Code).ToList();

    [Fact]
    public void EveryRecordedUpdateRebuilds()
    {
        Assert.Equal(2, Updates.Count);
        foreach (ServerPacket p in Updates)
            Assert.Equal(p.Payload, InventoryUpdate.Parse(p.Payload).ToBytes());
    }

    [Fact]
    public void TheEquipmentUpdateReadsThePracticeSword()
    {
        InventoryUpdate u = InventoryUpdate.Parse(Updates[0].Payload);
        Assert.Equal(1, u.Window);   // all the equipment
        Assert.Equal(12, u.Items.Count);
        (byte slot, InventoryItem sword) = u.Items[0];
        Assert.Equal(InventorySlot.RightHand, slot);
        Assert.Equal("practice sword", sword.Name);
        Assert.Equal(3, sword.ObjectType);       // slashing
        Assert.Equal(2, sword.DamageType);
        Assert.Equal(165, sword.Value1);         // DPS 16.5
        Assert.Equal(25, sword.Value2);          // speed 2.5
        Assert.Equal((ushort)10, sword.WeightTenths);
        Assert.Equal(90, sword.Quality);
        Assert.Equal((ushort)3, sword.Model);
        Assert.Equal(InventorySlot.LeftHand, u.Items[1].Slot);
        Assert.Equal("small training shield", u.Items[1].Item.Name);
        Assert.True(u.Items[1].Item.IsShield);
    }

    [Fact]
    public void ACloakWithTwoSpellsReadsBoth()
    {
        // Flags 0x1A: salvage, then two spells (0x08, 0x10), each an icon and a name, before the item's own name.
        InventoryItem cloak = Updates.SelectMany(p => InventoryUpdate.Parse(p.Payload).Items)
            .First(i => i.Item.SpellName1.Length > 0).Item;
        Assert.Equal(0x1A, cloak.Flags);
        Assert.Equal((ushort)0x24C3, cloak.SpellIcon1);
        Assert.Equal("Veil-Touched Renewal", cloak.SpellName1);
        Assert.Equal((ushort)0x1F81, cloak.SpellIcon2);
        Assert.Equal("replenish power", cloak.SpellName2);
        Assert.Equal("SoloDAoC Cloak of Continuance", cloak.Name);
    }

    [Fact]
    public void AnEmptySlotIs24ZeroBytesAndClearsTheSlot()
    {
        var w = new PayloadWriter();
        InventoryItem.Empty.Write(w);
        Assert.Equal(new byte[24], w.ToArray());

        var inv = new Inventory();
        foreach (ServerPacket p in Updates)
            inv.Apply(InventoryUpdate.Parse(p.Payload));
        Assert.Equal("practice sword", inv[InventorySlot.RightHand]?.Name);
        inv.Apply(new InventoryUpdate(0, 0, 0, 0, [(InventorySlot.RightHand, InventoryItem.Empty)]));
        Assert.Null(inv[InventorySlot.RightHand]);
    }

    [Fact]
    public void AnotherWindowsItemsAreNotTheCharacters()
    {
        var inv = new Inventory();
        InventoryItem sword = InventoryUpdate.Parse(Updates[0].Payload).Items[0].Item;
        Assert.False(inv.Apply(new InventoryUpdate(0, 0, 0, 3, [(110, sword)])));   // a vault
        Assert.Empty(inv.Items);
    }

    [Fact]
    public void EncumbranceReadsMaxAndCarried()
    {
        Encumbrance e = Encumbrance.Parse(Server.First(p => p.Code == Encumbrance.Code).Payload);
        Assert.Equal((ushort)196, e.Max);
        Assert.Equal((ushort)22, e.Carried);
        Assert.Equal(Server.First(p => p.Code == Encumbrance.Code).Payload, e.ToBytes());
    }

    [Fact]
    public void MoveItemIsZeroToFromCount() =>
        Assert.Equal(new byte[] { 0, 0, 0, 40, 0, 10, 0, 0 }, MoveItemRequest.Payload(40, InventorySlot.RightHand));

    [Fact]
    public void TheBridgeKeepsTheInventory()
    {
        var bridge = new NetworkBridge();
        int changes = 0;
        bridge.InventoryChanged += _ => changes++;
        foreach (ServerPacket p in Updates)
            bridge.Dispatch(p);
        Assert.Equal(2, changes);
        Assert.True(bridge.Inventory.Items.Count > 12);
        Assert.False(bridge.UnhandledCounts.ContainsKey(InventoryUpdate.Code));
    }
}
