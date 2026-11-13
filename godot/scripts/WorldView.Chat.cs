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
    // ----- Chat (M6) -----

    // A 0xAF Message arrived: to its chat line (by retail's type rules), the middle of the screen, the NPC's window.
    private void OnChatReceived(ChatMessage msg)
    {
        _chatReceived++;
        GD.Print($"[chat {(byte)msg.Type:X2}{(msg.Popup ? " popup" : "")}] {msg.Text}");   // the log keeps what the player read
        if (ChatRules.ShownAs(msg) is ChatType shown)
            AddChatLine(msg.Text, shown, null);
        if (ChatRules.Centre(msg) is CentreSize size)
            ShowCentreText(msg.Text, size);
        // An NPC's words to the player: its window, where [keywords] can be clicked.
        if (msg.Popup)
        {
            ShowNpcWords(msg.Text);
            GD.Print($"[npc] {msg.Text}");
        }
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
        string raw = text;   // "/g " with its trailing space switches the chat mode (ChatRules.Typed)
        string line = text.Trim();
        _chatInput.Clear();
        _chatInput.ReleaseFocus();
        if (line.Length == 0 || _closedReason is not null)
            return;

        // Commands retail's client answers itself (the server has none by these names: sent, /loc reached OpenDAoC's
        // /loco emote, "You think this is crazy").
        if (LocalCommand(line))
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
        // /useskill <name>: use a skill or list spell by name (C→S 0xBB or 0x7D), until the quickbars exist. The
        // client's own command: the server has none by that name.
        if (command.Equals("/useskill", System.StringComparison.OrdinalIgnoreCase))
        {
            UseSkillByName(line.Split(' ', 2) is [_, string name] ? name.Trim() : "");
            return;
        }
        (string? send, ChatMode mode) = ChatRules.Typed(raw, _chatMode, TargetName());
        if (mode != _chatMode)
            GD.Print($"[chat] mode {_chatMode} -> {mode}");
        _chatMode = mode;
        UpdateChatPrompt();
        // The same command again within 250 ms is dropped (retail).
        if (send is null || (send == _lastChatSent && _seconds - _lastChatSentAt < 0.25))
            return;
        (_lastChatSent, _lastChatSentAt) = (send, _seconds);
        try
        {
            await _session.SendCommandAsync(send);
        }
        catch (System.Exception e)
        {
            AppendChat($"(could not send: {e.Message})", new Color(0.8f, 0.5f, 0.5f));
        }
    }

    // A line of the client's own (a local command's reply, a notice): shown on every tab, in its own colour (ours: retail's
    // own lines' type isn't traced).
    private void AppendChat(string text, Color color) => AddChatLine(text, null, color);
}
