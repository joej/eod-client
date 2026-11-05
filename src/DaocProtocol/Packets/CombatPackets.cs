// SPDX-License-Identifier: GPL-3.0-or-later
//
// Attack mode: drawing weapons and putting them away. Layouts ported (field order only) from OpenDAoC-Core,
// GPL-3.0: PlayerAttackRequestHandler (C→S 0x74) and PacketLib168.SendAttackMode (S→C 0x74). The server
// decides: it answers a request with its own attack state, and also ends attack mode itself (target dead or
// gone, a ranged weapon readied). Others see it as bit 0x80 of the health byte in that player's 0xA9 / 0xBA
// (OpenDAoC PlayerPositionUpdateHandler.GetHealthByte).

using DaocProtocol.Net;

namespace DaocProtocol.Packets;

/// <summary>C→S 0x74: enter or leave attack mode.</summary>
public static class AttackRequest
{
    public const byte Code = 0x74;

    /// <param name="start">True to draw weapons and attack, false to stop.</param>
    /// <param name="byPlayer">True when the player asked (the key); false when the client itself stops
    /// attacking (OpenDAoC's second byte: 0 = the player, 1 = the client). The server only starts an attack
    /// the player asked for.</param>
    public static byte[] Payload(bool start, bool byPlayer = true) =>
        new PayloadWriter().U8(start ? (byte)1 : (byte)0).U8(byPlayer ? (byte)0 : (byte)1).ToArray();
}

/// <summary>
/// C→S 0xC7 PlayerSitRequest (OpenDAoC PlayerSitRequestHandler): one byte, non-zero to sit, 0 to stand.
/// Standing up is also how a quit is cancelled: /quit sits the character down, and the server stops
/// the quit timer when it stands (GamePlayer.Sit). Moving alone does not stand it up on the server.
/// </summary>
public static class SitRequest
{
    public const byte Code = 0xC7;

    public static byte[] Payload(bool sit) => [sit ? (byte)1 : (byte)0];
}

/// <summary>S→C 0x74: the character's attack mode, as the server now has it (weapons drawn or not).</summary>
public static class AttackMode
{
    public const byte Code = 0x74;

    public static bool IsOn(ReadOnlySpan<byte> payload) => new PayloadReader(payload).U8() != 0;
}
