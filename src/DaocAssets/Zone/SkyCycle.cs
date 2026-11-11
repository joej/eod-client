// SPDX-License-Identifier: GPL-3.0-or-later
//
// The sky through the game day (W10). A sky file has four canopies, dawn, day, dusk and night, and the client blends
// between them by the time of day (daoc-re RE/73: six time slots, dawn, -, day, -, dusk, night). The slots' hours
// aren't known yet, so this is a stand-in around OpenDAoC's 6 AM / 6 PM day boundaries:
//   night until 4:30; night → dawn to 6:00; dawn → day to 8:00; day until 16:30; day → dusk to 18:00;
//   dusk → night to 19:30; night.
// The sun rises due east at 6 AM, is highest at noon (to the south) and sets due west at 6 PM; at night a moon
// takes the same arc for the night light, without a disk.

namespace DaocAssets.Zone;

public static class SkyCycle
{
    private const float Hour = 3_600_000f;

    // (hour, canopy): the canopy is pure at each key and blends linearly to the next.
    private static readonly (float Hour, string Time)[] Keys =
    [
        (0f, "night"), (4.5f, "night"), (6f, "dawn"), (8f, "day"), (16.5f, "day"), (18f, "dusk"), (19.5f, "night"), (24f, "night"),
    ];

    /// <summary>The two canopies to blend at <paramref name="millisOfDay"/> and how far from the first to the second
    /// (0–1).</summary>
    public static (string From, string To, float T) Blend(uint millisOfDay)
    {
        float h = millisOfDay % 86_400_000 / Hour;
        for (int i = 0; i < Keys.Length - 1; i++)
            if (h < Keys[i + 1].Hour)
                return (Keys[i].Time, Keys[i + 1].Time, (h - Keys[i].Hour) / (Keys[i + 1].Hour - Keys[i].Hour));
        return ("night", "night", 0);
    }

    /// <summary>True from 6 AM to 6 PM, when the sun (not the moon) is up.</summary>
    public static bool SunUp(uint millisOfDay) => millisOfDay % 86_400_000 is >= 21_600_000 and < 64_800_000;

    /// <summary>
    /// The direction toward the sun (by day) or moon (by night), as east, up, south (a unit vector): due east at 6
    /// (AM or PM for the moon), highest at 12, due west at 18; the arc leans south, peaking at <paramref name="peak"/>
    /// degrees above the horizon.
    /// </summary>
    public static (float East, float Up, float South) Toward(uint millisOfDay, float peak = 60)
    {
        float h = millisOfDay % 86_400_000 / Hour;
        float a = (h - 6) % 12 / 12 * MathF.PI;           // 0 at rise, π at set
        if (a < 0) a += MathF.PI;
        float tilt = peak * MathF.PI / 180;
        float east = MathF.Cos(a), across = MathF.Sin(a);   // across: from the horizon to the top of the arc
        return (east, across * MathF.Sin(tilt), across * MathF.Cos(tilt));
    }
}
