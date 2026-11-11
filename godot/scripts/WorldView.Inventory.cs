// SPDX-License-Identifier: GPL-3.0-or-later
//
// WorldView: the stats panel's Inventory page (daoc-re RE/74 section 3.1). Retail draws it itself on pperdoll.bmp
// (192 x 448, the panel's tab strip included); ours puts each slot where that art has its frame, measured off the
// stock skin's copy (data/atlantis/pperdoll.bmp), in our plain style:
//   big slots (32 x 32): torso and helm, arms and hands, legs and feet down the sides; right hand, left hand,
//     two-handed and ranged in a row under the figure
//   small slots (14 x 14): neck, mythical, cloak along the top; jewellery and waist, the rings, the wrists down the sides
//   the figure window in the middle (retail draws the character there; ours names it and shows its weight)
//   the backpack: 8 rows (icon and name) and 5 bags on the right, one per 8 of the 40 slots (40–79)
//   a strip along the bottom: money and encumbrance (0xBD)
// Items come from 0x02 (DaocProtocol.Session.Inventory); each is drawn by its model's icon (objects.csv → icons.csv).
// Hover: what the item is. Right-click: select it and show the mini_info window (WorldView.MiniInfo; again to close).
// Double-click: use it (0x71, as retail's use). Drag: onto another slot moves it (0xDD; the server swaps, equips or
// refuses), onto the figure lets the server choose where it's worn, onto a quickbar slot puts "use this slot" there.

using System.Collections.Generic;
using System.Linq;
using DaocAssets.Ui;
using DaocProtocol.Packets;
using Godot;

namespace EodClient;

public partial class WorldView
{
    private const int BackpackRows = 8, Bags = 5;
    private readonly Dictionary<byte, InvSlot> _invSlots = [];
    private readonly List<InvSlot> _backpackRows = [];
    private readonly List<Button> _bagButtons = [];
    private int _bag;
    private Label? _invFigure, _invWeight, _invMoney;
    private Encumbrance? _encumbrance;

    // Paper-doll slots: (slot, x, y, big, label), in page pixels (pperdoll.bmp's, less the 32-pixel tab strip).
    private static readonly (byte Slot, int X, int Y, bool Big, string Label)[] DollSlots =
    [
        (InventorySlot.Torso, 7, 5, true, "TORSO"), (InventorySlot.Head, 155, 5, true, "HELM"),
        (InventorySlot.Arms, 7, 44, true, "ARMS"), (InventorySlot.Hands, 155, 44, true, "HANDS"),
        (InventorySlot.Legs, 7, 164, true, "LEGS"), (InventorySlot.Feet, 155, 164, true, "FEET"),
        (InventorySlot.RightHand, 15, 204, true, "R HAND"), (InventorySlot.LeftHand, 58, 204, true, "L HAND"),
        (InventorySlot.TwoHand, 102, 204, true, "2-HAND"), (InventorySlot.Ranged, 147, 204, true, "RANGED"),
        (InventorySlot.Neck, 41, 4, false, "NECK"), (InventorySlot.Mythical, 71, 4, false, "MYTHICAL"),
        (InventorySlot.Cloak, 137, 4, false, "CLOAK"),
        (InventorySlot.Jewellery, 7, 84, false, "JWLRY"), (InventorySlot.Waist, 172, 84, false, "WAIST"),
        (InventorySlot.LeftRing, 7, 111, false, "L RING"), (InventorySlot.RightRing, 172, 111, false, "R RING"),
        (InventorySlot.LeftWrist, 7, 138, false, "L WRIST"), (InventorySlot.RightWrist, 172, 138, false, "R WRIST"),
    ];

