// SPDX-License-Identifier: GPL-3.0-or-later
//
// Trainers (roadmap M13). Layouts ported (field order only) from OpenDAoC-Core @f0aaa1e, GPL-3.0:
// PacketLib1105.SendTrainerWindow (S→C 0x7B) and PlayerTrainRequestHandler (C→S 0x53). A right-click on the
// character's trainer (or C→S 0x7B) makes the server send a burst of 0x7B, one per section: the trainable specialisations
// (code 0), the realm abilities (1), one list of skill names per specialisation (4), the skills each specialisation gives
// and at which level (3), and the realm abilities' costs per level (5).

using DaocProtocol.Net;

namespace DaocProtocol.Packets;

/// <summary>A trainable specialisation (code 0): index, level, a third byte, name. Retail uses only the name and the third
/// byte: the current level is that byte − 1 (OpenDAoC sends level + 1), and an entry whose third byte is 0 gets no slider
/// (daoc-re, 2026-11-24).</summary>
public sealed record TrainerSpec(byte Index, byte Level, byte Next, string Name)
{
    public int Current => Math.Max(0, Next - 1);
    public bool HasSlider => Next != 0;
}

/// <summary>A realm ability (code 1): index, level, the cost of its next level, its name ("[name]" when the character
/// doesn't meet its requirement).</summary>
public sealed record TrainerRealmAbility(byte Index, byte Level, byte Cost, string Name);

/// <summary>One skill a specialisation gives (code 3): the level it comes at (0 means 50), its icon (page 2: a style-table
/// row, page 3: a spells.csv row, else the icon itself), its skill page, two bytes (OpenDAoC: a style's opening requirement
/// type and value, else 0 and 0xFD an ability / 0xFE a spell / 0xFF a song) and its id. When the first of the two bytes is
/// 200, retail reads two more bytes before the id (kept in <see cref="Extra"/>).</summary>
public sealed record TrainerSkill(byte Level, ushort Icon, byte Page, byte Extra1, byte Extra2, ushort Id, ushort Extra = 0)
{
    public int AtLevel => Level == 0 ? 50 : Level;
}

/// <summary>A specialisation's skills (code 3): its index, the levels auto-trained for the class (level / 4 on OpenDAoC),
/// and the skills in order (their names are the code 4 names from the running index).</summary>
public sealed record TrainerSpecSkills(byte Index, byte Autotrain, IReadOnlyList<TrainerSkill> Skills);

/// <summary>A realm ability's costs (code 5): level, max level and the cost of each level, then the name.</summary>
public sealed record TrainerRealmCosts(byte Level, byte MaxLevel, IReadOnlyList<byte> Costs, string Name);

/// <summary>
/// S→C 0x7B TrainerWindow: u8 count, u8 points (specialisation points; realm points for codes 1 and 5), u8 code, u8 0, then
/// by code:
/// <list type="bullet">
/// <item>0: count × (index, level, next, pascal name)</item>
/// <item>1: count × (index, level, cost, pascal name)</item>
/// <item>3: 50 bytes, the spec points gained at each character level 1–50 (for Minimum Level; OpenDAoC writes 0 then 255s),
///   then count × (index, skill count, autotrain: the free levels, skills × (level, u16 BE icon, page, b4, b5,
///   [u16 BE when b4 is 200], u16 BE id))</item>
/// <item>4: u8 start index, then count pascal names</item>
/// <item>5: count × (level, 0, max level, max level × cost, pascal name)</item>
/// </list>
/// Pascal strings are a u8 length and the bytes (no terminator).
/// </summary>
public sealed record TrainerWindow(byte Code, byte Points)
{
    public const byte Code0Specs = 0, CodeRealm = 1, CodeSkills = 3, CodeNames = 4, CodeRealmCosts = 5;
    public const byte PacketCode = 0x7B;

    public IReadOnlyList<TrainerSpec> Specs { get; init; } = [];
    public IReadOnlyList<TrainerRealmAbility> RealmAbilities { get; init; } = [];
    public IReadOnlyList<TrainerSpecSkills> SpecSkills { get; init; } = [];
    public byte[] LevelTable { get; init; } = [];
    public byte NameStart { get; init; }
    public IReadOnlyList<string> Names { get; init; } = [];
    public IReadOnlyList<TrainerRealmCosts> RealmCosts { get; init; } = [];

