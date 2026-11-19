// SPDX-License-Identifier: GPL-3.0-or-later
//
// WorldView: slash commands the client answers itself, as retail's does; the server never sees them.
//   /loc                     where you are, in the zone's own coordinates (the ones /faceloc takes) and heading
//   /map                     the zone map, as the M key
//   /quickbar <bar> <on|off> show or hide a quickbar (1|0 too, as retail; also show/hide, yes/no); /qbar the same
//   /follow, /stick, /face   on the current target (the target window's buttons; retail's ranges and messages)
//   /clock                   show or hide the clock
//   /setcamera               saves the camera's distance, height, angle and tilt as this character's home (retail)
//   /resetcamera             sets home to retail's 240 units away, height 10, behind, level, and applies it
//   /snapback                retail's camera swing-back (off by default): not built

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
        string word = words[0].ToLowerInvariant();
        if (word.StartsWith("/qbar"))   // one of the few prefix-matched commands: /qbar3 = page 3 (daoc-re, commands note)
        {
            QbarCommand(word, words);
            return true;
        }
        switch (word)
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
            case "/follow":
                Follow(5f);
                return true;
            case "/stick":
                Follow(2f);
                return true;
            case "/face":
                FaceTarget();
                return true;
            case "/clock":
                ClientSettings.ShowClock = !ClientSettings.ShowClock;
                ClientSettings.NotifyChanged(false);   // the clock window follows the setting
                return true;
            case "/setcamera":
                SetCameraHome();
                return true;
            case "/resetcamera":
                ResetCameraHome();
                return true;
            case "/snapback":
                Reply("The camera swing-back isn't built yet (retail's default is off).");
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

    // Retail's line (daoc-re, commands-and-keys note): "<zone>: loc=x,y,z dir=d", x and y from the zone's corner and z
    // the region's, all truncated; dir = ((heading + 2048) * 360 / 4096) % 360. Not built: 0,0 outside every zone, a
    // dungeon's origin (the region's first zone), the House line.
    private string LocText()
    {
        PositionAndObjectId p = _world.Position;
        Zone z = _world.Zone;
        int dir = ((p.Heading & 0x0FFF) + 2048) * 360 / 4096 % 360;
        return $"{z.Name}: loc={(int)(p.X - z.X)},{(int)(p.Y - z.Y)},{(int)p.Z} dir={dir}";
    }

    // "/qbar<page> [bar]": turns a bar (default 1) to that page, 1-10.
    private void QbarCommand(string word, string[] words)
    {
        int bar = 1;
        if (!int.TryParse(word[5..], out int page) || page is < 1 or > 10
            || words.Length > 1 && (!int.TryParse(words[1], out bar) || bar < 1 || bar > _bars.Count))
        {
            Reply("Usage: /qbar [[bank#] [quickbar#(defaults to 1)]");
            return;
        }
        _bars[bar - 1].TurnTo(page - 1);
    }

    private void QuickbarUsage()
    {
        Reply("Usage: /quickbar [quickbar#(1-3)] [1/0(1=on, 0=off)]");
        Reply("Example: \"/quickbar 2 0\" to disable quickbar #2.");
    }

    private void QuickbarCommand(string[] words)
    {
        if (words.Length < 2 || !int.TryParse(words[1], out int bar) || bar < 1 || bar > _bars.Count)
        {
            QuickbarUsage();
            return;
        }
        bool? show = words.Length < 3 ? null : OnOff(words[2]);
        if (words.Length >= 3 && show is null)
        {
            QuickbarUsage();
            return;
        }
        // Without 1/0, flip it; on/off and the confirmation line are ours (retail takes 1/0).
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

    // The torch (T), as retail's (daoc-re's commands-and-keys note Q6 and torch-bit answer, 2026-11-19): two white point
    // lights 128 units above the character, brightness 2.0 and 0.5, linear attenuation 0.006 (Godot's falloff differs:
    // ours reaches ~32 m, where retail's has faded to a twentieth). While lit, 0xA9 carries action bit 0x80, so others
    // see it. The state is kept between sessions (torchmode) and is on in a new settings file, as retail's.
    // Not yet: the model's own torch node, and others' torches (their 0xA9 0x80, an NPC's 0xDA flags 0x04).
    private Node3D? _torch;
    private const float TorchHeight = 128 * RegionMetresPerUnit;

    private void ToggleTorch() => SetTorch(_torch is null || !_torch.Visible, announce: true);

    private void SetTorch(bool lit, bool announce = false)
    {
        if (_torch is null)
        {
            _torch = new Node3D { Name = "Torch", Position = new Vector3(0, TorchHeight, 0), Visible = false };
            foreach (float energy in new[] { 2.0f, 0.5f })
                _torch.AddChild(new OmniLight3D
                {
                    LightColor = Colors.White, LightEnergy = energy, LightSpecular = 0, OmniRange = 32f, OmniAttenuation = 1f,
                    ShadowEnabled = energy > 1,
                });
            _body.AddChild(_torch);
        }
        if (_session is not null)
            _session.TorchLit = lit;
        if (_torch.Visible == lit)
            return;
        _torch.Visible = lit;
        ClientSettings.Torch = lit;
        GD.Print($"[world] torch {(lit ? "lit" : "out")}");
        if (announce)
            AppendChat(lit ? "You light your torch." : "You put out your torch.", LocalReply);   // the wording is ours
    }
}
