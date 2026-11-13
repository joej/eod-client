// SPDX-License-Identifier: GPL-3.0-or-later
//
// WorldView: retail's Interface Elements window, the command window and the Toggle Interface key (daoc-re RE/74 "Other
// windows worth having early", RE/63).
// - Interface Elements (350 x 475; main menu → INTERFACE ELEMENTS): the player's switchboard, one button per optional
//   window, grouped as retail's. A button lights when its window is on; the windows we haven't built are greyed.
// - The command window (135 x 216; Interface Elements → Command): two columns of buttons sending the commands a player
//   would type, or doing the client's own (follow, stick, face, clock, compass, map, quit, keyboard).
// - Toggle Interface (F12, ours: retail's key isn't known) hides every window and shows them again.
// Each window's shown state is saved (ClientSettings.WindowShown); the command window starts hidden (ours).

using Godot;

namespace EodClient;

public partial class WorldView
{
    private HudWindow? _elementsWindow, _commandWindow;
    private bool _elementsOpen;
    private readonly List<(Button Button, System.Func<bool> On)> _elementButtons = [];
    private bool _showTargetWindow = ClientSettings.WindowShown("target", true);
    private bool _showMenuBar = ClientSettings.WindowShown("menu_bar", true);
    private bool _showCommand = ClientSettings.WindowShown("command", false);
    private CanvasLayer? _overlay;

    private void BuildElementWindows(CanvasLayer overlay)
    {
        _overlay = overlay;
        float s = UiScale;
        _elementButtons.Clear();

        _elementsWindow = new HudWindow("interface_elements", new Vector2(350, 475), s,
            screen => new Vector2(screen.X / 2 - 175, screen.Y / 2 - 237)) { Visible = _elementsOpen };
        Label title = _elementsWindow.Label(new Vector2(0, 2), new Vector2(350, 14), 10, centred: true);
        title.Text = "INTERFACE ELEMENTS";
        _elementsWindow.Arrow("✕", new Vector2(330, 1), new Vector2(18, 14), () => ShowElements(false));
        (string Group, (string Name, System.Func<bool>? On, System.Action? Toggle)[] Items)[] groups =
        [
            ("Personal Info", [
                ("Summary", () => ClientSettings.ClassicSummary, () => SetClassicSummary(true)),
                ("New Summary", () => !ClientSettings.ClassicSummary, () => SetClassicSummary(false)),
                ("Bonuses", null, null),
                ("Floating Target", () => _showTargetWindow, () => _showTargetWindow = Flip("target", _showTargetWindow)),
                ("Level Exp.", () => ClientSettings.ShowLevelXp, () => FlipSetting(() => ClientSettings.ShowLevelXp = !ClientSettings.ShowLevelXp)),
                ("Realm Exp.", null, null), ("Champion Exp.", null, null), ("Pet Window", null, null), ("Quiver", null, null),
                ("Title Window", null, null), ("Mount Window", null, null),
            ]),
            ("Groups & Buffs", [
                ("Concentration", null, null), ("Group Benefits", null, null), ("Floating Group", null, null),
                ("Mini-Group", null, null), ("Social Window", null, null), ("Mini-Friends", null, null),
            ]),
            ("Maps & Journals", [
                ("Master Levels", null, null), ("Quest Journal", null, null),
                ("Map", () => _mapWindow.Visible, ToggleMap), ("Warmap", null, null),
            ]),
            ("Miscellaneous", [
                ("Performance", null, null),
                ("Clock", () => ClientSettings.ShowClock, () => FlipSetting(() => ClientSettings.ShowClock = !ClientSettings.ShowClock)),
                ("Compass", () => ClientSettings.ShowCompass, () => FlipSetting(() => ClientSettings.ShowCompass = !ClientSettings.ShowCompass)),
                ("Command", () => _showCommand, () => _showCommand = Flip("command", _showCommand)),
                ("Menu Bar", () => _showMenuBar, () => _showMenuBar = Flip("menu_bar", _showMenuBar)),
            ]),
            ("Realm Info", [("Realm Bonuses", null, null), ("Relic Status", null, null), ("Keep Status", null, null)]),
        ];
        float y = 20;
        foreach ((string group, var items) in groups)
        {
            Label head = _elementsWindow.Label(new Vector2(12, y), new Vector2(326, 12), 9);
            head.Text = group.ToUpperInvariant();
            head.AddThemeColorOverride("font_color", new Color(0.6f, 0.85f, 1f));
            y += 14;
            for (int i = 0; i < items.Length; i++)
            {
                (string name, System.Func<bool>? on, System.Action? toggle) = items[i];
                var at = new Vector2(12 + i % 3 * 110, y + i / 3 * 19);
                Button b = _elementsWindow.Button(name, at, 106, () =>
                {
                    toggle?.Invoke();
                    ApplyElementWindows();
                });
                if (on is null)
                {
                    b.Disabled = true;
                    b.TooltipText = "Not built yet";
                }
                else
                    _elementButtons.Add((b, on));
            }
            y += (items.Length + 2) / 3 * 19 + 6;
        }
        overlay.AddChild(_elementsWindow);

        _commandWindow = new HudWindow("command", new Vector2(135, 216), s,
            screen => new Vector2(screen.X - 135 - 200, screen.Y / 2 - 108));
        Label commandTitle = _commandWindow.Label(new Vector2(0, 1), new Vector2(135, 13), 9, centred: true);
        commandTitle.Text = "COMMANDS";
        _commandWindow.Arrow("✕", new Vector2(116, 0), new Vector2(18, 14), () =>
        {
            _showCommand = Flip("command", true);
            ApplyElementWindows();
        });
        (string Name, System.Action? Pressed)[][] columns =
        [
            [
                ("ATTACK", () => _ = SendAttack(!_attacking)), ("SPRINT", () => SendLine("/sprint")),
                ("SIT", () => SendLine(_sitting ? "/stand" : "/sit")), ("FOLLOW", () => Follow(5f)), ("STICK", () => Follow(2f)),
                ("FACE", FaceTarget), ("PRAY", () => SendLine("/pray")), ("RELEASE", () => SendLine("/release")),
                ("BONUSES", () => SendLine("/bonuses")), ("BIND", () => SendLine("/bind")),
                ("CLOCK", () => { FlipSetting(() => ClientSettings.ShowClock = !ClientSettings.ShowClock); ApplyElementWindows(); }),
            ],
            [
                ("KEEPS", null), ("RELICS", () => SendLine("/relic")), ("TASK", () => SendLine("/task")), ("FRIENDS", null),
                ("AFK", () => SendLine("/afk")),
                ("COMPASS", () => { FlipSetting(() => ClientSettings.ShowCompass = !ClientSettings.ShowCompass); ApplyElementWindows(); }),
                ("HELP", null), ("JOURNAL", null), ("QUIT", MenuQuit), ("KEYBOARD", () => SettingsDialog.Open(this, 1)),
                ("MAP", ToggleMap),
            ],
        ];
        for (int c = 0; c < columns.Length; c++)
            for (int r = 0; r < columns[c].Length; r++)
            {
                (string name, System.Action? pressed) = columns[c][r];
                Button b = _commandWindow.Button(name, new Vector2(4 + c * 65, 16 + r * 18), 62, pressed ?? (() => { }));
                if (pressed is null)
                {
                    b.Disabled = true;
                    b.TooltipText = "Not built yet";
                }
            }
        overlay.AddChild(_commandWindow);
        ApplyElementWindows();
    }

