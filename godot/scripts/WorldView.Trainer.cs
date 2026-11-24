// SPDX-License-Identifier: GPL-3.0-or-later
//
// WorldView: the training window (roadmap M13), as retail 1.130 draws and drives it (daoc-re, 2026-11-24): the stock
// skin's training_window.xml (390 x 536) at its positions. The server sends it as a burst of 0x7B sections when the player
// right-clicks the class trainer, or after /train (C→S 0x7B); codes 0 and 1 open it (DaocProtocol.Session.TrainerState
// keeps the sections and does the sums).
//   - Top: Specialization Points and Realm Points, each Available (0x91) / Needed / Remaining, and Minimum Level;
//     Remaining below 0 and a Minimum Level above the character's level in the "over" colour.
//   - Specializations: 8 rows, a name, a slider (it can't go below the current level) and the level, in the "changed"
//     colour once moved; "New Styles and Skills": up to 20 icons for the skills between the current and target levels.
//   - Ability Summary: per line the target level, how many new abilities, and their icons 9 at a time (PREV / NEXT).
//   - Realm Abilities: a row per ability with a slider to its max level and the cost from its current level.
//   - TRAIN sends every line's and ability's target in one 0x53, unchecked; RESET puts the sliders back; CANCEL hides it;
//     /RESPEC ALL / REALM / BUY are the chat commands. Clicking a line's or ability's name delves it (0xD8 type 9, id = row,
//     or 50 + the ability's index); those and the respecs wait a second after the last interact.
//   - It closes on a change of target or more than 256 units (2-D) from the trainer; closing sends nothing.
// Ours: the "over" (red) and "changed" (gold) colours, a slider's top at 50 whatever the character's level, Esc closes it.

using System.Collections.Generic;
using System.Linq;
using DaocProtocol.Packets;
using DaocProtocol.Session;
using Godot;

namespace EodClient;

public partial class WorldView
{
    private static readonly Color TrainOver = new(1f, 0.4f, 0.4f), TrainChanged = new(1f, 0.75f, 0f);
    private const int TrainSummaryIcons = 9;
    private readonly TrainerState _trainer = new();
    private HudWindow? _trainWindow;
    private ushort _trainerId;
    private int _trainTab;
    private bool _trainBuilding;     // setting sliders from the state: their change signals are ignored
    private Label? _trainSpecAvail, _trainSpecNeeded, _trainSpecRemaining, _trainRealmAvail, _trainRealmNeeded,
        _trainRealmRemaining, _trainMinLevel;
    private readonly List<Button> _trainTabs = [];
    private readonly List<Control> _trainPages = [];
    private readonly List<(Button Name, HSlider Slider, Label Level)> _trainRows = [];
    private readonly List<TextureRect> _trainNewIcons = [];
    private VBoxContainer? _trainSummary, _trainRealmList;
    private readonly Dictionary<int, int> _trainSummaryPage = [];
    private string? _devTrain;       // --train LINE: once the trainer's sections are in, raise LINE by one level and TRAIN

