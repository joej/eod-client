// SPDX-License-Identifier: GPL-3.0-or-later
//
// The character's own progress and identity (roadmap M9: the summary and menu-bar windows). Layouts ported
// (field order only) from OpenDAoC-Core @f0aaa1e, GPL-3.0: PacketLib190.SendUpdatePoints (0x91),
// PacketLib168.SendUpdateMoney (0xFA), PacketLib179.SendUpdatePlayer (0x16 subcode 3). Checked against the
// RustDAoC 1.127e capture, whose 0x91, 0xFA and 0x16/3 decode field for field (a level 50 Paladin).

using DaocProtocol.Net;

namespace DaocProtocol.Packets;

/// <summary>
/// S→C 0x91 CharacterPointsUpdate: realm points u32 BE, level progress in ‰ u16 BE, specialisation points
/// u16 BE, bounty points u32 BE, realm specialisation points u16 BE, champion-level progress ‰ u16 BE, then
/// four u64 little-endian: experience, experience for the next level, champion experience and its next
/// level (OpenDAoC writes 0 for both).
/// </summary>
public sealed record PointsUpdate(uint RealmPoints, ushort LevelPermill, ushort SpecPoints, uint BountyPoints,
    ushort RealmSpecPoints, ushort ChampionPermill, ulong Experience, ulong ExperienceForNextLevel,
    ulong ChampionExperience, ulong ChampionExperienceForNextLevel)
{
    public const byte Code = 0x91;

    /// <summary>Full XP bubbles, 0–9 of 10: retail's coarse bar (level progress / 100, whole bubbles only).</summary>
    public int Bubbles => Math.Min(LevelPermill, (ushort)999) / 100;

    /// <summary>Progress within the current bubble, 0–99: retail's fine (blue) bar, filling ten times per level.</summary>
    public int BubbleProgress => Math.Min(LevelPermill, (ushort)999) % 100;

    public static PointsUpdate Parse(ReadOnlySpan<byte> payload)
    {
        var r = new PayloadReader(payload);
        return new PointsUpdate(r.U32BE(), r.U16BE(), r.U16BE(), r.U32BE(), r.U16BE(), r.U16BE(),
            r.U64LE(), r.U64LE(), r.U64LE(), r.U64LE());
    }

    public byte[] ToBytes() => new PayloadWriter().U32BE(RealmPoints).U16BE(LevelPermill).U16BE(SpecPoints)
        .U32BE(BountyPoints).U16BE(RealmSpecPoints).U16BE(ChampionPermill).U64LE(Experience)
        .U64LE(ExperienceForNextLevel).U64LE(ChampionExperience).U64LE(ChampionExperienceForNextLevel).ToArray();
}

/// <summary>
/// S→C 0xFA MoneyUpdate: copper u8, silver u8, gold u16 BE, mithril u16 BE, platinum u16 BE (that order).
/// 100 copper make a silver, 100 silver a gold, 1000 gold a platinum, 1000 platinum a mithril.
/// </summary>
public sealed record MoneyUpdate(byte Copper, byte Silver, ushort Gold, ushort Mithril, ushort Platinum)
{
    public const byte Code = 0xFA;

    /// <summary>All of it in copper.</summary>
    public long TotalCopper => (((Mithril * 1000L + Platinum) * 1000 + Gold) * 100 + Silver) * 100 + Copper;

    public static MoneyUpdate Parse(ReadOnlySpan<byte> payload)
    {
        var r = new PayloadReader(payload);
        return new MoneyUpdate(r.U8(), r.U8(), r.U16BE(), r.U16BE(), r.U16BE());
    }

    public byte[] ToBytes() =>
        new PayloadWriter().U8(Copper).U8(Silver).U16BE(Gold).U16BE(Mithril).U16BE(Platinum).ToArray();
}

