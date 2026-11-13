// SPDX-License-Identifier: GPL-3.0-or-later
//
// Which keys and mouse buttons drive which actions, by preset (docs/controls.md; docs/ui-windows.md "Keys"): retail's
// RPG (the default: W/S, A/D turn, Q/E strafe), MMO (A/D strafe, arrows turn) and FPS (E/D, S/F strafe, arrows turn),
// and the 2001 manual's classic layout (arrows, right/left mouse button forward/back), which takes RPG's other keys.
// A key may carry modifiers (Godot's KeyModifierMask bits, e.g. Shift+Y): movement keys are held and ignore them,
// the others need exactly their modifiers (retail's rule).

using System.Collections.Generic;
using System.Linq;
using Godot;

namespace EodClient;

public enum ControlAction
{
    Forward, Back, TurnLeft, TurnRight, StrafeLeft, StrafeRight, Jump, Sprint, AutoRun, AttackMode, Map, Torch, ResetCamera, ToggleInterface,
}

public static class KeyBindings
{
    // Retail's shipped presets (keyboard.dat's rpgdefault, mmodefault, fpsdefault: daoc-re, 2026-11-06) and the 2001
    // manual's default. Walk (RPG: Scroll Lock; FPS/MMO: Shift+K) waits for the walk speed (W41).
    public static readonly string[] Presets = ["rpg", "mmo", "fps", "classic"];

    private static (Key[] Keys, MouseButton[] Buttons) K(params Key[] keys) => (keys, []);

    /// <summary>Movement actions are read while held and ignore modifiers; the rest are presses with exact modifiers.</summary>
    public static bool IsHeldAction(ControlAction a) => a <= ControlAction.Jump;

    private static Key Shift(Key k) => k | (Key)KeyModifierMask.MaskShift;
    private static Key Alt(Key k) => k | (Key)KeyModifierMask.MaskAlt;

    // One preset: movement, then retail's defaults for the other actions (keyboard.dat, daoc-re 2026-11-13).
    private static Dictionary<ControlAction, (Key[] Keys, MouseButton[] Buttons)> Layout(Key forward, Key back, Key[] turnLeft,
        Key[] turnRight, Key strafeLeft, Key strafeRight, Key[] runlock, Key sprint, Key combat, Key torch, Key resetCamera) => new()
    {
        [ControlAction.Forward] = K(forward), [ControlAction.Back] = K(back),
        [ControlAction.TurnLeft] = K(turnLeft), [ControlAction.TurnRight] = K(turnRight),
        [ControlAction.StrafeLeft] = K(strafeLeft), [ControlAction.StrafeRight] = K(strafeRight),
        [ControlAction.Jump] = K(Key.Space), [ControlAction.Sprint] = K(sprint), [ControlAction.AutoRun] = K(runlock),
        [ControlAction.AttackMode] = K(combat), [ControlAction.Map] = K(Key.M), [ControlAction.Torch] = K(torch),
        [ControlAction.ResetCamera] = K(resetCamera), [ControlAction.ToggleInterface] = K(Alt(Key.Z)),
    };

    // Turning in MMO/FPS: the arrows; retail forces NumLock off, so keypad 4/6 turn too.
    private static readonly Key[] TurnL = [Key.Left, Key.Kp4], TurnR = [Key.Right, Key.Kp6];

    private static readonly Dictionary<string, Dictionary<ControlAction, (Key[] Keys, MouseButton[] Buttons)>> Tables = new()
    {
        ["rpg"] = Layout(Key.W, Key.S, [Key.A], [Key.D], Key.Q, Key.E, [Key.Numlock], Key.V, Key.Quoteleft, Key.T, Key.Kp5),
        // MMO and FPS: Runlock is T / Y with NumLock as the second bind (Run Lock 2).
        ["mmo"] = Layout(Key.W, Key.S, TurnL, TurnR, Key.A, Key.D, [Key.T, Key.Numlock], Key.F, Key.X, Shift(Key.Y), Key.Z),
        ["fps"] = Layout(Key.E, Key.D, TurnL, TurnR, Key.S, Key.F, [Key.Y, Key.Numlock], Key.G, Key.C, Shift(Key.Y), Key.X),
        // 2001 manual: arrows, and the mouse buttons walk (right forward, left back); the rest as RPG.
        ["classic"] = Layout(Key.Up, Key.Down, [Key.Left], [Key.Right], Key.Q, Key.W, [Key.Numlock], Key.V, Key.Quoteleft, Key.T, Key.Kp5),
    };

