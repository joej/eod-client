// SPDX-License-Identifier: GPL-3.0-or-later
//
// Client 1.127 character-screen exchange. Layouts follow OpenDAoC-Core 2026.09.36 (f0aaa1e):
// CharacterSelectRequestHandler, CharacterOverviewRequestHandler._HandlePacket1124,
// PacketLib168.SendSessionID, PacketLib1125.SendRealm, PacketLib1126.SendCharacterOverview.
// Checked against a recorded 1.127e session in the tests.

using DaocProtocol.Net;

namespace DaocProtocol.Packets;

public enum Realm : byte
{
    None = 0,
    Albion = 1,
    Midgard = 2,
    Hibernia = 3,
}

/// <summary>
/// 0x10. On 1.127 the client sends a single zero byte. OpenDAoC ignores the payload and answers
/// with 0x2A LoginGranted and 0x28 SessionId.
/// </summary>
public static class CharacterSelectRequest
{
    public const byte Code = 0x10;

    public static byte[] Payload() => [0];
}

/// <summary>
/// 0xFC (client → server). <see cref="Realm.None"/> asks which realm the account belongs to
/// (answered with 0xFE); a realm asks for that realm's characters (answered with 0xFC).
/// </summary>
public static class CharacterOverviewRequest
{
    public const byte Code = 0xFC;

    public static byte[] Payload(Realm realm) => [(byte)realm];
}

/// <summary>0x28: the session id, to be sent in every following client packet header.</summary>
public static class SessionId
{
    public const byte Code = 0x28;

    /// <summary>Little-endian, unlike the client header field it goes into.</summary>
    public static ushort Parse(ReadOnlySpan<byte> payload) => new PayloadReader(payload).U16LE();
}

/// <summary>0xFE: the account's realm (<see cref="Realm.None"/>: not chosen yet, or characters allowed in all realms), then 12 zero bytes.</summary>
public static class RealmReply
{
    public const byte Code = 0xFE;

    public static Realm Parse(ReadOnlySpan<byte> payload) => (Realm)new PayloadReader(payload).U8();
}

/// <summary>One character on the character screen.</summary>
/// <param name="Slot">0–9 within the realm's page.</param>
public sealed record CharacterSummary(
    int Slot, byte Level, string Name, string Location, string ClassName, string RaceName,
    ushort Model, byte Region, byte ClassId, Realm Realm, byte Race, bool Female);

/// <summary>0xFC (server → client): the characters on one realm's page, 1.126+ layout.</summary>
/// <param name="Flags">Bit 0: characters may be created in all realms (realm switcher shown).</param>
public sealed record CharacterOverview(uint Flags, IReadOnlyList<CharacterSummary> Characters)
{
    public const byte Code = 0xFC;
    public const int SlotsPerRealm = 10;

    /// <summary>Parses the whole payload; throws <see cref="InvalidDataException"/> if it is short or has bytes left over.</summary>
    public static CharacterOverview Parse(ReadOnlySpan<byte> payload)
    {
        var reader = new PayloadReader(payload);
        uint flags = reader.U32LE();
        reader.Skip(12);

        var characters = new List<CharacterSummary>();
        // An account without characters gets the 16-byte header only. Otherwise all ten slots
        // follow, so a payload that runs out early is truncated, not short of characters.
        if (reader.Remaining == 0)
            return new CharacterOverview(flags, characters);

        for (int slot = 0; slot < SlotsPerRealm; slot++)
        {
            byte level = reader.U8();
            if (level == 0)
                continue; // empty slot
            characters.Add(ReadCharacter(ref reader, slot, level));
        }

        if (reader.Remaining != 0)
            throw new InvalidDataException($"{reader.Remaining} unexpected bytes after the character slots.");

        return new CharacterOverview(flags, characters);
    }

    private static CharacterSummary ReadCharacter(ref PayloadReader reader, int slot, byte level)
    {
        string name = reader.StringIntLE();
        reader.Skip(4 + 1);  // u32 0x18, u8 1
        reader.Skip(10);     // face and hair customisation, armour extensions, hood, customisation step, mood
        reader.Skip(13);     // zero fill
        string location = reader.StringIntLE();
        string className = reader.StringIntLE();
        string raceName = reader.StringIntLE();
        ushort model = reader.U16LE();
        byte region = reader.U8();
        reader.Skip(1);      // expansion zone flag
        reader.Skip(8 * 2);  // armour models (and right-hand colour)
        reader.Skip(8 * 2);  // armour colours
        reader.Skip(4 * 2);  // weapon models
        reader.Skip(8);      // stats: str dex con qui int pie emp cha
        byte classId = reader.U8();
        var realm = (Realm)reader.U8();
        // ((race & 0x10) << 2) + (race & 0x0F), gender in bit 7
        byte raceGender = reader.U8();
        byte race = (byte)((raceGender & 0x0F) | ((raceGender & 0x40) >> 2));
        bool female = (raceGender & 0x80) != 0;
        reader.Skip(2);      // active weapon slots
        reader.Skip(3);      // SI flag, constitution, unknown
        return new CharacterSummary(slot, level, name, location, className, raceName, model, region, classId, realm, race, female);
    }
}
