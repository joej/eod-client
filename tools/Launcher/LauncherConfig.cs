// SPDX-License-Identifier: GPL-3.0-or-later
//
// The launcher's saved settings: the client folder and a list of servers, each with its accounts.
// Stored as JSON in %AppData%\EodLauncher\launcher.json. Passwords are kept only if the user asks,
// encrypted for the current Windows user (see Passwords.cs); this file never sees them in clear.

using System.Text.Json;

namespace EodLauncher;

public sealed class LauncherConfig
{
    public string ClientFolder { get; set; } = "";
    public List<ServerEntry> Servers { get; set; } = [];

    private static readonly JsonSerializerOptions Json = new() { WriteIndented = true };

    public static LauncherConfig Load(string path) =>
        File.Exists(path) ? JsonSerializer.Deserialize<LauncherConfig>(File.ReadAllText(path), Json) ?? new() : new();

    public void Save(string path)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        string temp = path + ".tmp";
        File.WriteAllText(temp, JsonSerializer.Serialize(this, Json));
        File.Move(temp, path, overwrite: true);
    }
}

public sealed class ServerEntry
{
    public const int DefaultPort = 10300;

    public string Name { get; set; } = "";
    public string Host { get; set; } = "";
    public int Port { get; set; } = DefaultPort;
    public List<AccountEntry> Accounts { get; set; } = [];

    public override string ToString() => Port == DefaultPort ? $"{Name}  ({Host})" : $"{Name}  ({Host}:{Port})";
}

public sealed class AccountEntry
{
    public string Account { get; set; } = "";

    /// <summary>Encrypted with Windows DPAPI for the current user, base64; null = ask at every launch.</summary>
    public string? ProtectedPassword { get; set; }

    /// <summary>Optional: log straight in with this character instead of stopping at the character screen.</summary>
    public string? Character { get; set; }

    public Realm Realm { get; set; }

    public override string ToString() =>
        string.IsNullOrEmpty(Character) ? Account : $"{Account}  →  {Character} ({Realm})";
}

/// <summary>Realm numbers as connect.exe takes them for a direct character login.</summary>
public enum Realm
{
    None = 0,
    Albion = 1,
    Midgard = 2,
    Hibernia = 3,
}
