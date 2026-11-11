// SPDX-License-Identifier: GPL-3.0-or-later
//
// S→C 0x7E Time (OpenDAoC-Core @f0aaa1e, GPL-3.0: PacketLib168.SendTime; field order only). Sent at world entry
// (0xE8 PlayerInit) and every 15 minutes after. The RustDAoC 1.127e capture's one 0x7E reads 8:00:59 AM, rate 24.

using DaocProtocol.Net;

namespace DaocProtocol.Packets;

/// <summary>
/// The game clock: the time of day in game milliseconds since midnight (0–86,399,999) u32 BE, then the day increment
/// u32 BE: game milliseconds per real millisecond in daytime (OpenDAoC's default 24, so a game day lasts an hour).
/// </summary>
public sealed record GameTime(uint MillisOfDay, uint DayIncrement)
{
    public const byte Code = 0x7E;

    public static GameTime Parse(ReadOnlySpan<byte> payload)
    {
        var r = new PayloadReader(payload);
        return new GameTime(r.U32BE(), r.U32BE());
    }

    public byte[] ToBytes() => new PayloadWriter().U32BE(MillisOfDay).U32BE(DayIncrement).ToArray();
}
