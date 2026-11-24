// SPDX-License-Identifier: GPL-3.0-or-later
//
// WorldView: the effect icons and the concentration window (roadmap M13), as retail 1.130 draws and drives them (daoc-re
// RE/97, 2026-11-24).
// Effect icons (S→C 0x7F, built into the client, no skin XML):
//   - slots 0–74, each a 32 x 32 icon; a grid of columns x rows (Options → Spell Effect Icons, default 10 x 4), slots past
//     it hidden; slot order, oldest first (the default; the option's reverse order isn't built). At retail's default.ini
//     place (SpellEffect=7,5).
//   - each 0x7F changes only the slots it names (an empty name clears one) and restarts only their countdowns; with its
//     redraw flag 0 nothing is redrawn (NetworkBridge).
//   - the icon: a spells.csv row's icon, or with source 0xFF the icon id itself. Immunity bit 0 tints it.
//   - the countdown as the quickbar writes it: nothing under 3 s, "Ns" under a minute, "Nm" under 20 minutes, nothing
//     from 20 minutes.
//   - Shift + right-click sends 0xF8 with the slot's tooltip id, buffs and debuffs alike, no confirmation.
//   Ours: the tint colour (retail's isn't known), the name as a tooltip, nothing on a plain right-click, no mark for a
//   negative effect (retail's isn't known), the grid size fixed at the default and not draggable.
// Concentration (S→C 0x75, ui/atlantis/concentration_window.xml, 128 x 104):
//   - each 0x75 replaces the list (50 rows at most): the spell's icon and the name of whom the buff is on; 0x75 never
//     opens the window. The yellow bar is 0xAD's concentration percent.
//   - hidden by default (default.ini MiniConc); Interface Elements → Concentration shows it.
//   - a double-click on a row sends 0x76 with the row (inferred: retail has a select event and a remove event on the list;
//     which gesture is which is open), no confirmation.

using DaocAssets.Ui;
using DaocProtocol.Packets;
using Godot;

namespace EodClient;

public partial class WorldView
{
    private const int EffectSlots = 75, EffectColumns = 10, EffectRows = 4;
    private HudWindow? _effectsWindow;
    private readonly EffectSlot?[] _effectSlots = new EffectSlot?[EffectSlots];
    private readonly Dictionary<byte, (EffectIcon Icon, double At)> _effectTimers = [];

    private HudWindow? _concWindow;
    private ItemList? _concList;
    private HudBar? _concBar;
    private ConcentrationList _concentration = new([]);
    private bool _showConcentration = ClientSettings.WindowShown("concentration", false);

    private void SubscribeEffects()
    {
        _bridge.EffectsChanged += _ => ShowEffects();
        _bridge.ConcentrationChanged += list =>
        {
            _concentration = list;
            ShowConcentration();
        };
        _bridge.StatusChanged += s => _concBar?.Set(s.ConcentrationPercent);
    }

    private void BuildEffectWindows(CanvasLayer overlay)
    {
        float s = UiScale;
        // Clicks between the icons go through to the world (ours: the grid isn't draggable).
        var w = new HudWindow("effects", new Vector2(EffectColumns, EffectRows) * 32, s, _ => new Vector2(7, 5))
        {
            MouseFilter = Control.MouseFilterEnum.Ignore,
        };
        w.AddThemeStyleboxOverride("panel", new StyleBoxEmpty());
        for (int i = 0; i < EffectColumns * EffectRows; i++)
        {
            var slot = new EffectSlot(s) { Position = new Vector2(i % EffectColumns, i / EffectColumns) * 32 * s, Visible = false };
            int index = i;
            slot.Cancel = () => CancelEffect(index);
            _effectSlots[i] = slot;
            w.AddChild(slot);
        }
        _effectsWindow = w;
        overlay.AddChild(w);
        _effectTimers.Clear();
        ShowEffects();
        var tick = new Godot.Timer { WaitTime = 0.25, Autostart = true };
        tick.Timeout += TickEffects;
        w.AddChild(tick);

        var c = new HudWindow("concentration", new Vector2(128, 104), s, screen => new Vector2(185, 55)) { Visible = _showConcentration };
        Label title = c.Label(new Vector2(12, 1), new Vector2(104, 14), 10, centred: true);
        title.Text = "Concentration";
        c.Arrow("✕", new Vector2(110, 0), new Vector2(16, 14), () =>
        {
            _showConcentration = Flip("concentration", true);
            ApplyElementWindows();
        });
        _concList = Styled(new ItemList(), c, s, new Vector2(6, 16), new Vector2(116, 70));
        _concList.FixedIconSize = new Vector2I(1, 1) * Mathf.RoundToInt(14 * s);
        _concList.ItemActivated += row => DropConcentration((int)row);
        _concBar = new HudBar(70 * s, 6 * s, new Color(0.9f, 0.8f, 0.2f)) { Position = new Vector2(29, 104 - 6 - 8) * s };
        c.AddChild(_concBar);
        _concBar.Set(_vitals?.ConcentrationPercent ?? 0);
        _concWindow = c;
        overlay.AddChild(c);
        ShowConcentration();
    }

