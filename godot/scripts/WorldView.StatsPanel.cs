// SPDX-License-Identifier: GPL-3.0-or-later
//
// WorldView: the right-hand stats panel (daoc-re RE/74 section 3; docs/ui-windows.md). Retail's is a 192 x 32 tab
// strip (stats_index) over one 192 x 416 page: Attributes, Inventory, Skills, Styles, Spells, Group, in that order.
// The menu bar's ATTRIB., INV., SKILLS, STYLES, SPELLS and GROUP open it at their page, and the button for the page
// already showing closes it. Default place: the top right (default.ini Stats=… at every resolution). Ours is one
// movable window in our plain style, with word tabs where retail draws icons.
//
// Pages, at the stock skin's positions (ui/atlantis/stats_attributes_window.xml, stats_spec_abil_window.xml):
//   Attributes: name; level, realm points; weapon skill and damage, hit points, armour factor (0x16 subcode 5); the
//     eight stats and nine resists (0xFB, both kinds), each with its make-up in a tooltip; an "additional info" list
//     (what retail puts there isn't known: ours lists race, class, guild, titles and points); the six buttons (only
//     OPTIONS works yet).
//   Skills: Specializations with the points left, Abilities, Tradeskills (0x16 subcodes 1 and 8).
//   Styles, Spells: retail draws these sheets itself (combstylesheet.bmp, Magicsheet.bmp); ours are lists with the
//     icons. Double-click uses one, drag puts it on a quickbar, right-click describes it in the item window (as retail's).
//   Inventory: the paper doll and backpack (WorldView.Inventory).
//   Group: the members, their bars and effect icons, and the group buttons (WorldView.Group).

using System.Collections.Generic;
using System.Linq;
using DaocProtocol.Packets;
using Godot;

namespace EodClient;

public partial class WorldView
{
    private const int StatsAttributes = 0, StatsInventory = 1, StatsSkills = 2, StatsStyles = 3, StatsSpells = 4, StatsGroup = 5;
    private static readonly string[] StatsTabs = ["Attr", "Inv", "Skill", "Style", "Spell", "Group"];
    private static readonly string[] StatsTabNames = ["Attributes", "Inventory", "Skills", "Styles", "Spells", "Group"];

    private HudWindow? _statsPanel;
    private readonly List<Button> _statsTabButtons = [];
    private readonly List<Control> _statsPages = [];
    private int _statsPage;
    private bool _statsDirty = true;
    private double _statsAt;
    private StatsUpdate? _stats, _resists;
    private CraftingSkills? _crafting;

    // Attributes page.
    private Label? _attrName, _attrLevel, _attrRealmPoints, _attrWeaponSkill, _attrWeaponDamage, _attrHitPoints, _attrArmour;
    private readonly Dictionary<string, Label> _attrStats = [], _attrResists = [];
    private ItemList? _attrInfo;
    // Skills page.
    private Label? _specPoints;
    private ItemList? _specList, _abilityList, _tradeList;
    // Styles and spells pages.
    private SheetList? _styleList, _spellList;

    private void BuildStatsPanel(CanvasLayer overlay)
    {
        float s = UiScale;
        _statsTabButtons.Clear();
        _statsPages.Clear();
        _statsPanel = new HudWindow("stats", new Vector2(192, 448), s, screen => new Vector2(screen.X - 192, 0))
        {
            DragAnywhere = true, Visible = false,
        };
        for (int t = 0; t < StatsTabs.Length; t++)
        {
            int tab = t;
            Button b = _statsPanel.Button(StatsTabs[t], new Vector2(2 + 29 * t, 6), 28, () => ShowStatsPage(tab), height: 20);
            b.TooltipText = StatsTabNames[t];
            b.AddThemeFontSizeOverride("font_size", Mathf.RoundToInt(6.5f * s));   // the word fits the tab's 28 skin pixels
            _statsTabButtons.Add(b);
        }
        _statsPanel.Arrow("✕", new Vector2(177, 9), new Vector2(13, 14), () => _statsPanel.Visible = false);
        for (int t = 0; t < StatsTabs.Length; t++)
        {
            var page = new Control { Position = new Vector2(0, 32) * s, Size = new Vector2(192, 416) * s, MouseFilter = Control.MouseFilterEnum.Ignore, Visible = false };
            _statsPanel.AddChild(page);
            _statsPages.Add(page);
        }
        BuildAttributesPage(_statsPages[StatsAttributes], s);
        BuildSkillsPage(_statsPages[StatsSkills], s);
        _styleList = Sheet(_statsPages[StatsStyles], s, new Vector2(4, 6), new Vector2(184, 404));
        _spellList = Sheet(_statsPages[StatsSpells], s, new Vector2(4, 6), new Vector2(184, 404));
        BuildInventoryPage(_statsPages[StatsInventory], s);
        BuildGroupPage(_statsPages[StatsGroup], s);
        ShowStatsPage(_statsPage);
        _statsDirty = true;
        overlay.AddChild(_statsPanel);
    }

