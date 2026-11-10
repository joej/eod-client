// SPDX-License-Identifier: GPL-3.0-or-later
//
// CharacterScreen: Command-line options: --offline, --autologin and the folders converted assets come from.

using DaocProtocol.Net;
using DaocProtocol.Packets;
using DaocProtocol.Session;
using Godot;

namespace EodClient;

public partial class CharacterScreen
{
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
        _world.QuitPressed += () => QuitCleanly(0);
        GetTree().Root.CallDeferred(Node.MethodName.AddChild, _world);
        Visible = false;
        // After _Ready: resizing the window from inside it fails ("Parent node is busy setting up children") and
        // stalled offline start-up once a window mode was saved.
        Callable.From(ClientSettings.ApplyWindowMode).CallDeferred();
    }

    private static int? Option(string[] args, string name)
    {
        int i = Array.IndexOf(args, name);
        if (i < 0)
            return null;
        if (i + 1 < args.Length && int.TryParse(args[i + 1], out int value))
            return value;
        GD.PrintErr($"{name} needs a number; ignored.");
        return null;
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
            if (_world is null || !IsInstanceValid(_world))
            {
                GD.PrintErr($"in world {staySeconds.Value} s: no world view (a region change failed?)");
                return;
            }
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
        QuitCleanly(ok ? 0 : 1);
    }
}
