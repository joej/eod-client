// SPDX-License-Identifier: GPL-3.0-or-later

namespace DaocProtocol.Net;

public enum FrameResult
{
    /// <summary>A whole packet was read.</summary>
    Complete,

    /// <summary>The buffer ends before the packet does; read more bytes and try again.</summary>
    Incomplete,

    /// <summary>A whole client→server packet was present, but its checksum is wrong. The server disconnects on this.</summary>
    BadChecksum,
}
