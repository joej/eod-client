// SPDX-License-Identifier: GPL-3.0-or-later
//
// WorldView: the menu bar (retail's menu_bar_window, 800 x 27, shown by default; daoc-re RE/74, 2026-11-10) and the main
// menu it opens (main_menu_window, 224 x 250). Laid out at the stock skin's positions, in our plain style. Left to right:
//   "Exp" (6, 8), the XP bubbles (27, 10; 90 x 8) and the fine blue bar (27, 19)
//   money: P (141), G (181), S (222), C (258), each after its coin (128, 168, 209, 245)
//   JOURNAL 284, MAP 340, WARMAP 376, ATTRIB. 437, INV. 488, SKILLS 521, SPELLS 564, STYLES 610, GROUP 656 (y 8)
//   the meters (703, 720, 737; 16 x 16): performance, lag, frame rate (DaocProtocol.Session.Meters), once a second
//   MENU (754)
// What we have works: MAP (as M), MENU. The other page buttons open windows we haven't built (the journal, the war map,
// the right-hand stats panel) and are dimmed until then. Retail's Exp button switches between level, realm and champion
// XP; ours shows level XP only, so it's a label. Performance is retail's share of packets lost or late; over TCP we
// have no such count, so ours is good while packets arrive and bad when none have for 6 s.
//
// Placed where retail's default.ini puts it at 1024 x 768 (x 349: just right of the chat pane), on the bottom edge.

using DaocProtocol.Session;
using Godot;

namespace EodClient;

public partial class WorldView
{
    private HudWindow? _menuBar, _mainMenu;
    private XpBubbles? _menuXp;
    private HudBar? _menuXpFine;
    private Label? _menuPlatinum, _menuGold, _menuSilver, _menuCopper;
    private MeterDot[] _meters = [];
    private double _meterAt;
    private int _lastRoundTrip, _packetsAtMeter;
    private uint _pingRepliedAt;
    private double _lastTrafficAt;

    private void BuildMenuBar(CanvasLayer overlay)
    {
        float s = UiScale;
        _menuBar = new HudWindow("menu_bar", new Vector2(800, 27), s,
            screen => new Vector2(Mathf.Max(0, Mathf.Min(349, screen.X - 800)), screen.Y - 27));
        Label exp = _menuBar.Label(new Vector2(5, 7), new Vector2(22, 13), 9);
        exp.Text = "Exp";
        _menuXp = new XpBubbles { Position = new Vector2(27, 10) * s, Size = new Vector2(90, 8) * s, MouseFilter = Control.MouseFilterEnum.Ignore };
        _menuBar.AddChild(_menuXp);
        _menuXpFine = _menuBar.Bar(new Vector2(27, 19), 90, new Color(0.25f, 0.5f, 1f), Vector2.Zero, height: 4);
        _menuXpFine.Number!.Visible = false;

        // The coins: a small disc in the coin's colour, then "P:0".
        Label Coin(float discX, float labelX, Color colour)
        {
            _menuBar!.AddChild(new CoinDot(colour) { Position = new Vector2(discX, 11) * s, Size = new Vector2(10, 10) * s, MouseFilter = Control.MouseFilterEnum.Ignore });
            return _menuBar.Label(new Vector2(labelX, 8), new Vector2(27, 14), 9);
        }
        _menuPlatinum = Coin(128, 141, new Color(0.75f, 0.85f, 0.95f));
        _menuGold = Coin(168, 181, new Color(0.95f, 0.78f, 0.25f));
        _menuSilver = Coin(209, 222, new Color(0.78f, 0.78f, 0.8f));
        _menuCopper = Coin(245, 258, new Color(0.8f, 0.45f, 0.25f));

        (string Text, float X, float Width, System.Action? Pressed)[] buttons =
        [
            ("JOURNAL", 284, 55, null), ("MAP", 340, 35, ToggleMap), ("WARMAP", 376, 60, null),
            ("ATTRIB.", 437, 50, null), ("INV.", 488, 32, null), ("SKILLS", 521, 42, null), ("SPELLS", 564, 45, null),
            ("STYLES", 610, 45, null), ("GROUP", 656, 45, null), ("MENU", 754, 40, ToggleMainMenu),
        ];
        foreach ((string text, float x, float width, System.Action? pressed) in buttons)
        {
            Button b = _menuBar.Button(text, new Vector2(x, 6), width, pressed ?? (() => { }), height: 16);
            if (pressed is null)
            {
                b.Disabled = true;
                b.TooltipText = "Not built yet";
            }
        }
        _meters =
        [
            new MeterDot("Performance") { Position = new Vector2(703, 6) * s, Size = new Vector2(16, 16) * s },
            new MeterDot("Lag") { Position = new Vector2(720, 6) * s, Size = new Vector2(16, 16) * s },
            new MeterDot("Frame rate") { Position = new Vector2(737, 6) * s, Size = new Vector2(16, 16) * s },
        ];
        foreach (MeterDot m in _meters)
            _menuBar.AddChild(m);
        _menuBar.Visible = _session is not null;
        overlay.AddChild(_menuBar);

        // The main menu: hidden until MENU; at the top, centred, as retail's default.ini (481, 0 at 1024 x 768).
        _mainMenu = new HudWindow("main_menu", new Vector2(224, 250), s, screen => new Vector2(screen.X / 2 - 112, 0)) { Visible = false };
        Label title = _mainMenu.Label(new Vector2(0, 18), new Vector2(224, 18), 11, centred: true);
        title.Text = "MAIN MENU";
        _mainMenu.Arrow("✕", new Vector2(204, 2), new Vector2(18, 14), () => _mainMenu.Visible = false);
        (string Text, float Y, System.Action? Pressed)[] items =
        [
            ("HELP", 65, null), ("INTERFACE ELEMENTS", 94, null),
            ("IN-GAME OPTIONS", 123, () => SettingsDialog.Open(this, 0)),
            ("CONFIGURE KEYBOARD", 152, () => SettingsDialog.Open(this, 1)),
            ("QUIT", 200, MenuQuit),
        ];
        foreach ((string text, float y, System.Action? pressed) in items)
        {
            Button b = _mainMenu.Button(text, new Vector2(32, y), 160, () =>
            {
                _mainMenu!.Visible = false;
                pressed?.Invoke();
            }, height: 20);
            if (pressed is null)
            {
                b.Disabled = true;
                b.TooltipText = "Not built yet";
            }
        }
        overlay.AddChild(_mainMenu);
    }

