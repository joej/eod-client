// SPDX-License-Identifier: GPL-3.0-or-later
//
// WorldView: the item windows (daoc-re RE/74 section 3.3).
//   mini_info (retail's miniinfo_window.xml, 144 wide, movable, closable): right-clicking an inventory item selects it and
//     opens this window, or switches it to that item; right-clicking the selected item again closes it and clears the
//     selection. The skin's layout: the name at 5, 10 in the item's con colour (ConColors.OfItem, the client's window-text
//     colours); a large icon at 4, 24; lines 1–3 beside it at x 39, lines 4–7 below at x 5, 11 pixels apart
//     (DaocProtocol.World.ItemInfo: all from the item data already held, nothing asked of the server); INFO and SALVAGE
//     at y 111, REPAIR and CRAFT at y 139; under them one "use" entry per use spell (0x71 type 1 or 2).
//     INFO asks for the full description (0xD8 type 1, the slot); REPAIR / CRAFT / SALVAGE send 0xD8 types 100 / 101 /
//     102 and the server does the rest. Salvage and self-craft only work from the backpack (the client's own refusals).
//   info (retail's info_window.xml, 300 x 448, movable, closable): the server's 0xC4 text window, its caption as the title
//     and its lines in a scrolling text area at 10, 22 (280 x 400). A 0xC4 marked "more" is continued by the next one.
//     Retail's info window is also resizable; ours isn't yet.

using System.Linq;
using DaocProtocol.Packets;
using DaocProtocol.World;
using Godot;

namespace EodClient;

public partial class WorldView
{
    private HudWindow? _miniInfo, _infoWindow;
    private byte? _miniSlot;   // the selected item's slot
    private Label? _miniTitle;
    private readonly Label[] _miniLines = new Label[8];
    private TextureRect? _miniIcon, _miniBorder;
    private Button[] _miniUses = [];
    private Label? _infoTitle;
    private RichTextLabel? _infoText;
    private bool _miniSubscribed;

    private void BuildItemWindows(CanvasLayer overlay)
    {
        float s = UiScale;
        _miniSlot = null;
        _miniInfo = new HudWindow("mini_info", new Vector2(144, 168), s, screen => new Vector2(screen.X - 192 - 150, 60)) { Visible = false };
        _miniTitle = _miniInfo.Label(new Vector2(5, 3), new Vector2(122, 18), 10);
        _miniInfo.Arrow("✕", new Vector2(126, 1), new Vector2(17, 14), CloseMiniInfo);
        _miniIcon = new TextureRect
        {
            Position = new Vector2(4, 24) * s, Size = new Vector2(32, 32) * s, ExpandMode = TextureRect.ExpandModeEnum.IgnoreSize,
            StretchMode = TextureRect.StretchModeEnum.Scale, MouseFilter = Control.MouseFilterEnum.Ignore,
        };
        _miniBorder = new TextureRect
        {
            Position = _miniIcon.Position, Size = _miniIcon.Size, ExpandMode = TextureRect.ExpandModeEnum.IgnoreSize,
            StretchMode = TextureRect.StretchModeEnum.Scale, MouseFilter = Control.MouseFilterEnum.Ignore,
        };
        _miniInfo.AddChild(_miniIcon);
        _miniInfo.AddChild(_miniBorder);
        _miniLines[0] = _miniTitle;
        for (int i = 1; i < 8; i++)
            _miniLines[i] = i <= 3
                ? _miniInfo.Label(new Vector2(39, 24 + 11 * (i - 1)), new Vector2(102, 11), 8)
                : _miniInfo.Label(new Vector2(5, 57 + 11 * (i - 4)), new Vector2(136, 11), 8);
        _miniInfo.Button("INFO", new Vector2(6, 111), 60, () => ItemAction(DetailRequest.InventoryItem), height: 22);
        _miniInfo.Button("SALVAGE", new Vector2(78, 111), 60, () => ItemAction(DetailRequest.Salvage), height: 22);
        _miniInfo.Button("REPAIR", new Vector2(6, 139), 60, () => ItemAction(DetailRequest.Repair), height: 22);
        _miniInfo.Button("CRAFT", new Vector2(78, 139), 60, () => ItemAction(DetailRequest.SelfCraft), height: 22);
        _miniUses =
        [
            _miniInfo.Button("", new Vector2(7, 166), 130, () => UseSelected(1)),
            _miniInfo.Button("", new Vector2(7, 185), 130, () => UseSelected(2)),
        ];
        overlay.AddChild(_miniInfo);

        _infoWindow = new HudWindow("info", new Vector2(300, 448), s, screen => new Vector2(screen.X / 2 - 150, 80)) { Visible = false };
        _infoTitle = _infoWindow.Label(new Vector2(0, 3), new Vector2(300, 16), 11, centred: true);
        _infoWindow.Arrow("✕", new Vector2(282, 1), new Vector2(17, 14), () => _infoWindow.Visible = false);
        _infoText = new RichTextLabel
        {
            Position = new Vector2(10, 22) * s, Size = new Vector2(280, 400) * s, ScrollActive = true, SelectionEnabled = true,
            FocusMode = Control.FocusModeEnum.None,
        };
        _infoText.AddThemeFontSizeOverride("normal_font_size", Mathf.RoundToInt(9 * s));
        _infoWindow.AddChild(_infoText);
        overlay.AddChild(_infoWindow);

        if (!_miniSubscribed)
        {
            _miniSubscribed = true;
            _bridge.InventoryChanged += _ => FillMiniInfo();
            _bridge.DetailWindowArrived += ShowDetail;
        }
    }

