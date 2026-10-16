// SPDX-License-Identifier: GPL-3.0-or-later
//
// Reader for DAoC "MPAK" client archives (.mpk / .npk): the container the game packs meshes,
// textures, zone data and UI into.
//
// Format reimplemented from public descriptions — DOLSharp (DOLBase/MPK; the field names
// _offset/_directoryOffset/_uncompressedSize/_unk come from there, GPL-2.0-or-later) and
// RustDAoC's caer-assets module spec (GPL-3.0) — and verified field-for-field against real
// OpenDAoC 1.127 client archives (1, 16 and 221 members; directory and per-member CRC-32 over
// the compressed bytes both confirmed). No third-party source was copied; only the on-disk
// layout is shared, and that is not copyrightable.
//
// Layout: "MPAK", one byte (always 0x02), then a 16-byte header whose bytes are each XORed with
// their index. De-obfuscated, the header is four little-endian uint32: CRC-32 of the compressed
// directory, compressed directory size, compressed name size, member count. Then three regions
// of concatenated zlib streams: the archive's own name, the directory (member-count × 284-byte
// records), and one stream per member in directory order. Each record is name[256] + timestamp +
// unk(=4) + decompressed-offset(unused) + uncompressed size + compressed offset + compressed size
// + CRC-32 of the compressed bytes, all uint32.
//
// Only the header and directory are read when the archive is opened; a member's bytes are read
// and inflated on demand, so pulling one file out of a large archive doesn't decompress the rest.

using System.Buffers.Binary;
using System.IO.Compression;
using System.IO.Hashing;
using Microsoft.Win32.SafeHandles;

namespace DaocAssets.Mpk;

/// <summary>
/// An opened MPK archive. Holds the directory in memory and reads member data lazily.
/// Open with <see cref="Open(string)"/> for a file, or <see cref="Read(ReadOnlySpan{byte})"/>
/// for bytes already in memory. Dispose to release the underlying file handle.
/// </summary>
public sealed class MpkArchive : IDisposable
{
    private const uint Magic = 0x4B41504D; // "MPAK" little-endian
    private const int SkipByte = 0x02;      // the byte after the magic, always 2
    private const int ObfHeaderSize = 16;
    private const int RecordSize = 284;
    private const int NameFieldSize = 256;

    private readonly Dictionary<string, MpkEntry> _byName;
    private readonly MpkEntry[] _entries;

    // Exactly one of these is set. _handle for a file (lazy, seeking reads); _data for bytes
    // already in memory. _dataStart is where the file-data region begins in both cases.
    private readonly SafeFileHandleHolder? _file;
    private readonly byte[]? _data;
    private readonly long _dataStart;

    private MpkArchive(MpkEntry[] entries, long dataStart, SafeFileHandleHolder? file, byte[]? data)
    {
        _entries = entries;
        _dataStart = dataStart;
        _file = file;
        _data = data;
        _byName = new Dictionary<string, MpkEntry>(entries.Length, StringComparer.OrdinalIgnoreCase);
        foreach (MpkEntry e in entries)
        {
            if (!_byName.TryAdd(e.Name, e))
                throw new MpkFormatException($"Duplicate member name '{e.Name}'.");
        }
    }

    /// <summary>The archive's members, in directory order.</summary>
    public IReadOnlyList<MpkEntry> Entries => _entries;

    /// <summary>Looks up a member by name, case-insensitively (the client's own references
    /// disagree with the directory on case, e.g. asking <c>.dds</c> for a <c>.DDS</c> entry).</summary>
    public bool TryGetEntry(string name, out MpkEntry entry) => _byName.TryGetValue(name, out entry!);

