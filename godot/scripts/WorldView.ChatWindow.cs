// SPDX-License-Identifier: GPL-3.0-or-later
//
// WorldView: the chat window as retail's (daoc-re RE/76, RE/82, RE/85; the rules are DaocProtocol's ChatRules):
// - one window, 312 x 195 skin pixels, at retail's default.ini place (0, 537 of 768: the bottom left), dragged by its
//   top edge, with retail's two tabs: Main (in front; channels, window 58% opaque) and Combat (game messages, 38%).
//   Every tab gets every line and shows the types its filter has on, each in the tab's palette colour.
// - a line stays drawn for 90 s while the window shows the newest lines; the mouse wheel scrolls back, and a window
//   scrolled up shows everything; scrolled back to the bottom, only the last 90 s again. 2500 lines kept.
// - plain text goes to the chat mode (the front tab's default channel, Say); /s /g /gu /c /as /os /bc and their long
//   forms followed by a space switch it; %t is the target's name; the same command within 250 ms is dropped.
// - 0xC8-0xCB in the middle of the screen (0xCA/0xCB also in chat, as Say).
// Ours: the client's own lines (local command replies) show on every tab; how long a middle-of-screen line stays (5 s)
// and its sizes; no chat options window yet (retail's per-tab filters and colours are editable and saved per character).

using DaocProtocol.Packets;
using DaocProtocol.Session;
using Godot;

namespace EodClient;

public partial class WorldView
{
    private readonly record struct ChatLine(string Text, ChatType? Type, Color Colour, double At);

    private static readonly ChatTab[] ChatTabs = [ChatRules.Main, ChatRules.Combat];
    private static readonly Vector2 ChatSize = new(312, 195);   // retail's default.ini, skin pixels
    private const float ChatFont = 10, CentreSeconds = 5;

    private readonly List<ChatLine> _chatHistory = [];
    private HudWindow? _chatWindow;
    private readonly List<Button> _chatTabButtons = [];
    private int _chatTab;                       // 0 Main, 1 Combat
    private ChatMode _chatMode = ChatMode.Say;
    private bool _chatScrolledUp;
    private int _chatWritten;                   // lines in the label now
    private double _chatNextExpiry = double.MaxValue;
    private string? _lastChatSent;
    private double _lastChatSentAt = -1;
    private Label? _centreText;
    private double _centreUntil;

    private void BuildChatWindow(CanvasLayer overlay)
    {
        float s = UiScale;
        _chatWindow = new HudWindow("chat", ChatSize, s, screen => new Vector2(0, screen.Y - ChatSize.Y - 36))
        {
            Wheel = ChatWheel,
        };
        _chatTabButtons.Clear();
        for (int i = 0; i < ChatTabs.Length; i++)
        {
            int tab = i;
            _chatTabButtons.Add(_chatWindow.Button(ChatTabs[i].Name, new Vector2(2 + 50 * i, 1), 48, () => SelectChatTab(tab), 12));
        }
        _chatLog = new RichTextLabel
        {
            BbcodeEnabled = false, ScrollFollowing = true, SelectionEnabled = false,
            MouseFilter = Control.MouseFilterEnum.Ignore,   // the window takes the wheel and the drag
            FocusMode = Control.FocusModeEnum.None,
            Position = new Vector2(4, 15) * s,
        };
        FixSize(_chatLog, new Vector2(ChatSize.X - 8, ChatSize.Y - 34) * s);
        foreach (string f in new[] { "normal_font_size", "bold_font_size", "italics_font_size", "mono_font_size" })
            _chatLog.AddThemeFontSizeOverride(f, Mathf.RoundToInt(ChatFont * s));
        _chatLog.GetVScrollBar().Modulate = new Color(1, 1, 1, 0.5f);
        _chatWindow.AddChild(_chatLog);
        string typed = _chatInput is not null && IsInstanceValid(_chatInput) ? _chatInput.Text : "";
        _chatInput = new LineEdit
        {
            MaxLength = 512,   // well inside one packet (CommandRequest.MaxLength) with the longest command in front
            Text = typed,
            Position = new Vector2(2, ChatSize.Y - 17) * s,
        };
        FixSize(_chatInput, new Vector2(ChatSize.X - 4, 15) * s);
        _chatInput.AddThemeFontSizeOverride("font_size", Mathf.RoundToInt(ChatFont * s));
        // A slim box: the default style's padding made it taller than the window's 15-pixel input row.
        foreach (string state in new[] { "normal", "focus", "read_only" })
            _chatInput.AddThemeStyleboxOverride(state, new StyleBoxFlat
            {
                BgColor = new Color(0.03f, 0.035f, 0.045f, state == "focus" ? 0.9f : 0.6f),
                BorderColor = new Color(0.55f, 0.85f, 1f, state == "focus" ? 0.6f : 0.25f),
                BorderWidthLeft = 1, BorderWidthTop = 1, BorderWidthRight = 1, BorderWidthBottom = 1,
                ContentMarginLeft = 3 * s, ContentMarginRight = 3 * s, ContentMarginTop = 0, ContentMarginBottom = 0,
            });
        _chatInput.TextSubmitted += OnChatSubmitted;
        _chatWindow.AddChild(_chatInput);
        overlay.AddChild(_chatWindow);

        _centreText ??= NewCentreText(overlay);
        SelectChatTab(_chatTab);
    }

