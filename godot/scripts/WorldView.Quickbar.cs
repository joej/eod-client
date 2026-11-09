// SPDX-License-Identifier: GPL-3.0-or-later
//
// WorldView: the quickbars (roadmap M9; docs/ui-windows.md). Retail's quickbars are built into the client (no XML
// layout); its default.ini places bar 1 centred low (329, 644 at 1024 x 768) and bar 2 just below (686), about
// 366 x 42 skin pixels each: ten icon slots and a handle with the bar's number and page. Ours: the same size and
// places, three bars of ten pages of ten slots (bar 3 at the left edge, as retail's default).
//
// A slot holds a skill, ability, style or spell (by name: the server's list order changes), an item slot, or a
// command. Left click or keys 1–0 (bar 1) use it: a skill by 0xBB, a list spell by 0x7D, an item slot by 0x71, a
// command as typed; Alt+1–0 bar 2, Ctrl+1–0 bar 3, Shift+ turns a bar's page. A darkened slot counts down a cooldown (0xD6). Right click: choose what goes there, from your
// skills and spell lines (until the spell and style windows exist). Drag a slot onto another to swap them. Kept
// per character in the client's settings. Retail keeps them in the character's ini file, in a format not read yet.

using System.Collections.Generic;
using System.Linq;
using DaocAssets.Ui;
using DaocProtocol.Packets;
using Godot;

namespace EodClient;

public partial class WorldView
{
    private const int BarSlots = 10, BarPages = 10, Bars = 3;
    private readonly List<Quickbar> _bars = [];
    private Dictionary<int, string>? _iconSheets;               // first icon number → sheet file (library/icons)
    private IconTable? _iconTable;                               // the server's number → picture (gamedata tables)
    private readonly Dictionary<string, Texture2D?> _sheetTextures = [];

    private void BuildQuickbars(CanvasLayer overlay)
    {
        float s = UiScale;
        for (int b = 0; b < Bars; b++)
        {
            int bar = b;
            // Retail's default.ini at 1024 x 768: bar 1 at (329, 644) and bar 2 at (328, 686), centred, 124 and 82 skin
            // pixels above the bottom; bar 3 at (0, 492): the left edge, 276 above the bottom.
            System.Func<Vector2, Vector2> at = b switch
            {
                0 => screen => new Vector2(screen.X / 2 - 183, screen.Y - 124),
                1 => screen => new Vector2(screen.X / 2 - 183, screen.Y - 82),
                _ => screen => new Vector2(0, screen.Y - 276),
            };
            var window = new HudWindow($"quickbar{b + 1}", new Vector2(366, 40), s, at)
            {
                DragAnywhere = true,
            };
            var qb = new Quickbar(this, bar, window, s);
            _bars.Add(qb);
            overlay.AddChild(window);
        }
    }

    private void UpdateQuickbars()
    {
        long now = System.Environment.TickCount64;
        foreach (Quickbar qb in _bars)
            qb.Refresh(now);
    }

    // The number keys, built in as retail's (daoc-re RE/68; not in its key presets): 1–0 use bar 1's slot, Alt+ bar 2's,
    // Ctrl+ bar 3's; with Shift added, the same keys turn that bar to page N (0 = page 10).
    private bool QuickbarKey(InputEvent e)
    {
        if (e is not InputEventKey { Pressed: true, Echo: false } k || k.MetaPressed || (k.AltPressed && k.CtrlPressed))
            return false;
        int slot = k.Keycode switch
        {
            >= Key.Key1 and <= Key.Key9 => (int)(k.Keycode - Key.Key1),
            Key.Key0 => 9,
            _ => -1,
        };
        int bar = k.AltPressed ? 1 : k.CtrlPressed ? 2 : 0;
        if (slot < 0 || bar >= _bars.Count)
            return false;
        if (k.ShiftPressed)
            _bars[bar].TurnTo(slot);
        else
            _bars[bar].Use(slot);
        return true;
    }

    // Whether a screen point is over one of the game windows (so the world doesn't also take the click).
    private bool OverHud(Vector2 at) =>
        _bars.Any(b => b.Window.Visible && b.Window.GetGlobalRect().HasPoint(at))
        || (_summary?.Visible == true && _summary.GetGlobalRect().HasPoint(at))
        || (_targetWindow?.Visible == true && _targetWindow.GetGlobalRect().HasPoint(at));

    // ----- what a slot holds -----