    private void BuildInventoryPage(Control p, float s)
    {
        _invSlots.Clear();
        _backpackRows.Clear();
        _bagButtons.Clear();
        Color caption = new(0.7f, 0.72f, 0.76f);

        // The figure window: dropping an item here asks the server to wear it wherever it goes.
        var figure = new InvSlot(this, (byte)InventorySlot.PaperDoll, new Vector2(106, 174) * s, s, frame: false) { Position = new Vector2(43, 20) * s };
        figure.AddThemeStyleboxOverride("panel", new StyleBoxFlat
        {
            BgColor = new Color(0, 0, 0, 0.35f), BorderColor = new Color(0.8f, 0.65f, 0.4f, 0.35f),
            BorderWidthLeft = 1, BorderWidthTop = 1, BorderWidthRight = 1, BorderWidthBottom = 1,
            CornerRadiusTopLeft = Mathf.RoundToInt(40 * s), CornerRadiusTopRight = Mathf.RoundToInt(40 * s),
        });
        figure.TooltipText = "Drop an item here to wear it";
        p.AddChild(figure);
        _invFigure = PageLabel(figure, s, new Vector2(4, 70), new Vector2(98, 40), 9, align: HorizontalAlignment.Center, colour: caption);
        _invFigure.AutowrapMode = TextServer.AutowrapMode.WordSmart;

        var labels = new List<(Vector2 At, string Text, HorizontalAlignment Align)>();
        foreach ((byte slot, int x, int y, bool big, string label) in DollSlots)
        {
            int size = big ? 32 : 14;
            var box = new InvSlot(this, slot, new Vector2(size, size) * s, s) { Position = new Vector2(x, y) * s };
            p.AddChild(box);
            _invSlots[slot] = box;
            // The label under the frame, as the art has it: beside it for the mythical slot; flush with the panel's edge
            // for the side columns' small slots; centred otherwise.
            (Vector2 at, HorizontalAlignment align) = (slot, big, x) switch
            {
                (InventorySlot.Mythical, _, _) => (new Vector2(x + 16, y + 3), HorizontalAlignment.Left),
                (_, false, < 20) => (new Vector2(x, y + size), HorizontalAlignment.Left),
                (_, false, > 150) => (new Vector2(x + size - 50, y + size), HorizontalAlignment.Right),
                _ => (new Vector2(x + size / 2 - 25, y + size), HorizontalAlignment.Center),
            };
            labels.Add((at, label, align));
        }

        // The backpack: 8 rows of 17 pixels, an icon and the item's name.
        for (int r = 0; r < BackpackRows; r++)
        {
            var row = new InvSlot(this, 0, new Vector2(160, 16) * s, s, nameRow: true) { Position = new Vector2(10, 245 + 17 * r) * s };
            p.AddChild(row);
            _backpackRows.Add(row);
        }
        for (int b = 0; b < Bags; b++)
        {
            int bag = b;
            var button = new Button
            {
                Text = (b + 1).ToString(), Position = new Vector2(172, 245 + 27 * b) * s,
                FocusMode = Control.FocusModeEnum.None, TooltipText = $"Bag {b + 1}: backpack slots {b * 8 + 1}–{b * 8 + 8}",
            };
            StyleButton(button, s, new Vector2(15, 24));
            button.Pressed += () => ShowBag(bag);
            p.AddChild(button);
            _bagButtons.Add(button);
        }
        // The labels last, over the frames and rows (the art's labels touch the frame below them).
        foreach ((Vector2 at, string text, HorizontalAlignment align) in labels)
            PageLabel(p, s, at, new Vector2(50, 8), 6, text, align, caption);
        _invMoney = PageLabel(p, s, new Vector2(8, 386), new Vector2(176, 12), 9);
        _invWeight = PageLabel(p, s, new Vector2(8, 399), new Vector2(176, 12), 9, colour: caption);
        ShowBag(_bag);
    }

    private void ShowBag(int bag)
    {
        _bag = bag;
        for (int b = 0; b < _bagButtons.Count; b++)
            _bagButtons[b].AddThemeColorOverride("font_color", b == bag ? new Color(1f, 0.75f, 0f) : new Color(0.75f, 0.75f, 0.75f));
        for (int r = 0; r < _backpackRows.Count; r++)
            _backpackRows[r].Slot = (byte)(InventorySlot.FirstBackpack + bag * BackpackRows + r);
        _statsDirty = true;
    }

