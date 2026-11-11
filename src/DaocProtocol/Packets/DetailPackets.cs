// SPDX-License-Identifier: GPL-3.0-or-later
//
// Asking the server for the full description of something ("delve") and its answer as a text window. Layouts: OpenDAoC-Core
// @f0aaa1e (GPL-3.0), GameServer/packets/Client/168/DetailDisplayHandler.cs (0xD8) and PacketLib181.SendCustomTextWindow
// with PacketLib168.WriteCustomTextWindowData (0xC4); the request's 12 bytes match the one 0xD8 in the 1.127e capture.

using DaocProtocol.Net;

namespace DaocProtocol.Packets;

/// <summary>
/// C→S 0xD8 DetailRequest: u16 BE type, 2 bytes the server doesn't read, u32 BE id, u32 BE extra (12 bytes). The server
/// reads the type, then u32 (the padding and the id's high half) and u16 (the id's low half), so an id fits in 16 bits.
/// Types used here: 1 an inventory item (id = its slot), 100 / 101 / 102 repair, self-craft and salvage it (mini_info's
/// buttons). An item's description comes back as a 0xC4 <see cref="DetailWindow"/>.
/// </summary>
public static class DetailRequest
{
    public const byte Code = 0xD8;
    public const ushort InventoryItem = 1, Repair = 100, SelfCraft = 101, Salvage = 102;

    public static byte[] Payload(ushort type, uint id, uint extra = 0) =>
        new PayloadWriter().U16BE(type).U16BE(0).U32BE(id).U32BE(extra).ToArray();
}

/// <summary>
/// S→C 0xC4 DetailWindow, the text window: u8 mode (0 a description, 1 the statistics and titles window), u8 more (not 0:
/// the next 0xC4 continues this window), u8-length caption, then (u8 line number, u8-length text) pairs until a 0 byte.
/// The client appends the lines in order and ignores their numbers.
/// </summary>
public sealed record DetailWindow(byte Mode, byte More, string Caption, IReadOnlyList<string> Lines)
{
    public const byte Code = 0xC4;

    public static DetailWindow Parse(ReadOnlySpan<byte> payload)
    {
        var r = new PayloadReader(payload);
        byte mode = r.U8(), more = r.U8();
        string caption = r.StringU8();
        var lines = new List<string>();
        while (r.Remaining > 0)
        {
            if (r.U8() == 0)
                break;
            lines.Add(r.StringU8());
        }
        return new DetailWindow(mode, more, caption, lines);
    }

    public byte[] ToBytes()
    {
        var w = new PayloadWriter().U8(Mode).U8(More).StringU8(Caption);
        for (int i = 0; i < Lines.Count; i++)
            w.U8((byte)(i + 1)).StringU8(Lines[i]);
        return w.U8(0).ToArray();
    }
}
