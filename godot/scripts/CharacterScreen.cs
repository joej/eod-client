// SPDX-License-Identifier: GPL-3.0-or-later
//
// Log in, list the account's characters, and enter the world with one (shown by WorldView).
// All protocol work is in DaocProtocol's ClientSession; this node only builds the UI.
// This file holds the state, connecting and listing, and the connection's lifetime; the rest is in
// CharacterScreen.Ui (the form, built in code), .Profiles (saved logins), .World (entering, zone
// changes, quitting) and .CommandLine (--offline, --autologin, asset folders).
//
// Logins are saved as profiles (LoginProfiles: server, account, realm, last character, and what to
// do on start); passwords optionally in the macOS Keychain. Started with no options, the client
// opens the last profile and, if it says so, logs in to the character list or straight into the
// world. Converted assets default to the repo's private/zones and private/library.
//
// Headless check (prints the result and quits; exit code 0 on success):
//   DAOC_PASSWORD=... Godot --headless --path godot -- --autologin <host> <account> [port]
//       [--enter <slot> [--goto <zone>] [--stay <seconds>]] [--zones <dir>] [--model <abs.glb>]
// --model loads a converted .glb as the character body (asset-pipeline stand-in); without it the
// world shows the MVC capsule, so the client still runs with no assets.
// --zones is the folder of converted zones (private/zones): each view loads zoneNNN/ for the zone
// the server puts the character in, and a new one whenever the server moves it to another zone.
// --zone/--terrain name one zone's files instead (whatever the server says). --goto moves the
// character to the middle of a zone after entering (GM command /jump: the account must be a GM).

using DaocProtocol.Net;
using DaocProtocol.Packets;
using DaocProtocol.Session;
using Godot;

namespace EodClient;

public partial class CharacterScreen : Control
{
    private OptionButton _profile = null!;
    private CheckBox _remember = null!;
    private OptionButton _autoStart = null!;
    private List<LoginProfile> _profiles = [];
    private LineEdit _host = null!;
    private SpinBox _port = null!;
    private LineEdit _account = null!;
    private LineEdit _password = null!;
    private OptionButton _realm = null!;
    private CheckBox _hexLog = null!;
    private CheckBox _logPackets = null!;
    // One log for every connection; off unless --packets or the checkbox (F4 in the world).
    private readonly PacketLog _packetLog = new(line => GD.Print(line)) { Enabled = false };
    private Button _connect = null!;
    private Button _enter = null!;
    private Label _status = null!;
    private ItemList _characters = null!;

    private DaocConnection? _connection;
    private ClientSession? _session;
    private Realm _listedRealm;
    private readonly List<CharacterSummary> _listed = [];
    private WorldView? _world;
    private ushort _playerModel;   // the entered character's model id, for its figure
    private CharacterLook _playerLook;   // ... and its face and hair
    private IReadOnlyList<WornItem> _playerWorn = [];   // ... and its armour (the server sends ours only here)
    private bool _playerHoodUp;
    private byte _playerActiveWeapons = 0xFF;
    private CancellationTokenSource? _keepAlive;

    public override void _Ready()
    {
        // Closing the window while in the world quits cleanly first (see _Notification).
        GetTree().AutoAcceptQuit = false;
        // --window WxH: a window of that size from now on (saved); --window max: maximized again.
        string[] userArgs = OS.GetCmdlineUserArgs();
        int w = Array.IndexOf(userArgs, "--window");
        if (w >= 0 && w + 1 < userArgs.Length)
        {
            ClientSettings.WindowSize = ClientSettings.ParseSize(userArgs[w + 1]);
            ClientSettings.Fullscreen = false;
        }
        // A login-sized window; the world gets the player's window mode (ClientSettings) when entered.
        ClientSettings.ApplyLoginWindow();
        ClientSettings.ApplyFrameCap();
        BuildUi();
        LoginProfiles.MigrateOldLogin();
        RefreshProfiles(LoginProfiles.Last()?.Name);

        string[] args = OS.GetCmdlineUserArgs();
        // --packets: log every packet's code and size (off by default: the client prints its own
        // status and asset messages, which a packet stream would bury).
        _logPackets.ButtonPressed = Array.IndexOf(args, "--packets") >= 0;
        // --settings [TAB]: open the settings window at start on that tab (0 Display, 1 Keyboard), for checking it in
        // a screenshot.
        int st = Array.IndexOf(args, "--settings");
        if (st >= 0)
            CallDeferred(MethodName.OpenSettings, st + 1 < args.Length && int.TryParse(args[st + 1], out int tab) ? tab : 0);
        // --offline X Y Z [REGION]: the world view at that position with no server (no login, no
        // entities, nothing sent). For checking converted assets (with --shots) any time.
        int o = Array.IndexOf(args, "--offline");
        if (o >= 0 && o + 3 < args.Length)
        {
            EnterOffline(args, o);
            return;
        }
        int i = Array.IndexOf(args, "--autologin");
        if (i >= 0 && args.Length >= i + 3)
        {
            int port = args.Length > i + 3 && int.TryParse(args[i + 3], out int p) ? p : 10300;
            int? enter = Option(args, "--enter");
            // --stay N: CI/headless — spend N seconds in the world, then /quit and close. Without it,
            // stay in the world indefinitely for interactive play (close the window or press Quit).
            Fire(() => AutoLogin(args[i + 1], args[i + 2], port, enter, Option(args, "--stay")));
            return;
        }
        if (Current() is { AutoStart: not AutoStart.Off } start && _password.Text.Length > 0)
            Fire(() => StartProfile(start));
    }