    /// <summary>Right-click on an item: select it and show it, or, if it is the selection already, close and deselect.</summary>
    private void SelectItem(byte slot)
    {
        if (_miniInfo is null || _bridge.Inventory[slot] is null)
            return;
        if (_miniSlot == slot && _miniInfo.Visible)
        {
            CloseMiniInfo();
            return;
        }
        _miniSlot = slot;
        _miniInfo.Visible = true;
        FillMiniInfo();
    }

    private void CloseMiniInfo()
    {
        _miniSlot = null;
        if (_miniInfo is not null)
            _miniInfo.Visible = false;
    }

    // The selected item's lines; the window closes when the slot empties (the item was moved, sold or used up).
    private void FillMiniInfo()
    {
        if (_miniInfo is null || !IsInstanceValid(_miniInfo) || _miniSlot is not byte slot || !_miniInfo.Visible)
            return;
        if (_bridge.Inventory[slot] is not InventoryItem item)
        {
            CloseMiniInfo();
            return;
        }
        string[] lines = ItemInfo.MiniInfo(item);
        for (int i = 0; i < 8; i++)
            _miniLines[i].Text = lines[i];
        _miniTitle!.AddThemeColorOverride("font_color", WindowConColour(ConColors.OfItem(PlayerLevel, item.Level)));
        SlotPicture picture = ItemPicture(item);
        (_miniIcon!.Texture, _miniBorder!.Texture) = (picture.Icon, picture.Border);
        // One use entry per use spell; the window grows to hold them, as retail's list does.
        string[] spells = [item.SpellName1, item.SpellName2];
        int shown = 0;
        for (int i = 0; i < 2; i++)
        {
            _miniUses[i].Visible = spells[i].Length > 0;
            _miniUses[i].Text = $"{(i == 0 ? "Use" : "Use2")}: {spells[i]}";
            if (_miniUses[i].Visible)
                _miniUses[i].Position = new Vector2(7, 166 + 19 * shown++) * UiScale;
        }
        _miniInfo.Size = new Vector2(144, 168 + 19 * shown) * UiScale;
    }

    // INFO, REPAIR, CRAFT or SALVAGE on the selected item: 0xD8 with the action's type and the item's slot.
    private void ItemAction(ushort type)
    {
        if (_session is null || _miniSlot is not byte slot)
            return;
        bool backpack = slot is >= InventorySlot.FirstBackpack and <= InventorySlot.LastBackpack;
        if (!backpack && type is DetailRequest.Salvage or DetailRequest.SelfCraft)
        {
            AppendChat(type == DetailRequest.Salvage ? "You can only salvage items in your backpack!" : "You can only self-craft items in your backpack!",
                new Color(1f, 1f, 1f));
            return;
        }
        GD.Print($"[world] item slot {slot}: detail request type {type}");
        _ = _session.RequestDetailAsync(type, slot);
    }

    private void UseSelected(byte type)
    {
        if (_session is null || _miniSlot is not byte slot)
            return;
        GD.Print($"[world] using slot {slot}, use type {type}");
        _ = _session.UseSlotAsync(slot, type);
    }

    // A 0xC4 text window: a new one replaces the info window's text; a continued one adds to it.
    private void ShowDetail(DetailWindow w, bool continues)
    {
        if (_infoWindow is null || !IsInstanceValid(_infoWindow) || _infoText is null)
            return;
        if (!continues)
        {
            _infoText.Clear();
            _infoTitle!.Text = w.Caption;
        }
        foreach (string line in w.Lines)
            _infoText.AddText(line + "\n");
        _infoWindow.Visible = true;
        GD.Print($"[world] info window \"{w.Caption}\": {w.Lines.Count} lines{(continues ? " (continued)" : "")}");
    }

    // The client's window-text con colours (the mini_info title, the target window), daoc-re RE/75.
    private static Color WindowConColour(ConColor c) => c switch
    {
        ConColor.Grey => Color.Color8(147, 145, 145),
        ConColor.Green => Color.Color8(156, 255, 133),
        ConColor.Blue => Color.Color8(99, 99, 251),
        ConColor.Yellow => Color.Color8(255, 255, 0),
        ConColor.Orange => Color.Color8(254, 131, 49),
        ConColor.Red => Color.Color8(245, 0, 0),
        ConColor.Purple => Color.Color8(210, 127, 252),
        _ => Colors.White,
    };

    private bool ItemWindowsHave(Vector2 at) =>
        new[] { _miniInfo, _infoWindow }.Any(w => w is { Visible: true } && IsInstanceValid(w) && w.GetGlobalRect().HasPoint(at));
}
