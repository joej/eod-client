// SPDX-License-Identifier: GPL-3.0-or-later
//
// Log in, list the account's characters, and enter the world with one (shown by WorldView).
// All protocol work is in DaocProtocol's ClientSession; this node only builds the UI.
//
// Headless check (prints the result and quits; exit code 0 on success):
//   DAOC_PASSWORD=... Godot --headless --path godot -- --autologin <host> <account> [port]
//       [--enter <slot> [--stay <seconds>]] [--model <abs.glb>]
// --model loads a converted .glb as the character body (asset-pipeline stand-in); without it the
// world shows the MVC capsule, so the client still runs with no assets.

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
    private Button _enter = null!;
    private Label _status = null!;
    private ItemList _characters = null!;

    private DaocConnection? _connection;
    private ClientSession? _session;
    private Realm _listedRealm;
    private readonly List<CharacterSummary> _listed = [];
    private WorldView? _world;
    private CancellationTokenSource? _keepAlive;

    public override void _Ready()
    {
        // Closing the window while in the world quits cleanly first (see _Notification).
        GetTree().AutoAcceptQuit = false;
        BuildUi();
        LoadSettings();

        string[] args = OS.GetCmdlineUserArgs();
        int i = Array.IndexOf(args, "--autologin");
        if (i >= 0 && args.Length >= i + 3)
        {
            int port = args.Length > i + 3 && int.TryParse(args[i + 3], out int p) ? p : 10300;
            int? enter = Option(args, "--enter");
            _ = AutoLogin(args[i + 1], args[i + 2], port, enter, Option(args, "--stay") ?? 10);
        }
    }

    private static int? Option(string[] args, string name)
    {
        int i = Array.IndexOf(args, name);
        return i >= 0 && i + 1 < args.Length ? int.Parse(args[i + 1]) : null;
    }

    private static string? StringOption(string[] args, string name)
    {
        int i = Array.IndexOf(args, name);
        return i >= 0 && i + 1 < args.Length ? args[i + 1] : null;
    }

    // Resolve a relative path against the shell's working directory (PWD); Godot's own working dir
    // is the project folder, so a repo-relative path would otherwise resolve under godot/. A model
    // path thus works whether given relative (from where the command was run) or absolute.
    private static string? ResolvedPath(string[] args, string name)
    {
        string? m = StringOption(args, name);
        if (string.IsNullOrEmpty(m) || System.IO.Path.IsPathRooted(m))
            return m;
        string cwd = OS.GetEnvironment("PWD");
        string baseDir = string.IsNullOrEmpty(cwd) ? System.IO.Directory.GetCurrentDirectory() : cwd;
        return System.IO.Path.GetFullPath(System.IO.Path.Combine(baseDir, m));
    }

    public override void _ExitTree() => _ = Disconnect();

    public override void _Notification(int what)
    {
        if (what == NotificationWMCloseRequest)
            _ = CloseWindow();
    }

    private async Task CloseWindow()
    {
        if (_world is not null && _world.ClosedReason is null)
            await LeaveWorld();
        await Disconnect();
        GetTree().Quit();
    }

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
        _enter = new Button { Text = "Enter world", Disabled = true };
        _enter.Pressed += () => _ = OnEnterPressed();
        buttons.AddChild(_enter);
        _hexLog = new CheckBox { Text = "Log packets as hex (Output panel)" };
        buttons.AddChild(_hexLog);

        _status = new Label { Text = "Not connected." };
        column.AddChild(_status);

        _characters = new ItemList { SizeFlagsVertical = SizeFlags.ExpandFill };
        _characters.ItemSelected += index => _enter.Disabled = _session is null;
        _characters.ItemActivated += index => _ = OnEnterPressed();
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

    private async Task OnEnterPressed()
    {
        int[] selected = _characters.GetSelectedItems();
        if (selected.Length == 1)
            await EnterWorld(_listed[selected[0]].Slot);
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
        _enter.Disabled = true;
        _characters.Clear();
        _listed.Clear();
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
            {
                _listed.Add(c);
                _characters.AddItem($"{c.Name}    level {c.Level} {c.RaceName} {c.ClassName}, {c.Location}");
            }

            _session = session;
            _listedRealm = realm;
            // Pings from here on (OpenDAoC drops a client after 150 s without one, even at the
            // character screen); position updates are added while in the world.
            _keepAlive = new CancellationTokenSource();
            _ = KeepAlive(session, _keepAlive.Token);
            _status.Text = $"{account} on {login.Granted!.ServerNameShort}: {overview.Characters.Count} character(s) in {realm}." +
                           (overview.Characters.Count > 0 ? " Select one and press Enter world." : "");
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

    /// <summary>Enters the world with the character in <paramref name="slot"/> and switches to the 3D view.</summary>
    private async Task<bool> EnterWorld(int slot)
    {
        if (_session is null || _connection is null)
            return false;

        _connect.Disabled = true;
        _enter.Disabled = true;
        try
        {
            _status.Text = "Entering the world…";
            WorldPosition world = await _session.EnterWorldAsync(_listedRealm, slot);

            _world = new WorldView();
            string[] a = OS.GetCmdlineUserArgs();
            _world.Init(_connection, _session, world, ResolvedPath(a, "--model"),
                ResolvedPath(a, "--zone"), ResolvedPath(a, "--library"), ResolvedPath(a, "--terrain"));
            _world.QuitPressed += () => _ = LeaveWorld();
            _world.BackToLoginPressed += () => _ = BackToLogin("Disconnected.");
            GetTree().Root.AddChild(_world);
            Visible = false;
            return true;
        }
        catch (Exception e) when (e is IOException or TimeoutException or InvalidDataException or ArgumentException)
        {
            _status.Text = $"Failed to enter the world: {e.Message}";
            _connect.Disabled = false;
            return false;
        }
    }

    /// <summary>
    /// Quits cleanly (/quit, about 20 s) and returns to the character screen. Closing the
    /// connection instead leaves the character link-dead and the account locked for 60 s.
    /// </summary>
    private async Task<bool> LeaveWorld()
    {
        if (_world is null || _session is null)
            return false;

        WorldView world = _world;
        world.BeginQuit();
        try
        {
            CharacterOverview? back = await _session.QuitAsync();
            _status.Text = back is null
                ? "The server ended the game."
                : $"Back at the character screen: {back.Characters.Count} character(s) in {_listedRealm}.";
            return true;
        }
        catch (Exception e) when (e is IOException or TimeoutException or InvalidOperationException)
        {
            _status.Text = $"Quit failed: {e.Message}";
            return false;
        }
        finally
        {
            _world = null;
            world.QueueFree();
            Visible = true;
            _enter.Disabled = _session?.State != SessionState.CharacterScreen;
        }
    }

    /// <summary>Drops the connection and shows the login form with a message.</summary>
    private async Task BackToLogin(string message)
    {
        await Disconnect();
        _characters.Clear();
        _listed.Clear();
        Visible = true;
        _enter.Disabled = true;
        _connect.Disabled = false;
        _status.Text = message;
    }

    private async Task KeepAlive(ClientSession session, CancellationToken token)
    {
        try
        {
            await session.KeepAliveAsync(token);
        }
        catch (Exception e) when (e is IOException or System.Net.Sockets.SocketException or ObjectDisposedException)
        {
            // The connection is gone; _Process notices and says so.
        }
    }

    public override void _Process(double delta)
    {
        // At the character screen nothing else is reading, so notice a closed connection here.
        if (_world is null && _connection is not null && _connection.Completion.IsCompleted)
        {
            string reason = _connection.Completion.Exception?.InnerException?.Message ?? "connection closed";
            _ = BackToLogin($"Disconnected: {reason}");
        }
    }

    private async Task Disconnect()
    {
        _keepAlive?.Cancel();
        _keepAlive = null;
        _session = null;
        if (_world is not null)
        {
            _world.QueueFree();
            _world = null;
        }

        if (_connection is null)
            return;
        DaocConnection connection = _connection;
        _connection = null;
        await connection.DisposeAsync();
    }

    private async Task AutoLogin(string host, string account, int port, int? enterSlot, int staySeconds)
    {
        bool ok = await ListCharacters(host, port, account, OS.GetEnvironment("DAOC_PASSWORD"));
        GD.Print(_status.Text);
        for (int i = 0; i < _characters.ItemCount; i++)
            GD.Print($"  {_characters.GetItemText(i)}");

        if (ok && enterSlot is int slot)
        {
            ok = await EnterWorld(slot);
            if (!ok)
                GD.Print(_status.Text);
        }

        if (ok && _world is not null)
        {
            await ToSignal(GetTree().CreateTimer(staySeconds), SceneTreeTimer.SignalName.Timeout);
            ok = _world.ClosedReason is null;
            GD.Print($"in world {staySeconds} s: {_world.Packets} packets, {_world.Pings} ping replies, " +
                     (ok ? "still connected" : $"disconnected: {_world.ClosedReason}"));
            if (ok)
            {
                GD.Print("quitting (/quit)…");
                ok = await LeaveWorld();
                GD.Print(_status.Text);
            }
        }

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
