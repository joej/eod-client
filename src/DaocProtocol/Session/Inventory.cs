// SPDX-License-Identifier: GPL-3.0-or-later
//
// What the character carries, kept from the server's 0x02 updates (roadmap M10: the Inventory page). The server sends
// everything at world entry (window 1, the equipment; window 2, the rest) and then each slot that changes; an empty
// item clears its slot. Updates that open another window (3 and up: a vault, a merchant…) are counted but not kept:
// their slots aren't the character's.

using DaocProtocol.Packets;

namespace DaocProtocol.Session;

public sealed class Inventory
{
    private readonly Dictionary<byte, InventoryItem> _items = [];

    /// <summary>Every filled slot.</summary>
    public IReadOnlyDictionary<byte, InventoryItem> Items => _items;

    /// <summary>The cloak and helm hidden flags, hood up, active quiver and visible weapons of the last update.</summary>
    public InventoryUpdate? Last { get; private set; }

    public InventoryItem? this[byte slot] => _items.GetValueOrDefault(slot);

    /// <summary>Applies an update; false when it was for another window (nothing changed here).</summary>
    public bool Apply(InventoryUpdate update)
    {
        if (update.Window > 2)
            return false;
        Last = update;
        foreach ((byte slot, InventoryItem item) in update.Items)
            if (item.IsEmpty)
                _items.Remove(slot);
            else
                _items[slot] = item;
        return true;
    }

    /// <summary>The weight of everything in the backpack and worn, in pounds.</summary>
    public double WeightPounds => _items.Where(kv => kv.Key is >= InventorySlot.RightHand and <= InventorySlot.LastBackpack)
        .Sum(kv => kv.Value.WeightTenths) / 10.0;
}
