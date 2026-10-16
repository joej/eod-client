// SPDX-License-Identifier: GPL-3.0-or-later
//
// One entry in an MPK directory. Format (284-byte fixed record) reimplemented from the
// public descriptions in DOLSharp (DOLBase/MPK, field names) and RustDAoC (caer-assets spec),
// and verified field-for-field against real OpenDAoC 1.127 client archives. No source copied.

namespace DaocAssets.Mpk;

/// <summary>
/// A single member of an <see cref="MpkArchive"/>: its name and where its compressed bytes sit.
/// </summary>
public sealed class MpkEntry
{
    /// <summary>Member name as stored in the directory (e.g. <c>bound.csv</c>), NUL-trimmed.</summary>
    public required string Name { get; init; }

    /// <summary>Directory timestamp field (seconds since the Unix epoch, as the client writes it).</summary>
    public uint Timestamp { get; init; }

    /// <summary>Length of the member once decompressed.</summary>
    public required int UncompressedSize { get; init; }

    /// <summary>Offset of the member's zlib stream, relative to the start of the file-data region.</summary>
    public required int CompressedOffset { get; init; }

    /// <summary>Length of the member's zlib stream.</summary>
    public required int CompressedSize { get; init; }

    /// <summary>CRC-32 of the member's <em>compressed</em> bytes (verified on read).</summary>
    public uint Crc32 { get; init; }

    public override string ToString() => $"{Name} ({UncompressedSize} bytes)";
}
