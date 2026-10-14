// SPDX-License-Identifier: GPL-3.0-or-later

namespace DaocProtocol.Net;

/// <summary>
/// Fills in the client→server header fields the way a real 1.127e client does: the sequence
/// starts at 0 and goes up by one per packet, and the session id is 0 until the server assigns
/// one (0x28). The packet id's high byte is sent as 0; the server ignores it.
/// </summary>
public sealed class ClientPacketSequencer
{
    private ushort _nextSequence;

    public ushort SessionId { get; set; }

    public ClientPacket Next(byte code, byte[] payload, ushort parameter = 0) =>
        new(_nextSequence++, SessionId, parameter, code, payload);
}
