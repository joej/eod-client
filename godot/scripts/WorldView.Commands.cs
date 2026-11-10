// SPDX-License-Identifier: GPL-3.0-or-later
//
// WorldView: slash commands the client answers itself, as retail's does; the server never sees them.
//   /loc                     where you are, in the zone's own coordinates (the ones /faceloc takes) and heading
//   /map                     the zone map, as the M key
//   /quickbar <bar> <on|off> show or hide a quickbar (1|0 too, as retail; also show/hide, yes/no)

using DaocProtocol.Packets;
using DaocProtocol.World;
using Godot;

namespace EodClient;

public partial class WorldView
{
    private static readonly Color LocalReply = new(0.6f, 0.8f, 1f);

    // True when the line was one of the client's own commands (handled here).
    private bool LocalCommand(string line)
    {
        string[] words = line.Split(' ', System.StringSplitOptions.RemoveEmptyEntries);
        switch (words[0].ToLowerInvariant())
        {
            case "/loc":
                Reply(LocText());
                return true;
            case "/map":
                ToggleMap();
                GD.Print("[local] /map: map toggled");
                return true;
            case "/quickbar":
                QuickbarCommand(words);
                return true;
            default:
                return false;
        }
    }

    // A reply in the chat window, and in the log as the server's lines are.
    private void Reply(string text)
    {
        GD.Print($"[local] {text}");
        AppendChat(text, LocalReply);
    }

    // Retail's exact wording is still to confirm (daoc-re); the numbers are what /faceloc and guides use: the position
    // from the zone's corner, and the heading 0-4095.
    private string LocText()
    {
        PositionAndObjectId p = _world.Position;
        Zone z = _world.Zone;
        return $"You are at X: {p.X - z.X:0} Y: {p.Y - z.Y:0} Z: {p.Z:0} Heading: {p.Heading & 0x0FFF} in {z.Name}";
    }

    private void QuickbarCommand(string[] words)
    {
        if (words.Length < 2 || !int.TryParse(words[1], out int bar) || bar < 1 || bar > _bars.Count)
        {
            Reply($"Usage: /quickbar <1-{_bars.Count}> <on|off>");
            return;
        }
        bool? show = words.Length < 3 ? null : OnOff(words[2]);
        if (words.Length >= 3 && show is null)
        {
            Reply($"/quickbar: say on or off (or 1 or 0), not \"{words[2]}\".");
            return;
        }
        // Without on/off, flip it.
        bool visible = show ?? !_bars[bar - 1].Window.Visible;
        ShowQuickbar(bar - 1, visible, save: true);
        Reply($"Quickbar {bar} {(visible ? "shown" : "hidden")}.");
    }

    /// <summary>"on"/"off" and their usual spellings; null for anything else.</summary>
    internal static bool? OnOff(string word) => word.ToLowerInvariant() switch
    {
        "1" or "on" or "show" or "yes" or "true" => true,
        "0" or "off" or "hide" or "no" or "false" => false,
        _ => null,
    };
}
