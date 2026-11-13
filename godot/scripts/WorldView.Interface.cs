// SPDX-License-Identifier: GPL-3.0-or-later
//
// WorldView: the interface size (retail's ui_scale; daoc-re RE/63). One multiplier for every window we draw: the summary,
// target window, quickbars, compass, XP bubbles, menu bar and clock (skin pixels × the scale), and the chat pane and NPC dialogue (laid out for 1.5,
// our default at 1080p, so they keep today's look there). Set in Settings → Display → Interface size, or as retail does:
// drag with the left button held in the screen's top-left 32 × 32 pixels ("UI Scale: x.xxx" in chat). Applied live.

using Godot;

namespace EodClient;

public partial class WorldView
{
    private const float ChatDesignScale = 1.5f;   // the chat pane and NPC window's sizes are for this scale
    private float _builtScale;                     // the scale the HUD windows were built at
    private Vector2? _scaleDragFrom;               // a corner drag in progress: where it was last
    private float _scaleDragValue;

    // Builds or rebuilds everything the scale sizes, if it changed.
    private void ApplyInterfaceScale()
    {
        float s = UiScale;
        if (Mathf.IsEqualApprox(s, _builtScale) && ClientSettings.ClassicSummary == _builtClassicSummary)
            return;
        _builtScale = s;
        if (_summary is not null && IsInstanceValid(_summary) && _summary.GetParent() is CanvasLayer overlay)
        {
            // Rebuilt at the new size; each window keeps its remembered place (stored in skin pixels).
            foreach (Node old in new Node?[] { _chatWindow, _summary, _targetWindow, _compassWindow, _xpWindow, _menuBar, _mainMenu, _clockWindow, _statsPanel, _miniInfo, _infoWindow, _dialogWindow }
                         .Concat(_bars.Select(b => (Node)b.Window)).OfType<Node>())
                old.QueueFree();
            _bars.Clear();
            _compassWindow = null;
            BuildChatWindow(overlay);
            BuildHud(overlay);
            BuildQuickbars(overlay);
            BuildCompass(overlay);
            BuildXpWindow(overlay);
            BuildMenuBar(overlay);
            BuildClock(overlay);
            BuildStatsPanel(overlay);
            BuildItemWindows(overlay);
            BuildDialog(overlay);
        }
        ScaleChatAndNpc(s / ChatDesignScale);
    }

    private void ScaleChatAndNpc(float k)
    {
        int font = Mathf.RoundToInt(16 * k);
        foreach (string f in new[] { "normal_font_size", "bold_font_size", "italics_font_size", "mono_font_size" })
            _npcText.AddThemeFontSizeOverride(f, font);
        _npcWindow.CustomMinimumSize = new Vector2(440 * k, 0);
        _npcText.CustomMinimumSize = new Vector2(400 * k, 0);
        _npcWindow.Position = new Vector2(-220 * k, 270 * k);
        foreach (Node n in _npcWindow.FindChildren("*", "Button", true, false))
            ((Button)n).AddThemeFontSizeOverride("font_size", font);
        _npcWindow.ResetSize();
    }

    // Retail's way: hold the left button in the top-left 32 × 32 pixels and drag; the scale moves by (dx + dy) × 0.001
    // per pixel (retail also multiplies by the mouse sensitivity, 1 here). Applied and saved on release.
    private bool InterfaceScaleDrag(InputEvent e)
    {
        if (e is InputEventMouseButton { ButtonIndex: MouseButton.Left } b)
        {
            if (b.Pressed && b.Position.X < 32 && b.Position.Y < 32)
            {
                (_scaleDragFrom, _scaleDragValue) = (b.Position, UiScale);
                return true;
            }
            if (!b.Pressed && _scaleDragFrom is not null)
            {
                _scaleDragFrom = null;
                ClientSettings.UiScale = Mathf.Clamp(_scaleDragValue, 1f, 4f);
                ClientSettings.NotifyChanged(display: false);
                AppendChat($"UI Scale: {UiScale:0.000}", new Color(0.6f, 0.8f, 1f));
                return true;
            }
        }
        else if (e is InputEventMouseMotion m && _scaleDragFrom is Vector2 from)
        {
            _scaleDragValue = Mathf.Clamp(_scaleDragValue + (m.Position.X - from.X + m.Position.Y - from.Y) * 0.001f, 1f, 4f);
            _scaleDragFrom = m.Position;
            return true;
        }
        return false;
    }
}
