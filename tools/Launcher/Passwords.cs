// SPDX-License-Identifier: GPL-3.0-or-later
//
// Saved passwords are encrypted with Windows DPAPI for the current user: only this Windows account
// on this machine can read them back.

using System.Security.Cryptography;
using System.Text;

namespace EodLauncher;

internal static class Passwords
{
    private static readonly byte[] Entropy = Encoding.UTF8.GetBytes("EodLauncher password v1");

    public static string Protect(string password) =>
        Convert.ToBase64String(ProtectedData.Protect(Encoding.UTF8.GetBytes(password), Entropy, DataProtectionScope.CurrentUser));

    public static string? Unprotect(string? protectedPassword)
    {
        if (string.IsNullOrEmpty(protectedPassword))
            return null;
        try
        {
            return Encoding.UTF8.GetString(ProtectedData.Unprotect(Convert.FromBase64String(protectedPassword), Entropy, DataProtectionScope.CurrentUser));
        }
        catch (Exception e) when (e is CryptographicException or FormatException)
        {
            return null; // saved by another Windows user or machine: ask again
        }
    }
}
