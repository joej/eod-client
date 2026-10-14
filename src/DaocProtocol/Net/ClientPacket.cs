// SPDX-License-Identifier: GPL-3.0-or-later
//
// TryRead() follows the server's own parser: OpenDAoC-Core 2026.09.36 (f0aaa1e),
// GameServer/GameClient.cs ProcessInboundTcpBytes and
// GameServer/packets/Client/GSPacketIn.cs Load. Copyright (C) Dawn of Light and OpenDAoC
// contributors; GPL-2.0-or-later (DOL header), GPL-3.0 (OpenDAoC).

using System.Buffers.Binary;

namespace DaocProtocol.Net;

/// <summary>
/// A client→server TCP packet. All header fields are big-endian:
/// <code>
/// [u16 payload size][u16 sequence][u16 session id][u16 parameter][u16 packet id]
/// [payload: size bytes][u16 checksum over everything before it]
/// </code>
/// The server reads only the low byte of the packet id as the packet code. Real 1.127 clients
/// still send other values in the high byte, so it is kept for byte-exact round trips.
/// </summary>
public sealed record ClientPacket(ushort Sequence, ushort SessionId, ushort Parameter, ushort PacketId, byte[] Payload)
{
    public const int HeaderSize = 10;

    /// <summary>Header plus checksum: bytes on the wire beyond the payload (GSPacketIn.HDR_SIZE).</summary>
    public const int Overhead = HeaderSize + Checksum.Size;

    public byte Code => (byte)PacketId;

    public byte[] ToBytes()
    {
        if (Payload.Length > ushort.MaxValue)
            throw new InvalidOperationException($"Payload of {Payload.Length} bytes does not fit the 16-bit size field.");

        var bytes = new byte[Payload.Length + Overhead];
        Span<byte> span = bytes;
        BinaryPrimitives.WriteUInt16BigEndian(span[0..], (ushort)Payload.Length);
        BinaryPrimitives.WriteUInt16BigEndian(span[2..], Sequence);
        BinaryPrimitives.WriteUInt16BigEndian(span[4..], SessionId);
        BinaryPrimitives.WriteUInt16BigEndian(span[6..], Parameter);
        BinaryPrimitives.WriteUInt16BigEndian(span[8..], PacketId);
        Payload.CopyTo(span[HeaderSize..]);
        Checksum.Write(span);
        return bytes;
    }

    /// <summary>
    /// Reads one packet from the start of <paramref name="buffer"/>. <paramref name="consumed"/>
    /// is the packet's length for <see cref="FrameResult.Complete"/> and
    /// <see cref="FrameResult.BadChecksum"/>, and 0 for <see cref="FrameResult.Incomplete"/>.
    /// </summary>
    public static FrameResult TryRead(ReadOnlySpan<byte> buffer, out ClientPacket? packet, out int consumed)
    {
        packet = null;
        consumed = 0;

        // Like the server, wait for a whole header + checksum before reading the size.
        if (buffer.Length < Overhead)
            return FrameResult.Incomplete;

        int length = BinaryPrimitives.ReadUInt16BigEndian(buffer) + Overhead;
        if (buffer.Length < length)
            return FrameResult.Incomplete;

        ReadOnlySpan<byte> raw = buffer[..length];
        consumed = length;
        if (!Checksum.Verify(raw))
            return FrameResult.BadChecksum;

        packet = new ClientPacket(
            Sequence: BinaryPrimitives.ReadUInt16BigEndian(raw[2..]),
            SessionId: BinaryPrimitives.ReadUInt16BigEndian(raw[4..]),
            Parameter: BinaryPrimitives.ReadUInt16BigEndian(raw[6..]),
            PacketId: BinaryPrimitives.ReadUInt16BigEndian(raw[8..]),
            Payload: raw[HeaderSize..^Checksum.Size].ToArray());
        return FrameResult.Complete;
    }
}
