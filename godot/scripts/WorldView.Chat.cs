// SPDX-License-Identifier: GPL-3.0-or-later
//
// WorldView: Chat (M6): the log and input box, incoming messages, and slash commands.

using DaocProtocol.Net;
using DaocProtocol.Packets;
using DaocProtocol.Session;
using DaocProtocol.World;
using Godot;

namespace EodClient;

public partial class WorldView
{
    // The chat log sits in the lower-left; the input is a thin bar beneath it. Both are anchored to
    // the bottom so they follow the window as it resizes. BBCode is on so each line can be coloured
    // by its chat type without any per-glyph work.
    private void BuildChatUi(CanvasLayer overlay)
    {
        _chatLog = new RichTextLabel
        {
            BbcodeEnabled = true,
            ScrollFollowing = true,
            // Don't steal clicks/keys from the 3D view; only the input line is interactive.
            MouseFilter = Control.MouseFilterEnum.Ignore,
            FocusMode = Control.FocusModeEnum.None,
            AnchorLeft = 0f,
            AnchorRight = 0f,
            AnchorTop = 1f,
            AnchorBottom = 1f,
            OffsetLeft = 16,
            OffsetRight = 520,
            OffsetTop = -236,
            OffsetBottom = -52,
        };
        overlay.AddChild(_chatLog);

        _chatInput = new LineEdit
        {
            // Well inside one packet (CommandRequest.MaxLength) even with the longest command in front.
            MaxLength = 512,
            PlaceholderText = "Press Enter to chat (/say by default; /who, /gu … work too)",
            AnchorLeft = 0f,
            AnchorRight = 0f,
            AnchorTop = 1f,
            AnchorBottom = 1f,
            OffsetLeft = 16,
            OffsetRight = 520,
            OffsetTop = -44,
            OffsetBottom = -16,
        };
        _chatInput.TextSubmitted += OnChatSubmitted;
        overlay.AddChild(_chatInput);
    }

    // ----- Chat (M6) -----

    // A 0xAF Message arrived. Show the real chat/system lines; drop the UI-control types
    // (SocialInterface structured strings, the centred ScreenCenter banner) that aren't text.
    private void OnChatReceived(ChatMessage msg)
    {
        _chatReceived++;
        if (msg.IsPlayerVisibleChat)
            AppendChat(msg.Text, ChatColor(msg.Type));
    }

    /// <summary>Count of 0xAF Message packets seen, for the headless summary and overlay.</summary>
    public int ChatMessages => _chatReceived;
    private int _chatReceived;

    // The player submitted the input line. Treat a leading '/' as a command and anything else as
    // /say. The server echoes it back as a 0xAF, so we don't print our own copy (avoids doubling).
    // Release focus so control returns to the game after each line (as DAoC does): press Enter again
    // to chat again. Keeping focus here trapped the player — the box looked empty but still ate all
    // input, so WASD and mouse-look went dead with no obvious way back.
    private async void OnChatSubmitted(string text)
    {
        string line = text.Trim();
        _chatInput.Clear();
        _chatInput.ReleaseFocus();
        if (line.Length == 0 || _closedReason is not null)
            return;

        if (_session is null)
        {
            AppendChat("(offline: nothing is sent)", new Color(0.8f, 0.5f, 0.5f));
            return;
        }
        // A typed /quit (or /q) runs the client's own quit, as the button does: sent as plain text,
        // the client wouldn't know a quit was under way (nor stand up when the player moves).
        string command = line.Split(' ', 2)[0];
        if (command.Equals("/quit", System.StringComparison.OrdinalIgnoreCase)
            || command.Equals("/q", System.StringComparison.OrdinalIgnoreCase))
        {
            if (!_quitting && !_button.Disabled)
            {
                _button.Disabled = true;
                QuitPressed?.Invoke();
            }
            return;
        }
        try
        {
            if (line.StartsWith('/'))
                await _session.SendCommandAsync(line);
            else
                await _session.SayAsync(line);
        }
        catch (System.Exception e)
        {
            AppendChat($"(could not send: {e.Message})", new Color(0.8f, 0.5f, 0.5f));
        }
    }

    private void AppendChat(string text, Color color)
    {
        // Cap the backlog so a long session doesn't grow the label without bound.
        if (_chatLines >= ChatMaxLines)
        {
            _chatLog.Clear();
            _chatLines = 0;
        }
        _chatLog.PushColor(color);
        _chatLog.AddText(text);
        _chatLog.Pop();
        _chatLog.Newline();
        _chatLines++;
    }

    // Colour by channel/category, roughly as DAoC's default chat colours read: say white, tells and
    // group/guild channels their own hues, system/important yellow-ish, combat/spell muted.
    private static Color ChatColor(ChatType type) => type switch
    {
        ChatType.Say => new Color(0.95f, 0.95f, 0.95f),
        ChatType.Send => new Color(0.7f, 0.7f, 1.0f),
        ChatType.Group => new Color(0.5f, 0.85f, 0.95f),
        ChatType.Guild => new Color(0.5f, 0.95f, 0.5f),
        ChatType.Officer => new Color(0.4f, 0.8f, 0.4f),
        ChatType.Alliance => new Color(0.6f, 0.9f, 0.7f),
        ChatType.Broadcast => new Color(0.95f, 0.75f, 0.4f),
        ChatType.Emote => new Color(0.85f, 0.7f, 0.95f),
        ChatType.Trade or ChatType.LookingForGroup => new Color(0.9f, 0.85f, 0.5f),
        ChatType.Help or ChatType.Advise => new Color(0.6f, 0.85f, 0.9f),
        ChatType.Important or ChatType.Staff => new Color(1.0f, 0.85f, 0.3f),
        ChatType.Spell or ChatType.SpellExpires or ChatType.SpellPulse or ChatType.SpellResisted
            => new Color(0.7f, 0.75f, 0.85f),
        ChatType.Loot or ChatType.Items or ChatType.Merchant => new Color(0.8f, 0.8f, 0.6f),
        ChatType.YourDeath or ChatType.OthersDeath or ChatType.Damaged or ChatType.YouHit
            or ChatType.OthersCombat => new Color(0.9f, 0.6f, 0.55f),
        _ => new Color(0.75f, 0.78f, 0.72f),  // Unspecified/System and the rest: muted grey-green
    };
}