    private void FillInventory()
    {
        DaocProtocol.Session.Inventory inv = _bridge.Inventory;
        foreach (InvSlot box in _invSlots.Values.Concat(_backpackRows))
            box.Show(inv[box.Slot] is InventoryItem item ? item : null);
        _invFigure!.Text = _info is PlayerInfo i ? $"{i.Name}\nLevel {i.Level} {PlayerInfo.Shown(i.ClassName)}" : PlayerName;
        _invMoney!.Text = _money is MoneyUpdate m ? Money(m) : "";
        _invWeight!.Text = _encumbrance is Encumbrance e
            ? $"Encumbrance {e.Carried} / {e.Max}" + (e.Carried > e.Max ? "  (over: you move slower)" : "")
            : $"Weight {inv.WeightPounds:0.#} lbs";
        // A bag with something in it shows so, as retail's bags fill.
        for (int b = 0; b < _bagButtons.Count; b++)
        {
            int first = InventorySlot.FirstBackpack + b * BackpackRows, used = Enumerable.Range(first, BackpackRows).Count(sl => inv[(byte)sl] is not null);
            _bagButtons[b].TooltipText = $"Bag {b + 1}: backpack slots {b * 8 + 1}–{b * 8 + 8}, {used} used";
        }
    }

    // An item's picture: its model → objects.csv icon → icons.csv base and border.
    private SlotPicture ItemPicture(InventoryItem item)
    {
        _iconTable ??= LoadIconTable();
        if (_iconTable.Resolve(IconKind.Item, item.Model) is not IconPicture p)
            return SlotPicture.None;
        return new SlotPicture(IconTexture(p.Base), p.Border >= 0 ? Cell("brdr_0.bmp", p.Border % 10 * 32, p.Border / 10 * 32) : null, []);
    }

    // What the item is, for its tooltip (retail's mini_info lines, roughly; daoc-re RE/74 3.3).
    private static string Describe(InventoryItem item)
    {
        var lines = new List<string> { item.Level > 0 ? $"{item.Name} (level {item.Level})" : item.Name };
        if (item.IsWeapon && item.ObjectType != 16)
            lines.Add($"{item.Kind} weapon: DPS {item.Value1 / 10.0:0.0}, speed {item.Value2 / 10.0:0.0}" +
                      (item.Hand switch { 1 => ", two-handed", 2 => ", left hand", _ => "" }));
        else if (item.IsArmour)
            lines.Add($"{item.Kind} armour: AF {item.Value1}" + (item.Value2 > 0 ? $", absorption {item.Value2}%" : ""));
        else if (item.IsShield)
            lines.Add($"Shield: DPS {item.Value2 / 10.0:0.0}");
        else if (item.ObjectType != 0)
            lines.Add(item.Kind);
        if (item.Quality > 0)
            lines.Add($"Quality {item.Quality}%, condition {item.Condition}%, durability {item.Durability}%");
        if (item.Bonus > 0)
            lines.Add($"Bonus {item.Bonus}%");
        if (item.SpellName1.Length > 0)
            lines.Add($"Spell: {item.SpellName1}");
        if (item.SpellName2.Length > 0)
            lines.Add($"Spell: {item.SpellName2}");
        if (item.WeightTenths > 0)
            lines.Add($"Weight {item.WeightTenths / 10.0:0.0} lbs");
        lines.Add("Right-click for details; double-click to use; drag to move or onto a quickbar.");
        return string.Join("\n", lines);
    }

    // Dropping "slot:<from>" on slot `to`: ask the server to move it there.
    private void MoveItem(byte from, ushort to)
    {
        if (_session is null || from == to)
            return;
        GD.Print($"[world] moving item from slot {from} to {to}");
        _ = _session.MoveItemAsync(to, from);
    }

    private void UseInventorySlot(byte slot)
    {
        if (_session is null || _bridge.Inventory[slot] is null)
            return;
        GD.Print($"[world] using slot {slot}");
        _ = _session.UseSlotAsync(slot);
    }

