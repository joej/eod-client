// SPDX-License-Identifier: GPL-3.0-or-later
//
// Which keys and mouse buttons drive which actions, by preset (docs/controls.md): retail's RPG (the default:
// W/S, A/D turn, Q/E strafe), MMO (A/D strafe, keypad turns) and FPS (E/D, S/F strafe), and the 2001 manual's
// classic layout (arrows, right/left mouse button forward/back). Sprint V and auto-run NumLock in all.

using System.Collections.Generic;
using Godot;

namespace EodClient;

public enum ControlAction
{
    Forward, Back, TurnLeft, TurnRight, StrafeLeft, StrafeRight, Jump, Sprint, AutoRun, AttackMode,
}

public static class KeyBindings
{
    // Retail's shipped presets (keyboard.dat's rpgdefault, mmodefault, fpsdefault: daoc-re, 2026-11-06) and the 2001
    // manual's default. Walk (RPG: Scroll Lock; FPS/MMO: Shift+K) waits for the walk speed (W41).
    public static readonly string[] Presets = ["rpg", "mmo", "fps", "classic"];

    private static (Key[] Keys, MouseButton[] Buttons) K(params Key[] keys) => (keys, []);

    private static Dictionary<ControlAction, (Key[] Keys, MouseButton[] Buttons)> Layout(Key forward, Key back, Key turnLeft,
        Key turnRight, Key strafeLeft, Key strafeRight, Key jump) => new()
    {
        [ControlAction.Forward] = K(forward), [ControlAction.Back] = K(back),
        [ControlAction.TurnLeft] = K(turnLeft), [ControlAction.TurnRight] = K(turnRight),
        [ControlAction.StrafeLeft] = K(strafeLeft), [ControlAction.StrafeRight] = K(strafeRight),
        [ControlAction.Jump] = K(jump), [ControlAction.Sprint] = K(Key.V), [ControlAction.AutoRun] = K(Key.Numlock),
        // Retail's attack key is open (W34); H is ours (F6, the 2001 default, opens our travel window).
        [ControlAction.AttackMode] = K(Key.H),
    };

    private static readonly Dictionary<string, Dictionary<ControlAction, (Key[] Keys, MouseButton[] Buttons)>> Tables = new()
    {
        ["rpg"] = Layout(Key.W, Key.S, Key.A, Key.D, Key.Q, Key.E, Key.Space),
        ["mmo"] = Layout(Key.W, Key.S, Key.Kp4, Key.Kp6, Key.A, Key.D, Key.Space),
        ["fps"] = Layout(Key.E, Key.D, Key.Kp4, Key.Kp6, Key.S, Key.F, Key.Space),
        // 2001 manual: arrows, and the mouse buttons walk (right forward, left back).
        ["classic"] = Layout(Key.Up, Key.Down, Key.Left, Key.Right, Key.Q, Key.W, Key.A),
    };

    static KeyBindings()
    {
        Tables["classic"][ControlAction.Forward] = ([Key.Up], [MouseButton.Right]);
        Tables["classic"][ControlAction.Back] = ([Key.Down], [MouseButton.Left]);
    }

    private static string? _preset;

    /// <summary>The layout in use (read from the settings once; setting it saves it). An unknown name is RPG.</summary>
    public static string Preset
    {
        get => _preset ??= Tables.ContainsKey(ClientSettings.Controls) ? ClientSettings.Controls : "rpg";
        set => ClientSettings.Controls = _preset = value;
    }

    private static Dictionary<ControlAction, (Key[] Keys, MouseButton[] Buttons)> Table => Tables[Preset];

    /// <summary>True while a key or button bound to <paramref name="action"/> is held.</summary>
    public static bool Held(ControlAction action)
    {
        var (keys, buttons) = Table[action];
        foreach (Key k in keys)
            if (Input.IsKeyPressed(k))
                return true;
        foreach (MouseButton b in buttons)
            if (Input.IsMouseButtonPressed(b))
                return true;
        return false;
    }

    /// <summary>True when <paramref name="e"/> is a fresh press (not a repeat) of a key bound to <paramref name="action"/>.</summary>
    public static bool Pressed(InputEvent e, ControlAction action) =>
        e is InputEventKey { Pressed: true, Echo: false } k && System.Array.IndexOf(Table[action].Keys, k.Keycode) >= 0;

    /// <summary>Whether the mouse button moves the character in this preset (classic: right forward, left back),
    /// so a click with it isn't also an inspect or a camera pan.</summary>
    public static bool Moves(MouseButton b) =>
        System.Array.IndexOf(Table[ControlAction.Forward].Buttons, b) >= 0 || System.Array.IndexOf(Table[ControlAction.Back].Buttons, b) >= 0;
}
