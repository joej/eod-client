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
//   The same window for a spell or style (right-click one on the Spells or Styles page; RE/74 3.2): the name, "Magical
//     Ability" or "Combat Style", its level, and the spell's target and concentration or the style's opening (DelveText).
//     The target and concentration come from the server's record for the spell (0xD8 type 24, 25 for a style, 26 a song,
//     by its id; answered with a 0xD8 record, kept in the session's DelveCache); the window fills in when it comes. INFO
//     shows the record's long text in the info window, in the client's wording, titled with its name. No item buttons.

using System.Collections.Generic;
using System.Linq;
using DaocProtocol.Packets;
using DaocProtocol.World;
using Godot;

namespace EodClient;

public partial class WorldView
{
    private HudWindow? _miniInfo, _infoWindow;
    private byte? _miniSlot;   // the selected item's slot
    private SkillInfo? _miniSkill;   // or the selected spell or style
    private Button[] _itemButtons = [];
    private SkillInfo? _infoSkill;   // the spell or style the info window shows (or will, once its record comes)
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
        _itemButtons =
        [
            _miniInfo.Button("SALVAGE", new Vector2(78, 111), 60, () => ItemAction(DetailRequest.Salvage), height: 22),
            _miniInfo.Button("REPAIR", new Vector2(6, 139), 60, () => ItemAction(DetailRequest.Repair), height: 22),
            _miniInfo.Button("CRAFT", new Vector2(78, 139), 60, () => ItemAction(DetailRequest.SelfCraft), height: 22),
        ];
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
            _bridge.DelveArrived += OnDelve;
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
        (_miniSlot, _miniSkill) = (slot, null);
        _miniInfo.Visible = true;
        FillMiniInfo();
    }

    /// <summary>Right-click on a spell or style: the same select-and-show, toggle and switch as an item's.</summary>
    private void SelectSkill(string name)
    {
        if (_miniInfo is null || _bridge.Skills.Describe(name) is not SkillInfo skill)
            return;
        if (_miniSkill?.Id == skill.Id && _miniSkill.Kind == skill.Kind && _miniInfo.Visible)
        {
            CloseMiniInfo();
            return;
        }
        (_miniSkill, _miniSlot) = (skill, null);
        _miniInfo.Visible = true;
        AskDelve(skill.DelveType, skill.Id);
        FillMiniInfo();
    }

    // Asks for a record once per connection (the server answers each once an hour); nothing if it's here already (the
    // server sends every ability's and style's record unasked at world entry).
    private void AskDelve(ushort type, ushort id)
    {
        string kind = type == DetailRequest.StyleDelve ? "Style" : "Spell";
        if (_session is null || _bridge.Delves.Get(kind, id) is not null || !_session.Delves.ShouldAsk(type, id))
            return;
        GD.Print($"[world] delve request type {type}, id {id}");
        _ = _session.RequestDetailAsync(type, id);
    }

    private DelveRecord? Record(SkillInfo s) => _bridge.Delves.Get(s.RecordKind, s.Id);

    private void CloseMiniInfo()
    {
        (_miniSlot, _miniSkill) = (null, null);
        if (_miniInfo is not null)
            _miniInfo.Visible = false;
    }

    // The selected item's lines; the window closes when the slot empties (the item was moved, sold or used up).
    private void FillMiniInfo()
    {
        if (_miniInfo is null || !IsInstanceValid(_miniInfo) || !_miniInfo.Visible)
            return;
        if (_miniSkill is SkillInfo skill)
        {
            FillSkillInfo(skill);
            return;
        }
        if (_miniSlot is not byte slot)
            return;
        foreach (Button b in _itemButtons)
            b.Visible = true;
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

    private void FillSkillInfo(SkillInfo skill)
    {
        string[] lines = DelveText.MiniInfo(skill, Record(skill));
        for (int i = 0; i < 8; i++)
            _miniLines[i].Text = lines[i];
        _miniTitle!.AddThemeColorOverride("font_color", Colors.White);
        SlotPicture picture = EntryPicture("skill:" + skill.Name);
        (_miniIcon!.Texture, _miniBorder!.Texture) = (picture.Icon, picture.Border);
        foreach (Button b in _itemButtons.Concat(_miniUses))
            b.Visible = false;
        _miniInfo!.Size = new Vector2(144, 140) * UiScale;   // INFO only
    }

    // A record came: refill the window and the info text that wait for it.
    private void OnDelve(DelveRecord r)
    {
        GD.Print($"[world] delve: {r.Kind} {r.Index} \"{r.Name}\" ({r.Values.Count} values)");
        if (_miniSkill is SkillInfo m && m.Id == r.Index)
            FillMiniInfo();
        if (_infoSkill is SkillInfo i && (i.Id == r.Index || (r.Kind == "Spell" && Record(i)?.Int("SpecialNumber") == r.Index)))
            ShowSkillDetail(i);
    }

    // INFO on a spell or style: its record's long text (the client builds it; the server's record has the values).
    private void ShowSkillDetail(SkillInfo skill)
    {
        if (_infoWindow is null || !IsInstanceValid(_infoWindow) || _infoText is null)
            return;
        _infoSkill = skill;
        if (Record(skill) is not DelveRecord r)
        {
            AskDelve(skill.DelveType, skill.Id);
            return;   // shown when it comes
        }
        List<DelveLine> lines;
        if (r.Kind == "Style")
        {
            if (r.Int("SpecialNumber") is int special and not 0)
                AskDelve(DetailRequest.SpellDelve, (ushort)special);
            lines = DelveText.Style(r, id => _bridge.Delves.Get("Spell", id));
        }
        else
            lines = DelveText.Spell(r);
        _infoText.Clear();
        _infoTitle!.Text = r.Name.Length > 0 ? r.Name : skill.Name;
        // Labels white, values light grey (200), or the value's own colour (a style's to-hit and defence words).
        foreach (DelveLine l in lines)
        {
            if (l.Label.Length > 0)
                _infoText.AddText(l.Label + ": ");
            _infoText.PushColor(l.Colour is var (cr, cg, cb) ? Color.Color8(cr, cg, cb) : (l.Label.Length > 0 ? Color.Color8(200, 200, 200) : Colors.White));
            _infoText.AddText(l.Value + "\n");
            _infoText.Pop();
        }
        _infoWindow.Visible = true;
        GD.Print($"[world] info window \"{_infoTitle.Text}\": {lines.Count} lines from the {r.Kind} record");
    }

    // INFO, REPAIR, CRAFT or SALVAGE on the selected item: 0xD8 with the action's type and the item's slot.
    private void ItemAction(ushort type)
    {
        if (_miniSkill is SkillInfo skill && type == DetailRequest.InventoryItem)
        {
            ShowSkillDetail(skill);
            return;
        }
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
        _infoSkill = null;
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