    // "skill:<name>" (a skill, ability, style or spell, found by name), "slot:<n>" (an inventory slot),
    // "cmd:<text>" (a command or a line to say).
    private void UseQuickbarEntry(string entry)
    {
        if (_session is null || entry.Length == 0)
            return;
        string[] kv = entry.Split(':', 2);
        switch (kv[0])
        {
            case "skill":
                UseSkillByName(kv[1]);
                break;
            case "slot" when byte.TryParse(kv[1], out byte slot):
                _ = _session.UseSlotAsync(slot);
                break;
            case "cmd":
                _ = kv[1].StartsWith('/') ? _session.SendCommandAsync(kv[1]) : _session.SayAsync(kv[1]);
                break;
        }
    }

    // The server's number for a slot's entry, and which table it is looked up in; null without one.
    private (IconKind, int)? EntryIcon(string entry)
    {
        if (!entry.StartsWith("skill:"))
            return null;
        string name = entry[6..];
        if (_bridge.Skills.Skills.FirstOrDefault(s => s.Name.Equals(name, System.StringComparison.OrdinalIgnoreCase)) is SkillEntry e)
            return (KindOf(e.Page), e.Icon);
        foreach (SpellLinePage line in _bridge.Skills.SpellLines)
            if (line.Entries.FirstOrDefault(l => l.Name.Equals(name, System.StringComparison.OrdinalIgnoreCase)) is LineEntry le)
                return (le.Kind switch { LineEntryKind.Spell => IconKind.Spell, LineEntryKind.Style => IconKind.Style, _ => IconKind.Icon }, le.Icon);
        return null;
    }

    private static IconKind KindOf(SkillPage page) => page switch
    {
        SkillPage.Spell or SkillPage.Song or SkillPage.AbilitySpell => IconKind.Spell,
        SkillPage.Style => IconKind.Style,
        _ => IconKind.Icon,
    };

    /// <summary>What a slot draws: the icon, its border frame, and the style marks over both (each a 10 x 10
    /// texture and its place on the 32 x 32 icon).</summary>
    private sealed record SlotPicture(Texture2D? Icon, Texture2D? Border, IReadOnlyList<(Texture2D Mark, Vector2 At)> Marks)
    {
        public static readonly SlotPicture None = new(null, null, []);
    }

    // The entry's picture through gamedata's tables (IconTable).
    private SlotPicture EntryPicture(string entry)
    {
        if (EntryIcon(entry) is not var (kind, number))
            return SlotPicture.None;
        _iconTable ??= LoadIconTable();
        if (_iconTable.Resolve(kind, number) is not IconPicture p)
            return SlotPicture.None;
        var marks = new List<(Texture2D, Vector2)>();
        foreach (IconMark m in p.Marks)
            if (Cell("wgsm_0.bmp", m.Cell * 10, 0, 10) is Texture2D t)
                marks.Add((t, new Vector2(m.X, m.Y)));
        return new SlotPicture(IconTexture(p.Base), p.Border >= 0 ? Cell("brdr_0.bmp", p.Border % 10 * 32, p.Border / 10 * 32) : null, marks);
    }

    private IconTable LoadIconTable()
    {
        string dir = (_libraryPath ?? "").PathJoin("icons");
        string Read(string f) => Godot.FileAccess.FileExists(dir.PathJoin(f)) ? Godot.FileAccess.GetFileAsString(dir.PathJoin(f)) : "";
        return IconTable.Parse(Read("icons.csv"), Read("spells.csv"), Read("styles.csv"));
    }

    private Texture2D? IconTexture(int icon)
    {
        if (icon < 0)
            return null;
        _iconSheets ??= LoadIconSheets();
        return IconSheets.Locate(_iconSheets, icon) is var (sheet, x, y) ? Cell(sheet, x, y) : null;
    }

    // A cell (32 x 32 unless given) of a sheet in library/icons.
    private Texture2D? Cell(string sheet, int x, int y, int size = IconSheets.IconSize)
    {
        if (!_sheetTextures.TryGetValue(sheet, out Texture2D? tex))
        {
            tex = null;
            string path = (_libraryPath ?? "").PathJoin("icons").PathJoin(sheet);
            if (Godot.FileAccess.FileExists(path))
            {
                var img = new Image();
                if (img.LoadBmpFromBuffer(Godot.FileAccess.GetFileAsBytes(path)) == Error.Ok)
                {
                    // The border frames are drawn on black: black is see-through, so the icon shows inside.
                    if (sheet is "brdr_0.bmp" or "wgsm_0.bmp")
                    {
                        img.Convert(Image.Format.Rgba8);
                        for (int py = 0; py < img.GetHeight(); py++)
                            for (int px = 0; px < img.GetWidth(); px++)
                                if (img.GetPixel(px, py) is { R: < 0.04f, G: < 0.04f, B: < 0.04f })
                                    img.SetPixel(px, py, Colors.Transparent);
                    }
                    tex = ImageTexture.CreateFromImage(img);
                }
            }
            _sheetTextures[sheet] = tex;
        }
        return tex is null ? null : new AtlasTexture { Atlas = tex, Region = new Rect2(x, y, size, size) };
    }

