// SPDX-License-Identifier: GPL-3.0-or-later
//
// CharacterScreen: Entering, moving between and leaving the world: the WorldView for each zone, /goto, quitting.

using DaocProtocol.Net;
using DaocProtocol.Packets;
using DaocProtocol.Session;
using Godot;

namespace EodClient;

public partial class CharacterScreen
{
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
        _world.QuitArrived += reply => Fire(() => FinishQuit(reply));
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
        ClientSession session = _session;
        // The character sits for the server's quit timer; moving stands it up and calls the quit off.
        using var interrupt = new CancellationTokenSource();
        void OnInterrupted() => interrupt.Cancel();
        world.QuitInterrupted += OnInterrupted;
        world.BeginQuit();
        try
        {
            CharacterOverview? back = await session.QuitAsync(interrupt.Token);
            _status.Text = back is null
                ? "The server ended the game."
                : $"Back at the character screen: {back.Characters.Count} character(s) in {_listedRealm}.";
            ShowCharacterScreen(world);
            return true;
        }
        catch (OperationCanceledException) when (interrupt.IsCancellationRequested)
        {
            // Standing up is what stops the server's timer (it doesn't stop on movement alone).
            await session.SitAsync(false);
            world.EndQuit();
            return false;
        }
        catch (Exception e) when (e is IOException or TimeoutException or InvalidOperationException)
        {
            _status.Text = $"Quit failed: {e.Message}";
            ShowCharacterScreen(world);
            return false;
        }
        finally
        {
            world.QuitInterrupted -= OnInterrupted;
        }
    }

    // The server ended a quit we weren't waiting on (sent some other way): list the realm, which also
    // moves the server's connection state to the character screen, and show it.
    private async Task FinishQuit(ServerPacket reply)
    {
        if (_world is not WorldView world || _session is not ClientSession session)
            return;
        try
        {
            CharacterOverview? back = await session.FinishQuitAsync(reply);
            _status.Text = back is null
                ? "The server ended the game."
                : $"Back at the character screen: {back.Characters.Count} character(s) in {_listedRealm}.";
        }
        catch (Exception e) when (e is IOException or TimeoutException or InvalidDataException)
        {
            _status.Text = $"Quit failed: {e.Message}";
        }
        ShowCharacterScreen(world);
    }

    // Leaves the world view for the character screen once the quit has ended.
    private void ShowCharacterScreen(WorldView world)
    {
        _world = null;
        world.QueueFree();
        Visible = true;
        ClientSettings.ApplyLoginWindow();
        _enter.Disabled = _session?.State != SessionState.CharacterScreen;
    }
}