    private void BuildTrainWindow(CanvasLayer overlay)
    {
        float s = UiScale;
        _trainTabs.Clear();
        _trainPages.Clear();
        _trainRows.Clear();
        _trainNewIcons.Clear();
        var w = new HudWindow("training", new Vector2(390, 536), s, screen => new Vector2(screen.X / 2 - 195, 60)) { Visible = false };
        _trainWindow = w;
        w.Label(new Vector2(0, 4), new Vector2(390, 15), 11, centred: true).Text = "Training";
        w.Arrow("✕", new Vector2(372, 2), new Vector2(15, 14), CloseTrainer);
        (_trainSpecAvail, _trainSpecNeeded, _trainSpecRemaining) = PointsBlock(w, 30, "Specialization Points");
        (_trainRealmAvail, _trainRealmNeeded, _trainRealmRemaining) = PointsBlock(w, 210, "Realm Points");
        w.Label(new Vector2(130, 115), new Vector2(110, 15), 10).Text = "Minimum Level";
        _trainMinLevel = w.Label(new Vector2(240, 115), new Vector2(40, 15), 10);

        string[] tabs = ["Specializations", "Ability Summary", "Realm Abilities"];
        for (int t = 0; t < tabs.Length; t++)
        {
            int tab = t;
            _trainTabs.Add(w.Button(tabs[t], new Vector2(8 + 125 * t, 138), 120, () => ShowTrainTab(tab), height: 17));
            var page = new Control { Position = Vector2.Zero, Size = new Vector2(390, 536) * s, MouseFilter = Control.MouseFilterEnum.Ignore, Visible = false };
            w.AddChild(page);
            _trainPages.Add(page);
        }

        // Specializations: 8 rows at y 180 + 25 i.
        Control specs = _trainPages[0];
        for (int i = 0; i < TrainerState.SliderRows; i++)
        {
            int row = i;
            float y = 180 + 25 * i;
            Button name = FlatButton(specs, s, new Vector2(35, y - 2), new Vector2(130, 15), () => DelveTrainLine((uint)row));
            var slider = new HSlider
            {
                Position = new Vector2(170, y) * s, Size = new Vector2(155, 15) * s, MinValue = 0, MaxValue = TrainerState.MaxLevel,
                Step = 1, FocusMode = Control.FocusModeEnum.None,
            };
            slider.ValueChanged += v => OnTrainSlider(row, (int)v);
            specs.AddChild(slider);
            Label level = PageLabel(specs, s, new Vector2(330, y), new Vector2(40, 15), 10);
            _trainRows.Add((name, slider, level));
        }
        PageLabel(specs, s, new Vector2(30, 390), new Vector2(300, 15), 10, "New Styles and Skills");
        for (int k = 0; k < TrainerState.MaxNewSkills; k++)
            _trainNewIcons.Add(TrainIcon(specs, s, new Vector2(25 + 34 * (k % 10), 410 + 34 * (k / 10))));

        // Ability Summary and Realm Abilities: scrolled lists, rebuilt with each window.
        _trainSummary = ScrollList(_trainPages[1], s, new Vector2(10, 160), new Vector2(370, 320));
        _trainRealmList = ScrollList(_trainPages[2], s, new Vector2(10, 175), new Vector2(370, 300));

        // Bottom rows (measured from the window's bottom in the XML).
        w.Button("/RESPEC ALL", new Vector2(14, 494), 115, () => Respec("all"));
        w.Button("/RESPEC REALM", new Vector2(135, 494), 115, () => Respec("realm"));
        w.Button("/RESPEC BUY", new Vector2(257, 494), 115, () => Respec("buy"));
        w.Button("TRAIN", new Vector2(55, 516), 75, Train);
        w.Button("RESET", new Vector2(143, 516), 100, () =>
        {
            _trainer.Reset();
            ShowTrainer();
        });
        w.Button("CANCEL", new Vector2(255, 516), 75, CloseTrainer);
        overlay.AddChild(w);
        ShowTrainTab(_trainTab);
        if (_trainer.Lines.Count > 0 && _trainerId == _target && _target != 0)
        {
            w.Visible = true;
            ShowTrainer();
        }
    }

    private (Label, Label, Label) PointsBlock(HudWindow w, float x, string title)
    {
        w.Label(new Vector2(x, 30), new Vector2(170, 15), 10).Text = title;
        string[] names = ["Available", "Needed", "Remaining"];
        var values = new Label[3];
        for (int i = 0; i < 3; i++)
        {
            w.Label(new Vector2(x + 10, 50 + 20 * i), new Vector2(80, 15), 10).Text = names[i];
            values[i] = w.Label(new Vector2(x + 90, 50 + 20 * i), new Vector2(60, 15), 10);
        }
        return (values[0], values[1], values[2]);
    }

    private static Button FlatButton(Control page, float s, Vector2 at, Vector2 size, System.Action pressed)
    {
        var b = new Button { Position = at * s, Flat = true, FocusMode = Control.FocusModeEnum.None, Alignment = HorizontalAlignment.Left, ClipText = true };
        b.AddThemeFontSizeOverride("font_size", Mathf.RoundToInt(10 * s));
        FixSize(b, size * s);
        b.Pressed += pressed;
        page.AddChild(b);
        return b;
    }