    private Dictionary<int, string> LoadIconSheets()
    {
        string dir = (_libraryPath ?? "").PathJoin("icons");
        return DirAccess.DirExistsAbsolute(dir) ? IconSheets.Index(DirAccess.GetFilesAt(dir)) : [];
    }

    // The menu of what can go in a slot: the skill list by page, then each spell line; and Clear.
    private void FillChoices(PopupMenu menu, System.Action<string> chosen)
    {
        menu.Clear();
        foreach (Node child in menu.GetChildren())
            child.QueueFree();
        var entries = new List<string>();
        void Sub(string title, IEnumerable<(string Name, int Icon)> items)
        {
            var list = items.ToList();
            if (list.Count == 0)
                return;
            var sub = new PopupMenu { Name = $"sub{entries.Count}" };
            foreach ((string name, int _) in list)
            {
                sub.AddItem(name, entries.Count);
                if (EntryPicture("skill:" + name).Icon is Texture2D t)
                    sub.SetItemIcon(sub.ItemCount - 1, t);
                sub.SetItemIconMaxWidth(sub.ItemCount - 1, 24);
                entries.Add("skill:" + name);
            }
            sub.IdPressed += id => chosen(entries[(int)id]);
            menu.AddChild(sub);
            menu.AddSubmenuNodeItem(title, sub);
        }
        IReadOnlyList<SkillEntry> skills = _bridge.Skills.Skills;
        foreach (var page in skills.Where(s => s.Page != SkillPage.Specialization).GroupBy(s => s.Page))
            Sub(page.Key switch
            {
                SkillPage.Ability => "Abilities", SkillPage.Style => "Styles", SkillPage.Spell => "Spells",
                SkillPage.Song => "Songs", SkillPage.RealmAbility => "Realm abilities", _ => page.Key.ToString(),
            }, page.Select(s => (s.Name, (int)s.Icon)));
        foreach (SpellLinePage line in _bridge.Skills.SpellLines)
            Sub(line.LineName, line.Entries.Select(e => (e.Name, (int)e.Icon)));
        menu.AddSeparator();
        menu.AddItem("Clear", 9999);
        menu.IdPressed += id =>
        {
            if (id == 9999)
                chosen("");
        };
    }

    // ----- one bar -----

    private sealed class Quickbar
    {
        public readonly HudWindow Window;
        private readonly WorldView _view;
        private readonly int _bar;
        private readonly QuickSlot[] _slots = new QuickSlot[BarSlots];
        private readonly Label _page;
        private int _pageIndex;

        public Quickbar(WorldView view, int bar, HudWindow window, float scale)
        {
            (_view, _bar, Window) = (view, bar, window);
            _pageIndex = Mathf.Clamp(ClientSettings.QuickbarPage(view.PlayerName, bar), 0, BarPages - 1);
            // The handle: the bar's number, and its page between up and down arrows (the mouse wheel turns it too).
            window.Label(new Vector2(1, 1), new Vector2(24, 11), 8, centred: true).Text = $"Bar {bar + 1}";
            window.Arrow("▲", new Vector2(1, 12), new Vector2(24, 9), () => Turn(-1));
            _page = window.Label(new Vector2(1, 20), new Vector2(24, 10), 9, centred: true);
            window.Arrow("▼", new Vector2(1, 29), new Vector2(24, 9), () => Turn(+1));
            window.Wheel = by => Turn(by);
            for (int i = 0; i < BarSlots; i++)
            {
                int slot = i;
                string digit = i == 9 ? "0" : (i + 1).ToString();
                var q = new QuickSlot(scale, bar switch { 0 => digit, 1 => "A" + digit, _ => "C" + digit })
                {
                    Position = new Vector2(26 + i * 34, 3) * scale,
                    Use = () => Use(slot),
                    Choose = () => Choose(slot),
                    Key = (bar, slot),
                };
                q.Swapped += other => Swap(slot, other);
                q.Dropped += entry => Set(slot, entry);
                _slots[i] = q;
                window.AddChild(q);
            }
            ShowPage();
        }

