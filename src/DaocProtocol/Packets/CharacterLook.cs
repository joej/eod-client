// SPDX-License-Identifier: GPL-3.0-or-later
//
// A character's Catacombs appearance: the seven bytes the client chose at character creation, which
// the server stores unread (DOLCharacters EyeSize, LipSize, EyeColor, HairColor, FaceType, HairStyle,
// MoodType) and sends back in the character overview (0xFC) and 0x4B PlayerCreate, in a different
// order in each. Two settings share a byte, one per half: OpenDAoC's PacketLib1126 comments read
// EyeSize as eyes (high) and nose (low), LipSize as lips and jaw, EyeColor as eye colour and skin tone,
// and FaceType and HairStyle as the face and hair style in the high half. Indices count from 0, the
// face sliders from 0 to 8 with 4 neutral. Evidence: two kobolds made with the retail 1.130 client on
// our server store EyeSize and LipSize 0x44 (every slider neutral) and HairStyle 0x10; gamedata.mpk's
// figtofig3main.csv numbers faces and hair styles from 0 and its neutral slider value is 4. Which
// half of HairColor is the colour (low here) is unconfirmed.

namespace DaocProtocol.Packets;

public readonly record struct CharacterLook(
    byte EyeSize, byte LipSize, byte EyeColor, byte HairColor, byte FaceType, byte HairStyle, byte MoodType)
{
    /// <summary>The face (fig3facemap column, from 0).</summary>
    public int Face => FaceType >> 4;
    /// <summary>The hair style (fig3map hair variant, from 0).</summary>
    public int Hair => HairStyle >> 4;
    /// <summary>The hair colour (fig3haircolormap colour, from 0).</summary>
    public int HairColour => HairColor & 0x0F;
    /// <summary>The eye colour (fig3eyecolormap column, from 0).</summary>
    public int EyeColour => EyeColor >> 4;
    /// <summary>The skin tone (fig3skincolormap column, from 0).</summary>
    public int SkinTone => EyeColor & 0x0F;
    /// <summary>The race's face slider <paramref name="k"/> (0-3, in its creation screen's order, the
    /// fig3descriptions "Morphs" row), 0..8 with 4 neutral. OpenDAoC's comments name the four for a Briton
    /// (nose, eyes, lips, jaw): eye size low and high half, then lip size high and low half. Other races
    /// put their own sliders in the same places (a kobold: nose, eyes, jaw, ears).</summary>
    public int Slider(int k) => k switch
    {
        0 => EyeSize & 0x0F,
        1 => EyeSize >> 4,
        2 => LipSize >> 4,
        3 => LipSize & 0x0F,
        _ => 4,
    };
}