    // The menu bar's page buttons: open the panel at that page, or close it if that page is already showing.
    private void ToggleStatsPage(int page)
    {
        if (_statsPanel is null || _session is null)
            return;
        if (_statsPanel.Visible && _statsPage == page)
        {
            _statsPanel.Visible = false;
            return;
        }
        _statsPanel.Visible = true;
        ShowStatsPage(page);
    }

    private void ShowStatsPage(int page)
    {
        _statsPage = page;
        for (int t = 0; t < _statsPages.Count; t++)
        {
            _statsPages[t].Visible = t == page;
            // The current tab in retail's pressed-tab gold (stats_index_window.xml: 255, 192, 0).
            _statsTabButtons[t].AddThemeColorOverride("font_color", t == page ? new Color(1f, 0.75f, 0f) : new Color(0.75f, 0.75f, 0.75f));
        }
        _statsDirty = true;
    }

    // ----- building -----

    private static Label PageLabel(Control page, float s, Vector2 at, Vector2 size, int font, string text = "",
        HorizontalAlignment align = HorizontalAlignment.Left, Color? colour = null)
    {
        var l = new Label
        {
            Text = text, Position = at * s, Size = size * s, ClipText = true, HorizontalAlignment = align,
            VerticalAlignment = VerticalAlignment.Center, MouseFilter = Control.MouseFilterEnum.Ignore,
        };
        l.AddThemeFontSizeOverride("font_size", Mathf.RoundToInt(font * s));
        if (colour is Color c)
            l.AddThemeColorOverride("font_color", c);
        page.AddChild(l);
        return l;
    }

    // A value in a sunken box (the skin's dialog_inset), right-aligned; tooltips need the mouse, so it stops it.
    private static Label Inset(Control page, float s, Vector2 at, float width)
    {
        var box = new Panel { Position = at * s, Size = new Vector2(width, 16) * s, MouseFilter = Control.MouseFilterEnum.Stop };
        box.AddThemeStyleboxOverride("panel", new StyleBoxFlat
        {
            BgColor = new Color(0, 0, 0, 0.45f), BorderColor = new Color(0.55f, 0.85f, 1f, 0.2f),
            BorderWidthLeft = 1, BorderWidthTop = 1, BorderWidthRight = 1, BorderWidthBottom = 1,
        });
        page.AddChild(box);
        var l = new Label
        {
            Position = new Vector2(1, 0) * s, Size = new Vector2(width - 4, 16) * s, ClipText = true,
            HorizontalAlignment = HorizontalAlignment.Right, VerticalAlignment = VerticalAlignment.Center,
            MouseFilter = Control.MouseFilterEnum.Ignore,
        };
        l.AddThemeFontSizeOverride("font_size", Mathf.RoundToInt(9 * s));
        box.AddChild(l);
        return l;
    }

    private static readonly Color Caption = new(0.78f, 0.78f, 0.78f);

