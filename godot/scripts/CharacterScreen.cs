// SPDX-License-Identifier: GPL-3.0-or-later
//
// Gate 2 screen: log in and list the account's characters. All protocol work is in
// DaocProtocol's ClientSession; this node only builds the UI and shows results.
//
// Headless check (prints the character names and quits; exit code 0 on success):
//   DAOC_PASSWORD=... Godot --headless --path godot -- --autologin <host> <account> [port]

using DaocProtocol.Net;
using DaocProtocol.Packets;
using DaocProtocol.Session;
using Godot;

namespace EodClient;

public partial class CharacterScreen : Control
{
    private const string SettingsPath = "user://settings.cfg";

    private LineEdit _host = null!;
    private SpinBox _port = null!;
    private LineEdit _account = null!;
    private LineEdit _password = null!;
    private OptionButton _realm = null!;
    private CheckBox _hexLog = null!;
    private Button _connect = null!;
    private Label _status = null!;
    private ItemList _characters = null!;

    private DaocConnection? _connection;

    public override void _Ready()
    {
        BuildUi();
        LoadSettings();

        string[] args = OS.GetCmdlineUserArgs();
        int i = Array.IndexOf(args, "--autologin");
        if (i >= 0 && args.Length >= i + 3)
            _ = AutoLogin(args[i + 1], args[i + 2], args.Length > i + 3 ? int.Parse(args[i + 3]) : 10300);
    }

    public override void _ExitTree() => _ = Disconnect();

    private void BuildUi()
    {
        var margin = new MarginContainer();
        margin.SetAnchorsPreset(LayoutPreset.FullRect);
        foreach (string side in new[] { "left", "right", "top", "bottom" })
            margin.AddThemeConstantOverride($"margin_{side}", 24);
        AddChild(margin);

        var column = new VBoxContainer();
        column.AddThemeConstantOverride("separation", 12);
        margin.AddChild(column);

        var form = new GridContainer { Columns = 2 };
        form.AddThemeConstantOverride("h_separation", 12);
        column.AddChild(form);

        _host = Field(form, "Server", new LineEdit { PlaceholderText = "host or IP" });
        _port = Field(form, "Port", new SpinBox { MinValue = 1, MaxValue = 65535, Value = 10300 });
        _account = Field(form, "Account", new LineEdit());
        _password = Field(form, "Password", new LineEdit { Secret = true });
        _realm = Field(form, "Realm (new account)", new OptionButton());
        foreach (Realm realm in new[] { Realm.Albion, Realm.Midgard, Realm.Hibernia })
            _realm.AddItem(realm.ToString(), (int)realm);

        var buttons = new HBoxContainer();
        column.AddChild(buttons);
        _connect = new Button { Text = "Connect" };
        _connect.Pressed += () => _ = OnConnectPressed();
        buttons.AddChild(_connect);
        _hexLog = new CheckBox { Text = "Log packets as hex (Output panel)" };
        buttons.AddChild(_hexLog);

        _status = new Label { Text = "Not connected." };
        column.AddChild(_status);

        _characters = new ItemList { SizeFlagsVertical = SizeFlags.ExpandFill };
        column.AddChild(_characters);

        _password.TextSubmitted += submitted => _ = OnConnectPressed();
    }

    private static T Field<T>(GridContainer form, string label, T control) where T : Control
    {
        form.AddChild(new Label { Text = label });
        control.CustomMinimumSize = new Vector2(320, 0);
        form.AddChild(control);
        return control;
    }

    private async Task OnConnectPressed()
    {
        SaveSettings();
        await ListCharacters(_host.Text.Trim(), (int)_port.Value, _account.Text.Trim(), _password.Text);
    }

    /// <summary>Connects, logs in and fills the list. Returns false on any failure (shown in the status line).</summary>
    private async Task<bool> ListCharacters(string host, int port, string account, string password)
    {
        if (host.Length == 0 || account.Length == 0 || password.Length == 0)
        {
            _status.Text = "Server, account and password are required.";
            return false;
        }

        _connect.Disabled = true;
        _characters.Clear();
        await Disconnect();
        try
        {
            // The log sink runs on the receive thread; GD.Print is safe to call from there.
            _connection = new DaocConnection(new PacketLog(line => GD.Print(line), _hexLog.ButtonPressed));
            var session = new ClientSession(_connection);

            _status.Text = $"Connecting to {host}:{port}…";
            await _connection.ConnectAsync(host, port);

            LoginResult login = await session.LoginAsync(account, password);
            if (!login.Success)
            {
                _status.Text = $"Login refused: {login.Error}";
                return false;
            }

            Realm realm = await session.OpenCharacterScreenAsync();
            if (realm == Realm.None)
                realm = (Realm)_realm.GetSelectedId();

            CharacterOverview overview = await session.GetCharactersAsync(realm);
            foreach (CharacterSummary c in overview.Characters)
                _characters.AddItem($"{c.Name}    level {c.Level} {c.RaceName} {c.ClassName}, {c.Location}");

            _status.Text = $"{account} on {login.Granted!.ServerNameShort}: {overview.Characters.Count} character(s) in {realm}.";
            return true;
        }
        catch (Exception e) when (e is IOException or TimeoutException or System.Net.Sockets.SocketException
                                      or InvalidDataException or NotSupportedException)
        {
            _status.Text = $"Failed: {e.Message}";
            return false;
        }
        finally
        {
            _connect.Disabled = false;
        }
    }

    private async Task Disconnect()
    {
        if (_connection is null)
            return;
        DaocConnection connection = _connection;
        _connection = null;
        await connection.DisposeAsync();
    }

    private async Task AutoLogin(string host, string account, int port)
    {
        bool ok = await ListCharacters(host, port, account, OS.GetEnvironment("DAOC_PASSWORD"));
        GD.Print(_status.Text);
        for (int i = 0; i < _characters.ItemCount; i++)
            GD.Print($"  {_characters.GetItemText(i)}");
        await Disconnect();
        GetTree().Quit(ok ? 0 : 1);
    }

    // Remembers server, port and account between runs; never the password.
    private void LoadSettings()
    {
        var config = new ConfigFile();
        if (config.Load(SettingsPath) != Error.Ok)
            return;
        _host.Text = (string)config.GetValue("login", "host", "");
        _port.Value = (int)config.GetValue("login", "port", 10300);
        _account.Text = (string)config.GetValue("login", "account", "");
    }

    private void SaveSettings()
    {
        var config = new ConfigFile();
        config.SetValue("login", "host", _host.Text.Trim());
        config.SetValue("login", "port", (int)_port.Value);
        config.SetValue("login", "account", _account.Text.Trim());
        config.Save(SettingsPath);
    }
}
