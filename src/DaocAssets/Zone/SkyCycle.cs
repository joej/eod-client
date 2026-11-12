// SPDX-License-Identifier: GPL-3.0-or-later
//
// The sky through the game day (W10). A sky file has four canopies, dawn, day, dusk and night, and the client blends
// between them by the time of day, linearly over 22.5 degrees of sun angle (daoc-re RE/73, RE/79):
//   night until 4:30; night → dawn to 6:00; dawn → day to 7:30; day until 16:30; day → dusk to 18:00;
//   dusk → night to 19:30; night.
// The sun disk and the sun's light take different paths (RE/79): the disk rises due east at 6:00 and sets due west at
// 18:00 on the sky file's tilted path; the light never drops below 45 degrees and holds still at night.

namespace DaocAssets.Zone;

public static class SkyCycle
{
    private const float Hour = 3_600_000f;

    // (hour, canopy): the canopy is pure at each key and blends linearly to the next.
    private static readonly (float Hour, string Time)[] Keys =
    [
        (0f, "night"), (4.5f, "night"), (6f, "dawn"), (7.5f, "day"), (16.5f, "day"), (18f, "dusk"), (19.5f, "night"), (24f, "night"),
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

    /// <summary>
    /// The sun's angle along its path, degrees: 15 per game hour from 270 at midnight, so 0 at 6:00 (rising), 90 at noon,
    /// 180 at 18:00 (setting) and below the horizon from there to 6:00 (daoc-re RE/79: the built-in sun's
    /// elevation_at_midnight). The moon is the same plus 180.
    /// </summary>
    public static float SunAngle(uint millisOfDay) => (millisOfDay % 86_400_000 / Hour * 15 + 270) % 360;

    /// <summary>The sun is above the horizon (6:00 to 18:00).</summary>
    public static bool SunUp(uint millisOfDay) => SunAngle(millisOfDay) < 180;

    /// <summary>
    /// The direction toward the sun disk (east, up, south; unit length) on the sky file's tilted path (RE/79): x = cos θ,
    /// sideways sin(ecliptic), up cos(ecliptic) sin θ, then normalised (the path as given isn't a unit circle). At noon it
    /// stands 90 - ecliptic degrees up, leaning south (inferred: which side the lean takes isn't confirmed).
    /// </summary>
    public static (float East, float Up, float South) Disk(uint millisOfDay, float eclipticDegrees) =>
        OnPath(SunAngle(millisOfDay), eclipticDegrees);

    /// <summary>
    /// The angle of the sun's directional light, degrees from the eastern horizon over the top (RE/79): by day
    /// 90 - (90 - θ) / 2, so 45 at 6:00, 90 at noon, 135 at 18:00 and never below 45 degrees up; at night held at 135 until
    /// midnight, then at 45 until dawn.
    /// </summary>
    public static float LightAngle(uint millisOfDay)
    {
        float theta = SunAngle(millisOfDay);
        if (theta < 180)
            return 90 - (90 - theta) / 2;
        return theta < 270 ? 135 : 45;   // 18:00-24:00, then 0:00-6:00
    }

    /// <summary>
    /// The direction toward the sun's light (east, up, south; unit length): <see cref="LightAngle"/> on the same tilted
    /// path as the disk (inferred: the note gives the angle, not the plane).
    /// </summary>
    public static (float East, float Up, float South) Light(uint millisOfDay, float eclipticDegrees) =>
        OnPath(LightAngle(millisOfDay), eclipticDegrees);

    private static (float East, float Up, float South) OnPath(float angleDegrees, float eclipticDegrees)
    {
        float t = angleDegrees * MathF.PI / 180, e = eclipticDegrees * MathF.PI / 180;
        float east = MathF.Cos(t), south = MathF.Sin(e), up = MathF.Cos(e) * MathF.Sin(t);
        float n = MathF.Sqrt(east * east + south * south + up * up);
        return (east / n, up / n, south / n);
    }
}
