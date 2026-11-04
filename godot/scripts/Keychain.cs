// SPDX-License-Identifier: GPL-3.0-or-later
//
// Remembered passwords, in the macOS login Keychain through /usr/bin/security. The password goes
// to `security -i` on its standard input, never on a command line (where other processes could
// read it). Elsewhere nothing is remembered and the client asks.

using System.Diagnostics;

namespace EodClient;

public static class Keychain
{
    private const string Service = "EoD Client";
    private const string Tool = "/usr/bin/security";

    public static bool Available => OperatingSystem.IsMacOS() && File.Exists(Tool);

    /// <summary>The stored password for <paramref name="account"/>, or null.</summary>
    public static string? Get(string account)
    {
        if (!Available)
            return null;
        (int code, string output) = Run(["find-generic-password", "-s", Service, "-a", account, "-w"], null);
        return code == 0 ? output.TrimEnd('\n') : null;
    }

    /// <summary>Stores (or replaces) the password; false if it couldn't.</summary>
    public static bool Set(string account, string password)
    {
        if (!Available || password.Contains('\n') || password.Contains('\r'))
            return false;
        string command = $"add-generic-password -U -s {Quote(Service)} -a {Quote(account)} -w {Quote(password)}\n";
        return Run(["-i"], command).Code == 0 && Get(account) == password;
    }

    public static void Delete(string account)
    {
        if (Available)
            Run(["delete-generic-password", "-s", Service, "-a", account], null);
    }

    // security's interactive mode reads double-quoted words with backslash escapes.
    private static string Quote(string s) => "\"" + s.Replace("\\", "\\\\").Replace("\"", "\\\"") + "\"";

    private static (int Code, string Output) Run(string[] args, string? input)
    {
        var start = new ProcessStartInfo(Tool)
        {
            RedirectStandardInput = true, RedirectStandardOutput = true, RedirectStandardError = true,
            UseShellExecute = false,
        };
        foreach (string a in args)
            start.ArgumentList.Add(a);
        using Process process = Process.Start(start)!;
        if (input is not null)
            process.StandardInput.Write(input);
        process.StandardInput.Close();
        string output = process.StandardOutput.ReadToEnd();
        process.StandardError.ReadToEnd();
        process.WaitForExit();
        return (process.ExitCode, output);
    }
}
