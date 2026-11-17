// SPDX-License-Identifier: GPL-3.0-or-later
//
// Doors (roadmap M11: dungeon doors). Layouts from OpenDAoC-Core (GPL-3.0, field order only): PacketLib168.SendDoorState
// (S→C 0x99) and DoorRequestHandler (C→S 0x99); meanings of the bytes as the retail client reads them from daoc-re's
// doors note (RE/80, 2026-11-11). Ints are big-endian, as the server writes and reads them (WriteInt, ReadInt).
// A door's id is the client's, from the model's node name (zone × 1,000,000 + fixture or room × 100 + n): the server
// keeps only a database row per known door, and OpenDAoC opens an unknown one through a stand-in (its "GM door hack"),
// so in a dungeon nearly every door works that way (2 of the database's 432 doors are in dungeons).

using DaocProtocol.Net;

namespace DaocProtocol.Packets;

/// <summary>
/// S→C 0x99 DoorState: a door opens or closes. Door id u32 BE; state (0 closed, 1 open; retail also reads 0xFF as
/// toggle); a sound row (the door sound table; OpenDAoC's flag, 1 by default outdoors, 0 in dungeons); a start point
/// (0xFF none: OpenDAoC always sends 0xFF); one unused byte.
/// </summary>
public sealed record DoorState(uint DoorId, byte State, byte Sound, byte Start)
{
    public const byte Code = 0x99;
    public const byte Closed = 0, Open = 1, Toggle = 0xFF;

    public bool IsOpen => State == Open;

    public static DoorState Parse(ReadOnlySpan<byte> payload)
    {
        var r = new PayloadReader(payload);
        return new DoorState(r.U32BE(), r.U8(), r.U8(), r.U8());
    }
}

/// <summary>
/// C→S 0x99 DoorRequest: open (1) or close (0) a door, by its id; retail sends 1 when you click a door (or the backpack
/// slot, 40–79, of an item used on it). Door id u32 BE, the request byte, three zero bytes. OpenDAoC opens on 1 and
/// closes on anything else.
/// </summary>
public static class DoorRequest
{
    public const byte Code = 0x99;

    public static byte[] Payload(uint doorId, bool open = true) =>
        new PayloadWriter().U32BE(doorId).U8(open ? (byte)1 : (byte)0).Zeros(3).ToArray();
}
