// SPDX-License-Identifier: GPL-3.0-or-later
//
// Saved logins: a server + account under a name, with what to do when the client starts (wait,
// log in to the character list, or log in and enter the last character). Kept in the same
// user://settings.cfg as ClientSettings, one [profile "<name>"] section each; passwords are never
// stored here (see Keychain).

using DaocProtocol.Packets;
using Godot;

namespace EodClient;

/// <summary>What the client does with a profile when it starts.</summary>
public enum AutoStart
{
    /// <summary>Show the login form and wait.</summary>
    Off,
    /// <summary>Log in and show the character list.</summary>
    CharacterList,
    /// <summary>Log in and enter the world with the last character played.</summary>
    LastCharacter,
}

public sealed record LoginProfile(
    string Name, string Host, int Port, string Account, AutoStart AutoStart = AutoStart.Off,
    Realm Realm = Realm.None, string Character = "")
{
    /// <summary>The Keychain entry this profile's password is stored under.</summary>
    public string KeychainAccount => $"{Account}@{Host}:{Port}";
}

public static class LoginProfiles
{
    private const string Path = "user://settings.cfg";
    private const string Prefix = "profile ";

    public static List<LoginProfile> All()
    {
        ConfigFile cfg = Load();
        var list = new List<LoginProfile>();
        foreach (string section in cfg.GetSections())
        {
            if (!section.StartsWith(Prefix, StringComparison.Ordinal))
                continue;
            string V(string key, string fallback = "") => cfg.GetValue(section, key, fallback).AsString();
            list.Add(new LoginProfile(
                section[Prefix.Length..], V("host"), cfg.GetValue(section, "port", 10300).AsInt32(), V("account"),
                Enum.TryParse(V("auto_start"), out AutoStart a) ? a : AutoStart.Off,
                Enum.TryParse(V("realm"), out Realm r) ? r : Realm.None, V("character")));
        }
        return [.. list.OrderBy(p => p.Name, StringComparer.OrdinalIgnoreCase)];
    }

    /// <summary>The profile used last, or null.</summary>
    public static LoginProfile? Last()
    {
        string name = Load().GetValue("login", "last_profile", "").AsString();
        return All().FirstOrDefault(p => p.Name == name);
    }

    /// <summary>Saves (or replaces) <paramref name="profile"/> and makes it the last one used.</summary>
    public static void Save(LoginProfile profile)
    {
        ConfigFile cfg = Load();
        string s = Prefix + profile.Name;
        if (cfg.HasSection(s))
            cfg.EraseSection(s);
        cfg.SetValue(s, "host", profile.Host);
        cfg.SetValue(s, "port", profile.Port);
        cfg.SetValue(s, "account", profile.Account);
        cfg.SetValue(s, "auto_start", profile.AutoStart.ToString());
        cfg.SetValue(s, "realm", profile.Realm.ToString());
        cfg.SetValue(s, "character", profile.Character);
        cfg.SetValue("login", "last_profile", profile.Name);
        cfg.Save(Path);
    }

    public static void Delete(string name)
    {
        ConfigFile cfg = Load();
        if (cfg.HasSection(Prefix + name))
            cfg.EraseSection(Prefix + name);
        cfg.Save(Path);
    }

    /// <summary>
    /// Before profiles the form remembered one host/port/account under [login]; that becomes a
    /// profile once, so the first start after the change still shows it.
    /// </summary>
    public static void MigrateOldLogin()
    {
        ConfigFile cfg = Load();
        string host = cfg.GetValue("login", "host", "").AsString();
        string account = cfg.GetValue("login", "account", "").AsString();
        if (host.Length == 0 || account.Length == 0 || All().Count > 0)
            return;
        int port = cfg.GetValue("login", "port", 10300).AsInt32();
        Save(new LoginProfile($"{account}@{host}", host, port, account));
    }

    private static ConfigFile Load()
    {
        var cfg = new ConfigFile();
        cfg.Load(Path);   // missing file = no profiles
        return cfg;
    }
}