    private static bool Flip(string window, bool shown)
    {
        ClientSettings.SaveWindowShown(window, !shown);
        return !shown;
    }

    private static void FlipSetting(System.Action change)
    {
        change();
        ClientSettings.NotifyChanged(false);   // the clock, compass and XP windows follow their settings
    }

    private void SetClassicSummary(bool classic)
    {
        if (ClientSettings.ClassicSummary == classic)
            return;
        ClientSettings.ClassicSummary = classic;
        ClientSettings.NotifyChanged(false);   // rebuilds the windows with the other summary
    }

    private void ShowElements(bool open)
    {
        _elementsOpen = open;
        if (_elementsWindow is not null && IsInstanceValid(_elementsWindow))
            _elementsWindow.Visible = open;
        ApplyElementWindows();
    }

    // Each optional window as its switch says, and the switches lit when on.
    private void ApplyElementWindows()
    {
        if (_commandWindow is not null && IsInstanceValid(_commandWindow))
            _commandWindow.Visible = _showCommand;
        if (_menuBar is not null && IsInstanceValid(_menuBar))
            _menuBar.Visible = _session is not null && _showMenuBar;
        foreach ((Button b, System.Func<bool> on) in _elementButtons)
            if (IsInstanceValid(b))
                b.AddThemeColorOverride("font_color", on() ? Colors.White : new Color(0.55f, 0.57f, 0.6f));
    }

    private void SendLine(string command)
    {
        if (_session is not null && _closedReason is null)
            _ = _session.SendCommandAsync(command);
    }

    private void ToggleInterface()
    {
        if (_overlay is null)
            return;
        _overlay.Visible = !_overlay.Visible;
        GD.Print($"[world] interface {(_overlay.Visible ? "shown" : "hidden")}");
    }
}
