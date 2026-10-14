// SPDX-License-Identifier: GPL-3.0-or-later

namespace DaocProtocol.Packets;

/// <summary>
/// What the client announces in its first packet (0xF4). The server derives the protocol
/// version from <c>Major*1000 + Minor*100 + Patch</c> (GameClient.CheckVersion) and needs an
/// exact match with one of its packet libraries; the revision letter and build are not used to
/// select a format.
/// </summary>
/// <param name="ClientType">Low nibble: client type; high nibble: installed add-ons.</param>
public sealed record ClientVersion(byte ClientType, byte Major, byte Minor, byte Patch, char Revision, byte BuildHigh, byte BuildLow)
{
    /// <summary>The values a 1.127e client sent in the recorded session (<c>36 01 01 1b 65 7f 05</c>).</summary>
    public static ClientVersion V1127e { get; } = new(0x36, 1, 1, 27, 'e', 0x7F, 0x05);

    public int ProtocolVersion => Major * 1000 + Minor * 100 + Patch;

    public override string ToString() => $"{Major}.{Minor * 100 + Patch}{Revision}";
}