    // The slots after a 0x7F: a slot whose entry is new restarts its countdown.
    private void ShowEffects()
    {
        foreach (byte gone in _effectTimers.Keys.Where(k => !_bridge.Effects.ContainsKey(k)).ToList())
            _effectTimers.Remove(gone);
        foreach ((byte slot, EffectIcon icon) in _bridge.Effects)
            if (!_effectTimers.TryGetValue(slot, out var t) || !ReferenceEquals(t.Icon, icon))
                _effectTimers[slot] = (icon, _seconds);
        for (int i = 0; i < EffectColumns * EffectRows; i++)
        {
            if (_effectSlots[i] is not EffectSlot view || !IsInstanceValid(view))
                continue;
            if (!_bridge.Effects.TryGetValue((byte)i, out EffectIcon? icon))
            {
                view.Visible = false;
                continue;
            }
            SlotPicture p = KindPicture(icon.IconIsSpell ? IconKind.Spell : IconKind.Icon, icon.Icon);
            view.Show(p.Icon, icon.Name, (icon.Immunity & 1) != 0);
        }
        TickEffects();
    }

    private void TickEffects()
    {
        foreach ((byte slot, (EffectIcon icon, double at)) in _effectTimers)
            if (slot < EffectColumns * EffectRows && _effectSlots[slot] is EffectSlot view && IsInstanceValid(view))
                view.Countdown(icon.SecondsLeft == 0 ? -1 : icon.SecondsLeft - (_seconds - at));
    }

    // Shift + right-click on an icon.
    private void CancelEffect(int slot)
    {
        if (_session is null || !_bridge.Effects.TryGetValue((byte)slot, out EffectIcon? icon))
            return;
        GD.Print($"[effects] cancelling {icon.Name} (tooltip {icon.TooltipId})");
        _ = _session.CancelEffectAsync(icon.TooltipId);
    }

    private void ShowConcentration()
    {
        if (_concList is null || !IsInstanceValid(_concList))
            return;
        _concList.Clear();
        foreach (ConcentrationEntry e in _concentration.Entries.Take(50))
        {
            int row = _concList.AddItem(e.Owner, KindPicture(IconKind.Spell, e.Spell).Icon);
            _concList.SetItemTooltip(row, $"{e.Name}\n{e.Owner}\nConcentration {e.Concentration}");
        }
    }

    private void DropConcentration(int row)
    {
        if (_session is null || row < 0 || row >= Math.Min(50, _concentration.Entries.Count))
            return;
        GD.Print($"[concentration] dropping row {row}: {_concentration.Entries[row].Name}");
        _ = _session.DropConcentrationAsync((byte)row);
    }

    // The quickbar's countdown text (daoc-re RE/97).
    private static string EffectTimeText(double seconds) =>
        seconds < 3 ? "" : seconds < 60 ? $"{(int)seconds}s" : seconds < 20 * 60 ? $"{(int)(seconds / 60)}m" : "";

    // One effect icon: the picture, the countdown over its lower edge, the name as a tooltip.
    private sealed partial class EffectSlot : Control
    {
        public System.Action? Cancel;
        private readonly TextureRect _icon;
        private readonly Label _time;

        public EffectSlot(float scale)
        {
            Size = new Vector2(32, 32) * scale;
            MouseFilter = MouseFilterEnum.Stop;
            _icon = new TextureRect
            {
                Size = Size, ExpandMode = TextureRect.ExpandModeEnum.IgnoreSize, StretchMode = TextureRect.StretchModeEnum.Scale,
                MouseFilter = MouseFilterEnum.Ignore,
            };
            AddChild(_icon);
            _time = new Label
            {
                Position = new Vector2(0, 18) * scale, Size = new Vector2(32, 14) * scale,
                HorizontalAlignment = HorizontalAlignment.Center, MouseFilter = MouseFilterEnum.Ignore,
            };
            _time.AddThemeFontSizeOverride("font_size", Mathf.RoundToInt(9 * scale));
            _time.AddThemeConstantOverride("outline_size", Mathf.RoundToInt(3 * scale));
            _time.AddThemeColorOverride("font_outline_color", Colors.Black);
            AddChild(_time);
            GuiInput += e =>
            {
                if (e is InputEventMouseButton { ButtonIndex: MouseButton.Right, Pressed: true, ShiftPressed: true })
                {
                    Cancel?.Invoke();
                    AcceptEvent();
                }
            };
        }

        public void Show(Texture2D? icon, string name, bool immune)
        {
            _icon.Texture = icon;
            _icon.Modulate = immune ? new Color(0.6f, 0.6f, 1f) : Colors.White;   // ours: retail's tint isn't known
            TooltipText = name;
            Visible = true;
        }

        public void Countdown(double seconds) => _time.Text = seconds < 0 ? "" : EffectTimeText(seconds);
    }
}
