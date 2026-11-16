// SPDX-License-Identifier: GPL-3.0-or-later
//
// Attack mode: drawing weapons and putting them away. Layouts ported (field order only) from OpenDAoC-Core,
// GPL-3.0: PlayerAttackRequestHandler (C→S 0x74) and PacketLib168.SendAttackMode (S→C 0x74). The server
// decides: it answers a request with its own attack state, and also ends attack mode itself (target dead or
// gone, a ranged weapon readied). Others see it as bit 0x80 of the health byte in that player's 0xA9 / 0xBA
// (OpenDAoC PlayerPositionUpdateHandler.GetHealthByte).
// Swings (S→C 0xBC): PacketLib186.SendCombatAnimation, and the result bytes WeaponAction writes; the retail client
// reads the same fields (docs/protocol.md "Combat").

using DaocProtocol.Net;

namespace DaocProtocol.Packets;

/// <summary>C→S 0x74: enter or leave attack mode.</summary>
public static class AttackRequest
{
    public const byte Code = 0x74;

    /// <param name="start">True to draw weapons and attack, false to stop.</param>
    /// <param name="byPlayer">True when the player asked (the key); false when the client itself stops
    /// attacking (OpenDAoC's second byte: 0 = the player, 1 = the client). The server only starts an attack
    /// the player asked for.</param>
    public static byte[] Payload(bool start, bool byPlayer = true) =>
        new PayloadWriter().U8(start ? (byte)1 : (byte)0).U8(byPlayer ? (byte)0 : (byte)1).ToArray();
}

/// <summary>
/// C→S 0xC7 PlayerSitRequest (OpenDAoC PlayerSitRequestHandler): one byte, non-zero to sit, 0 to stand.
/// Standing up is also how a quit is cancelled: /quit sits the character down, and the server stops
/// the quit timer when it stands (GamePlayer.Sit). Moving alone does not stand it up on the server.
/// </summary>
public static class SitRequest
{
    public const byte Code = 0xC7;

    public static byte[] Payload(bool sit) => [sit ? (byte)1 : (byte)0];
}

/// <summary>S→C 0x74: the character's attack mode, as the server now has it (weapons drawn or not).</summary>
public static class AttackMode
{
    public const byte Code = 0x74;

    public static bool IsOn(ReadOnlySpan<byte> payload) => new PayloadReader(payload).U8() != 0;
}

/// <summary>
/// S→C 0xBC CombatAnimation: one swing, or a hit with no swing. Attacker and defender object ids (0 when unknown or
/// unseen), the attacker's weapon model and the defender's shield model (u16 BE each), a style id or ranged code
/// (u16 <b>little-endian</b>), a time in 100 ms units (ranged only), the result, the defender's health %, then 1 byte.
/// </summary>
public sealed record CombatAnimation(ushort AttackerId, ushort DefenderId, ushort WeaponModel, ushort ShieldModel,
    ushort Style, byte Time, byte Result, byte DefenderHealth)
{
    public const byte Code = 0xBC;

    // Results as OpenDAoC sends them (WeaponAction): 0 miss, 1 parry, 2 block, 3 evade, 4 fumble, 10 hit, 11 styled
    // hit; 0x0A with no attacker is a bleed, 0x14 a hit with no swing (traps, area abilities, siege).
    public const byte Missed = 0, Parried = 1, Blocked = 2, Evaded = 3, Fumbled = 4, Hit = 0x0A, StyledHit = 0x0B,
        NoSwing = 0x14;

    // Ranged and dual-wield codes in the style field (16000 and up; styles are 1-15999).
    public const ushort RangedPrepare = 0x3E80, LeftHand = 0x3E81, BothHands = 0x3E82, RangedShoot = 0x3E83,
        RangedPrepareAlt = 0x3E86, RangedShootAlt = 0x3E87;

    /// <summary>The attacker swings: there is one, and the result isn't a no-swing hit.</summary>
    public bool Swings => AttackerId != 0 && Result < NoSwing;

    /// <summary>The defender was hit (a flinch): 0x0A and up.</summary>
    public bool IsHit => Result >= Hit;

    /// <summary>A style id (1-15999), else 0.</summary>
    public ushort StyleId => Style is > 0 and < RangedPrepare ? Style : (ushort)0;

    /// <summary>A bow, crossbow or thrown-weapon code.</summary>
    public bool IsRanged => Style is RangedPrepare or RangedShoot or RangedPrepareAlt or RangedShootAlt;

    public static CombatAnimation Parse(ReadOnlySpan<byte> payload)
    {
        var r = new PayloadReader(payload);
        return new CombatAnimation(r.U16BE(), r.U16BE(), r.U16BE(), r.U16BE(), r.U16LE(), r.U8(), r.U8(), r.U8());
    }

    public byte[] ToBytes() => new PayloadWriter().U16BE(AttackerId).U16BE(DefenderId).U16BE(WeaponModel)
        .U16BE(ShieldModel).U16LE(Style).U8(Time).U8(Result).U8(DefenderHealth).U8(0).ToArray();
}