    /// <summary>Opens an archive file, reading only its header and directory.</summary>
    public static MpkArchive Open(string path)
    {
        var handle = File.OpenHandle(path, FileMode.Open, FileAccess.Read, FileShare.Read);
        try
        {
            long length = RandomAccess.GetLength(handle);
            // Magic (4) + skip byte (1) + obfuscated 16-byte header, then the name and directory
            // blocks. We don't know the directory size until the header is decoded, so read the
            // fixed prefix first. The obfuscated block starts at offset 5.
            Span<byte> prefix = stackalloc byte[5 + ObfHeaderSize];
            ReadExact(handle, prefix, 0, "header");
            if (BinaryPrimitives.ReadUInt32LittleEndian(prefix) != Magic)
                throw new MpkFormatException("Not an MPK archive (bad magic).");
            if (prefix[4] != SkipByte)
                throw new MpkFormatException($"Unexpected byte after magic: 0x{prefix[4]:X2} (expected 0x02).");
            (uint dirCrc, int dirCSize, int nameCSize, int count) = DecodeHeader(prefix.Slice(5, ObfHeaderSize));

            long namePos = 5 + ObfHeaderSize;
            long dirPos = namePos + nameCSize;
            long dataStart = dirPos + dirCSize;
            if (dataStart > length)
                throw new MpkFormatException("Header describes more data than the file holds.");

            byte[] dirComp = new byte[dirCSize];
            ReadExact(handle, dirComp, dirPos, "directory");
            MpkEntry[] entries = ParseDirectory(dirComp, dirCrc, count, length - dataStart);

            var archive = new MpkArchive(entries, dataStart, new SafeFileHandleHolder(handle), data: null);
            handle = null!; // ownership transferred
            return archive;
        }
        finally
        {
            handle?.Dispose();
        }
    }

    /// <summary>Reads an archive already held in memory (copies the member region it needs).</summary>
    public static MpkArchive Read(ReadOnlySpan<byte> bytes)
    {
        if (bytes.Length < 5 + ObfHeaderSize)
            throw new MpkFormatException("Too short to be an MPK archive.");
        if (BinaryPrimitives.ReadUInt32LittleEndian(bytes) != Magic)
            throw new MpkFormatException("Not an MPK archive (bad magic).");
        if (bytes[4] != SkipByte)
            throw new MpkFormatException($"Unexpected byte after magic: 0x{bytes[4]:X2} (expected 0x02).");

        (uint dirCrc, int dirCSize, int nameCSize, int count) = DecodeHeader(bytes.Slice(5, ObfHeaderSize));
        int namePos = 5 + ObfHeaderSize;
        int dirPos = namePos + nameCSize;
        long dataStart = (long)dirPos + dirCSize;
        if (dataStart > bytes.Length)
            throw new MpkFormatException("Header describes more data than the buffer holds.");

        MpkEntry[] entries = ParseDirectory(bytes.Slice(dirPos, dirCSize), dirCrc, count, bytes.Length - dataStart);
        return new MpkArchive(entries, dataStart, file: null, data: bytes.ToArray());
    }

    /// <summary>Reads and decompresses one member, verifying its CRC and size.</summary>
    public byte[] Extract(MpkEntry entry)
    {
        ArgumentNullException.ThrowIfNull(entry);
        byte[] comp = new byte[entry.CompressedSize];
        long start = _dataStart + entry.CompressedOffset;
        if (_data is not null)
            _data.AsSpan((int)start, entry.CompressedSize).CopyTo(comp);
        else
            ReadExact(_file!.Handle, comp, start, entry.Name);

        if (Crc32.HashToUInt32(comp) != entry.Crc32)
            throw new MpkFormatException($"Member '{entry.Name}' failed its CRC check (corrupt archive).");

        byte[] outBuf = new byte[entry.UncompressedSize];
        using var zlib = new ZLibStream(new MemoryStream(comp, writable: false), CompressionMode.Decompress);
        int total = 0;
        while (total < outBuf.Length)
        {
            int n = zlib.Read(outBuf, total, outBuf.Length - total);
            if (n == 0)
                throw new MpkFormatException($"Member '{entry.Name}' ended early: got {total} of {outBuf.Length} bytes.");
            total += n;
        }
        // A trailing byte means the member is larger than declared — also a corrupt directory.
        if (zlib.ReadByte() != -1)
            throw new MpkFormatException($"Member '{entry.Name}' is longer than its declared size ({outBuf.Length}).");
        return outBuf;
    }

    /// <summary>Convenience: look up by name and extract, or throw if absent.</summary>
    public byte[] Extract(string name) =>
        TryGetEntry(name, out MpkEntry e) ? Extract(e) : throw new KeyNotFoundException($"No member '{name}' in archive.");

    // --- parsing helpers ---