    private Label NewCentreText(CanvasLayer overlay)
    {
        var label = new Label
        {
            AnchorLeft = 0, AnchorRight = 1, AnchorTop = 0.28f, AnchorBottom = 0.28f, Visible = false,
            HorizontalAlignment = HorizontalAlignment.Center, AutowrapMode = TextServer.AutowrapMode.WordSmart,
            MouseFilter = Control.MouseFilterEnum.Ignore,
        };
        label.AddThemeColorOverride("font_outline_color", new Color(0, 0, 0, 0.9f));
        overlay.AddChild(label);
        return label;
    }

    private void SelectChatTab(int tab)
    {
        _chatTab = tab;
        ChatTab t = ChatTabs[tab];
        _chatMode = t.Channel;   // the mode starts as the front tab's default channel
        for (int i = 0; i < _chatTabButtons.Count; i++)
            _chatTabButtons[i].AddThemeColorOverride("font_color", i == tab ? Colors.White : new Color(0.6f, 0.62f, 0.66f));
        _chatWindow?.AddThemeStyleboxOverride("panel", new StyleBoxFlat
        {
            BgColor = new Color(0.06f, 0.07f, 0.09f, t.Opacity), BorderColor = new Color(0.55f, 0.85f, 1f, 0.35f),
            BorderWidthLeft = 1, BorderWidthTop = 1, BorderWidthRight = 1, BorderWidthBottom = 1,
            CornerRadiusTopLeft = 3, CornerRadiusTopRight = 3, CornerRadiusBottomLeft = 3, CornerRadiusBottomRight = 3,
        });
        _chatScrolledUp = false;
        RenderChat();
        UpdateChatPrompt();
    }

    private void UpdateChatPrompt()
    {
        if (_chatInput is not null && IsInstanceValid(_chatInput))
            _chatInput.PlaceholderText = $"Enter to chat ({_chatMode}), / for a command";
    }

    private bool ChatShows(ChatLine line) => line.Type is not ChatType type || ChatTabs[_chatTab].Has(type);

    private Color ChatColour(ChatLine line)
    {
        if (line.Type is not ChatType type)
            return line.Colour;
        (byte r, byte g, byte b) = ChatRules.Palette[ChatTabs[_chatTab].ColourIndex(type)];
        return Color.Color8(r, g, b);
    }

    private void AddChatLine(string text, ChatType? type, Color? colour)
    {
        var line = new ChatLine(text, type, colour ?? Colors.White, _seconds);
        _chatHistory.Add(line);
        if (_chatHistory.Count > ChatRules.MaxLines)
            _chatHistory.RemoveRange(0, _chatHistory.Count - ChatRules.MaxLines);
        if (_chatLog is null || !IsInstanceValid(_chatLog) || !ChatShows(line))
            return;
        WriteChatLine(line);
        if (!_chatScrolledUp)
            _chatNextExpiry = System.Math.Min(_chatNextExpiry, line.At + ChatRules.LineSeconds);
    }

    private void WriteChatLine(ChatLine line)
    {
        if (_chatWritten++ > 0)
            _chatLog.Newline();
        _chatLog.PushColor(ChatColour(line));
        _chatLog.AddText(line.Text);
        _chatLog.Pop();
    }

    // The front tab's lines: all of them while scrolled up, else those under 90 s old.
    private void RenderChat()
    {
        if (_chatLog is null || !IsInstanceValid(_chatLog))
            return;
        _chatLog.Clear();
        _chatWritten = 0;
        _chatNextExpiry = double.MaxValue;
        foreach (ChatLine line in _chatHistory)
        {
            if (!ChatShows(line))
                continue;
            if (!_chatScrolledUp)
            {
                double expires = line.At + ChatRules.LineSeconds;
                if (expires <= _seconds)
                    continue;
                _chatNextExpiry = System.Math.Min(_chatNextExpiry, expires);
            }
            WriteChatLine(line);
        }
        _chatLog.ScrollFollowing = !_chatScrolledUp;
    }

    // Each frame: lines past their 90 s drop out of a window at the bottom; the middle-of-screen text times out.
    private void UpdateChat()
    {
        if (!_chatScrolledUp && _seconds >= _chatNextExpiry)
            RenderChat();
        if (_centreText is not null && _centreText.Visible && _seconds >= _centreUntil)
            _centreText.Visible = false;
    }

    // The wheel over the window: up scrolls back (showing every line), down forward; at the bottom again, the last 90 s.
    private void ChatWheel(int direction)
    {
        VScrollBar bar = _chatLog.GetVScrollBar();
        float step = 3 * ChatFont * UiScale * 1.3f;
        if (direction < 0)
        {
            if (!_chatScrolledUp)
            {
                _chatScrolledUp = true;
                RenderChat();
                bar.Value = bar.MaxValue;
            }
            bar.Value = System.Math.Max(0, bar.Value - step);
        }
        else if (_chatScrolledUp)
        {
            bar.Value += step;
            if (bar.Value >= bar.MaxValue - bar.Page - 1)
            {
                _chatScrolledUp = false;
                RenderChat();
            }
        }
    }

    private void ShowCentreText(string text, CentreSize size)
    {
        if (_centreText is null)
            return;
        _centreText.Text = text;
        _centreText.AddThemeFontSizeOverride("font_size", Mathf.RoundToInt((size == CentreSize.Large ? 20 : 14) * UiScale));
        _centreText.AddThemeConstantOverride("outline_size", Mathf.RoundToInt(3 * UiScale));
        _centreText.Visible = true;
        _centreUntil = _seconds + CentreSeconds;
    }

    // The current target's name, for %t ("" when it has none).
    private string? TargetName() =>
        _target != 0 && _entities.TryGetValue(_target, out Entity? e) && e.Name.Length > 0 ? e.Name : null;
}