        private string KeyOf(int slot) => $"{_bar + 1}.{_pageIndex + 1}.{slot + 1}";

        private string Entry(int slot) => ClientSettings.QuickbarSlot(_view.PlayerName, KeyOf(slot));

        public void Use(int slot) => _view.UseQuickbarEntry(Entry(slot));

        public void TurnTo(int page)
        {
            _pageIndex = page;
            ClientSettings.SaveQuickbarPage(_view.PlayerName, _bar, _pageIndex);
            ShowPage();
        }

        private void Turn(int by)
        {
            _pageIndex = (_pageIndex + by + BarPages) % BarPages;
            ClientSettings.SaveQuickbarPage(_view.PlayerName, _bar, _pageIndex);
            ShowPage();
        }

        private void ShowPage()
        {
            _page.Text = (_pageIndex + 1).ToString();
            for (int i = 0; i < BarSlots; i++)
            {
                string entry = Entry(i);
                _slots[i].Show(entry, _view.EntryPicture(entry));
            }
        }

        private void Choose(int slot)
        {
            var menu = new PopupMenu();
            _view.FillChoices(menu, entry =>
            {
                ClientSettings.SaveQuickbarSlot(_view.PlayerName, KeyOf(slot), entry);
                ShowPage();
            });
            Window.AddChild(menu);
            menu.PopupHide += () => menu.QueueFree();
            menu.Popup(new Rect2I((Vector2I)Window.GetScreenPosition() + (Vector2I)_slots[slot].Position, Vector2I.Zero));
        }

        // Something dragged in from elsewhere (a spell or style window): it goes in that slot.
        private void Set(int slot, string entry)
        {
            ClientSettings.SaveQuickbarSlot(_view.PlayerName, KeyOf(slot), entry);
            ShowPage();
        }

        // A slot of this bar dropped on slot `to` (same bar or another): swap the two.
        private void Swap(int to, (int Bar, int Slot) from)
        {
            Quickbar other = _view._bars[from.Bar];
            string a = Entry(to), b = other.Entry(from.Slot);
            ClientSettings.SaveQuickbarSlot(_view.PlayerName, KeyOf(to), b);
            ClientSettings.SaveQuickbarSlot(_view.PlayerName, other.KeyOf(from.Slot), a);
            ShowPage();
            if (other != this)
                other.ShowPage();
        }

        // Icons appear once the skill list arrives; cooldowns count down.
        public void Refresh(long now)
        {
            for (int i = 0; i < BarSlots; i++)
            {
                string entry = Entry(i);
                if (_slots[i].Entry != entry || (_slots[i].Icon is null && entry.Length > 0))
                    _slots[i].Show(entry, _view.EntryPicture(entry));
                double wait = entry.StartsWith("skill:") ? _view._bridge.Skills.CooldownLeft(entry[6..], now) : 0;
                _slots[i].Cooldown(wait);
            }
        }
    }

    // One slot: a 32 x 32 icon in a frame, the key's number, a dark cooldown veil with the seconds left.
    private sealed partial class QuickSlot : Panel
    {
        public System.Action? Use, Choose;
        public event System.Action<(int, int)>? Swapped;
        public event System.Action<string>? Dropped;   // a "skill:<name>" (or other entry) dragged from another window
        public (int Bar, int Slot) Key;
        public string Entry { get; private set; } = "";
        public Texture2D? Icon { get; private set; }
        private readonly TextureRect _icon, _border;
        private readonly Control _marks;
        private readonly float _scale;
        private readonly Label _name, _wait;
        private readonly ColorRect _veil;

