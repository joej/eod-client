// SPDX-License-Identifier: GPL-3.0-or-later
//
// WorldView: loot (roadmap M13), as the retail 1.130 client does it (daoc-re RE/99, RE/76, RE/85, the keyboard note):
// - a double left click on the current target, when it is a ground object (any: an item, a banner, a vehicle), asks the
//   server to pick it up (C→S 0xB5); so does Get (G, or = in FPS), with no check at all. No range check, no text: the
//   server answers.
// - Nearest Loot (F7 in RPG, H in MMO) and Target Object (F8, Shift+H, Shift+-) target the nearest lootable / any ground
//   object (WorldView.TargetKeys). A 0xD9 object is lootable with its flag 0x04 (OpenDAoC's drops for their owner).
// - Destroy (Shift+D in every preset) asks "Do you really wish to destroy this item?" about the selected inventory item
//   (worn ones too: the server refuses those); ACCEPT checks the selection again ("You no longer have the proper item
//   selected!") and sends C→S 0x80.
// - Dragging an inventory item out over the world asks "Do you really wish to drop this item?" (clearing the target
//   first); ACCEPT checks again and sends 0xDD to slot 1, the ground, with count 0.
// Ours: a double click is two clicks on the same object within 0.5 s (retail's timing isn't traced).

using DaocProtocol.Packets;
using Godot;

namespace EodClient;

public partial class WorldView
{
    private const double DoubleClickSeconds = 0.5;
    private (ushort Id, double At) _lastObjectClick;

    // A left click that targeted something: the second click on the same ground object picks it up.
    private void ClickedEntity(ushort id)
    {
        bool twice = _lastObjectClick.Id == id && _seconds - _lastObjectClick.At <= DoubleClickSeconds;
        _lastObjectClick = (id, _seconds);
        if (twice && _target == id && id != SelfId && _entities.TryGetValue(id, out Entity? e) && e.GroundObject)
        {
            _lastObjectClick = (0, 0);
            GD.Print($"[loot] picking up #{id} \"{e.Name}\"");
            PickUp();
        }
    }

    // Get: 0xB5 whatever is targeted, as retail sends it (no target or kind check).
    private void GetKey()
    {
        GD.Print($"[loot] get: target #{_target}");
        PickUp();
    }

    private void PickUp()
    {
        if (_session is { } session)
            _ = Send(() => session.PickUpAsync(), "pick up");
    }

    // Shift+D: the selected inventory item, after the client's own question; checked again on ACCEPT.
    private void DestroySelected()
    {
        if (_session is not { } session || _miniSlot is not byte slot || !DestroyItemRequest.Destroyable(slot))
            return;
        InventoryItem? item = _bridge.Inventory[slot];
        AskYesNo("Do you really wish to destroy this item?", () =>
        {
            if (_miniSlot != slot || !ReferenceEquals(_bridge.Inventory[slot], item))
            {
                AppendChat("You no longer have the proper item selected!", LocalReply);
                return;
            }
            GD.Print($"[loot] destroying slot {slot}");
            _ = Send(() => session.DestroyItemAsync(slot), "destroy");
        });
    }

    // An inventory item dragged out and let go over the world (not over the interface): drop it on the ground.
    private void DropOnGround(byte slot, Vector2 at)
    {
        if (!OverHud(at))
            DropItem(slot);
    }

    // "Do you really wish to drop this item?", then 0xDD to the ground (also --drop-item SLOT, WorldView.Shots).
    private void DropItem(byte slot)
    {
        if (_session is not { } session || !DestroyItemRequest.Destroyable(slot) || _bridge.Inventory[slot] is not InventoryItem item)
            return;
        SetTarget(0);
        AskYesNo("Do you really wish to drop this item?", () =>
        {
            if (!ReferenceEquals(_bridge.Inventory[slot], item))
            {
                AppendChat("You no longer have the proper item selected!", LocalReply);
                return;
            }
            GD.Print($"[loot] dropping slot {slot}");
            _ = Send(() => session.MoveItemAsync(MoveItemRequest.Ground, slot), "drop");
        });
    }
}
