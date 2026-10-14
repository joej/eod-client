// SPDX-License-Identifier: GPL-3.0-or-later
//
// Client 1.127 login exchange. Layouts follow OpenDAoC-Core 2026.09.36 (f0aaa1e):
// CryptKeyRequestHandler, LoginRequestHandler (1.126+ branch), PacketLib1126.SendVersionAndCryptKey,
// PacketLib1127.SendLoginGranted, PacketLib168.SendLoginDenied, eLoginError. Client payloads are
// checked byte for byte against a recorded 1.127e session in the tests.

using System.Buffers.Binary;
using System.Text;
using DaocProtocol.Net;

namespace DaocProtocol.Packets;

/// <summary>0xF4: the first packet. Announces the client version; the server answers with 0x22.</summary>
public static class CryptKeyRequest
{
    public const byte Code = 0xF4;

    public static byte[] Payload(ClientVersion version) =>
    [
        version.ClientType, version.Major, version.Minor, version.Patch,
        checked((byte)version.Revision), version.BuildHigh, version.BuildLow,
    ];
}

/// <summary>0xA7: account name and password, sent in plain text (the server only hashes them for storage).</summary>
public static class LoginRequest
{
    public const byte Code = 0xA7;

    /// <summary>
    /// 1.126+ layout: username, then password, each as a u32 little-endian length followed by the
    /// bytes. Like the recorded 1.127e client, the length includes a terminating zero byte (the
    /// server stops reading a string at the first zero). <paramref name="trailer"/> is appended
    /// as-is: the recorded client sends 13 more bytes (<c>2a 08 00 00 00</c> + 8 bytes) that the
    /// server does not read.
    /// </summary>
    public static byte[] Payload(string username, string password, ReadOnlySpan<byte> trailer = default) =>
        new PayloadWriter().StringIntLE(username).StringIntLE(password).Bytes(trailer).ToArray();
}

/// <summary>0x22: the server's answer to 0xF4.</summary>
/// <param name="KeyLength">0 means the server does not enable encryption (OpenDAoC never does).</param>
public sealed record CryptKeyReply(uint KeyLength, string Version, byte Revision, byte BuildHigh, byte BuildLow)
{
    public const byte Code = 0x22;

    public static CryptKeyReply Parse(ReadOnlySpan<byte> payload)
    {
        uint keyLength = BinaryPrimitives.ReadUInt32LittleEndian(payload);
        if (keyLength != 0)
            throw new NotSupportedException($"Server sent a {keyLength}-byte encryption key; encrypted sessions are not supported.");

        ReadOnlySpan<byte> rest = payload[4..];
        string version = FixedString(rest[..5]);
        return new CryptKeyReply(keyLength, version, rest[5], rest[6], rest[7]);
    }

    private static string FixedString(ReadOnlySpan<byte> field)
    {
        int end = field.IndexOf((byte)0);
        return Encoding.ASCII.GetString(end < 0 ? field : field[..end]);
    }
}

/// <summary>0x2A: login accepted.</summary>
/// <param name="ServerType">Server ruleset colour: 0 normal, 1 Mordred, 3 Gaheris, 7 Ywain (all realms).</param>
public sealed record LoginGranted(string AccountName, string ServerNameShort, byte ServerId, byte ServerType, bool Trial)
{
    public const byte Code = 0x2A;

    public static LoginGranted Parse(ReadOnlySpan<byte> payload)
    {
        int pos = 0;
        string account = PascalString(payload, ref pos);
        string server = PascalString(payload, ref pos);
        return new LoginGranted(account, server, payload[pos], payload[pos + 1], payload[pos + 2] != 0);
    }

    // u8 length + bytes (PacketOut.WritePascalString)
    private static string PascalString(ReadOnlySpan<byte> payload, ref int pos)
    {
        int length = payload[pos];
        string value = Encoding.Latin1.GetString(payload.Slice(pos + 1, length));
        pos += 1 + length;
        return value;
    }
}

/// <summary>0x2C: login refused. Only the first byte, the reason, is decoded.</summary>
public sealed record LoginDenied(LoginError Error)
{
    public const byte Code = 0x2C;

    public static LoginDenied Parse(ReadOnlySpan<byte> payload) => new((LoginError)payload[0]);
}

/// <summary>Reasons in 0x2C, from OpenDAoC's eLoginError (client 1.127 messages).</summary>
public enum LoginError : byte
{
    IncorrectPassword = 0x01,
    InvalidAccount = 0x02,
    AuthorizationServerUnavailable = 0x03,
    IncorrectClientVersion = 0x05,
    Error = 0x06,
    AccountNotFound = 0x07,
    AccountNoAccessAnyGame = 0x08,
    AccountNoAccessThisGame = 0x09,
    ClosedAccount = 0x0a,
    AccountAlreadyLoggedIn = 0x0b,
    TooManyPlayersLoggedIn = 0x0c,
    GameCurrentlyClosed = 0x0d,
    AccountAlreadyLoggedInToOtherServer = 0x10,
    AccountIsInLogoutProcedure = 0x11,
    TooManyFreeAccountsLoggedIn = 0x12,
    AccountNeedsInvite = 0x13,
    BannedAccount = 0x14,
    Unknown = 0x15,
    NotAuthorizedToUseExpansionVersion = 0x17,
    OnlyOneFreeAccountPerPc = 0x18,
    RequestedServerNotFound = 0x19,
    EndlessConquestAccountNotAllowed = 0x1a,
    ServiceNotAvailable = 0xaa,
}
