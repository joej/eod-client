// SPDX-License-Identifier: GPL-3.0-or-later
//
// A zone's ambient sound regions (zones/zoneNNN/sounds.dat; roadmap M12, docs/sound.md). Each [SoundRegionNN] has its
// settings, up to 128 SoundNN lines and its shapes. The file's own header gives a line as "Type, Sound, Period, Chance,
// Volume, Start Time, End Time, Weather, Environment Type": type 1 a random one-shot, 2 a loop; the sound a cc_sounds id
// or a structure name; period in seconds (a range, "2-6"); chance in %; volume 0–100; start and end HHMM (0, 0 always;
// may wrap past midnight); weather 0 any, 1 clear, 2 rain, 3 fog; with version=2 an environment, 1 above water and
// 2 underwater. Shapes are zone-local units: "0, left, top, right, bottom" or "1, x, y, radius"; an xShape is silent.

using DaocAssets.Zone;

namespace DaocAssets.Sound;

public sealed record ZoneSoundLine(int Kind, string Sound, SoundRange PeriodS, float Chance, float Volume,
    int Start, int End, int Weather, int Environment)
{
    public const int Random = 1, Loop = 2;

    /// <summary>Whether the line plays at game time hhmm (0, 0: always; a range may wrap past midnight).</summary>
    public bool PlaysAt(int hhmm) =>
        (Start == 0 && End == 0) || (Start <= End ? hhmm >= Start && hhmm <= End : hhmm >= Start || hhmm <= End);

    /// <summary>Whether the line plays in this weather (0 any, 1 clear, 2 rain, 3 fog).</summary>
    public bool PlaysIn(int weather) => Weather == 0 || Weather == weather;
}

/// <summary>A rectangle (Radius 0) or a circle, zone-local units.</summary>
public readonly record struct ZoneSoundShape(bool Circle, float X1, float Y1, float X2, float Y2, float Radius)
{
    public bool Contains(float x, float y) => Circle
        ? (x - X1) * (x - X1) + (y - Y1) * (y - Y1) <= Radius * Radius
        : x >= Math.Min(X1, X2) && x <= Math.Max(X1, X2) && y >= Math.Min(Y1, Y2) && y <= Math.Max(Y1, Y2);

    internal static ZoneSoundShape? Parse(string? text)
    {
        float[]? p = text?.Split(',').Select(s => SoundRange.Num(s) ?? float.NaN).ToArray();
        if (p is null || p.Length < 4 || p.Any(float.IsNaN))
            return null;
        if (p[0] == 1)
            return new ZoneSoundShape(true, p[1], p[2], 0, 0, p[3]);
        return p.Length >= 5 ? new ZoneSoundShape(false, p[1], p[2], p[3], p[4], 0) : null;
    }
}

public sealed record ZoneSoundRegion(string Name, bool ZoneWide, float FadeTime, float RandomSpacing, bool FadeOnMp3,
    bool IgnoreRandomOffset, int SoundType, int Version, IReadOnlyList<ZoneSoundLine> Sounds,
    IReadOnlyList<ZoneSoundShape> Shapes, IReadOnlyList<ZoneSoundShape> Silent)
{
    /// <summary>sound_type 2: ambient music (one part per region at a time).</summary>
    public bool IsMusic => SoundType == 2;

    /// <summary>Whether a zone-local point is in the region (in a shape, outside every xShape).</summary>
    public bool Contains(float x, float y) =>
        (ZoneWide || Shapes.Any(s => s.Contains(x, y))) && !Silent.Any(s => s.Contains(x, y));
}

public static class ZoneSounds
{
    public static IReadOnlyList<ZoneSoundRegion> Parse(string text)
    {
        IniFile ini = IniFile.Parse(text);
        var regions = new List<ZoneSoundRegion>();
        foreach ((string section, Dictionary<string, string> keys) in ini.Sections
                     .Where(s => s.Key.StartsWith("SoundRegion", StringComparison.OrdinalIgnoreCase))
                     .OrderBy(s => int.TryParse(s.Key[11..], out int n) ? n : 999))
        {
            int version = Int(keys, "version", 1);
            var sounds = new List<ZoneSoundLine>();
            var shapes = new List<ZoneSoundShape>();
            var silent = new List<ZoneSoundShape>();
            foreach ((string key, string value) in keys.OrderBy(k => k.Key, StringComparer.OrdinalIgnoreCase))
            {
                if (Numbered(key, "Sound") && Line(value, version) is ZoneSoundLine line)
                    sounds.Add(line);
                else if (Numbered(key, "Shape") && ZoneSoundShape.Parse(value) is ZoneSoundShape s)
                    shapes.Add(s);
                else if (Numbered(key, "xShape") && ZoneSoundShape.Parse(value) is ZoneSoundShape x)
                    silent.Add(x);
            }
            regions.Add(new ZoneSoundRegion(keys.GetValueOrDefault("Name") ?? section, Int(keys, "zone_wide_sounds", 0) == 1,
                Float(keys, "fade_time", 3), Float(keys, "random_sound_spacing", 5), Int(keys, "fade_on_mp3", 0) == 1,
                Int(keys, "ignore_random_offset", 0) == 1, Int(keys, "sound_type", 1), version, sounds, shapes, silent));
        }
        return regions;
    }

    private static bool Numbered(string key, string prefix) =>
        key.Length > prefix.Length && key.StartsWith(prefix, StringComparison.OrdinalIgnoreCase) &&
        key[prefix.Length..].All(char.IsAsciiDigit);

    private static ZoneSoundLine? Line(string value, int version)
    {
        string[] f = value.Split(',').Select(s => s.Trim()).ToArray();
        if (f.Length < 8 || !int.TryParse(f[0], out int kind) || f[1].Length == 0)
            return null;
        return new ZoneSoundLine(kind, f[1], SoundRange.Parse(f[2]) ?? new(0, 0), SoundRange.Num(f[3]) ?? 100,
            SoundRange.Num(f[4]) ?? 100, Hhmm(f[5]), Hhmm(f[6]), int.TryParse(f[7], out int w) ? w : 0,
            version == 2 && f.Length > 8 && int.TryParse(f[8], out int e) ? e : 1);
    }

    private static int Hhmm(string s) => int.TryParse(s, out int v) ? v : 0;
    private static int Int(Dictionary<string, string> k, string key, int fallback) =>
        k.TryGetValue(key, out string? v) && int.TryParse(v, out int i) ? i : fallback;
    private static float Float(Dictionary<string, string> k, string key, float fallback) =>
        k.TryGetValue(key, out string? v) && SoundRange.Num(v) is float f ? f : fallback;
}

public static class RegionMusic
{
    /// <summary>
    /// The music for entering region <paramref name="region"/>: zones.dat [regionNNN] entry_music, a cc_mp3s id (2 =
    /// mp3_Enter_Albion), taken from its proxy_region when it has one; null for none (0) or no entry. Ours, until daoc-re
    /// answers: the region's value only, not a [zoneNNN] one; dr_entry_music (Darkness Rising) isn't used.
    /// </summary>
    public static int? EntryMusic(IniFile zonesDat, int region)
    {
        for (int hops = 0; hops < 4; hops++)
        {
            string section = $"region{region:000}";
            if (int.TryParse(zonesDat.Get(section, "proxy_region")?.Trim(), out int proxy) && proxy != region)
            {
                region = proxy;
                continue;
            }
            return int.TryParse(zonesDat.Get(section, "entry_music")?.Trim(), out int id) && id != 0 ? id : null;
        }
        return null;
    }
}