    private static (uint DirCrc, int DirCSize, int NameCSize, int Count) DecodeHeader(ReadOnlySpan<byte> obf)
    {
        Span<byte> h = stackalloc byte[ObfHeaderSize];
        for (int i = 0; i < ObfHeaderSize; i++)
            h[i] = (byte)(obf[i] ^ i);
        uint dirCrc = BinaryPrimitives.ReadUInt32LittleEndian(h);
        int dirCSize = ReadSize(h.Slice(4), "compressed directory size");
        int nameCSize = ReadSize(h.Slice(8), "compressed name size");
        int count = ReadSize(h.Slice(12), "member count");
        return (dirCrc, dirCSize, nameCSize, count);
    }

    private static MpkEntry[] ParseDirectory(ReadOnlySpan<byte> dirComp, uint expectedCrc, int count, long dataRegionLen)
    {
        if (Crc32.HashToUInt32(dirComp) != expectedCrc)
            throw new MpkFormatException("Directory failed its CRC check (corrupt archive).");

        long declared = (long)count * RecordSize;
        if (declared > Array.MaxLength)
            throw new MpkFormatException($"Member count {count} is implausibly large.");
        byte[] dir = Inflate(dirComp, (int)declared, "directory");

        var entries = new MpkEntry[count];
        for (int k = 0; k < count; k++)
        {
            ReadOnlySpan<byte> rec = dir.AsSpan(k * RecordSize, RecordSize);
            string name = ReadName(rec.Slice(0, NameFieldSize));
            ReadOnlySpan<byte> f = rec.Slice(NameFieldSize);
            uint ts = BinaryPrimitives.ReadUInt32LittleEndian(f);
            // f[4..8] is the constant 4; f[8..12] is the decompressed-offset, unused on read.
            int uSize = ReadSize(f.Slice(12), $"member '{name}' uncompressed size");
            int cOff = ReadSize(f.Slice(16), $"member '{name}' offset");
            int cSize = ReadSize(f.Slice(20), $"member '{name}' compressed size");
            uint crc = BinaryPrimitives.ReadUInt32LittleEndian(f.Slice(24));

            if (cOff + (long)cSize > dataRegionLen)
                throw new MpkFormatException($"Member '{name}' points past the end of the archive.");

            entries[k] = new MpkEntry
            {
                Name = name,
                Timestamp = ts,
                UncompressedSize = uSize,
                CompressedOffset = cOff,
                CompressedSize = cSize,
                Crc32 = crc,
            };
        }
        return entries;
    }

    private static string ReadName(ReadOnlySpan<byte> field)
    {
        int nul = field.IndexOf((byte)0);
        ReadOnlySpan<byte> bytes = nul >= 0 ? field.Slice(0, nul) : field;
        return System.Text.Encoding.UTF8.GetString(bytes);
    }

    private static byte[] Inflate(ReadOnlySpan<byte> comp, int expectedLen, string what)
    {
        byte[] outBuf = new byte[expectedLen];
        using var zlib = new ZLibStream(new MemoryStream(comp.ToArray(), writable: false), CompressionMode.Decompress);
        int total = 0;
        while (total < expectedLen)
        {
            int n = zlib.Read(outBuf, total, expectedLen - total);
            if (n == 0)
                throw new MpkFormatException($"{what} ended early: got {total} of {expectedLen} bytes.");
            total += n;
        }
        return outBuf;
    }

    private static int ReadSize(ReadOnlySpan<byte> src, string what)
    {
        uint v = BinaryPrimitives.ReadUInt32LittleEndian(src);
        if (v > int.MaxValue)
            throw new MpkFormatException($"{what} is too large ({v}).");
        return (int)v;
    }

    private static void ReadExact(SafeFileHandle handle, Span<byte> buf, long offset, string what)
    {
        int total = 0;
        while (total < buf.Length)
        {
            int n = RandomAccess.Read(handle, buf.Slice(total), offset + total);
            if (n == 0)
                throw new MpkFormatException($"Unexpected end of file reading {what}.");
            total += n;
        }
    }

    public void Dispose() => _file?.Dispose();

    // Thin holder so the archive owns the handle without exposing it.
    private sealed class SafeFileHandleHolder(SafeFileHandle handle) : IDisposable
    {
        public SafeFileHandle Handle { get; } = handle;
        public void Dispose() => Handle.Dispose();
    }
}