    // One inventory slot: a frame with the item's icon (and, in a backpack row, its name).
    private sealed partial class InvSlot : Panel
    {
        private readonly WorldView _view;
        private readonly TextureRect _icon, _border;
        private readonly Label? _name;
        public byte Slot;
        private InventoryItem? _item;

        public InvSlot(WorldView view, byte slot, Vector2 size, float s, bool frame = true, bool nameRow = false)
        {
            (_view, Slot, Size) = (view, slot, size);
            MouseFilter = MouseFilterEnum.Stop;
            if (frame)
                AddThemeStyleboxOverride("panel", new StyleBoxFlat
                {
                    BgColor = new Color(0, 0, 0, nameRow ? 0.2f : 0.5f), BorderColor = new Color(0.55f, 0.85f, 1f, nameRow ? 0.12f : 0.3f),
                    BorderWidthLeft = 1, BorderWidthTop = 1, BorderWidthRight = 1, BorderWidthBottom = 1,
                });
            float side = nameRow ? size.Y : size.X;
            _icon = new TextureRect
            {
                Size = new Vector2(side, side), ExpandMode = TextureRect.ExpandModeEnum.IgnoreSize,
                StretchMode = TextureRect.StretchModeEnum.Scale, MouseFilter = MouseFilterEnum.Ignore,
            };
            _border = new TextureRect
            {
                Size = _icon.Size, ExpandMode = TextureRect.ExpandModeEnum.IgnoreSize,
                StretchMode = TextureRect.StretchModeEnum.Scale, MouseFilter = MouseFilterEnum.Ignore,
            };
            AddChild(_icon);
            AddChild(_border);
            if (nameRow)
            {
                _name = new Label
                {
                    Position = new Vector2(side + 3 * s, 0), Size = new Vector2(size.X - side - 4 * s, size.Y), ClipText = true,
                    VerticalAlignment = VerticalAlignment.Center, MouseFilter = MouseFilterEnum.Ignore,
                };
                _name.AddThemeFontSizeOverride("font_size", Mathf.RoundToInt(9 * s));
                AddChild(_name);
            }
        }

        public void Show(InventoryItem? item)
        {
            _item = item;
            SlotPicture picture = item is null ? SlotPicture.None : _view.ItemPicture(item);
            _icon.Texture = picture.Icon;
            _border.Texture = picture.Border;
            if (_name is not null)
                _name.Text = item?.Name ?? "";
            TooltipText = item is null ? "" : Describe(item);
        }

        public override void _GuiInput(InputEvent e)
        {
            if (e is InputEventMouseButton { Pressed: true, DoubleClick: true, ButtonIndex: MouseButton.Left } && _item is not null)
            {
                _view.UseInventorySlot(Slot);
                AcceptEvent();
            }
            else if (e is InputEventMouseButton { Pressed: true, ButtonIndex: MouseButton.Right } && _item is not null && Slot != InventorySlot.PaperDoll)
            {
                _view.SelectItem(Slot);
                AcceptEvent();
            }
        }

        public override Variant _GetDragData(Vector2 atPosition)
        {
            if (_item is null || Slot == InventorySlot.PaperDoll)
                return default;
            SetDragPreview(new TextureRect
            {
                Texture = _icon.Texture, Size = new Vector2(32, 32), ExpandMode = TextureRect.ExpandModeEnum.IgnoreSize,
                Modulate = new Color(1, 1, 1, 0.75f),
            });
            return $"slot:{Slot}";   // also what a quickbar slot keeps to use it
        }

        public override bool _CanDropData(Vector2 atPosition, Variant data) =>
            data.VariantType == Variant.Type.String && data.AsString().StartsWith("slot:");

        public override void _DropData(Vector2 atPosition, Variant data)
        {
            if (byte.TryParse(data.AsString()[5..], out byte from))
                _view.MoveItem(from, Slot);
        }
    }
}