    private void BuildAttributesPage(Control p, float s)
    {
        _attrName = PageLabel(p, s, new Vector2(11, 3), new Vector2(170, 18), 11, align: HorizontalAlignment.Center);
        PageLabel(p, s, new Vector2(6, 21), new Vector2(30, 16), 9, "Level", colour: Caption);
        _attrLevel = Inset(p, s, new Vector2(34, 21), 26);
        PageLabel(p, s, new Vector2(64, 21), new Vector2(66, 16), 9, "Realm Points", colour: Caption);
        _attrRealmPoints = Inset(p, s, new Vector2(131, 21), 57);
        PageLabel(p, s, new Vector2(6, 39), new Vector2(50, 16), 9, "WeapSkill", colour: Caption);
        _attrWeaponSkill = Inset(p, s, new Vector2(56, 39), 38);
        PageLabel(p, s, new Vector2(6, 57), new Vector2(50, 16), 9, "WeapDam", colour: Caption);
        _attrWeaponDamage = Inset(p, s, new Vector2(56, 57), 38);
        PageLabel(p, s, new Vector2(100, 39), new Vector2(52, 16), 9, "Hit Points", colour: Caption);
        _attrHitPoints = Inset(p, s, new Vector2(153, 39), 35);
        PageLabel(p, s, new Vector2(100, 57), new Vector2(52, 16), 9, "ArmorFact", colour: Caption);
        _attrArmour = Inset(p, s, new Vector2(153, 57), 35);

        // The stats, two columns as the skin has them (not the packet's order).
        string[] left = ["Strength", "Constitution", "Dexterity", "Quickness"], right = ["Intelligence", "Empathy", "Piety", "Charisma"];
        _attrStats.Clear();
        for (int i = 0; i < 4; i++)
        {
            _attrStats[left[i]] = Inset(p, s, new Vector2(5, 77 + 18 * i), 28);
            PageLabel(p, s, new Vector2(35, 77 + 18 * i), new Vector2(60, 16), 9, left[i]);
            _attrStats[right[i]] = Inset(p, s, new Vector2(96, 77 + 18 * i), 28);
            PageLabel(p, s, new Vector2(126, 77 + 18 * i), new Vector2(62, 16), 9, right[i]);
        }

        PageLabel(p, s, new Vector2(11, 153), new Vector2(80, 16), 9, "Resists", colour: new Color(1f, 0.85f, 0.5f));
        string[] rLeft = ["Thrust", "Crush", "Slash", "Heat"], rRight = ["Cold", "Matter", "Energy", "Spirit", "Body"];
        _attrResists.Clear();
        for (int i = 0; i < rLeft.Length; i++)
        {
            _attrResists[rLeft[i]] = Inset(p, s, new Vector2(5, 171 + 18 * i), 36);
            PageLabel(p, s, new Vector2(45, 171 + 18 * i), new Vector2(48, 16), 9, rLeft[i]);
        }
        for (int i = 0; i < rRight.Length; i++)
        {
            _attrResists[rRight[i]] = Inset(p, s, new Vector2(96, 153 + 18 * i), 36);
            PageLabel(p, s, new Vector2(136, 153 + 18 * i), new Vector2(52, 16), 9, rRight[i]);
        }

        _attrInfo = List(p, s, new Vector2(4, 243), new Vector2(181, 130));

        (string Text, Vector2 At, System.Action? Pressed)[] buttons =
        [
            ("SOCIAL", new Vector2(5, 377), null), ("MOUNT", new Vector2(66, 377), null), ("PRAY", new Vector2(127, 377), null),
            ("COMMAND", new Vector2(5, 395), null), ("OPTIONS", new Vector2(66, 395), () => SettingsDialog.Open(this, 0)),
            ("BIND", new Vector2(127, 395), null),
        ];
        foreach ((string text, Vector2 at, System.Action? pressed) in buttons)
        {
            var b = new Button
            {
                Text = text, Position = at * s, Size = new Vector2(58, 17) * s, FocusMode = Control.FocusModeEnum.None,
                ClipText = true, Disabled = pressed is null, TooltipText = pressed is null ? "Not built yet" : "",
            };
            StyleButton(b, s);
            if (pressed is not null)
                b.Pressed += pressed;
            p.AddChild(b);
        }
    }

    private void BuildSkillsPage(Control p, float s)
    {
        Color head = new(1f, 0.85f, 0.5f);
        PageLabel(p, s, new Vector2(11, 6), new Vector2(110, 16), 11, "Specializations", colour: head);
        _specPoints = PageLabel(p, s, new Vector2(121, 6), new Vector2(64, 16), 11);
        _specList = List(p, s, new Vector2(4, 24), new Vector2(181, 104));
        PageLabel(p, s, new Vector2(11, 133), new Vector2(169, 16), 11, "Abilities", colour: head);
        _abilityList = List(p, s, new Vector2(4, 151), new Vector2(181, 127));
        PageLabel(p, s, new Vector2(11, 283), new Vector2(169, 16), 11, "Tradeskills", colour: head);
        _tradeList = List(p, s, new Vector2(4, 301), new Vector2(181, 110));
    }

    private static void NotBuilt(Control p, float s, string text)
    {
        Label l = PageLabel(p, s, new Vector2(12, 160), new Vector2(168, 60), 10, text, HorizontalAlignment.Center, new Color(0.6f, 0.62f, 0.66f));
        l.AutowrapMode = TextServer.AutowrapMode.WordSmart;
    }

