// SPDX-License-Identifier: GPL-3.0-or-later

using EodLauncher;

namespace DaocProtocol.Tests;

/// <summary>
/// The launcher's platform-neutral parts (linked in from tools/Launcher, which itself only builds
/// for Windows): connect.exe's command line, finding the client folder, saving the settings.
/// </summary>
public sealed class LauncherTests : IDisposable
{
    private readonly string _folder = Directory.CreateTempSubdirectory("eodlauncher-").FullName;
    private static readonly ServerEntry Local = new() { Name = "Test", Host = "192.0.2.10" };
    private static readonly ClientInstall Install = new("C:\\DAoC", "C:\\DAoC\\connect.exe", "game1127.dll");

    public void Dispose() => Directory.Delete(_folder, recursive: true);

    [Fact]
    public void DefaultPortIsLeftOut()
    {
        Assert.Equal(["game1127.dll", "192.0.2.10", "player", "pw"], Install.Arguments(Local, "player", "pw"));
    }

    [Fact]
    public void OtherPortIsAppendedToTheHost()
    {
        var server = new ServerEntry { Name = "Test", Host = "shard.example", Port = 10500 };
        Assert.Equal("shard.example:10500", Install.Arguments(server, "player", "pw")[1]);
    }

    [Fact]
    public void CharacterAndRealmNumberGoLast()
    {
        Assert.Equal(["game1127.dll", "192.0.2.10", "player", "pw", "Eodarms", "1"],
            Install.Arguments(Local, "player", "pw", "Eodarms", EodLauncher.Realm.Albion));
    }

    [Theory]
    [InlineData("two words", "pw", null)]
    [InlineData("player", "with space", null)]
    [InlineData("player", "quote\"d", null)]
    [InlineData("player", "", null)]
    [InlineData("player", "pw", "Two Words")]
    public void RefusesValuesConnectExeCannotPassOn(string account, string password, string? character)
    {
        Assert.Throws<ArgumentException>(() => Install.Arguments(Local, account, password, character, EodLauncher.Realm.Midgard));
    }

    [Fact]
    public void CharacterNeedsARealm()
    {
        Assert.Throws<ArgumentException>(() => Install.Arguments(Local, "player", "pw", "Eodarms"));
    }

    [Fact]
    public void FindsConnectAndPrefersTheVersionedDll()
    {
        Assert.Null(ClientInstall.Find(_folder, out string problem));
        Assert.Contains("connect.exe", problem);

        File.WriteAllText(Path.Combine(_folder, "connect.exe"), "");
        Assert.Null(ClientInstall.Find(_folder, out problem));
        Assert.Contains("game DLL", problem);

        File.WriteAllText(Path.Combine(_folder, "game.dll"), "");
        Assert.Equal("game.dll", ClientInstall.Find(_folder, out _)?.GameDll);

        File.WriteAllText(Path.Combine(_folder, "game1127.dll"), "");
        Assert.Equal("game1127.dll", ClientInstall.Find(_folder, out _)?.GameDll);
    }

    [Fact]
    public void SettingsSurviveASaveAndLoad()
    {
        string path = Path.Combine(_folder, "sub", "launcher.json");
        Assert.Empty(LauncherConfig.Load(path).Servers);

        var config = new LauncherConfig { ClientFolder = "C:\\DAoC" };
        config.Servers.Add(new ServerEntry
        {
            Name = "Test", Host = "192.0.2.10", Port = 10301,
            Accounts = [new AccountEntry { Account = "player", Character = "Eodarms", Realm = EodLauncher.Realm.Hibernia }],
        });
        config.Save(path);

        LauncherConfig loaded = LauncherConfig.Load(path);
        Assert.Equal("C:\\DAoC", loaded.ClientFolder);
        ServerEntry server = Assert.Single(loaded.Servers);
        Assert.Equal(10301, server.Port);
        AccountEntry account = Assert.Single(server.Accounts);
        Assert.Equal(("player", "Eodarms", EodLauncher.Realm.Hibernia, (string?)null),
            (account.Account, account.Character, account.Realm, account.ProtectedPassword));
    }
}
