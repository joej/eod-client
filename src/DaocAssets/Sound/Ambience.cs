// SPDX-License-Identifier: GPL-3.0-or-later
//
// A zone's ambience over time (roadmap M12): which sounds.dat lines play where the listener is, at the game time and in
// the weather. As retail's client does (daoc-re 2026-11-21):
//   - a region is heard inside one of its shapes (or zone-wide) and outside all its xShapes; fade_on_mp3 silences it
//     while music plays;
//   - a loop line (type 2) starts almost silent and fades in over its region's fade_time while the region is heard and
//     its hours, weather and environment match, and fades out the same way when they stop matching;
//   - a random line (type 1) takes a turn each period (re-rolled within its range each turn). On a turn it is skipped
//     while its previous sound still plays, or when any random line of its region started less than
//     random_sound_spacing s ago; otherwise it rolls its chance, and on a win notes the time and plays once. A line
//     whose sound resolves to nothing still takes its turns and still resets the spacing: a silent slot;
//   - the first turn is due at once with instant_start=1, else after a random 1 to period s, or after one period with
//     ignore_random_offset=1 (that case: inferred).
// Ours: a random line's turns restart when its region is left or its hours end, and a period of 0 is taken as 1 s.

namespace DaocAssets.Sound;

/// <summary>A line to play once now: region and line index, and its volume 0–1.</summary>
public readonly record struct AmbientShot(int Region, int Line, float Volume);

public sealed class Ambience(IReadOnlyList<ZoneSoundRegion> regions, Random? rng = null)
{
    /// <summary>The level a bed starts at before it fades in (retail's 1/128).</summary>
    public const float BedStart = 0.0078f;

    private readonly Random _rng = rng ?? Random.Shared;
    private readonly bool[] _heard = new bool[regions.Count];
    private readonly double[] _lastRandom = Enumerable.Repeat(double.NegativeInfinity, regions.Count).ToArray();
    private readonly Dictionary<(int, int), double> _nextTurn = [];
    private readonly Dictionary<(int, int), float> _bedGain = [];
    private double _time;

    public IReadOnlyList<ZoneSoundRegion> Regions => regions;

    /// <summary>Whether a region is heard now (the listener in it, not silenced by music).</summary>
    public bool Heard(int region) => _heard[region];

    /// <summary>A loop line's fade now, 0–1.</summary>
    public float BedGain(int region, int line) => _bedGain.GetValueOrDefault((region, line));

    /// <summary>
    /// Advances by <paramref name="dt"/> seconds with the listener at zone-local (x, y), game time hhmm, weather 1 clear /
    /// 2 rain / 3 snow, under water or not, and music playing or not. <paramref name="stillPlaying"/> says whether a
    /// random line's previous sound still plays. Returns the random lines that play now.
    /// </summary>
    public List<AmbientShot> Step(double dt, float x, float y, int hhmm, int weather, bool underwater, bool musicPlaying,
        Func<int, int, bool>? stillPlaying = null)
    {
        _time += dt;
        var shots = new List<AmbientShot>();
        for (int r = 0; r < regions.Count; r++)
        {
            ZoneSoundRegion region = regions[r];
            bool inside = region.Contains(x, y) && !(region.FadeOnMp3 && musicPlaying);
            _heard[r] = inside;
            float rate = region.FadeTime <= 0 ? float.MaxValue : (float)(dt / region.FadeTime);
            for (int l = 0; l < region.Sounds.Count; l++)
            {
                ZoneSoundLine line = region.Sounds[l];
                bool plays = inside && Plays(line, hhmm, weather, underwater);
                if (line.Kind == ZoneSoundLine.Loop)
                {
                    float g = _bedGain.GetValueOrDefault((r, l));
                    g = plays ? Math.Min(1, Math.Max(g, BedStart) + rate) : Math.Max(0, g - rate);
                    if (g > 0)
                        _bedGain[(r, l)] = g;
                    else
                        _bedGain.Remove((r, l));
                    continue;
                }
                if (line.Kind != ZoneSoundLine.Random)
                    continue;
                if (!plays)
                {
                    _nextTurn.Remove((r, l));
                    continue;
                }
                if (!_nextTurn.TryGetValue((r, l), out double next))
                {
                    _nextTurn[(r, l)] = _time + FirstTurn(region, line);
                    if (!region.InstantStart)
                        continue;
                    next = _time;
                }
                if (_time < next)
                    continue;
                _nextTurn[(r, l)] = _time + Period(line);
                if (stillPlaying?.Invoke(r, l) == true || _time - _lastRandom[r] < region.RandomSpacing)
                    continue;
                if (_rng.NextDouble() * 100 >= Math.Clamp(line.Chance, 0, 100))
                    continue;
                _lastRandom[r] = _time;
                shots.Add(new AmbientShot(r, l, line.Level));
            }
        }
        return shots;
    }

    /// <summary>The loop lines that sound now, with their level (the line's fade × its volume).</summary>
    public IEnumerable<AmbientShot> Loops()
    {
        foreach (((int r, int l), float g) in _bedGain.OrderBy(kv => kv.Key))
            yield return new AmbientShot(r, l, g * regions[r].Sounds[l].Level);
    }

    private static bool Plays(ZoneSoundLine line, int hhmm, int weather, bool underwater) =>
        line.PlaysAt(hhmm) && line.PlaysIn(weather) && line.PlaysUnder(underwater);

    private double FirstTurn(ZoneSoundRegion region, ZoneSoundLine line)
    {
        if (region.InstantStart)
            return 0;
        double period = Period(line);
        return region.IgnoreRandomOffset ? period : 1 + _rng.NextDouble() * Math.Max(0, period - 1);
    }

    private double Period(ZoneSoundLine line) => Math.Max(1, line.PeriodS.Pick(_rng));
}
