// SPDX-License-Identifier: GPL-3.0-or-later
//
// The Settings window's Keyboard tab, after retail's Configure Keyboard: the preset (retail's RPG, MMO, FPS, and the
// 2001 classic layout), then each action with its key. Click a key and press the new one; Backspace leaves the action
// without a key, Esc keeps it. The player's keys are saved over the preset (KeyBindings, ClientSettings "keys").

using Godot;

namespace EodClient;

public partial class KeyboardPanel : VBoxContainer
{
    private OptionButton _preset = null!;
    private readonly System.Collections.Generic.Dictionary<ControlAction, Button> _keys = new();
    private ControlAction? _waiting;   // the action whose new key the next key press sets

    public override void _Ready()
    {
        Name = "Keyboard";
        AddThemeConstantOverride("separation", 10);

        var top = new HBoxContainer();
        top.AddThemeConstantOverride("separation", 12);
        top.AddChild(new Label { Text = "Layout" });
        _preset = new OptionButton { CustomMinimumSize = new Vector2(160, 0) };
        foreach (string p in KeyBindings.Presets)
            _preset.AddItem(p.ToUpperInvariant() == "CLASSIC" ? "Classic (2001)" : p.ToUpperInvariant());
        _preset.Selected = System.Array.IndexOf(KeyBindings.Presets, KeyBindings.Preset);
        _preset.ItemSelected += i =>
        {
            KeyBindings.Preset = KeyBindings.Presets[i];
            Refresh();
        };
        top.AddChild(_preset);
        var reset = new Button { Text = "Reset to layout" };
        reset.Pressed += () =>
        {
            KeyBindings.ResetToPreset();
            Refresh();
        };
        top.AddChild(reset);
        AddChild(top);

        var grid = new GridContainer { Columns = 2 };
        grid.AddThemeConstantOverride("h_separation", 12);
        grid.AddThemeConstantOverride("v_separation", 6);
        foreach (ControlAction action in System.Enum.GetValues<ControlAction>())
        {
            grid.AddChild(new Label { Text = KeyBindings.Label(action), CustomMinimumSize = new Vector2(160, 0) });
            var key = new Button { CustomMinimumSize = new Vector2(220, 0), FocusMode = FocusModeEnum.None };
            ControlAction a = action;
            key.Pressed += () =>
            {
                _waiting = a;
                Refresh();
            };
            _keys[action] = key;
            grid.AddChild(key);
        }
        AddChild(grid);
        AddChild(new Label
        {
            Text = "Click a key, then press the new one. Backspace: no key. Esc: keep it. A key taken from another action " +
                "leaves that one without it. Your keys are kept over the layout's; Reset gives the layout's back.",
            AutowrapMode = TextServer.AutowrapMode.WordSmart, CustomMinimumSize = new Vector2(420, 0),
        });
        Refresh();
    }

    // The next key press while waiting is the new binding (consumed, so the dialog and the world don't see it).
    public override void _Input(InputEvent @event)
    {
        if (_waiting is not { } action || @event is not InputEventKey { Pressed: true, Echo: false } k)
            return;
        // A modifier on its own waits for the key it modifies (movement keys ignore modifiers, so take them bare).
        if (k.Keycode is Key.Shift or Key.Alt or Key.Ctrl or Key.Meta)
        {
            GetViewport().SetInputAsHandled();
            return;
        }
        GetViewport().SetInputAsHandled();
        _waiting = null;
        if (k.Keycode == Key.Backspace)
            KeyBindings.Bind(action, Key.None);
        else if (k.Keycode != Key.Escape)
            KeyBindings.Bind(action, KeyBindings.IsHeldAction(action) ? k.Keycode : k.GetKeycodeWithModifiers());
        Refresh();
    }

    private void Refresh()
    {
        foreach ((ControlAction action, Button button) in _keys)
        {
            Key[] keys = KeyBindings.KeysFor(action);
            button.Text = _waiting == action ? "Press a key…"
                : keys.Length == 0 ? "(no key)"
                : string.Join(", ", System.Linq.Enumerable.Select(keys, OS.GetKeycodeString)) + (KeyBindings.IsOwn(action) ? "  (yours)" : "");
        }
    }
}
