// SPDX-License-Identifier: GPL-3.0-or-later
//
// Small modal dialogs: edit a server, edit an account, ask for a password.

namespace EodLauncher;

internal static class Dialogs
{
    public static bool EditServer(IWin32Window owner, ServerEntry server)
    {
        var name = new TextBox { Text = server.Name };
        var host = new TextBox { Text = server.Host };
        var port = new NumericUpDown { Minimum = 1, Maximum = 65535, Value = server.Port };

        bool ok = Show(owner, server.Name.Length == 0 ? "Add server" : "Edit server",
            [("Name", name), ("Address", host), ("Port", port)],
            () => name.Text.Trim().Length > 0 && host.Text.Trim().Length > 0 ? null : "Enter a name and an address.");
        if (ok)
        {
            server.Name = name.Text.Trim();
            server.Host = host.Text.Trim();
            server.Port = (int)port.Value;
        }
        return ok;
    }

    public static bool EditAccount(IWin32Window owner, AccountEntry account)
    {
        var name = new TextBox { Text = account.Account };
        var password = new TextBox { UseSystemPasswordChar = true, PlaceholderText = account.ProtectedPassword is null ? "" : "(saved; leave empty to keep)" };
        var remember = new CheckBox { Text = "Remember password", Checked = account.ProtectedPassword is not null, AutoSize = true };
        var character = new TextBox { Text = account.Character ?? "", PlaceholderText = "(optional: log straight in)" };
        var realm = new ComboBox { DropDownStyle = ComboBoxStyle.DropDownList, DataSource = Enum.GetValues<Realm>() };
        realm.HandleCreated += (_, _) => realm.SelectedItem = account.Realm;

        bool ok = Show(owner, account.Account.Length == 0 ? "Add account" : "Edit account",
            [("Account", name), ("Password", password), ("", remember), ("Character", character), ("Realm", realm)],
            () =>
            {
                if (name.Text.Trim().Length == 0)
                    return "Enter the account name.";
                if (remember.Checked && password.Text.Length == 0 && account.ProtectedPassword is null)
                    return "Enter the password to remember, or untick Remember password.";
                if (character.Text.Trim().Length > 0 && (Realm)realm.SelectedItem! == Realm.None)
                    return "Choose the character's realm.";
                return null;
            });
        if (ok)
        {
            account.Account = name.Text.Trim();
            if (!remember.Checked)
                account.ProtectedPassword = null;
            else if (password.Text.Length > 0)
                account.ProtectedPassword = Passwords.Protect(password.Text);
            account.Character = character.Text.Trim().Length > 0 ? character.Text.Trim() : null;
            account.Realm = account.Character is null ? Realm.None : (Realm)realm.SelectedItem!;
        }
        return ok;
    }

    public static string? AskPassword(IWin32Window owner, string account, string server)
    {
        var password = new TextBox { UseSystemPasswordChar = true };
        return Show(owner, $"Password for {account} on {server}", [("Password", password)],
            () => password.Text.Length > 0 ? null : "Enter the password.")
            ? password.Text
            : null;
    }

    /// <summary>A form with one labelled row per control and OK/Cancel. <paramref name="validate"/> returns an error or null.</summary>
    private static bool Show(IWin32Window owner, string title, (string Label, Control Control)[] rows, Func<string?> validate)
    {
        using var form = new Form
        {
            Text = title,
            FormBorderStyle = FormBorderStyle.FixedDialog,
            StartPosition = FormStartPosition.CenterParent,
            MinimizeBox = false,
            MaximizeBox = false,
            AutoSize = true,
            AutoSizeMode = AutoSizeMode.GrowAndShrink,
            Padding = new Padding(8),
        };
        var table = new TableLayoutPanel { ColumnCount = 2, AutoSize = true, Dock = DockStyle.Fill };
        foreach (var (label, control) in rows)
        {
            control.Width = 260;
            table.Controls.Add(new Label { Text = label, AutoSize = true, Anchor = AnchorStyles.Left });
            table.Controls.Add(control);
        }

        var error = new Label { AutoSize = true, ForeColor = Color.Firebrick };
        var ok = new Button { Text = "OK", AutoSize = true };
        var cancel = new Button { Text = "Cancel", AutoSize = true, DialogResult = DialogResult.Cancel };
        ok.Click += (_, _) =>
        {
            string? problem = validate();
            error.Text = problem ?? "";
            if (problem is null)
                form.DialogResult = DialogResult.OK;
        };
        var buttons = new FlowLayoutPanel { FlowDirection = FlowDirection.RightToLeft, AutoSize = true, Dock = DockStyle.Fill };
        buttons.Controls.AddRange([cancel, ok]);
        table.Controls.Add(error);
        table.SetColumnSpan(error, 2);
        table.Controls.Add(buttons);
        table.SetColumnSpan(buttons, 2);

        form.Controls.Add(table);
        form.AcceptButton = ok;
        form.CancelButton = cancel;
        return form.ShowDialog(owner) == DialogResult.OK;
    }
}
