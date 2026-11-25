// SPDX-License-Identifier: GPL-3.0-or-later
//
// The sky through the game day (W10). A sky file has four canopies, dawn, day, dusk and night, and the client blends
// between them by the time of day, linearly over 22.5 degrees of sun angle (daoc-re RE/73, RE/79):
//   night until 4:30; night → dawn to 6:00; dawn → day to 7:30; day until 16:30; day → dusk to 18:00;
//   dusk → night to 19:30; night.
// The sun's light comes from the sun disk itself (daoc-re RE/79 §2, corrected 2026-11-25 from retail 1.130's lights):
// it rises due east at 6:00 and sets due west at 18:00 on the sky file's tilted path, 15 degrees an hour, leaning toward
// -y (north), and keeps going below the horizon at night. (RE/79 first read a light that never dropped below 45 degrees
// and held still at night; the client's lights show otherwise.)

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
    /// y = -sin(ecliptic), up cos(ecliptic) sin θ, then normalised (the path as given isn't a unit circle). At noon it
    /// stands 90 - ecliptic degrees up, leaning north (toward -y; measured on retail 1.130, RE/79 §2), so a south-facing
    /// wall never gets direct sun.
    /// </summary>
    public static (float East, float Up, float South) Disk(uint millisOfDay, float eclipticDegrees) =>
        OnPath(SunAngle(millisOfDay), eclipticDegrees);

    /// <summary>
    /// The direction toward the sun's light (east, up, south; unit length): the disk's own (RE/79 §2: the client's light
    /// matched this to three decimals at five game times), below the horizon at night too.
    /// </summary>
    public static (float East, float Up, float South) Light(uint millisOfDay, float eclipticDegrees) =>
        Disk(millisOfDay, eclipticDegrees);

    private static (float East, float Up, float South) OnPath(float angleDegrees, float eclipticDegrees)
    {
        float t = angleDegrees * MathF.PI / 180, e = eclipticDegrees * MathF.PI / 180;
        float east = MathF.Cos(t), south = -MathF.Sin(e), up = MathF.Cos(e) * MathF.Sin(t);
        float n = MathF.Sqrt(east * east + south * south + up * up);
        return (east / n, up / n, south / n);
    }
}
