// SPDX-License-Identifier: GPL-3.0-or-later

namespace DaocAssets.Mpk;

/// <summary>Thrown when a file is not a valid MPK archive or is corrupt (bad magic, truncated,
/// a checksum mismatch, a member offset outside the archive, or a declared size the data
/// doesn't match).</summary>
public sealed class MpkFormatException(string message) : Exception(message);
