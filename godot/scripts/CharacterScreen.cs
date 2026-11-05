// SPDX-License-Identifier: GPL-3.0-or-later
//
// Log in, list the account's characters, and enter the world with one (shown by WorldView).
// All protocol work is in DaocProtocol's ClientSession; this node only builds the UI.
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
        // --settings: open the settings window at start (for checking it in a screenshot).
        if (Array.IndexOf(args, "--settings") >= 0)
            CallDeferred(MethodName.OpenSettings);
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

    private void OpenSettings() => SettingsDialog.Open(this);

    /// <summary>The profile the form shows, as last saved (null for a new one).</summary>
    private LoginProfile? Current() =>
        _profile.Selected >= 0 && _profile.Selected < _profiles.Count ? _profiles[_profile.Selected] : null;

    // Fills the login list and selects <paramref name="select"/> (or "New"); with <paramref name="show"/>
    // the form shows it too (not after a save: the form already holds it, password included).
    private void RefreshProfiles(string? select, bool show = true)
    {
        _profiles = LoginProfiles.All();
        _profile.Clear();
        foreach (LoginProfile p in _profiles)
            _profile.AddItem(p.Name);
        _profile.AddItem("New server or account…");
        int index = _profiles.FindIndex(p => p.Name == select);
        _profile.Select(index >= 0 ? index : _profiles.Count);
        if (show)
            ShowProfile(index >= 0 ? _profiles[index] : null);
    }

    private void ShowProfile(LoginProfile? p)
    {
        _host.Text = p?.Host ?? "";
        _port.Value = p?.Port ?? 10300;
        _account.Text = p?.Account ?? "";
        _autoStart.Select((int)(p?.AutoStart ?? AutoStart.Off));
        string? saved = p is null ? null : Keychain.Get(p.KeychainAccount);
        _password.Text = saved ?? OS.GetEnvironment("DAOC_PASSWORD");
        _remember.ButtonPressed = saved is not null;
        if (p is { Realm: not Realm.None })
            _realm.Select(_realm.GetItemIndex((int)p.Realm));
    }

    // The form as a saved login, named by its account and server: changing either makes another
    // entry (the old one stays until deleted). Keeps that login's realm and last character.
    private LoginProfile SaveFormProfile()
    {
        string host = _host.Text.Trim(), account = _account.Text.Trim();
        int port = (int)_port.Value;
        string name = LoginProfile.NameFor(account, host, port);
        LoginProfile? old = _profiles.FirstOrDefault(p => p.Name == name);
        var profile = new LoginProfile(name, host, port, account, (AutoStart)_autoStart.Selected,
            old?.Realm ?? Realm.None, old?.Character ?? "");
        LoginProfiles.Save(profile);
        RefreshProfiles(name, show: false);
        return profile;
    }

    private void DeleteProfile()
    {
        if (Current() is not LoginProfile p)
            return;
        LoginProfiles.Delete(p.Name);
        Keychain.Delete(p.KeychainAccount);
        RefreshProfiles(null);
        _status.Text = $"Removed {p.Name}.";
    }

    // Startup: log in with the last profile, then stop at the list or enter its last character.
    private async Task StartProfile(LoginProfile p)
    {
        _status.Text = $"Logging in as {p.Account} on {p.Host}…";
        GD.Print(_status.Text);
        bool listed = await ListCharacters(p.Host, p.Port, p.Account, _password.Text);
        GD.Print(_status.Text);
        if (!listed || p.AutoStart != AutoStart.LastCharacter)
            return;
        CharacterSummary? last = _listed.FirstOrDefault(c => c.Name == p.Character);
        if (last is null)
        {
            _status.Text += p.Character.Length == 0
                ? " (No character entered yet with this profile: pick one.)"
                : $" ({p.Character} isn't in {_listedRealm}: pick one.)";
            GD.Print(_status.Text);
            return;
        }
        GD.Print($"Entering the world as {last.Name}…");
        if (!await EnterWorld(last.Slot))
            GD.Print(_status.Text);
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
        ClientSettings.ApplyWindowMode();
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

    // Converted assets: --zones/--library, else the repo's own private/ folders when they exist
    // (the client run from its source checkout, as tools/play.sh does).
    private static string? ZonesDir() => ResolvedPath(OS.GetCmdlineUserArgs(), "--zones") ?? RepoPrivate("zones");
    private static string? LibraryDir() => ResolvedPath(OS.GetCmdlineUserArgs(), "--library") ?? RepoPrivate("library");

    private static string? RepoPrivate(string name)
    {
        string dir = System.IO.Path.GetFullPath(System.IO.Path.Combine(ProjectSettings.GlobalizePath("res://"), "..", "private", name));
        return System.IO.Directory.Exists(dir) ? dir : null;
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
            CharacterSummary? entered = _listed.FirstOrDefault(c => c.Slot == slot);
            _playerModel = entered?.Model ?? 0;
            _playerLook = entered?.Look ?? default;
            _playerWorn = entered?.Worn ?? [];
            _playerHoodUp = entered?.HoodUp ?? false;
            _playerActiveWeapons = entered?.ActiveWeapons ?? 0xFF;
            if (entered is not null && Current() is LoginProfile p && p.Account == _account.Text.Trim())
            {
                LoginProfiles.Save(p with { Realm = _listedRealm, Character = entered.Name });
                _profiles = LoginProfiles.All();
            }
            if (Option(OS.GetCmdlineUserArgs(), "--goto") is int gotoZone)
                world = await GoTo(world, gotoZone);

            ShowWorld(world);
            Visible = false;
            ClientSettings.ApplyWindowMode();
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
        // The converter writes a landing spot where the middle is a bad place (a city: its first gate).
        (float x, float y, float z, string where) = TravelDialog.Target(zone, ZonesDir(), from.Position.Z);
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
        _world.QuitPressed += () => Fire(LeaveWorld);
        _world.BackToLoginPressed += () => Fire(() => BackToLogin("Disconnected."));
        _world.Relocated += moved => CallDeferred(MethodName.ShowWorldDeferred, moved.Zone.Id);
        _world.RegionChanging += () => Fire(FollowRegionChange);
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
        if ((zoneCsv is null ? ZonesDir() : null) is string zonesDir)
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
        var view = new WorldView
        {
            AssetsNote = note, PlayerModel = _playerModel, PlayerLook = _playerLook,
            PlayerWorn = _playerWorn, PlayerHoodUp = _playerHoodUp, PlayerActiveWeapons = _playerActiveWeapons, ZonesDir = ZonesDir(),
            PacketLogging = (() => _packetLog.Enabled, on => _logPackets.ButtonPressed = on),
        };
        view.Init(offline ? null : _connection, offline ? null : _session, world, ResolvedPath(a, "--model"),
            zoneCsv, LibraryDir(), terrain);
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
            ClientSettings.ApplyLoginWindow();
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

    private async Task AutoLogin(string host, string account, int port, int? enterSlot, int? staySeconds)
    {
        // Show what we log in with, so Connect (or a re-login after a disconnect) uses the same values.
        _host.Text = host;
        _port.Value = port;
        _account.Text = account;
        _password.Text = OS.GetEnvironment("DAOC_PASSWORD");
        bool ok = await ListCharacters(host, port, account, _password.Text);
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
}
