// SPDX-License-Identifier: GPL-3.0-or-later
//
// Log in, list the account's characters, and enter the world with one (shown by WorldView).
// All protocol work is in DaocProtocol's ClientSession; this node only builds the UI.
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
    private ushort _playerModel;   // the entered character's model id, for its figure
    private CancellationTokenSource? _keepAlive;

    public override void _Ready()
    {
        // Closing the window while in the world quits cleanly first (see _Notification).
        GetTree().AutoAcceptQuit = false;
        ClientSettings.ApplyWindowMode();
        BuildUi();
        LoadSettings();

        string[] args = OS.GetCmdlineUserArgs();
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
            _ = AutoLogin(args[i + 1], args[i + 2], port, enter, Option(args, "--stay"));
        }
    }

    private void EnterOffline(string[] args, int o)
    {
        var inv = System.Globalization.CultureInfo.InvariantCulture;
        float x = float.Parse(args[o + 1], inv), y = float.Parse(args[o + 2], inv), z = float.Parse(args[o + 3], inv);
        ushort region = o + 4 < args.Length && ushort.TryParse(args[o + 4], out ushort r) ? r : (ushort)1;
        DaocProtocol.World.Zone? zone = DaocProtocol.World.Zones.Find(region, x, y);
        if (zone is null)
        {
            _status.Text = $"--offline: no zone at {x} {y} in region {region}";
            return;
        }
        var world = new WorldPosition(new PositionAndObjectId(x, y, z, 0, 0, region, 0), zone);
        _world = NewWorldView(world, offline: true);
        _world.QuitPressed += () => GetTree().Quit();
        GetTree().Root.CallDeferred(Node.MethodName.AddChild, _world);
        Visible = false;
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

    // F11 toggles fullscreen from anywhere: the login and character screen, and the world, which
    // leaves this node in the tree.
    public override void _Input(InputEvent @event)
    {
        if (@event is InputEventKey { Pressed: true, Echo: false, Keycode: Key.F11 })
        {
            ClientSettings.Fullscreen = !ClientSettings.Fullscreen;
            ClientSettings.ApplyWindowMode();
            GetViewport().SetInputAsHandled();
        }
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
            _playerModel = _listed.FirstOrDefault(c => c.Slot == slot)?.Model ?? 0;
            if (Option(OS.GetCmdlineUserArgs(), "--goto") is int gotoZone)
                world = await GoTo(world, gotoZone);

            ShowWorld(world);
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
    /// Moves the character to the middle of <paramref name="zoneId"/> before the world is shown (no
    /// view is draining yet, so the session sees the server's answer). A refused or failed move
    /// leaves the character where it entered, with a line saying why.
    /// </summary>
    private async Task<WorldPosition> GoTo(WorldPosition from, int zoneId)
    {
        DaocProtocol.World.Zone? zone = DaocProtocol.World.Zones.All.FirstOrDefault(z => z.Id == zoneId);
        if (zone is null || _session is null)
        {
            GD.PrintErr($"--goto: no zone {zoneId} in zones.tsv");
            return from;
        }
        (float x, float y) = zone.Centre;
        float z = from.Position.Z;
        string where = "the middle";
        // The converter writes a landing spot where the middle is a bad place (a city: its first gate).
        if (ResolvedPath(OS.GetCmdlineUserArgs(), "--zones") is string zonesDir
            && System.IO.File.Exists(System.IO.Path.Combine(zonesDir, $"zone{zoneId:D3}", "landing.txt"))
            && System.IO.File.ReadAllText(System.IO.Path.Combine(zonesDir, $"zone{zoneId:D3}", "landing.txt"))
                .Split(' ', StringSplitOptions.RemoveEmptyEntries) is [var sx, var sy, var sz, ..]
            && float.TryParse(sx, System.Globalization.CultureInfo.InvariantCulture, out float lx)
            && float.TryParse(sy, System.Globalization.CultureInfo.InvariantCulture, out float ly)
            && float.TryParse(sz, System.Globalization.CultureInfo.InvariantCulture, out float lz))
            (x, y, z, where) = (lx, ly, lz, "its landing spot");
        GD.Print($"--goto {zoneId}: jumping to {where} in {zone.Name} (region {zone.RegionId})");
        try
        {
            // Z is the server's to keep until the view snaps the body onto the ground and reports it.
            return await _session.JumpAsync(zone.RegionId, x, y, z);
        }
        catch (Exception e) when (e is TimeoutException or IOException or InvalidDataException)
        {
            GD.PrintErr($"--goto {zoneId}: {e.Message}");
            return _session.World ?? from;
        }
    }

    /// <summary>
    /// Shows the world at <paramref name="world"/>, replacing the current view. Called on entry and
    /// again whenever the server moves the character to another zone or region: each view holds one
    /// zone's terrain and objects.
    /// </summary>
    private void ShowWorld(WorldPosition world)
    {
        WorldView? old = _world;
        _world = NewWorldView(world, offline: false);
        _world.QuitPressed += () => _ = LeaveWorld();
        _world.BackToLoginPressed += () => _ = BackToLogin("Disconnected.");
        _world.Relocated += moved => CallDeferred(MethodName.ShowWorldDeferred, moved.Zone.Id);
        _world.RegionChanging += () => _ = FollowRegionChange();
        old?.QueueFree();
        GetTree().Root.AddChild(_world);
    }

    // Relocated fires while the old view dispatches a packet; swap views outside that call.
    private void ShowWorldDeferred(int zoneId)
    {
        if (_session?.World is WorldPosition world && world.Zone.Id == zoneId)
            ShowWorld(world);
    }

    private async Task FollowRegionChange()
    {
        if (_session is null)
            return;
        try
        {
            WorldPosition world = await _session.ChangeRegionAsync();
            GD.Print($"[world] region change: now in {world.Zone.Name} (zone {world.Zone.Id}, region {world.Position.RegionId})");
            ShowWorld(world);
        }
        catch (Exception e) when (e is IOException or TimeoutException or InvalidDataException or InvalidOperationException)
        {
            await BackToLogin($"Region change failed: {e.Message}");
        }
    }

    /// <summary>
    /// A world view for <paramref name="world"/>, with the zone's converted files: from --zones (the
    /// zone the server put us in), else the explicit --zone/--terrain files.
    /// </summary>
    private WorldView NewWorldView(WorldPosition world, bool offline)
    {
        string[] a = OS.GetCmdlineUserArgs();
        string? zoneCsv = ResolvedPath(a, "--zone"), terrain = ResolvedPath(a, "--terrain"), note = "";
        if (ResolvedPath(a, "--zones") is string zonesDir)
        {
            string dir = System.IO.Path.Combine(zonesDir, $"zone{world.Zone.Id:D3}");
            zoneCsv = System.IO.Path.Combine(dir, "zone.csv");
            terrain = System.IO.Path.Combine(dir, "terrain.glb");
            if (!System.IO.File.Exists(zoneCsv))
            {
                note = $"zone {world.Zone.Id} ({world.Zone.Name}) is not converted: flat ground (make zone{world.Zone.Id})";
                zoneCsv = terrain = null;
            }
        }
        var view = new WorldView { AssetsNote = note, PlayerModel = _playerModel };
        view.Init(offline ? null : _connection, offline ? null : _session, world, ResolvedPath(a, "--model"),
            zoneCsv, ResolvedPath(a, "--library"), terrain);
        if (note.Length > 0)
            GD.Print($"[world] {note}");
        return view;
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

    private async Task AutoLogin(string host, string account, int port, int? enterSlot, int? staySeconds)
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

        // Interactive play: no --stay means stay in the world until the user closes the window or
        // presses Quit. The keep-alive keeps the session up; we don't self-quit. (Earlier this
        // defaulted to 10 s then /quit, so the window vanished ~10-30 s in during live testing.)
        if (staySeconds is null)
            return;

        if (ok && _world is not null)
        {
            await ToSignal(GetTree().CreateTimer(staySeconds.Value), SceneTreeTimer.SignalName.Timeout);
            ok = _world.ClosedReason is null;
            GD.Print($"in world {staySeconds.Value} s: {_world.Packets} packets, {_world.Pings} ping replies, " +
                     $"{_world.Entities} live entities, {_world.ChatMessages} chat/system messages, " +
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
