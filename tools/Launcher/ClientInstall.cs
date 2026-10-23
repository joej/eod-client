// SPDX-License-Identifier: GPL-3.0-or-later
//
// A DAoC client folder that can be started against a private server: connect.exe next to a 1.127
// game DLL, as installed by OpenDAoC's client installer. connect.exe is not ours; it starts the
// game DLL pointed at the server with encryption off. Its command line, as shard launchers use it
// (OpenDAoC-Launcher, nostalgia-launcher):
//
//   connect.exe <game dll> <host>[:<port>] <account> <password> [<character> <realm 1-3>]

namespace EodLauncher;

public sealed record ClientInstall(string Folder, string ConnectExe, string GameDll)
{
    /// <summary>DLL names to look for, in order. OpenDAoC-style installs keep the 1.127 DLL as game1127.dll.</summary>
    public static readonly string[] GameDllNames = ["game1127.dll", "game.dll"];

    /// <summary>The install in <paramref name="folder"/>, or null with the reason.</summary>
    public static ClientInstall? Find(string folder, out string problem)
    {
        problem = "";
        if (string.IsNullOrWhiteSpace(folder) || !Directory.Exists(folder))
        {
            problem = "Choose the DAoC client folder.";
            return null;
        }

        string connect = Path.Combine(folder, "connect.exe");
        if (!File.Exists(connect))
        {
            problem = "No connect.exe in this folder. Choose the folder OpenDAoC's client installer created.";
            return null;
        }

        string? dll = GameDllNames.FirstOrDefault(name => File.Exists(Path.Combine(folder, name)));
        if (dll is null)
        {
            problem = $"No game DLL in this folder (looked for {string.Join(", ", GameDllNames)}).";
            return null;
        }

        return new ClientInstall(folder, connect, dll);
    }

    /// <summary>
    /// connect.exe's arguments. The port is only added when it isn't the default, as the plain
    /// <c>host</c> form is the one documented most widely. connect.exe splits its command line on
    /// spaces, so values containing whitespace are refused rather than quoted.
    /// </summary>
    public IReadOnlyList<string> Arguments(ServerEntry server, string account, string password, string? character = null, Realm realm = Realm.None)
    {
        string host = server.Port == ServerEntry.DefaultPort ? server.Host : $"{server.Host}:{server.Port}";
        List<string> args = [GameDll, Checked(host, "Server address"), Checked(account, "Account"), Checked(password, "Password")];

        if (!string.IsNullOrEmpty(character))
        {
            if (realm == Realm.None)
                throw new ArgumentException("Choose the character's realm.", nameof(realm));
            args.Add(Checked(character, "Character"));
            args.Add(((int)realm).ToString(System.Globalization.CultureInfo.InvariantCulture));
        }

        return args;
    }

    private static string Checked(string value, string what)
    {
        if (string.IsNullOrEmpty(value))
            throw new ArgumentException($"{what} is empty.");
        if (value.Any(char.IsWhiteSpace) || value.Contains('"'))
            throw new ArgumentException($"{what} can't contain spaces or quotes (connect.exe can't pass them on).");
        return value;
    }
}
