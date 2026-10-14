// SPDX-License-Identifier: GPL-3.0-or-later

using System.Buffers.Binary;
using System.Text;

namespace DaocProtocol.Net;

/// <summary>
/// Builds a packet payload. The counterpart of <see cref="PayloadReader"/>: every multi-byte
/// write names its endianness.
/// </summary>
public sealed class PayloadWriter
{
    private readonly List<byte> _bytes = [];

    public PayloadWriter U8(byte value)
    {
        _bytes.Add(value);
        return this;
    }

    public PayloadWriter U16LE(ushort value)
    {
        Span<byte> buffer = stackalloc byte[2];
        BinaryPrimitives.WriteUInt16LittleEndian(buffer, value);
        return Bytes(buffer);
    }

    public PayloadWriter U32LE(uint value)
    {
        Span<byte> buffer = stackalloc byte[4];
        BinaryPrimitives.WriteUInt32LittleEndian(buffer, value);
        return Bytes(buffer);
    }

    public PayloadWriter Bytes(ReadOnlySpan<byte> value)
    {
        _bytes.AddRange(value.ToArray());
        return this;
    }

    public PayloadWriter Zeros(int count) => Bytes(new byte[count]);

    /// <summary>
    /// A u32 little-endian length counting a terminating zero, the Latin-1 bytes, then the zero
    /// (what the server reads with ReadIntPascalStringLowEndian). An empty string is written as
    /// length 1 plus the zero, as the 1.127 clients do.
    /// </summary>
    public PayloadWriter StringIntLE(string value)
    {
        if (value.Contains('\0'))
            throw new ArgumentException("Strings cannot contain a zero byte.", nameof(value));

        byte[] text = Encoding.Latin1.GetBytes(value);
        return U32LE((uint)text.Length + 1).Bytes(text).U8(0);
    }

    public byte[] ToArray() => [.. _bytes];
}