    private void ToggleMainMenu()
    {
        if (_mainMenu is not null)
            _mainMenu.Visible = !_mainMenu.Visible;
    }

    // As the status panel's "Quit to character screen" (retail's QUIT asks the same of the server: the quit timer runs).
    private void MenuQuit()
    {
        _button.Disabled = true;
        if (_closedReason is null)
            QuitPressed?.Invoke();
        else
            BackToLoginPressed?.Invoke();
    }

    private void OnPingReplied(uint stamp)
    {
        _pings++;
        uint now = (uint)System.Environment.TickCount;
        _lastRoundTrip = DaocProtocol.Packets.PingRequest.RoundTrip(stamp, now);
        _pingRepliedAt = now;
    }

    private void UpdateMenuBar()
    {
        if (_menuBar is null || !_menuBar.Visible)
            return;
        _menuXp!.Full = _points?.Bubbles ?? 0;
        _menuXpFine!.Set(_points?.BubbleProgress ?? 0);
        if (_money is DaocProtocol.Packets.MoneyUpdate m)
        {
            _menuPlatinum!.Text = $"P:{m.Platinum + m.Mithril * 1000}";   // retail's bar has no mithril
            _menuGold!.Text = $"G:{m.Gold}";
            _menuSilver!.Text = $"S:{m.Silver}";
            _menuCopper!.Text = $"C:{m.Copper}";
        }
        else
            (_menuPlatinum!.Text, _menuGold!.Text, _menuSilver!.Text, _menuCopper!.Text) = ("P:0", "G:0", "S:0", "C:0");

        // The meters, once a second.
        if (_seconds - _meterAt < 1)
            return;
        _meterAt = _seconds;
        int packets = _bridge.PacketCount;
        if (packets != _packetsAtMeter)
            (_packetsAtMeter, _lastTrafficAt) = (packets, _seconds);
        bool connected = _session is not null && _closedReason is null;
        uint now = (uint)System.Environment.TickCount, sent = _session?.PingSentAt ?? 0;
        // A ping out and unanswered: how long it has waited (its stamp is later than the last reply's).
        int waiting = sent != 0 && (int)(sent - _pingRepliedAt) > 0 ? DaocProtocol.Packets.PingRequest.RoundTrip(sent, now) : 0;
        _meters[0].Set(Meters.Performance(0, _seconds - _lastTrafficAt < 6), "no packets lost (TCP)");
        _meters[1].Set(Meters.Lag(_lastRoundTrip, waiting, connected), waiting > 500 ? $"waiting {waiting} ms" : $"{_lastRoundTrip} ms");
        double fps = Engine.GetFramesPerSecond();
        _meters[2].Set(Meters.FrameRate(fps), $"{fps:0} fps");
    }

    // A meter: a dot, green, yellow or red for level 0, 1, 2.
    private sealed partial class MeterDot(string name) : Control
    {
        private int _level = -1;

        public void Set(int level, string detail)
        {
            TooltipText = $"{name}: {detail}";
            if (level == _level)
                return;
            _level = level;
            QueueRedraw();
        }

        public override void _Draw()
        {
            Color c = _level switch { 0 => new Color(0.3f, 0.85f, 0.3f), 1 => new Color(0.95f, 0.8f, 0.2f), _ => new Color(0.9f, 0.25f, 0.2f) };
            float r = Mathf.Min(Size.X, Size.Y) / 2 - 2;
            DrawCircle(Size / 2, r, c);
            DrawArc(Size / 2, r, 0, Mathf.Tau, 20, new Color(0, 0, 0, 0.6f), 1, true);
        }
    }

    private sealed partial class CoinDot(Color colour) : Control
    {
        public override void _Draw()
        {
            float r = Mathf.Min(Size.X, Size.Y) / 2 - 1;
            DrawCircle(Size / 2, r, colour);
            DrawArc(Size / 2, r, 0, Mathf.Tau, 16, new Color(0, 0, 0, 0.5f), 1, true);
        }
    }
}
