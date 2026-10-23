// SPDX-License-Identifier: GPL-3.0-or-later
//
// EoD Launcher: keeps a list of private servers and accounts and starts a DAoC 1.127 client
// (OpenDAoC's client install: connect.exe + game DLL) against the one you pick. Windows only.

namespace EodLauncher;

internal static class Program
{
    [STAThread]
    private static void Main()
    {
        ApplicationConfiguration.Initialize();
        Application.Run(new MainForm());
    }
}