/// <summary>
/// S→C 0x16 VariousUpdate subcode 3: who the character is, for the summary and character sheet. After the
/// four-byte head (3, 0x0F entries, 0, 0) come fifteen pairs of a byte and a u8-length string; several bytes
/// belong to their neighbours (max health high/low, the house number high/low, master level + 1, champion
/// level + 1). The server writes "None" for a string the client should hide (some, such as the champion
/// title, it shows anyway); see <see cref="Shown"/>.
/// </summary>
public sealed record PlayerInfo(byte Level, string Name, ushort MaxHealth, string ClassName, string Profession,
    string LevelTitle, byte RealmLevel, string RealmTitle, byte RealmSpecPoints, string BaseClass,
    ushort HouseNumber, string GuildName, string LastName, byte MasterLevelPlusOne, string RaceName,
    string GuildRankTitle, string CraftingSkill, string CraftingTitle, string MasterLevelTitle, string CustomTitle,
    byte ChampionLevelPlusOne, string ChampionTitle)
{
    public const byte Code = 0x16;
    public const byte Subcode = 3;

    /// <summary>Realm rank as players write it: realm level 40 is 5L0.</summary>
    public string RealmRank => $"{RealmLevel / 10 + 1}L{RealmLevel % 10}";

    /// <summary>The text to display: empty for the server's "None".</summary>
    public static string Shown(string s) => s.Equals("None", StringComparison.OrdinalIgnoreCase) ? "" : s;

    /// <summary>Whether a 0x16 payload is this subcode.</summary>
    public static bool Matches(ReadOnlySpan<byte> payload) => payload.Length > 0 && payload[0] == Subcode;

    public static PlayerInfo Parse(ReadOnlySpan<byte> payload)
    {
        var r = new PayloadReader(payload);
        r.Skip(4);
        byte level = r.U8(); string name = r.StringU8();
        byte hpHigh = r.U8(); string className = r.StringU8();
        byte hpLow = r.U8(); string profession = r.StringU8();
        r.Skip(1); string levelTitle = r.StringU8();
        byte realmLevel = r.U8(); string realmTitle = r.StringU8();
        byte realmSpec = r.U8(); string baseClass = r.StringU8();
        byte houseHigh = r.U8(); string guild = r.StringU8();
        byte houseLow = r.U8(); string lastName = r.StringU8();
        byte ml = r.U8(); string race = r.StringU8();
        r.Skip(1); string guildRank = r.StringU8();
        r.Skip(1); string crafting = r.StringU8();
        r.Skip(1); string craftTitle = r.StringU8();
        r.Skip(1); string mlTitle = r.StringU8();
        r.Skip(1); string custom = r.StringU8();
        byte cl = r.U8(); string clTitle = r.StringU8();
        return new PlayerInfo(level, name, (ushort)(hpHigh << 8 | hpLow), className, profession, levelTitle,
            realmLevel, realmTitle, realmSpec, baseClass, (ushort)(houseHigh << 8 | houseLow), guild, lastName, ml,
            race, guildRank, crafting, craftTitle, mlTitle, custom, cl, clTitle);
    }

    public byte[] ToBytes() => new PayloadWriter().U8(Subcode).U8(0x0F).U8(0).U8(0)
        .U8(Level).StringU8(Name).U8((byte)(MaxHealth >> 8)).StringU8(ClassName)
        .U8((byte)MaxHealth).StringU8(Profession).U8(0).StringU8(LevelTitle)
        .U8(RealmLevel).StringU8(RealmTitle).U8(RealmSpecPoints).StringU8(BaseClass)
        .U8((byte)(HouseNumber >> 8)).StringU8(GuildName).U8((byte)HouseNumber).StringU8(LastName)
        .U8(MasterLevelPlusOne).StringU8(RaceName).U8(0).StringU8(GuildRankTitle).U8(0).StringU8(CraftingSkill)
        .U8(0).StringU8(CraftingTitle).U8(0).StringU8(MasterLevelTitle).U8(0).StringU8(CustomTitle)
        .U8(ChampionLevelPlusOne).StringU8(ChampionTitle).ToArray();
}
