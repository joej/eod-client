// SPDX-License-Identifier: GPL-3.0-or-later
//
// Calculate() is ported from OpenDAoC-Core 2026.09.36 (f0aaa1e),
// GameServer/packets/Server/PacketProcessor.cs, PacketProcessor.CalculateChecksum.
// Copyright (C) Dawn of Light and OpenDAoC contributors; GPL-2.0-or-later (DOL header),
// GPL-3.0 (OpenDAoC). Keep it identical to the server: the server disconnects on a mismatch.

using System.Buffers.Binary;

namespace DaocProtocol.Net;

/// <summary>
/// The 16-bit checksum that ends every client→server TCP packet (big-endian). It covers the
/// whole packet, header included, up to but not including the checksum itself.
/// </summary>
public static class Checksum
{
    public const int Size = 2;

    public static ushort Calculate(ReadOnlySpan<byte> data)
    {
        // Byte arithmetic on purpose: val1/val2 wrap at 256 on every add, exactly as on the
        // server. The final expression is evaluated as int before truncating to ushort.
        byte val1 = 0x7E;
        byte val2 = 0x7E;
        foreach (byte b in data)
        {
            val1 += b;
            val2 += val1;
        }

        return (ushort)(val2 - ((val1 + val2) << 8));
    }

    /// <summary>Fills the last <see cref="Size"/> bytes of <paramref name="packet"/> with the checksum of the rest.</summary>
    public static void Write(Span<byte> packet)
    {
        ushort sum = Calculate(packet[..^Size]);
        BinaryPrimitives.WriteUInt16BigEndian(packet[^Size..], sum);
    }

    /// <summary>True if the last <see cref="Size"/> bytes of <paramref name="packet"/> are the checksum of the rest.</summary>
    public static bool Verify(ReadOnlySpan<byte> packet) =>
        packet.Length >= Size &&
        Calculate(packet[..^Size]) == BinaryPrimitives.ReadUInt16BigEndian(packet[^Size..]);
}
