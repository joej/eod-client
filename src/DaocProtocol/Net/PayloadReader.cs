// SPDX-License-Identifier: GPL-3.0-or-later

using System.Buffers.Binary;
using System.Text;

namespace DaocProtocol.Net;

/// <summary>
/// Sequential reads from a packet payload. DAoC mixes byte orders, so every multi-byte read
/// names its endianness. Reading past the end throws <see cref="InvalidDataException"/>.
/// </summary>
public ref struct PayloadReader(ReadOnlySpan<byte> payload)
{
    private readonly ReadOnlySpan<byte> _payload = payload;
    private int _pos;

    public readonly int Position => _pos;
    public readonly int Remaining => _payload.Length - _pos;

    public byte U8() => Take(1)[0];

    public ushort U16LE() => BinaryPrimitives.ReadUInt16LittleEndian(Take(2));

    public ushort U16BE() => BinaryPrimitives.ReadUInt16BigEndian(Take(2));

    public uint U32LE() => BinaryPrimitives.ReadUInt32LittleEndian(Take(4));

    public void Skip(int count) => Take(count);

    /// <summary>
    /// A string written by the server's PacketOut.WritePascalStringIntLE: a u32 little-endian
    /// length counting a terminating zero, the bytes, then the zero. An empty string is length 0.
    /// </summary>
    public string StringIntLE()
    {
        int length = checked((int)U32LE());
        if (length == 0)
            return string.Empty;

        ReadOnlySpan<byte> bytes = Take(length);
        int end = bytes.IndexOf((byte)0);
        return Encoding.Latin1.GetString(end < 0 ? bytes : bytes[..end]);
    }

    private ReadOnlySpan<byte> Take(int count)
    {
        if (count < 0 || count > Remaining)
            throw new InvalidDataException($"Payload too short: need {count} bytes at offset {_pos}, {Remaining} left.");

        ReadOnlySpan<byte> slice = _payload.Slice(_pos, count);
        _pos += count;
        return slice;
    }
}
