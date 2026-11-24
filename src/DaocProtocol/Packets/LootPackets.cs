// SPDX-License-Identifier: GPL-3.0-or-later
//
// Loot (roadmap M13). Layouts as the retail 1.130 client writes them (daoc-re RE/99, 2026-11-24), checked against what
// OpenDAoC-Core @f0aaa1e reads (PlayerPickUpRequestHandler, DestroyItemRequestHandler; GPL-3.0, field order only).

using DaocProtocol.Net;

namespace DaocProtocol.Packets;

/// <summary>
/// C→S 0xB5 PickUpRequest: one byte, whatever retail's shared send buffer last held (no meaning; we send 0). The server
/// picks up the character's current target. OpenDAoC's handler reads x, y and two ids past the packet's end, but uses
/// only the target.
/// </summary>
public static class PickUpRequest
{
    public const byte Code = 0xB5;

    public static byte[] Payload() => [0];
}

/// <summary>
/// C→S 0x80 DestroyItemRequest: 8 bytes of u16 BE: the session id, 2 bytes retail leaves unwritten (we send 0), the
/// inventory slot (server numbering: weapons 10–13, worn 21–36, backpack 40–79), 2 more unwritten. OpenDAoC skips 4 and
/// reads the slot; it refuses an equipped item ("You cannot destroy an equipped item!").
/// </summary>
public static class DestroyItemRequest
{
    public const byte Code = 0x80;

    public static byte[] Payload(ushort sessionId, ushort slot) =>
        new PayloadWriter().U16BE(sessionId).U16BE(0).U16BE(slot).U16BE(0).ToArray();

    /// <summary>The slots retail can send: weapons, worn items and the backpack.</summary>
    public static bool Destroyable(int slot) => slot is >= 10 and <= 13 or >= 21 and <= 36 or >= 40 and <= 79;
}
