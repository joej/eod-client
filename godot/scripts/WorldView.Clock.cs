// SPDX-License-Identifier: GPL-3.0-or-later
//
// WorldView: the clock (retail's `clock` window, 120 x 40, movable, closable; daoc-re RE/74): one yellow line, centred at
// y 18, with the game time of day as "h:mm AM" / "h:mm PM" (DaocProtocol.Session.GameClock). The time is the server's
// 0x7E, advanced here between updates; it redraws when the game minute changes. Settings → Game → Clock shows or hides
// it (so does its ✕); ours starts shown, at the top, right of the middle.

using DaocProtocol.Packets;
using DaocProtocol.Session;
using Godot;

namespace EodClient;

public partial class WorldView
{
    private HudWindow? _clockWindow;
    private Label? _clockText;
    private GameTime? _gameTime;
    private double _gameTimeAt;   // _seconds when it arrived
    private bool _clockSubscribed;

    private void BuildClock(CanvasLayer overlay)
    {
        float s = UiScale;
        _clockWindow = new HudWindow("clock", new Vector2(120, 40), s, screen => new Vector2(screen.X / 2 + 80, 0));
        _clockText = _clockWindow.Label(new Vector2(0, 18), new Vector2(120, 16), 11, centred: true);
        _clockText.AddThemeColorOverride("font_color", new Color(1f, 0.9f, 0.2f));
        _clockWindow.Arrow("✕", new Vector2(102, 1), new Vector2(17, 14), () =>
        {
            ClientSettings.ShowClock = false;
            ClientSettings.NotifyChanged(display: false);
        });
        _clockWindow.Visible = _session is not null && ClientSettings.ShowClock;
        if (!_clockSubscribed)
        {
            _clockSubscribed = true;
            _bridge.TimeChanged += t =>
            {
                if (_timeForced)
                    return;
                (_gameTime, _gameTimeAt, _skyTimeAt) = (t, _seconds, -1);
                GD.Print($"[world] game time {GameClock.Format(t.MillisOfDay)}, day increment {t.DayIncrement}");
            };
            ClientSettings.Changed += OnClockSetting;
            TreeExiting += () => ClientSettings.Changed -= OnClockSetting;
        }
        overlay.AddChild(_clockWindow);
    }

    private void OnClockSetting(bool display)
    {
        if (_clockWindow is not null && IsInstanceValid(_clockWindow))
            _clockWindow.Visible = _session is not null && ClientSettings.ShowClock;
    }

    /// <summary>The game time of day now (ms since midnight), or null before the server has sent one.</summary>
    private uint? GameMillisOfDay => _gameTime is GameTime t ? GameClock.Advance(t, (_seconds - _gameTimeAt) * 1000) : null;

    private double _skyTimeAt = -1;
    private bool _timeForced;   // --time: a fixed start, the server's 0x7E ignored

    // The sky follows the clock: re-blended four times a second (a game minute takes 2.5 s at the usual rate).
    private void UpdateSkyTime()
    {
        if (_gameTime is null || _seconds - _skyTimeAt < 0.25)
            return;
        _skyTimeAt = _seconds;
        ApplySkyTime();
    }

    private void UpdateClock()
    {
        if (_clockWindow is null || !_clockWindow.Visible || _clockText is null)
            return;
        string text = GameMillisOfDay is uint ms ? GameClock.Format(ms) : "";
        if (_clockText.Text != text)
            _clockText.Text = text;
    }
}
