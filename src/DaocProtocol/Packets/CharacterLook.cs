// SPDX-License-Identifier: GPL-3.0-or-later
//
// A character's Catacombs appearance: the seven bytes the client chose at character creation, which
// the server stores unread (DOLCharacters EyeSize, LipSize, EyeColor, HairColor, FaceType, HairStyle,
// MoodType) and sends back in the character overview (0xFC) and 0x4B PlayerCreate, in a different
// order in each. Retail 1.130's reading (open question W30, confirmed against a character made in the
// retail creator with known choices: bytes 08 26 10 17 05 30 30):
//
//   EyeSize   high: slider 0 (Briton: nose)   low: slider 1 (eyes)
//   LipSize   high: slider 2 (lips)           low: slider 3 (jaw)
//   MoodType  high: the mood slider, remapped (wire 0 = neutral position 4, 1-4 = 0-3, 5-8 as they are)
//   EyeColor  high: eye colour                low: skin tone
//   HairColor the whole byte: hair colour
//   FaceType  high: face                      low: decal (tattoo)
//   HairStyle high: hair style, from 1 (0 = no hair)
//
// Sliders are positions 0-8 with 4 neutral; every index but the hair style counts from 0.

namespace DaocProtocol.Packets;

public readonly record struct CharacterLook(
    byte EyeSize, byte LipSize, byte EyeColor, byte HairColor, byte FaceType, byte HairStyle, byte MoodType)
{
    /// <summary>The face (fig3facemap column, from 0).</summary>
    public int Face => FaceType >> 4;
    /// <summary>The decal or tattoo (fig3decalmap column, from 0).</summary>
    public int Decal => FaceType & 0x0F;
    /// <summary>The hair style, from 1 (fig3haircolormap "style #"); 0 = no hair.</summary>
    public int Hair => HairStyle >> 4;
    /// <summary>The hair colour (the style's fig3haircolormap colour group, from 0).</summary>
    public int HairColour => HairColor;
    /// <summary>The eye colour (fig3eyecolormap column, from 0).</summary>
    public int EyeColour => EyeColor >> 4;
    /// <summary>The skin tone (fig3skincolormap column, from 0).</summary>
    public int SkinTone => EyeColor & 0x0F;

    /// <summary>The mood slider's position, 0..8 with 4 neutral (the byte's 0 is neutral).</summary>
    public int Mood => (MoodType >> 4) switch
    {
        0 => 4,
        int m and <= 4 => m - 1,
        int m => m,
    };

    /// <summary>The race's face slider <paramref name="k"/> (0-3, its creation screen's order, the
    /// fig3descriptions "Morphs" row: nose, eyes, lips, jaw for a Briton), 0..8 with 4 neutral.</summary>
    public int Slider(int k) => k switch
    {
        0 => EyeSize >> 4,
        1 => EyeSize & 0x0F,
        2 => LipSize >> 4,
        3 => LipSize & 0x0F,
        _ => 4,
    };
}

/// <summary>
/// A player's 16-bit model field (character overview, 0x4B PlayerCreate): the model id in the low 11 bits
/// and the body size in bits 11-12 (1 short, 2 average, 3 tall), as OpenDAoC's GamePlayer reads it
/// (<c>Model &amp; 0x7FF</c>, eSize). The retail creator stores the size there (a Briton made in it: 45528 =
/// model 472, average); our own creator sends the bare id. NPC models (0xDA) carry no size and go past 2047.
/// </summary>
public static class PlayerModel
{
    public static ushort Id(ushort model) => (ushort)(model & 0x7FF);

    /// <summary>1 short, 2 average, 3 tall; 0 when the field carries none.</summary>
    public static int Size(ushort model) => (model >> 11) & 3;
}