    static KeyBindings()
    {
        Tables["classic"][ControlAction.Forward] = ([Key.Up], [MouseButton.Right]);
        Tables["classic"][ControlAction.Back] = ([Key.Down], [MouseButton.Left]);
        Tables["classic"][ControlAction.Jump] = K(Key.A);
        Tables["classic"][ControlAction.ResetCamera] = ([Key.D, Key.Home], []);
    }

    private static string? _preset;

    /// <summary>The layout in use (read from the settings once; setting it saves it). An unknown name is RPG.</summary>
    public static string Preset
    {
        get => _preset ??= Tables.ContainsKey(ClientSettings.Controls) ? ClientSettings.Controls : "rpg";
        set => ClientSettings.Controls = _preset = value;
    }

    private static Dictionary<ControlAction, (Key[] Keys, MouseButton[] Buttons)> Table => Tables[Preset];

    // The player's own keys per action (ClientSettings "keys"), over the preset: retail's rule (user.dat first, the
    // preset only for actions left unbound). An empty array is "no key".
    private static Dictionary<ControlAction, Key[]>? _own;
    private static Dictionary<ControlAction, Key[]> Own => _own ??= ClientSettings.LoadKeys();

    /// <summary>What each action is called in the keyboard settings.</summary>
    public static string Label(ControlAction a) => a switch
    {
        ControlAction.Forward => "Move forward", ControlAction.Back => "Move backward",
        ControlAction.TurnLeft => "Turn left", ControlAction.TurnRight => "Turn right",
        ControlAction.StrafeLeft => "Strafe left", ControlAction.StrafeRight => "Strafe right",
        ControlAction.Jump => "Jump", ControlAction.Sprint => "Sprint", ControlAction.AutoRun => "Auto-run",
        ControlAction.AttackMode => "Attack mode", ControlAction.Map => "Map", ControlAction.Torch => "Torch",
        ControlAction.ResetCamera => "Reset camera", ControlAction.ToggleInterface => "Toggle interface", _ => a.ToString(),
    };

    /// <summary>The keys that trigger <paramref name="action"/>: the player's own, else the preset's.</summary>
    public static Key[] KeysFor(ControlAction action) => Own.TryGetValue(action, out Key[]? own) ? own : Table[action].Keys;

    /// <summary>Whether the player has bound <paramref name="action"/> themselves.</summary>
    public static bool IsOwn(ControlAction action) => Own.ContainsKey(action);

    /// <summary>Binds <paramref name="action"/> to <paramref name="key"/> (Key.None: no key), taking that key off any
    /// other action so one key never does two things. Saved at once.</summary>
    public static void Bind(ControlAction action, Key key)
    {
        if (key != Key.None)
            foreach (ControlAction other in System.Enum.GetValues<ControlAction>())
                if (other != action && System.Array.IndexOf(KeysFor(other), key) >= 0)
                    Own[other] = [.. KeysFor(other).Where(k => k != key)];
        Own[action] = key == Key.None ? [] : [key];
        ClientSettings.SaveKeys(Own);
    }

    /// <summary>Drops the player's own keys: every action back to the preset's.</summary>
    public static void ResetToPreset()
    {
        Own.Clear();
        ClientSettings.SaveKeys(Own);
    }

    /// <summary>True while a key or button bound to <paramref name="action"/> is held.</summary>
    public static bool Held(ControlAction action)
    {
        var (_, buttons) = Table[action];
        Key[] keys = KeysFor(action);
        foreach (Key k in keys)
            if (Input.IsKeyPressed(k & (Key)KeyModifierMask.CodeMask))
                return true;
        foreach (MouseButton b in buttons)
            if (Input.IsMouseButtonPressed(b))
                return true;
        return false;
    }

    /// <summary>True when <paramref name="e"/> is a fresh press (not a repeat) of a key bound to <paramref name="action"/>.</summary>
    public static bool Pressed(InputEvent e, ControlAction action) =>
        e is InputEventKey { Pressed: true, Echo: false } k && System.Array.IndexOf(KeysFor(action), k.GetKeycodeWithModifiers()) >= 0;

    /// <summary>Whether the mouse button moves the character in this preset (classic: right forward, left back),
    /// so a click with it isn't also an inspect or a camera pan.</summary>
    public static bool Moves(MouseButton b) =>
        System.Array.IndexOf(Table[ControlAction.Forward].Buttons, b) >= 0 || System.Array.IndexOf(Table[ControlAction.Back].Buttons, b) >= 0;
}
