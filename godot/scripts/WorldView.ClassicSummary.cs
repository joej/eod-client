// SPDX-License-Identifier: GPL-3.0-or-later
//
// WorldView: the classic summary (retail's `summary` window, 128 x 100; the stock skin's ui/atlantis/summary.xml), our
// default. Retail still offers it in Interface Elements beside the newer new_summary_window; it's the one classic
// players know. Laid out at the skin's positions, in our plain style:
//   your name, centred (0, 4; arial11)
//   health, endurance, power: the bar at 30, 25 / 37 / 49 (90 x 6). The skin puts each percentage at 5, 22 / 34 / 46;
//     we write "Hit", "End", "Pow" there instead, as retail screenshots show (the owner's preference, 2026-11-10).
//     A class without power has an empty Pow bar.
//   "Exp" (8, 58), the XP bubbles (30, 58; 90 x 8) and the fine blue bar under them (30, 66)
//   your target's name in its con colour (5, 74; 118 wide), its health number (6, 86) and bar (30, 88)
// Retail's Exp is a button (SummaryToggleExp: most likely level, realm and champion XP in turn); ours shows level XP
// only, so it's a label. Settings → Game → Summary picks this or the new summary.

using Godot;

namespace EodClient;

public partial class WorldView
{
    private XpBubbles? _summaryXp;
    private HudBar? _summaryXpFine;
    private bool _builtClassicSummary;

    private void BuildClassicSummary(float s)
    {
        _summary = new HudWindow("summary", new Vector2(128, 100), s, screen => new Vector2(0, screen.Y * 0.29f));
        _summaryTitle = _summary.Label(new Vector2(0, 2), new Vector2(128, 16), 11, centred: true);
        _summaryLevel = _summary.Label(Vector2.Zero, Vector2.Zero, 9);   // the new summary's level line; none here
        _summaryLevel.Visible = false;
        // Word labels where the stock skin draws the percentages, as the owner's retail screenshot shows (2026-11-10).
        _hpBar = _summary.Bar(new Vector2(30, 25), 90, new Color(0.85f, 0.15f, 0.12f), new Vector2(4, 22), height: 6);
        _endBar = _summary.Bar(new Vector2(30, 37), 90, new Color(0.2f, 0.75f, 0.25f), new Vector2(4, 34), height: 6);
        _powerBar = _summary.Bar(new Vector2(30, 49), 90, new Color(0.9f, 0.8f, 0.2f), new Vector2(4, 46), height: 6);
        foreach ((HudBar bar, string word, float y) in new[] { (_hpBar, "Hit", 22f), (_endBar, "End", 34f), (_powerBar, "Pow", 46f) })
        {
            bar.Number!.Visible = false;
            _summary.Label(new Vector2(6, y), new Vector2(24, 11), 9).Text = word;
        }
        Label exp = _summary.Label(new Vector2(6, 56), new Vector2(22, 11), 9);
        exp.Text = "Exp";
        _summaryXp = new XpBubbles { Position = new Vector2(30, 58) * s, Size = new Vector2(90, 8) * s, MouseFilter = Control.MouseFilterEnum.Ignore };
        _summary.AddChild(_summaryXp);
        _summaryXpFine = _summary.Bar(new Vector2(30, 67), 90, new Color(0.25f, 0.5f, 1f), Vector2.Zero, height: 4);
        _summaryXpFine.Number!.Visible = false;
        _summaryTarget = _summary.Label(new Vector2(5, 74), new Vector2(118, 13), 9, centred: true);
        _targetBar = _summary.Bar(new Vector2(30, 88), 90, new Color(0.85f, 0.15f, 0.12f), new Vector2(5, 85), height: 6);
    }
}