    public static TrainerWindow Parse(ReadOnlySpan<byte> payload)
    {
        var r = new PayloadReader(payload);
        int count = r.U8();
        byte points = r.U8(), code = r.U8();
        r.Skip(1);
        switch (code)
        {
            case Code0Specs:
            {
                var specs = new List<TrainerSpec>(count);
                for (int i = 0; i < count; i++)
                    specs.Add(new TrainerSpec(r.U8(), r.U8(), r.U8(), r.StringU8()));
                return new TrainerWindow(code, points) { Specs = specs };
            }
            case CodeRealm:
            {
                var ras = new List<TrainerRealmAbility>(count);
                for (int i = 0; i < count; i++)
                    ras.Add(new TrainerRealmAbility(r.U8(), r.U8(), r.U8(), r.StringU8()));
                return new TrainerWindow(code, points) { RealmAbilities = ras };
            }
            case CodeSkills:
            {
                byte[] table = r.Bytes(50).ToArray();
                var lines = new List<TrainerSpecSkills>(count);
                // OpenDAoC stops writing near 2045 bytes, so a long list may end early.
                for (int i = 0; i < count && r.Remaining >= 3; i++)
                {
                    byte index = r.U8(), n = r.U8(), auto = r.U8();
                    var skills = new List<TrainerSkill>(n);
                    for (int k = 0; k < n && r.Remaining >= 8; k++)
                    {
                        byte level = r.U8();
                        ushort icon = r.U16BE();
                        byte page = r.U8(), b4 = r.U8(), b5 = r.U8();
                        ushort extra = b4 == 200 ? r.U16BE() : (ushort)0;
                        skills.Add(new TrainerSkill(level, icon, page, b4, b5, r.U16BE(), extra));
                    }
                    lines.Add(new TrainerSpecSkills(index, auto, skills));
                }
                return new TrainerWindow(code, points) { LevelTable = table, SpecSkills = lines };
            }
            case CodeNames:
            {
                byte start = r.U8();
                var names = new List<string>(count);
                for (int i = 0; i < count; i++)
                    names.Add(r.StringU8());
                return new TrainerWindow(code, points) { NameStart = start, Names = names };
            }
            case CodeRealmCosts:
            {
                var costs = new List<TrainerRealmCosts>(count);
                for (int i = 0; i < count; i++)
                {
                    byte level = r.U8();
                    r.Skip(1);
                    byte max = r.U8();
                    costs.Add(new TrainerRealmCosts(level, max, r.Bytes(max).ToArray(), r.StringU8()));
                }
                return new TrainerWindow(code, points) { RealmCosts = costs };
            }
            default:
                return new TrainerWindow(code, points);
        }
    }
}

/// <summary>
/// C→S 0x53 TrainRequest, one per TRAIN, 264 bytes of big-endian u32s (retail, daoc-re 2026-11-24; OpenDAoC's
/// PlayerTrainHandler reads the same): 8 specialisation indices (code 3 block i's index; the first unused slot 0xFFFFFFFF,
/// later ones 0); from byte 32 each line's target level, absolute (an unchanged line sends its current level); from byte
/// 64, each code 5 realm ability's target level in code 5 order (unchanged: current; past the count 0). Retail sends it
/// unchecked, even with too few points.
/// </summary>
public static class TrainRequest
{
    public const byte Code = 0x53;
    public const int MaxSpecs = 8, RealmAbilities = 50;

    public static byte[] Payload(IReadOnlyList<(byte Index, byte Level)> specs, IReadOnlyList<byte>? realmLevels = null)
    {
        if (specs.Count > MaxSpecs)
            throw new ArgumentException($"At most {MaxSpecs} lines in one request.", nameof(specs));
        if (realmLevels is { Count: > RealmAbilities })
            throw new ArgumentException($"At most {RealmAbilities} realm abilities.", nameof(realmLevels));
        var w = new PayloadWriter();
        for (int i = 0; i < MaxSpecs; i++)
            w.U32BE(i < specs.Count ? specs[i].Index : i == specs.Count ? 0xFFFFFFFF : 0u);
        for (int i = 0; i < MaxSpecs; i++)
            w.U32BE(i < specs.Count ? specs[i].Level : 0u);
        for (int i = 0; i < RealmAbilities; i++)
            w.U32BE(realmLevels is not null && i < realmLevels.Count ? realmLevels[i] : 0u);
        return w.ToArray();
    }
}

/// <summary>C→S 0x7B TrainWindowRequest: one byte 0. Retail sends it only for the /train command; OpenDAoC answers with the
/// trainer window when the character may train here (its trainer targeted, or anywhere for a GM).</summary>
public static class TrainWindowRequest
{
    public const byte Code = 0x7B;

    public static byte[] Payload() => [0];
}