    private static void StyleButton(Button b, float s)
    {
        b.AddThemeFontSizeOverride("font_size", Mathf.RoundToInt(8 * s));
        b.AddThemeColorOverride("font_disabled_color", new Color(0.5f, 0.52f, 0.55f, 0.6f));
        foreach ((string state, float shade) in new[] { ("normal", 0.16f), ("hover", 0.24f), ("pressed", 0.10f), ("disabled", 0.08f) })
            b.AddThemeStyleboxOverride(state, new StyleBoxFlat
            {
                BgColor = new Color(shade, shade + 0.02f, shade + 0.05f, 0.95f), BorderColor = new Color(0.55f, 0.85f, 1f, 0.3f),
                BorderWidthLeft = 1, BorderWidthTop = 1, BorderWidthRight = 1, BorderWidthBottom = 1,
                CornerRadiusTopLeft = 2, CornerRadiusTopRight = 2, CornerRadiusBottomLeft = 2, CornerRadiusBottomRight = 2,
            });
    }

    private static T Styled<T>(T list, Control page, float s, Vector2 at, Vector2 size) where T : ItemList
    {
        list.Position = at * s;
        list.Size = size * s;
        list.FocusMode = Control.FocusModeEnum.None;
        list.SelectMode = ItemList.SelectModeEnum.Single;
        list.AddThemeFontSizeOverride("font_size", Mathf.RoundToInt(9 * s));
        list.AddThemeConstantOverride("v_separation", Mathf.RoundToInt(1 * s));
        list.AddThemeStyleboxOverride("panel", new StyleBoxFlat
        {
            BgColor = new Color(0, 0, 0, 0.4f), BorderColor = new Color(0.55f, 0.85f, 1f, 0.2f),
            BorderWidthLeft = 1, BorderWidthTop = 1, BorderWidthRight = 1, BorderWidthBottom = 1,
            ContentMarginLeft = 3 * s, ContentMarginTop = 2 * s, ContentMarginRight = 3 * s, ContentMarginBottom = 2 * s,
        });
        list.AddThemeStyleboxOverride("focus", new StyleBoxEmpty());
        page.AddChild(list);
        return list;
    }

    private static ItemList List(Control page, float s, Vector2 at, Vector2 size) => Styled(new ItemList(), page, s, at, size);

    private SheetList Sheet(Control page, float s, Vector2 at, Vector2 size)
    {
        SheetList list = Styled(new SheetList(), page, s, at, size);
        list.FixedIconSize = new Vector2I(1, 1) * Mathf.RoundToInt(20 * s);
        // Right-click a spell or style: the item window describes it (as an item's right-click on the INV. page).
        list.AllowRmbSelect = true;
        list.ItemClicked += (i, _, button) =>
        {
            if (button == (long)MouseButton.Right && list.GetItemMetadata((int)i).AsString() is string e && e.StartsWith("skill:"))
                SelectSkill(e[6..]);
        };
        list.ItemActivated += i =>
        {
            if (list.GetItemMetadata((int)i).AsString() is string entry && entry.StartsWith("skill:"))
                UseSkillByName(entry[6..]);
        };
        return list;
    }

    // ----- filling -----

    private void SubscribeStats()
    {
        _bridge.StatsChanged += u =>
        {
            if (u.IsResists)
                _resists = u;
            else
                _stats = u;
            _statsDirty = true;
        };
        _bridge.CraftingSkillsChanged += c => (_crafting, _statsDirty) = (c, true);
        _bridge.SkillsChanged += _ => _statsDirty = true;
        _bridge.SpellLinesChanged += _ => _statsDirty = true;
        _bridge.PointsChanged += _ => _statsDirty = true;
        _bridge.PlayerInfoChanged += _ => _statsDirty = true;
        _bridge.WeaponStatsChanged += _ => _statsDirty = true;
        _bridge.InventoryChanged += inv =>
        {
            _statsDirty = true;
            GD.Print($"[world] inventory: {inv.Items.Count} item(s): " +
                     string.Join(", ", inv.Items.OrderBy(kv => kv.Key).Select(kv => $"{kv.Key} {kv.Value.Name}")));
        };
        _bridge.EncumbranceChanged += e => (_encumbrance, _statsDirty) = (e, true);
        _bridge.MoneyChanged += _ => _statsDirty = true;
    }