        public QuickSlot(float scale, string key)
        {
            _scale = scale;
            Size = new Vector2(32, 32) * scale;
            MouseFilter = MouseFilterEnum.Stop;
            AddThemeStyleboxOverride("panel", new StyleBoxFlat
            {
                BgColor = new Color(0, 0, 0, 0.5f), BorderColor = new Color(0.55f, 0.85f, 1f, 0.3f),
                BorderWidthLeft = 1, BorderWidthTop = 1, BorderWidthRight = 1, BorderWidthBottom = 1,
            });
            _icon = new TextureRect
            {
                Size = Size, ExpandMode = TextureRect.ExpandModeEnum.IgnoreSize, StretchMode = TextureRect.StretchModeEnum.Scale,
                MouseFilter = MouseFilterEnum.Ignore,
            };
            AddChild(_icon);
            _border = new TextureRect
            {
                Size = Size, ExpandMode = TextureRect.ExpandModeEnum.IgnoreSize, StretchMode = TextureRect.StretchModeEnum.Scale,
                MouseFilter = MouseFilterEnum.Ignore,
            };
            AddChild(_border);
            _marks = new Control { Size = Size, MouseFilter = MouseFilterEnum.Ignore };
            AddChild(_marks);
            // Without an icon (a command, or one the sheets lack): its name, small.
            _name = Text(new Vector2(1, 8), new Vector2(30, 18), 7, HorizontalAlignment.Center, scale);
            _name.AutowrapMode = TextServer.AutowrapMode.Arbitrary;
            _veil = new ColorRect { Size = Size, Color = new Color(0, 0, 0, 0.6f), Visible = false, MouseFilter = MouseFilterEnum.Ignore };
            AddChild(_veil);
            _wait = Text(new Vector2(0, 9), new Vector2(32, 14), 10, HorizontalAlignment.Center, scale);
            // The key, bottom left: the one corner the style marks never use (their grid has no down-left place).
            Label k = Text(new Vector2(1, 22), new Vector2(14, 10), 8, HorizontalAlignment.Left, scale);
            k.Text = key;
            k.AddThemeColorOverride("font_outline_color", Colors.Black);
            k.AddThemeConstantOverride("outline_size", Mathf.RoundToInt(2 * scale));
        }

        private Label Text(Vector2 at, Vector2 size, int font, HorizontalAlignment align, float scale)
        {
            var l = new Label
            {
                Position = at * scale, Size = size * scale, HorizontalAlignment = align, VerticalAlignment = VerticalAlignment.Center,
                MouseFilter = MouseFilterEnum.Ignore, ClipText = true,
            };
            l.AddThemeFontSizeOverride("font_size", Mathf.RoundToInt(font * scale));
            AddChild(l);
            return l;
        }

        public void Show(string entry, SlotPicture picture)
        {
            Texture2D? icon = picture.Icon;
            (Entry, Icon) = (entry, icon);
            _icon.Texture = icon;
            _border.Texture = picture.Border;
            foreach (Node m in _marks.GetChildren())
                m.QueueFree();
            foreach ((Texture2D mark, Vector2 at) in picture.Marks)
                _marks.AddChild(new TextureRect
                {
                    Texture = mark, Position = at * _scale, Size = new Vector2(10, 10) * _scale,
                    ExpandMode = TextureRect.ExpandModeEnum.IgnoreSize, StretchMode = TextureRect.StretchModeEnum.Scale,
                    MouseFilter = MouseFilterEnum.Ignore,
                });
            _name.Text = icon is null && entry.Length > 0 ? entry[(entry.IndexOf(':') + 1)..] : "";
            TooltipText = entry.Length > 0 ? entry[(entry.IndexOf(':') + 1)..] : "Right-click to choose what goes here";
        }

        public void Cooldown(double seconds)
        {
            _veil.Visible = seconds > 0;
            _wait.Text = seconds > 0 ? (seconds >= 60 ? $"{seconds / 60:0}m" : $"{System.Math.Ceiling(seconds):0}") : "";
        }

        public override void _GuiInput(InputEvent e)
        {
            if (e is InputEventMouseButton { Pressed: false } mb)
            {
                if (mb.ButtonIndex == MouseButton.Left)
                    Use?.Invoke();
                else if (mb.ButtonIndex == MouseButton.Right)
                    Choose?.Invoke();
                AcceptEvent();
            }
        }

        public override Variant _GetDragData(Vector2 atPosition)
        {
            if (Entry.Length == 0)
                return default;
            SetDragPreview(new TextureRect { Texture = Icon, Size = Size, ExpandMode = TextureRect.ExpandModeEnum.IgnoreSize, Modulate = new Color(1, 1, 1, 0.7f) });
            return new Vector2I(Key.Bar, Key.Slot);
        }

        // Another slot (its bar and slot), or an entry from another window: what the spell and style windows will
        // drag in, as retail's do.
        public override bool _CanDropData(Vector2 atPosition, Variant data) =>
            data.VariantType == Variant.Type.Vector2I
            || (data.VariantType == Variant.Type.String && data.AsString() is string s && (s.StartsWith("skill:") || s.StartsWith("cmd:") || s.StartsWith("slot:")));

        public override void _DropData(Vector2 atPosition, Variant data)
        {
            if (data.VariantType == Variant.Type.String)
            {
                Dropped?.Invoke(data.AsString());
                return;
            }
            Vector2I from = data.AsVector2I();
            Swapped?.Invoke((from.X, from.Y));
        }
    }
}
