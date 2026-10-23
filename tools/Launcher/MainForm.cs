// SPDX-License-Identifier: GPL-3.0-or-later
//
// Main window, built in code: servers on the left, the selected server's accounts on the right,
// the client folder and the Check / Play buttons below.

using System.Diagnostics;
using DaocProtocol.Net;
using DaocProtocol.Session;

namespace EodLauncher;

internal sealed class MainForm : Form
{
    private static readonly string ConfigPath =
        Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "EodLauncher", "launcher.json");

    private readonly LauncherConfig _config = LauncherConfig.Load(ConfigPath);
    private readonly ListBox _servers = new() { Dock = DockStyle.Fill, IntegralHeight = false };
    private readonly ListBox _accounts = new() { Dock = DockStyle.Fill, IntegralHeight = false };
    private readonly TextBox _folder = new() { Dock = DockStyle.Fill };
    private readonly Label _status = new() { Dock = DockStyle.Fill, AutoEllipsis = true, TextAlign = ContentAlignment.MiddleLeft };
    private readonly Button _check = new() { Text = "Check server", AutoSize = true };
    private readonly Button _play = new() { Text = "Play", AutoSize = true, Font = new Font(DefaultFont, FontStyle.Bold) };

    public MainForm()
    {
        Text = "EoD Launcher";
        ClientSize = new Size(720, 420);
        MinimumSize = new Size(560, 340);

        var lists = new TableLayoutPanel { Dock = DockStyle.Fill, ColumnCount = 2, RowCount = 3 };
        lists.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 50));
        lists.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 50));
        lists.RowStyles.Add(new RowStyle(SizeType.AutoSize));
        lists.RowStyles.Add(new RowStyle(SizeType.Percent, 100));
        lists.RowStyles.Add(new RowStyle(SizeType.AutoSize));
        lists.Controls.Add(new Label { Text = "Servers", AutoSize = true }, 0, 0);
        lists.Controls.Add(new Label { Text = "Accounts", AutoSize = true }, 1, 0);
        lists.Controls.Add(_servers, 0, 1);
        lists.Controls.Add(_accounts, 1, 1);
        lists.Controls.Add(Buttons(("Add", AddServer), ("Edit", EditServer), ("Remove", RemoveServer)), 0, 2);
        lists.Controls.Add(Buttons(("Add", AddAccount), ("Edit", EditAccount), ("Remove", RemoveAccount)), 1, 2);

        var browse = new Button { Text = "Browse…", AutoSize = true };
        browse.Click += (_, _) => BrowseFolder();
        var folderRow = new TableLayoutPanel { Dock = DockStyle.Top, ColumnCount = 3, AutoSize = true };
        folderRow.ColumnStyles.Add(new ColumnStyle(SizeType.AutoSize));
        folderRow.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100));
        folderRow.ColumnStyles.Add(new ColumnStyle(SizeType.AutoSize));
        folderRow.Controls.Add(new Label { Text = "Client folder", AutoSize = true, Anchor = AnchorStyles.Left }, 0, 0);
        folderRow.Controls.Add(_folder, 1, 0);
        folderRow.Controls.Add(browse, 2, 0);

        var actionRow = new TableLayoutPanel { Dock = DockStyle.Top, ColumnCount = 3, AutoSize = true };
        actionRow.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100));
        actionRow.ColumnStyles.Add(new ColumnStyle(SizeType.AutoSize));
        actionRow.ColumnStyles.Add(new ColumnStyle(SizeType.AutoSize));
        actionRow.Controls.Add(_status, 0, 0);
        actionRow.Controls.Add(_check, 1, 0);
        actionRow.Controls.Add(_play, 2, 0);

        var root = new TableLayoutPanel { Dock = DockStyle.Fill, Padding = new Padding(8), RowCount = 3 };
        root.RowStyles.Add(new RowStyle(SizeType.Percent, 100));
        root.RowStyles.Add(new RowStyle(SizeType.AutoSize));
        root.RowStyles.Add(new RowStyle(SizeType.AutoSize));
        root.Controls.Add(lists, 0, 0);
        root.Controls.Add(folderRow, 0, 1);
        root.Controls.Add(actionRow, 0, 2);
        Controls.Add(root);
        AcceptButton = _play;

        _folder.Text = _config.ClientFolder;
        _folder.Leave += (_, _) => { _config.ClientFolder = _folder.Text.Trim(); Save(); };
        _servers.SelectedIndexChanged += (_, _) => ShowAccounts();
        _servers.DoubleClick += (_, _) => EditServer();
        _accounts.DoubleClick += (_, _) => EditAccount();
        _check.Click += async (_, _) => await CheckServerAsync();
        _play.Click += (_, _) => Play();

        ShowServers();
    }

    private ServerEntry? SelectedServer => _servers.SelectedItem as ServerEntry;
    private AccountEntry? SelectedAccount => _accounts.SelectedItem as AccountEntry;

    private static FlowLayoutPanel Buttons(params (string Text, Action OnClick)[] buttons)
    {
        var panel = new FlowLayoutPanel { AutoSize = true, Dock = DockStyle.Fill };
        foreach (var (text, onClick) in buttons)
        {
            var button = new Button { Text = text, AutoSize = true };
            button.Click += (_, _) => onClick();
            panel.Controls.Add(button);
        }
        return panel;
    }

    private void ShowServers(ServerEntry? select = null)
    {
        _servers.Items.Clear();
        foreach (ServerEntry server in _config.Servers)
            _servers.Items.Add(server);
        if (select is not null)
            _servers.SelectedItem = select;
        else if (_servers.Items.Count > 0)
            _servers.SelectedIndex = 0;
        ShowAccounts();
    }

    private void ShowAccounts(AccountEntry? select = null)
    {
        _accounts.Items.Clear();
        foreach (AccountEntry account in SelectedServer?.Accounts ?? [])
            _accounts.Items.Add(account);
        if (select is not null)
            _accounts.SelectedItem = select;
        else if (_accounts.Items.Count > 0)
            _accounts.SelectedIndex = 0;
    }

    private void AddServer()
    {
        var server = new ServerEntry();
        if (!Dialogs.EditServer(this, server))
            return;
        _config.Servers.Add(server);
        Save();
        ShowServers(server);
    }

    private void EditServer()
    {
        if (SelectedServer is not { } server || !Dialogs.EditServer(this, server))
            return;
        Save();
        ShowServers(server);
    }

    private void RemoveServer()
    {
        if (SelectedServer is not { } server || !Confirm($"Remove {server.Name} and its saved accounts?"))
            return;
        _config.Servers.Remove(server);
        Save();
        ShowServers();
    }

    private void AddAccount()
    {
        if (SelectedServer is not { } server)
        {
            SetStatus("Add a server first.");
            return;
        }
        var account = new AccountEntry();
        if (!Dialogs.EditAccount(this, account))
            return;
        server.Accounts.Add(account);
        Save();
        ShowAccounts(account);
    }

    private void EditAccount()
    {
        if (SelectedAccount is not { } account || !Dialogs.EditAccount(this, account))
            return;
        Save();
        ShowAccounts(account);
    }

    private void RemoveAccount()
    {
        if (SelectedServer is not { } server || SelectedAccount is not { } account || !Confirm($"Remove account {account.Account}?"))
            return;
        server.Accounts.Remove(account);
        Save();
        ShowAccounts();
    }

    private void BrowseFolder()
    {
        using var dialog = new FolderBrowserDialog { Description = "DAoC client folder (with connect.exe)", InitialDirectory = _folder.Text };
        if (dialog.ShowDialog(this) != DialogResult.OK)
            return;
        _folder.Text = dialog.SelectedPath;
        _config.ClientFolder = dialog.SelectedPath;
        Save();
        SetStatus(ClientInstall.Find(dialog.SelectedPath, out string problem) is { } install
            ? $"Found connect.exe and {install.GameDll}."
            : problem);
    }

    /// <summary>Sends the 1.127 version announcement and reports whether the server accepts it.</summary>
    private async Task CheckServerAsync()
    {
        if (SelectedServer is not { } server)
            return;
        _check.Enabled = false;
        SetStatus($"Checking {server.Host}:{server.Port}…");
        try
        {
            await using var connection = new DaocConnection();
            using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(10));
            await connection.ConnectAsync(server.Host, server.Port, timeout.Token);
            var reply = await new ClientSession(connection).HandshakeAsync(timeout.Token);
            SetStatus($"{server.Name} is up and accepts the 1.127 client (it answered as {reply.Version}).");
        }
        catch (IOException)
        {
            SetStatus($"{server.Name} closed the connection: it doesn't accept the 1.127 client.");
        }
        catch (Exception e) when (e is System.Net.Sockets.SocketException or OperationCanceledException or TimeoutException)
        {
            SetStatus($"Can't reach {server.Host}:{server.Port} ({e.Message}).");
        }
        catch (NotSupportedException e)
        {
            SetStatus($"{server.Name} answered, but not as an OpenDAoC-style server: {e.Message}");
        }
        finally
        {
            _check.Enabled = true;
        }
    }

    private void Play()
    {
        _config.ClientFolder = _folder.Text.Trim();
        Save();
        if (SelectedServer is not { } server || SelectedAccount is not { } account)
        {
            SetStatus("Choose a server and an account.");
            return;
        }
        if (ClientInstall.Find(_config.ClientFolder, out string problem) is not { } install)
        {
            SetStatus(problem);
            return;
        }

        string? password = Passwords.Unprotect(account.ProtectedPassword) ?? Dialogs.AskPassword(this, account.Account, server.Name);
        if (password is null)
            return;

        try
        {
            var start = new ProcessStartInfo(install.ConnectExe) { WorkingDirectory = install.Folder, UseShellExecute = false };
            foreach (string arg in install.Arguments(server, account.Account, password, account.Character, account.Realm))
                start.ArgumentList.Add(arg);
            Process.Start(start)?.Dispose();
            SetStatus($"Started {install.GameDll} for {account.Account} on {server.Name}.");
        }
        catch (ArgumentException e)
        {
            SetStatus(e.Message);
        }
        catch (System.ComponentModel.Win32Exception e)
        {
            SetStatus($"Couldn't start connect.exe: {e.Message}");
        }
    }

    private bool Confirm(string question) =>
        MessageBox.Show(this, question, Text, MessageBoxButtons.YesNo, MessageBoxIcon.Question) == DialogResult.Yes;

    private void SetStatus(string text) => _status.Text = text;

    private void Save()
    {
        try
        {
            _config.Save(ConfigPath);
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException)
        {
            SetStatus($"Couldn't save settings: {e.Message}");
        }
    }
}