    // F11 toggles fullscreen from anywhere: the login and character screen, and the world, which
    // leaves this node in the tree.
    public override void _Input(InputEvent @event)
    {
        if (@event is InputEventKey { Pressed: true, Echo: false, Keycode: Key.F11 })
        {
            // The login screen stays a small window; F11 switches the world's mode.
            ClientSettings.Fullscreen = !ClientSettings.Fullscreen;
            if (_world is not null)
                ClientSettings.ApplyWindowMode();
            GetViewport().SetInputAsHandled();
        }
    }

    public override void _ExitTree() => Fire(Disconnect);

    public override void _Notification(int what)
    {
        if (what == NotificationWMCloseRequest)
            Fire(CloseWindow);
    }

    private async Task CloseWindow()
    {
        try
        {
            if (_world is not null && _world.ClosedReason is null)
                await LeaveWorld();
            await Disconnect();
        }
        finally
        {
            GetTree().Quit();   // whatever happened, the window was asked to close
        }
    }

    // Runs a UI action's task without awaiting it. Each action catches the failures it expects (a lost
    // connection, a timeout); anything else would vanish with the unobserved task and could leave
    // Connect or Enter disabled, so it is logged and the login form comes back.
    private async void Fire(Func<Task> action)
    {
        try
        {
            await action();
        }
        catch (Exception e)
        {
            GD.PrintErr($"[client] unexpected error: {e}");
            if (!IsInsideTree())
                return;
            try
            {
                await BackToLogin($"Error: {e.Message}");
            }
            catch (Exception again)
            {
                GD.PrintErr($"[client] could not reset the login form: {again}");
            }
        }
    }

    private async Task OnConnectPressed()
    {
        LoginProfile p = SaveFormProfile();
        if (!await ListCharacters(p.Host, p.Port, p.Account, _password.Text) || !Keychain.Available)
            return;
        // Remembered only once it has worked.
        if (_remember.ButtonPressed)
        {
            if (!Keychain.Set(p.KeychainAccount, _password.Text))
                _status.Text += " (Couldn't store the password in the Keychain.)";
        }
        else
        {
            Keychain.Delete(p.KeychainAccount);
        }
    }

    // Lists another realm's characters on the open character screen.
    private async Task ListRealm(Realm realm)
    {
        if (_session?.State != SessionState.CharacterScreen || realm == _listedRealm)
            return;
        try
        {
            ShowCharacters(await _session.GetCharactersAsync(realm), realm);
            if (Current() is LoginProfile p)
                LoginProfiles.Save(p with { Realm = realm });
            _profiles = LoginProfiles.All();
        }
        catch (Exception e) when (e is IOException or TimeoutException or InvalidDataException)
        {
            _status.Text = $"Failed to list {realm}: {e.Message}";
        }
    }

    private void ShowCharacters(CharacterOverview overview, Realm realm)
    {
        _characters.Clear();
        _listed.Clear();
        foreach (CharacterSummary c in overview.Characters)
        {
            _listed.Add(c);
            _characters.AddItem($"{c.Name}    level {c.Level} {c.RaceName} {c.ClassName}, {c.Location}");
        }
        _listedRealm = realm;
        _realm.Select(_realm.GetItemIndex((int)realm));
        _enter.Disabled = true;
        _status.Text = $"{_account.Text.Trim()}: {overview.Characters.Count} character(s) in {realm}." +
                       (overview.Characters.Count > 0 ? " Select one and press Enter world." : "");
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
            _connection = new DaocConnection(_packetLog);
            var session = new ClientSession(_connection);

            _status.Text = $"Connecting to {host}:{port}…";
            await _connection.ConnectAsync(host, port);

            LoginResult login = await session.LoginAsync(account, password);
            if (!login.Success)
            {
                // The server hangs up after a refusal; drop the connection first, or _Process sees
                // it close and replaces this message with "Disconnected".
                await Disconnect();
                _status.Text = $"Login refused: {login.Error}";
                return false;
            }

            // The profile's realm if it has one, else the account's, else the one picked below.
            Realm realm = await session.OpenCharacterScreenAsync();
            if (Current() is { Realm: not Realm.None } p && p.Account == account && p.Host == host)
                realm = p.Realm;
            if (realm == Realm.None)
                realm = (Realm)_realm.GetSelectedId();

            CharacterOverview overview = await session.GetCharactersAsync(realm);
            ShowCharacters(overview, realm);
            _session = session;
            // Pings from here on (OpenDAoC drops a client after 150 s without one, even at the
            // character screen); position updates are added while in the world.
            _keepAlive = new CancellationTokenSource();
            Fire(() => KeepAlive(session, _keepAlive.Token));
            _status.Text = $"{account} on {login.Granted!.ServerNameShort}: {overview.Characters.Count} character(s) in {realm}." +
                           (overview.Characters.Count > 0 ? " Select one and press Enter world." : "");
            return true;
        }
        catch (Exception e) when (e is IOException or TimeoutException or System.Net.Sockets.SocketException
                                      or InvalidDataException or NotSupportedException)
        {
            await Disconnect();
            _status.Text = $"Failed: {e.Message}";
            return false;
        }
        finally
        {
            _connect.Disabled = false;
        }
    }

    /// <summary>Drops the connection and shows the login form with a message.</summary>
    private async Task BackToLogin(string message)
    {
        await Disconnect();
        _characters.Clear();
        _listed.Clear();
        Visible = true;
        ClientSettings.ApplyLoginWindow();
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
            Fire(() => BackToLogin($"Disconnected: {reason}"));
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

}
