// SPDX-License-Identifier: GPL-3.0-or-later
//
// Crafting. Layout ported (field order only) from OpenDAoC-Core @f0aaa1e, GPL-3.0: MakeProductHandler
// (eClientPackets.CraftRequest, 0xED = 0x45 ^ 168), which reads one ReadShort (big-endian) and calls
// GamePlayer.CraftItem with it.

using DaocProtocol.Net;

namespace DaocProtocol.Packets;

/// <summary>
/// C→S 0xED CraftRequest: u16 BE recipe id (a row of the server's crafted-item table). The server answers in chat and,
/// when crafting starts, with a progress bar; an unknown id is only logged on the server.
/// </summary>
public static class CraftRequest
{
    public const byte Code = 0xED;

    public static byte[] Payload(ushort recipeId) => new PayloadWriter().U16BE(recipeId).ToArray();
}
