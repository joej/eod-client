// SPDX-License-Identifier: GPL-3.0-or-later
//
// Vitals, death and release (roadmap M8/M9). Layouts ported (field order only) from OpenDAoC-Core, GPL-3.0:
// PacketLib190.SendStatusUpdate (0xAD), PacketLib168.SendPlayerDied (0xAE), SendPlayerRevive (0x89),
// SendTimerWindow / SendCloseTimerWindow (0xF3). Retail 1.127 sends the same death sequence (killing blow,
// "Release Timer" 900 s, 0xAE, 0xAD with health 0; after /release: the move to the bind point, 0x89, 0xAD
// at full health).

using DaocProtocol.Net;

namespace DaocProtocol.Packets;

/// <summary>
/// S→C 0xAD CharacterStatusUpdate: our own vitals. Percentages first (health, mana, a sitting byte: 2 while
/// sitting, endurance, concentration, one unknown), then u16 BE maximum mana, endurance, concentration and
/// health, and the current health, endurance, mana and concentration.
/// </summary>
public sealed record StatusUpdate(byte HealthPercent, byte ManaPercent, byte Sitting, byte EndurancePercent,
    byte ConcentrationPercent, ushort MaxMana, ushort MaxEndurance, ushort MaxConcentration, ushort MaxHealth,
    ushort Health, ushort Endurance, ushort Mana, ushort Concentration)
{
    public const byte Code = 0xAD;

    public bool IsSitting => Sitting != 0;

    public static StatusUpdate Parse(ReadOnlySpan<byte> payload)
    {
        var r = new PayloadReader(payload);
        byte hp = r.U8(), mana = r.U8(), sitting = r.U8(), end = r.U8(), conc = r.U8();
        r.Skip(1);
        return new StatusUpdate(hp, mana, sitting, end, conc,
            MaxMana: r.U16BE(), MaxEndurance: r.U16BE(), MaxConcentration: r.U16BE(), MaxHealth: r.U16BE(),
            Health: r.U16BE(), Endurance: r.U16BE(), Mana: r.U16BE(), Concentration: r.U16BE());
    }

    public byte[] ToBytes() => new PayloadWriter().U8(HealthPercent).U8(ManaPercent).U8(Sitting).U8(EndurancePercent)
        .U8(ConcentrationPercent).U8(0).U16BE(MaxMana).U16BE(MaxEndurance).U16BE(MaxConcentration).U16BE(MaxHealth)
        .U16BE(Health).U16BE(Endurance).U16BE(Mana).U16BE(Concentration).ToArray();
}

/// <summary>S→C 0xAE PlayerDeath: a player died (us, or one in view). The killer is 0 when nothing killed it
/// (a fall). Then 4 zero bytes.</summary>
public sealed record PlayerDied(ushort VictimId, ushort KillerId)
{
    public const byte Code = 0xAE;

    public static PlayerDied Parse(ReadOnlySpan<byte> payload)
    {
        var r = new PayloadReader(payload);
        return new PlayerDied(r.U16BE(), r.U16BE());
    }

    public byte[] ToBytes() => new PayloadWriter().U16BE(VictimId).U16BE(KillerId).Zeros(4).ToArray();
}

/// <summary>S→C 0x89 PlayerRevive: a player is alive again (released or resurrected), then 2 zero bytes.</summary>
public sealed record PlayerRevived(ushort ObjectId)
{
    public const byte Code = 0x89;

    public static PlayerRevived Parse(ReadOnlySpan<byte> payload) => new(new PayloadReader(payload).U16BE());

    public byte[] ToBytes() => new PayloadWriter().U16BE(ObjectId).Zeros(2).ToArray();
}

/// <summary>
/// S→C 0xF3 TimerWindow: a countdown window (the "Release Timer" after death, 900 s). Seconds u16 BE, title
/// length u8, 1, then the title; seconds 0 with an empty title closes it.
/// </summary>
public sealed record TimerWindow(ushort Seconds, string Title)
{
    public const byte Code = 0xF3;

    public bool IsClose => Seconds == 0 && Title.Length == 0;

    public static TimerWindow Parse(ReadOnlySpan<byte> payload)
    {
        var r = new PayloadReader(payload);
        ushort seconds = r.U16BE();
        int length = r.U8();
        r.Skip(1);
        return new TimerWindow(seconds, length == 0 ? "" : System.Text.Encoding.Latin1.GetString(r.Bytes(length)));
    }

    public byte[] ToBytes()
    {
        var w = new PayloadWriter().U16BE(Seconds).U8((byte)Title.Length).U8(Title.Length == 0 ? (byte)0 : (byte)1);
        return (Title.Length == 0 ? w : w.Bytes(System.Text.Encoding.Latin1.GetBytes(Title))).ToArray();
    }
}
