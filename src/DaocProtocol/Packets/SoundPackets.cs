// SPDX-License-Identifier: GPL-3.0-or-later
//
// Sounds the server asks for (roadmap M12). Layouts from OpenDAoC-Core (GPL-3.0, field order only): PacketLib168
// SendRegionEnterSound (0xEF), SendPlaySound (0xD3) and SendSoundEffect (0xC9). Meanings as the retail client reads them
// from daoc-re's sound note (docs/sound.md, 2026-11-08). Shorts are big-endian, as the server writes them (WriteShort).

using DaocProtocol.Net;

namespace DaocProtocol.Packets;

/// <summary>
/// S→C 0xEF RegionSound: u16 player object id, a type and a value. Type 1 plays the level-up fanfare of realm = value
/// (CC_events "Raise Level"); type 2 is the music of the region or area just entered (a cc_music row = value; OpenDAoC
/// sends it as you enter an area that has a sound).
/// </summary>
public sealed record RegionSound(ushort ObjectId, byte Type, byte Value)
{
    public const byte Code = 0xEF;
    public const byte LevelUp = 1, AreaMusic = 2;

    public static RegionSound Parse(ReadOnlySpan<byte> payload)
    {
        var r = new PayloadReader(payload);
        return new RegionSound(r.U16BE(), r.U8(), r.U8());
    }
}

/// <summary>
/// S→C 0xD3 PlaySound: u16 type, u16 id, then padding (OpenDAoC: 8 zero bytes). Type 1 is crafting (id 1 fumble,
/// 2 failure, 3 success, 4 masterpiece); type 2 plays cc_sounds id 2D; type 3, 3D, carries a zone and position whose
/// field order isn't known (open question W52), so we play it 2D.
/// </summary>
public sealed record PlaySound(ushort Type, ushort Id)
{
    public const byte Code = 0xD3;
    public const ushort Craft = 1, Sound2D = 2, Sound3D = 3;

    /// <summary>The cc_sounds id the client plays for a crafting result (1–4), or null.</summary>
    public static ushort? CraftSound(ushort result) => result switch
    {
        1 => 924,   // fumble
        2 => 923,   // failure
        3 => 926,   // success
        4 => 925,   // masterpiece
        _ => null,
    };

    public static PlaySound Parse(ReadOnlySpan<byte> payload)
    {
        var r = new PayloadReader(payload);
        return new PlaySound(r.U16BE(), r.U16BE());
    }
}

/// <summary>
/// S→C 0xC9 SoundEffect: u16 cc_sounds id, zone, x, y, z (zone-local units), radius. A radius of 0 plays it 2D;
/// otherwise it plays at that spot (retail uses the radius only to choose 3D).
/// </summary>
public sealed record SoundEffect(ushort SoundId, ushort Zone, ushort X, ushort Y, ushort Z, ushort Radius)
{
    public const byte Code = 0xC9;

    public bool Positional => Radius != 0;

    public static SoundEffect Parse(ReadOnlySpan<byte> payload)
    {
        var r = new PayloadReader(payload);
        return new SoundEffect(r.U16BE(), r.U16BE(), r.U16BE(), r.U16BE(), r.U16BE(), r.U16BE());
    }
}