    private static TextureRect TrainIcon(Control page, float s, Vector2 at)
    {
        var icon = new TextureRect
        {
            Position = at * s, Size = new Vector2(32, 32) * s, ExpandMode = TextureRect.ExpandModeEnum.IgnoreSize,
            StretchMode = TextureRect.StretchModeEnum.Scale, MouseFilter = Control.MouseFilterEnum.Stop,
        };
        page.AddChild(icon);
        return icon;
    }

    private static VBoxContainer ScrollList(Control page, float s, Vector2 at, Vector2 size)
    {
        var scroll = new ScrollContainer { Position = at * s, Size = size * s, HorizontalScrollMode = ScrollContainer.ScrollMode.Disabled };
        page.AddChild(scroll);
        var list = new VBoxContainer { SizeFlagsHorizontal = Control.SizeFlags.ExpandFill };
        scroll.AddChild(list);
        return list;
    }

    private void ShowTrainTab(int tab)
    {
        _trainTab = tab;
        for (int t = 0; t < _trainPages.Count; t++)
        {
            _trainPages[t].Visible = t == tab;
            _trainTabs[t].AddThemeColorOverride("font_color", t == tab ? new Color(1f, 0.75f, 0f) : new Color(0.75f, 0.75f, 0.75f));
        }
    }

    // A 0x7B section: kept; codes 0 and 1 open (or refresh) the window.
    private void OnTrainerWindow(TrainerWindow w)
    {
        bool opens = _trainer.Apply(w);
        GD.Print($"[world] trainer section {w.Code}" + (w.Code == TrainerWindow.Code0Specs
            ? $": {w.Specs.Count} line(s): " + string.Join(", ", w.Specs.Select(x => $"{x.Name} {x.Current}")) : ""));
        if (_trainWindow is null)
            return;
        if (opens)
        {
            _trainerId = _target;
            _trainWindow.Visible = true;
        }
        if (_trainWindow.Visible)
            ShowTrainer();
        if (_devTrain is { } line && w.Code == TrainerWindow.CodeRealmCosts && _session is { } session)
        {
            _devTrain = null;
            int row = _trainer.Lines.ToList().FindIndex(l => l.Name.Equals(line, System.StringComparison.OrdinalIgnoreCase));
            if (row < 0)
            {
                GD.Print($"[world] --train: no line \"{line}\"");
                return;
            }
            _trainer.Targets[row]++;
            GD.Print($"[world] --train: {line} → {_trainer.Targets[row]}, needs {_trainer.Needed}, minimum level {_trainer.MinimumLevel}, " +
                     $"new: {string.Join(", ", _trainer.AllNewSkills().Select(n => _trainer.NameOf(n.Row, n.Skill)))}");
            ShowTrainer();
            _ = Send(() => session.TrainAsync(_trainer), "train");
        }
    }

    // Everything the window shows, from the state.
    private void ShowTrainer()
    {
        if (_trainWindow is null)
            return;
        _trainBuilding = true;
        IReadOnlyList<TrainerSpec> lines = _trainer.Lines;
        for (int i = 0; i < _trainRows.Count; i++)
        {
            (Button name, HSlider slider, Label level) = _trainRows[i];
            bool used = i < lines.Count;
            name.Visible = slider.Visible = level.Visible = used;
            if (!used)
                continue;
            name.Text = lines[i].Name;
            slider.Value = _trainer.Targets[i];
            level.Text = _trainer.Targets[i].ToString(System.Globalization.CultureInfo.InvariantCulture);
            level.AddThemeColorOverride("font_color", _trainer.Targets[i] != lines[i].Current ? TrainChanged : new Color(1, 1, 1));
        }
        var news = _trainer.AllNewSkills();
        for (int k = 0; k < _trainNewIcons.Count; k++)
        {
            bool has = k < news.Count;
            _trainNewIcons[k].Texture = has ? TrainSkillIcon(news[k].Info) : null;
            _trainNewIcons[k].TooltipText = has ? $"{_trainer.NameOf(news[k].Row, news[k].Skill)} ({news[k].Info.AtLevel})" : "";
        }
        _trainBuilding = false;
        ShowTrainSummary();
        ShowTrainRealm();
        ShowTrainPoints();
    }

