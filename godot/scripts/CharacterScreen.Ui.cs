// SPDX-License-Identifier: GPL-3.0-or-later
//
// CharacterScreen: The login form and character list, built in code.

using DaocProtocol.Net;
using DaocProtocol.Packets;
using DaocProtocol.Session;
using Godot;

namespace EodClient;

public partial class CharacterScreen
{
    // Two panels, used left to right: the server and account to log in with, then (once logged in)
    // the realm and character to enter the world with. Settings and the packet log along the bottom.
    private void BuildUi()
    {
        var margin = new MarginContainer();
        margin.SetAnchorsPreset(LayoutPreset.FullRect);
        foreach (string side in new[] { "left", "right", "top", "bottom" })
            margin.AddThemeConstantOverride($"margin_{side}", 24);
        AddChild(margin);

        var page = new VBoxContainer();
        page.AddThemeConstantOverride("separation", 12);
        margin.AddChild(page);
        var panels = new HBoxContainer { SizeFlagsVertical = SizeFlags.ExpandFill };
        panels.AddThemeConstantOverride("separation", 24);
        page.AddChild(panels);

        // --- 1. Log in ---
        VBoxContainer login = Panel(panels, "1  Log in");
        var form = new GridContainer { Columns = 2 };
        form.AddThemeConstantOverride("h_separation", 12);
        form.AddThemeConstantOverride("v_separation", 8);
        login.AddChild(form);

        var profileRow = new HBoxContainer();
        _profile = new OptionButton { SizeFlagsHorizontal = SizeFlags.ExpandFill };
        _profile.ItemSelected += index => ShowProfile(index < _profiles.Count ? _profiles[(int)index] : null);
        profileRow.AddChild(_profile);
        var delete = new Button { Text = "Remove", TooltipText = "Forget this server and account" };
        delete.Pressed += DeleteProfile;
        profileRow.AddChild(delete);
        Field(form, "Saved", profileRow);
        _host = Field(form, "Server", new LineEdit { PlaceholderText = "host or IP" });
        _port = Field(form, "Port", new SpinBox { MinValue = 1, MaxValue = 65535, Value = 10300 });
        _account = Field(form, "Account", new LineEdit());
        _password = Field(form, "Password", new LineEdit { Secret = true });
        _remember = Field(form, "", new CheckBox
        {
            Text = Keychain.Available ? "Remember password (Keychain)" : "Remember password (macOS only)",
            Disabled = !Keychain.Available,
        });
        _autoStart = Field(form, "On start", new OptionButton());
        _autoStart.AddItem("Wait at this screen", (int)AutoStart.Off);
        _autoStart.AddItem("Log in to the character list", (int)AutoStart.CharacterList);
        _autoStart.AddItem("Log in and enter the last character", (int)AutoStart.LastCharacter);
        _autoStart.ItemSelected += index =>
        {
            // Saved at once for an existing profile, so it holds without another login.
            if (Current() is LoginProfile p)
                LoginProfiles.Save(p with { AutoStart = (AutoStart)_autoStart.Selected });
            _profiles = LoginProfiles.All();
        };
        _connect = new Button { Text = "Log in", SizeFlagsHorizontal = SizeFlags.ShrinkEnd, CustomMinimumSize = new Vector2(140, 0) };
        _connect.Pressed += () => Fire(OnConnectPressed);
        login.AddChild(_connect);
        _status = new Label { Text = "Not connected.", AutowrapMode = TextServer.AutowrapMode.WordSmart };
        login.AddChild(_status);

        // --- 2. Character ---
        VBoxContainer character = Panel(panels, "2  Choose a character");
        var realmRow = new HBoxContainer();
        realmRow.AddChild(new Label { Text = "Realm" });
        _realm = new OptionButton { SizeFlagsHorizontal = SizeFlags.ExpandFill };
        foreach (Realm realm in new[] { Realm.Albion, Realm.Midgard, Realm.Hibernia })
            _realm.AddItem(realm.ToString(), (int)realm);
        // At the character screen, picking a realm lists that realm's characters.
        _realm.ItemSelected += index => Fire(() => ListRealm((Realm)_realm.GetItemId((int)index)));
        realmRow.AddChild(_realm);
        character.AddChild(realmRow);
        _characters = new ItemList { SizeFlagsVertical = SizeFlags.ExpandFill };
        _characters.ItemSelected += index => _enter.Disabled = _session is null;
        _characters.ItemActivated += index => Fire(OnEnterPressed);
        character.AddChild(_characters);
        _enter = new Button { Text = "Enter world", Disabled = true, SizeFlagsHorizontal = SizeFlags.ShrinkEnd, CustomMinimumSize = new Vector2(140, 0) };
        _enter.Pressed += () => Fire(OnEnterPressed);
        character.AddChild(_enter);

        // --- bottom bar ---
        var bar = new HBoxContainer();
        bar.AddThemeConstantOverride("separation", 16);
        page.AddChild(bar);
        var settings = new Button { Text = "Settings" };
        settings.Pressed += () => SettingsDialog.Open(this);
        bar.AddChild(settings);
        _logPackets = new CheckBox { Text = "Log packets (terminal)" };
        _logPackets.Toggled += on => _packetLog.Enabled = on;
        bar.AddChild(_logPackets);
        _hexLog = new CheckBox { Text = "as hex" };
        _hexLog.Toggled += on => _packetLog.Hex = on;
        bar.AddChild(_hexLog);

        _password.TextSubmitted += submitted => Fire(OnConnectPressed);
    }

    // A titled panel taking half the width; returns its content column.
    private static VBoxContainer Panel(HBoxContainer parent, string title)
    {
        var panel = new PanelContainer { SizeFlagsHorizontal = SizeFlags.ExpandFill, SizeFlagsStretchRatio = 1 };
        parent.AddChild(panel);
        var pad = new MarginContainer();
        foreach (string side in new[] { "left", "right", "top", "bottom" })
            pad.AddThemeConstantOverride($"margin_{side}", 14);
        panel.AddChild(pad);
        var column = new VBoxContainer();
        column.AddThemeConstantOverride("separation", 10);
        pad.AddChild(column);
        var heading = new Label { Text = title };
        heading.AddThemeFontSizeOverride("font_size", 18);
        column.AddChild(heading);
        return column;
    }

    private static T Field<T>(GridContainer form, string label, T control) where T : Control
    {
        form.AddChild(new Label { Text = label });
        control.SizeFlagsHorizontal = SizeFlags.ExpandFill;
        form.AddChild(control);
        return control;
    }
}