    private void UpdateStatsPanel()
    {
        if (_statsPanel is null || !_statsPanel.Visible || !_statsDirty || _seconds - _statsAt < 0.2)
            return;
        (_statsDirty, _statsAt) = (false, _seconds);
        switch (_statsPage)
        {
            case StatsAttributes: FillAttributes(); break;
            case StatsInventory: FillInventory(); break;
            case StatsSkills: FillSkills(); break;
            case StatsStyles: FillStyles(); break;
            case StatsSpells: FillSpells(); break;
            case StatsGroup: FillGroup(); break;
        }
    }

    private void FillAttributes()
    {
        _attrName!.Text = _info?.Name ?? PlayerName;
        _attrLevel!.Text = _info?.Level.ToString() ?? "";
        _attrRealmPoints!.Text = _points?.RealmPoints.ToString("N0") ?? "";
        _attrWeaponSkill!.Text = _weapon?.WeaponSkill.ToString() ?? "";
        _attrWeaponDamage!.Text = _weapon is WeaponStats w ? w.WeaponDamage.ToString("0.0") : "";
        _attrArmour!.Text = _weapon?.ArmorFactor.ToString() ?? "";
        _attrHitPoints!.Text = (_stats?.MaxHealth ?? _info?.MaxHealth)?.ToString() ?? "";

        static string MakeUp(StatsUpdate u, int i, string unit)
        {
            string text = $"base {u.Base[i]}{unit}";
            if (u.Buff[i] != 0)
                text += $", {(u.Buff[i] > 0 ? "buffs +" : "debuffs ")}{u.Buff[i]}";
            if (u.Item[i] != 0)
                text += $", items +{u.Item[i]} (cap {u.ItemCap[i]})";
            if (u.Bonus[i] != 0)
                text += $", realm abilities +{u.Bonus[i]}";
            return text;
        }
        for (int i = 0; i < StatsUpdate.StatNames.Length; i++)
            if (_attrStats.TryGetValue(StatsUpdate.StatNames[i], out Label? l))
            {
                l.Text = _stats is StatsUpdate u ? u.Total(i).ToString() : "";
                ((Control)l.GetParent()).TooltipText = _stats is StatsUpdate t
                    ? $"{StatsUpdate.StatNames[i]}: " + MakeUp(t, i, "") + (i == 2 && t.ConstitutionLost > 0 ? $", {t.ConstitutionLost} lost to deaths" : "")
                    : "";
            }
        for (int i = 0; i < StatsUpdate.ResistNames.Length; i++)
            if (_attrResists.TryGetValue(StatsUpdate.ResistNames[i], out Label? l))
            {
                l.Text = _resists is StatsUpdate u ? $"{u.Total(i)}%" : "";
                ((Control)l.GetParent()).TooltipText = _resists is StatsUpdate t ? $"{StatsUpdate.ResistNames[i]}: " + MakeUp(t, i, "% racial") : "";
            }

        _attrInfo!.Clear();
        void Line(string caption, string value)
        {
            if (value.Length > 0)
                _attrInfo.AddItem($"{caption}: {value}", selectable: false);
        }
        if (_info is PlayerInfo i2)
        {
            Line("Race", PlayerInfo.Shown(i2.RaceName));
            Line("Class", PlayerInfo.Shown(i2.ClassName));
            Line("Title", PlayerInfo.Shown(i2.LevelTitle));
            Line("Realm rank", $"{i2.RealmRank} {PlayerInfo.Shown(i2.RealmTitle)}".Trim());
            Line("Guild", PlayerInfo.Shown(i2.GuildName) is { Length: > 0 } g
                ? g + (PlayerInfo.Shown(i2.GuildRankTitle) is { Length: > 0 } r ? $" ({r})" : "") : "");
            Line("Last name", PlayerInfo.Shown(i2.LastName));
            Line("Crafting", $"{PlayerInfo.Shown(i2.CraftingSkill)} {PlayerInfo.Shown(i2.CraftingTitle)}".Trim());
        }
        if (_points is PointsUpdate pts)
        {
            Line("Experience", $"{pts.LevelPermill / 10.0:0.0}% of the level");
            Line("Spec points", pts.SpecPoints.ToString());
            Line("Realm spec points", pts.RealmSpecPoints.ToString());
            Line("Bounty points", pts.BountyPoints.ToString("N0"));
        }
    }