    private void ShowTrainPoints()
    {
        int specAvail = _points?.SpecPoints ?? 0, realmAvail = _points?.RealmSpecPoints ?? 0;
        int needed = _trainer.Needed, realmNeeded = _trainer.RealmNeeded, min = _trainer.MinimumLevel;
        SetPoint(_trainSpecAvail, specAvail, false);
        SetPoint(_trainSpecNeeded, needed, false);
        SetPoint(_trainSpecRemaining, specAvail - needed, specAvail - needed < 0);
        SetPoint(_trainRealmAvail, realmAvail, false);
        SetPoint(_trainRealmNeeded, realmNeeded, false);
        SetPoint(_trainRealmRemaining, realmAvail - realmNeeded, realmAvail - realmNeeded < 0);
        SetPoint(_trainMinLevel, min, min > (_info?.Level ?? TrainerState.MaxLevel));
    }

    private static void SetPoint(Label? l, int value, bool over)
    {
        if (l is null)
            return;
        l.Text = value.ToString(System.Globalization.CultureInfo.InvariantCulture);
        l.AddThemeColorOverride("font_color", over ? TrainOver : new Color(1, 1, 1));
    }

    // Ability Summary: per line, its target level, how many new abilities, and 9 of their icons per page.
    private void ShowTrainSummary()
    {
        if (_trainSummary is null)
            return;
        float s = UiScale;
        foreach (Node c in _trainSummary.GetChildren())
            c.QueueFree();
        IReadOnlyList<TrainerSpec> lines = _trainer.Lines;
        for (int row = 0; row < lines.Count; row++)
        {
            int r = row;
            var news = _trainer.NewSkills(row).ToList();
            int pages = news.Count == 0 ? 1 : (news.Count - 1) / TrainSummaryIcons + 1;
            int page = Mathf.Clamp(_trainSummaryPage.GetValueOrDefault(row), 0, pages - 1);
            var block = new Control { CustomMinimumSize = new Vector2(360, 80) * s, MouseFilter = Control.MouseFilterEnum.Ignore };
            PageLabel(block, s, new Vector2(15, 0), new Vector2(125, 15), 10, lines[row].Name);
            PageLabel(block, s, new Vector2(140, 0), new Vector2(80, 15), 10, $"Level: {_trainer.Targets[row]}");
            PageLabel(block, s, new Vector2(220, 0), new Vector2(130, 15), 10, $"New Abilities: {news.Count}");
            foreach ((var n, int k) in news.Skip(page * TrainSummaryIcons).Take(TrainSummaryIcons).Select((n, k) => (n, k)))
            {
                TextureRect icon = TrainIcon(block, s, new Vector2(17 + 34 * k, 19));
                icon.Texture = TrainSkillIcon(n.Info);
                icon.TooltipText = $"{_trainer.NameOf(n.Row, n.Skill)} ({n.Info.AtLevel})";
            }
            Button prev = FlatButton(block, s, new Vector2(70, 55), new Vector2(50, 17), () => TurnSummary(r, -1));
            prev.Text = "PREV";
            prev.Disabled = page == 0;
            PageLabel(block, s, new Vector2(150, 57), new Vector2(60, 15), 10, $"{page + 1} of {pages}");
            Button next = FlatButton(block, s, new Vector2(212, 55), new Vector2(50, 17), () => TurnSummary(r, 1));
            next.Text = "NEXT";
            next.Disabled = page >= pages - 1;
            _trainSummary.AddChild(block);
        }
    }

    private void TurnSummary(int row, int step)
    {
        _trainSummaryPage[row] = _trainSummaryPage.GetValueOrDefault(row) + step;
        ShowTrainSummary();
    }

