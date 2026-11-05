// SPDX-License-Identifier: GPL-3.0-or-later
//
// CharacterScreen: Saved logins (LoginProfiles): the profile picker, the form it fills, and start-up actions.

using DaocProtocol.Net;
using DaocProtocol.Packets;
using DaocProtocol.Session;
using Godot;

namespace EodClient;

public partial class CharacterScreen
{
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
}