    private void FillSkills()
    {
        IReadOnlyList<SkillEntry> skills = _bridge.Skills.Skills;
        _specPoints!.Text = _points is PointsUpdate p ? $"pts: {p.SpecPoints}" : "";
        _specList!.Clear();
        foreach (SkillEntry e in skills.Where(e => e.Page == SkillPage.Specialization))
        {
            int at = _specList.AddItem($"{e.Name}  {e.Level}" + (e.Bonus > 0 ? $" (+{e.Bonus})" : ""));
            _specList.SetItemTooltip(at, e.Bonus > 0 ? $"{e.Name}: trained {e.Level}, +{e.Bonus} from items and effects" : $"{e.Name}: {e.Level}");
        }
        _abilityList!.Clear();
        foreach (SkillEntry e in skills.Where(e => e.Page is SkillPage.Ability or SkillPage.RealmAbility))
        {
            int at = _abilityList.AddItem(e.Name);
            _abilityList.SetItemTooltip(at, e.Page == SkillPage.RealmAbility ? $"{e.Name} (realm ability)" : e.Name);
        }
        _tradeList!.Clear();
        foreach (CraftingSkill c in _crafting?.Skills ?? [])
            _tradeList.AddItem($"{c.Name}  {c.Points}");
    }

    // A sheet row: the icon, the name and level; double-click uses it, drag carries "skill:<name>" to a quickbar.
    private void SheetRow(SheetList list, string name, int level, string tooltip)
    {
        int at = list.AddItem($"{name}  ({level})", EntryPicture("skill:" + name).Icon);
        list.SetItemMetadata(at, "skill:" + name);
        list.SetItemTooltip(at, tooltip);
    }

    private void SheetHeading(SheetList list, string text)
    {
        int at = list.AddItem(text, selectable: false);
        list.SetItemCustomFgColor(at, new Color(1f, 0.85f, 0.5f));
        list.SetItemMetadata(at, "");
    }

    private void FillStyles()
    {
        _styleList!.Clear();
        List<SkillEntry> styles = _bridge.Skills.Skills.Where(e => e.Page == SkillPage.Style).ToList();
        if (styles.Count == 0)
            SheetHeading(_styleList, "No combat styles.");
        foreach (SkillEntry e in styles)
            SheetRow(_styleList, e.Name, e.Level, $"{e.Name}: combat style, level {e.Level}. Double-click to ready it, right-click for its details; drag it to a quickbar.");
    }

    private void FillSpells()
    {
        _spellList!.Clear();
        int rows = 0;
        // A list caster's spells come by line (0x16 subcode 2); a hybrid's and songs in the skill list.
        foreach (SpellLinePage line in _bridge.Skills.SpellLines)
        {
            SheetHeading(_spellList, line.LineName);
            foreach (LineEntry e in line.Entries)
            {
                SheetRow(_spellList, e.Name, e.Level, $"{e.Name}: {line.LineName}, level {e.Level}. Double-click to cast, right-click for its details; drag it to a quickbar.");
                rows++;
            }
        }
        foreach (var page in _bridge.Skills.Skills.Where(e => e.Page is SkillPage.Spell or SkillPage.Song or SkillPage.AbilitySpell).GroupBy(e => e.Page))
        {
            SheetHeading(_spellList, page.Key switch { SkillPage.Song => "Songs", SkillPage.AbilitySpell => "Abilities", _ => "Spells" });
            foreach (SkillEntry e in page)
            {
                SheetRow(_spellList, e.Name, e.Level, $"{e.Name}: level {e.Level}. Double-click to cast, right-click for its details; drag it to a quickbar.");
                rows++;
            }
        }
        if (rows == 0)
            SheetHeading(_spellList, "No spells.");
    }

    // A spell or style list whose rows can be dragged onto a quickbar slot (QuickSlot takes a "skill:<name>" string).
    private sealed partial class SheetList : ItemList
    {
        public override Variant _GetDragData(Vector2 atPosition)
        {
            int i = GetItemAtPosition(atPosition, exact: true);
            if (i < 0 || GetItemMetadata(i).AsString() is not { Length: > 0 } entry)
                return default;
            Texture2D? icon = GetItemIcon(i);
            Control preview = icon is not null
                ? new TextureRect { Texture = icon, Size = new Vector2(32, 32), ExpandMode = TextureRect.ExpandModeEnum.IgnoreSize, Modulate = new Color(1, 1, 1, 0.7f) }
                : new Label { Text = entry[6..] };
            SetDragPreview(preview);
            return entry;
        }
    }
}