    // Realm Abilities: a row each: name (click: delve), slider to its max level, level, cost.
    private void ShowTrainRealm()
    {
        if (_trainRealmList is null)
            return;
        float s = UiScale;
        foreach (Node c in _trainRealmList.GetChildren())
            c.QueueFree();
        for (int i = 0; i < _trainer.Realm.Count; i++)
        {
            int index = i;
            TrainerRealmCosts ra = _trainer.Realm[i];
            var row = new Control { CustomMinimumSize = new Vector2(360, 21) * s, MouseFilter = Control.MouseFilterEnum.Ignore };
            Button name = FlatButton(row, s, new Vector2(10, 0), new Vector2(160, 15), () => DelveTrainLine(50u + (uint)index));
            name.Text = ra.Name;
            var slider = new HSlider
            {
                Position = new Vector2(175, 0) * s, Size = new Vector2(90, 15) * s, MinValue = 0, MaxValue = ra.MaxLevel, Step = 1,
                FocusMode = Control.FocusModeEnum.None, Value = _trainer.RealmTargets[i],
            };
            slider.ValueChanged += v => OnRealmSlider(index, (int)v);
            row.AddChild(slider);
            Label level = PageLabel(row, s, new Vector2(270, 0), new Vector2(20, 15), 10, _trainer.RealmTargets[i].ToString(System.Globalization.CultureInfo.InvariantCulture));
            level.AddThemeColorOverride("font_color", _trainer.RealmTargets[i] != ra.Level ? TrainChanged : new Color(1, 1, 1));
            PageLabel(row, s, new Vector2(290, 0), new Vector2(70, 15), 10, $"Cost: {_trainer.RealmCost(i)}");
            _trainRealmList.AddChild(row);
        }
    }

    private void OnTrainSlider(int row, int value)
    {
        if (_trainBuilding || row >= _trainer.Lines.Count)
            return;
        _trainer.Targets[row] = System.Math.Max(value, _trainer.Lines[row].Current);
        ShowTrainer();
    }

    private void OnRealmSlider(int index, int value)
    {
        if (_trainBuilding || index >= _trainer.Realm.Count)
            return;
        _trainer.RealmTargets[index] = System.Math.Max(value, (int)_trainer.Realm[index].Level);
        CallDeferred(nameof(ShowTrainer));   // the realm rows are rebuilt: not from inside their own slider's signal
    }

    private Texture2D? TrainSkillIcon(TrainerSkill skill)
    {
        _iconTable ??= LoadIconTable();
        DaocAssets.Ui.IconKind kind = skill.Page switch { 2 => DaocAssets.Ui.IconKind.Style, 3 => DaocAssets.Ui.IconKind.Spell, _ => DaocAssets.Ui.IconKind.Icon };
        return _iconTable.Resolve(kind, skill.Icon) is { } p ? IconTexture(p.Base) : null;
    }

    private bool InteractLocked()
    {
        if (_seconds - _lastInteractAt < 1)
        {
            AppendChat("You must wait one more second to interact again!", new Color(1f, 1f, 1f));
            return true;
        }
        _lastInteractAt = _seconds;
        return false;
    }

    // A line's (or realm ability's) name: 0xD8 type 9; the server answers with a text window.
    private void DelveTrainLine(uint id)
    {
        if (_session is { } session && !InteractLocked())
            _ = Send(() => session.RequestDetailAsync(9, id), "delve line");
    }

    private void Respec(string what)
    {
        if (_session is { } session && !InteractLocked())
            _ = Send(() => session.SendCommandAsync($"&respec {what}"), "respec");
    }

    private void Train()
    {
        if (_session is { } session)
            _ = Send(() => session.TrainAsync(_trainer), "train");
    }

    // Each frame while open: more than 256 units (2-D) from the trainer closes it.
    private void UpdateTrainerRange()
    {
        if (_trainWindow is not { Visible: true } || !_entities.TryGetValue(_trainerId, out Entity? e))
            return;
        Vector3 d = e.Node.Position - _body.Position;
        d.Y = 0;
        if (d.Length() > MerchantRange * RegionMetresPerUnit)
            CloseTrainer();
    }

    private void CloseTrainer()
    {
        if (_trainWindow is not null)
            _trainWindow.Visible = false;
    }
}
