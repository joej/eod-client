// SPDX-License-Identifier: GPL-3.0-or-later
//
// WorldView: the XP bubbles (daoc-re RE/74, 2026-11-10), as retail's floating Level Exp. window (float_level_exp,
// 108 x 40): ten bubbles (8, 9; 90 x 8), the fine blue bar under them (8, 19; 90 wide) and "Level: n". From the
// server's level progress v in ‰ (0x91): whole bubbles = v / 100, the fine bar = v mod 100, so the bubbles step every
// 10% and the blue bar fills ten times per level (PointsUpdate.Bubbles / BubbleProgress).
//
// Retail shows the bubbles on its menu bar along the bottom (Exp button, bubbles, money, the page buttons, the meters)
// and on the classic summary (ours: WorldView.ClassicSummary), and starts this window hidden; so does ours. Settings →
// Game → Floating XP window shows it, where retail's default.ini puts it (bottom right).
// Drawn in our plain style, not the skin's exp_low / exp_high art.

using DaocProtocol.Packets;
using Godot;

namespace EodClient;

public partial class WorldView
{
    private HudWindow? _xpWindow;
    private XpBubbles? _xpBubbles;
    private HudBar? _xpFine;
    private Label? _xpLevel;
    private bool _xpSubscribed;

    // Built with the other HUD windows (and rebuilt with them when the interface size changes).
    private void BuildXpWindow(CanvasLayer overlay)
    {
        float s = UiScale;
        _xpWindow = new HudWindow("level_xp", new Vector2(108, 40), s, screen => new Vector2(screen.X - 108, screen.Y - 144))
        {
            DragAnywhere = true,
        };
        _xpBubbles = new XpBubbles { Position = new Vector2(8, 9) * s, Size = new Vector2(90, 8) * s, MouseFilter = Control.MouseFilterEnum.Ignore };
        _xpWindow.AddChild(_xpBubbles);
        _xpFine = _xpWindow.Bar(new Vector2(8, 19), 90, new Color(0.25f, 0.5f, 1f), new Vector2(0, 0));
        _xpFine.Number!.Visible = false;   // retail's fine bar has no number
        _xpLevel = _xpWindow.Label(new Vector2(8, 27), new Vector2(92, 11), 9);
        _xpWindow.TooltipText = "Experience";
        _xpWindow.Visible = _session is not null && ClientSettings.ShowLevelXp;
        if (!_xpSubscribed)
        {
            _xpSubscribed = true;
            ClientSettings.Changed += OnXpSetting;
            TreeExiting += () => ClientSettings.Changed -= OnXpSetting;
        }
        overlay.AddChild(_xpWindow);
    }

    private void OnXpSetting(bool display)
    {
        if (_xpWindow is not null && IsInstanceValid(_xpWindow))
            _xpWindow.Visible = _session is not null && ClientSettings.ShowLevelXp;
    }

    private void UpdateXp()
    {
        if (_xpWindow is null || !_xpWindow.Visible)
            return;
        // Until the first 0x91 the bubbles are empty, as at the start of a level.
        int bubbles = _points?.Bubbles ?? 0, fine = _points?.BubbleProgress ?? 0;
        _xpBubbles!.Full = bubbles;
        _xpFine!.Set(fine);
        _xpLevel!.Text = _info is PlayerInfo i ? $"Level: {i.Level}" : "";
        _xpWindow.TooltipText = _points is PointsUpdate p
            ? $"Experience: {p.LevelPermill / 10.0:0.0}% of the level ({p.Experience:N0} / {p.ExperienceForNextLevel:N0})"
            : "Experience";
    }

    // Ten bubbles, the first `Full` of them filled.
    private sealed partial class XpBubbles : Control
    {
        private int _full = -1;

        public int Full
        {
            set
            {
                if (value == _full)
                    return;
                _full = value;
                QueueRedraw();
            }
        }

        public override void _Draw()
        {
            float step = Size.X / 10, r = Mathf.Min(step, Size.Y) / 2 - 0.5f;
            for (int i = 0; i < 10; i++)
            {
                var at = new Vector2(step * i + step / 2, Size.Y / 2);
                DrawCircle(at, r, i < _full ? new Color(0.35f, 0.65f, 1f) : new Color(0, 0, 0, 0.55f));
                DrawArc(at, r, 0, Mathf.Tau, 16, new Color(0.55f, 0.85f, 1f, 0.5f), 1, true);
            }
        }
    }
}
