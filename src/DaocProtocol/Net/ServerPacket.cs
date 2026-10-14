// SPDX-License-Identifier: GPL-3.0-or-later
//
// Layout from OpenDAoC-Core 2026.09.36 (f0aaa1e) GSTCPPacketOut, which reserves a 2-byte
// size and writes the code after it. The size excludes the 3-byte header: checked against
// every server packet in a recorded 1.127e session (tests/DaocProtocol.Tests/Fixtures).

using System.Buffers.Binary;

namespace DaocProtocol.Net;

/// <summary>
/// A server→client TCP packet: <c>[u16 payload size, big-endian][u8 code][payload]</c>.
/// There is no checksum in this direction.
/// </summary>
public sealed record ServerPacket(byte Code, byte[] Payload)
{
    public const int HeaderSize = 3;

    public byte[] ToBytes()
    {
        if (Payload.Length > ushort.MaxValue)
            throw new InvalidOperationException($"Payload of {Payload.Length} bytes does not fit the 16-bit size field.");

        var bytes = new byte[HeaderSize + Payload.Length];
        BinaryPrimitives.WriteUInt16BigEndian(bytes, (ushort)Payload.Length);
        bytes[2] = Code;
        Payload.CopyTo(bytes, HeaderSize);
        return bytes;
    }

    /// <summary>
    /// Reads one packet from the start of <paramref name="buffer"/>. Never returns
    /// <see cref="FrameResult.BadChecksum"/>. <paramref name="consumed"/> is 0 when incomplete.
    /// </summary>
    public static FrameResult TryRead(ReadOnlySpan<byte> buffer, out ServerPacket? packet, out int consumed)
    {
        packet = null;
        consumed = 0;
        if (buffer.Length < HeaderSize)
            return FrameResult.Incomplete;

        int length = HeaderSize + BinaryPrimitives.ReadUInt16BigEndian(buffer);
        if (buffer.Length < length)
            return FrameResult.Incomplete;

        packet = new ServerPacket(buffer[2], buffer[HeaderSize..length].ToArray());
        consumed = length;
        return FrameResult.Complete;
    }
}
