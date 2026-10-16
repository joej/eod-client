// SPDX-License-Identifier: GPL-3.0-or-later
//
// Builds a valid MPK archive in memory from (name, bytes) members, so the reader can be tested
// without any proprietary client data. Writing the format here also double-checks our reading
// of it: a round-trip through build → read must return the original members.

using System.Buffers.Binary;
using System.IO.Compression;
using System.IO.Hashing;

namespace DaocAssets.Tests;

internal static class MpkBuilder
{
    private const int RecordSize = 284;

    public static byte[] Build(params (string Name, byte[] Data)[] members)
    {
        // Compress each member; lay out the directory with running offsets.
        var compressed = new byte[members.Length][];
        var dir = new byte[members.Length * RecordSize];
        int runningOffset = 0;
        for (int i = 0; i < members.Length; i++)
        {
            byte[] comp = Deflate(members[i].Data);
            compressed[i] = comp;
            Span<byte> rec = dir.AsSpan(i * RecordSize, RecordSize);
            byte[] name = System.Text.Encoding.UTF8.GetBytes(members[i].Name);
            name.CopyTo(rec); // rest stays zero (NUL-terminated)
            Span<byte> f = rec.Slice(256);
            BinaryPrimitives.WriteUInt32LittleEndian(f, 0);                    // timestamp
            BinaryPrimitives.WriteUInt32LittleEndian(f.Slice(4), 4);           // constant 4
            BinaryPrimitives.WriteUInt32LittleEndian(f.Slice(8), 0);           // decompressed offset (unused)
            BinaryPrimitives.WriteUInt32LittleEndian(f.Slice(12), (uint)members[i].Data.Length);
            BinaryPrimitives.WriteUInt32LittleEndian(f.Slice(16), (uint)runningOffset);
            BinaryPrimitives.WriteUInt32LittleEndian(f.Slice(20), (uint)comp.Length);
            BinaryPrimitives.WriteUInt32LittleEndian(f.Slice(24), Crc32.HashToUInt32(comp));
            runningOffset += comp.Length;
        }

        byte[] nameComp = Deflate(System.Text.Encoding.UTF8.GetBytes("test.mpk"));
        byte[] dirComp = Deflate(dir);

        var ms = new MemoryStream();
        ms.Write("MPAK"u8);
        ms.WriteByte(0x02);
        Span<byte> hdr = stackalloc byte[16];
        BinaryPrimitives.WriteUInt32LittleEndian(hdr, Crc32.HashToUInt32(dirComp));
        BinaryPrimitives.WriteUInt32LittleEndian(hdr.Slice(4), (uint)dirComp.Length);
        BinaryPrimitives.WriteUInt32LittleEndian(hdr.Slice(8), (uint)nameComp.Length);
        BinaryPrimitives.WriteUInt32LittleEndian(hdr.Slice(12), (uint)members.Length);
        for (int i = 0; i < 16; i++) // obfuscate: XOR each byte with its index
            ms.WriteByte((byte)(hdr[i] ^ i));
        ms.Write(nameComp);
        ms.Write(dirComp);
        foreach (byte[] c in compressed)
            ms.Write(c);
        return ms.ToArray();
    }

    private static byte[] Deflate(byte[] data)
    {
        var ms = new MemoryStream();
        using (var zlib = new ZLibStream(ms, CompressionLevel.Optimal, leaveOpen: true))
            zlib.Write(data);
        return ms.ToArray();
    }
}
