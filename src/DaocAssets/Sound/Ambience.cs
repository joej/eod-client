// SPDX-License-Identifier: GPL-3.0-or-later
//
// A zone's ambience over time (roadmap M12): which sounds.dat lines play where the listener is, at the game time and in
// the weather. A region fades in over its fade_time when the listener enters it (or is zone-wide) and out when they leave.
// A loop line (type 2) plays while its region is heard and its time, weather and environment match. A random line
// (type 1) rolls its chance every period seconds and plays once when it wins, unless its region played a random sound less
// than random_sound_spacing seconds ago. fade_on_mp3 silences the region while music plays. The rules come from the
// file's own header and daoc-re's note; the timing details are ours until daoc-re answers (W52): the first roll comes one
// period after the region is entered, and a period of 0 rolls once a second.

namespace DaocAssets.Sound;

/// <summary>A line to play once now: region and line index, and its volume 0–1.</summary>
public readonly record struct AmbientShot(int Region, int Line, float Volume);

public sealed class Ambience(IReadOnlyList<ZoneSoundRegion> regions, Random? rng = null)
{
    private readonly Random _rng = rng ?? Random.Shared;
    private readonly float[] _gain = new float[regions.Count];
    private readonly double[] _lastRandom = Enumerable.Repeat(double.NegativeInfinity, regions.Count).ToArray();
    private readonly Dictionary<(int, int), double> _nextRoll = [];
    private double _time;

    public IReadOnlyList<ZoneSoundRegion> Regions => regions;

    /// <summary>A region's heard level now, 0–1.</summary>
    public float Gain(int region) => _gain[region];

    /// <summary>
    /// Advances by <paramref name="dt"/> seconds with the listener at zone-local (x, y), game time hhmm, weather 1 clear /
    /// 2 rain / 3 fog, under water or not, and music playing or not. Returns the random lines that play now.
    /// </summary>
    public List<AmbientShot> Step(double dt, float x, float y, int hhmm, int weather, bool underwater, bool musicPlaying)
    {
        _time += dt;
        var shots = new List<AmbientShot>();
        for (int r = 0; r < regions.Count; r++)
        {
            ZoneSoundRegion region = regions[r];
            bool inside = region.Contains(x, y) && !(region.FadeOnMp3 && musicPlaying);
            float rate = region.FadeTime <= 0 ? float.MaxValue : (float)(dt / region.FadeTime);
            _gain[r] = inside ? Math.Min(1, _gain[r] + rate) : Math.Max(0, _gain[r] - rate);
            for (int l = 0; l < region.Sounds.Count; l++)
            {
                ZoneSoundLine line = region.Sounds[l];
                if (line.Kind != ZoneSoundLine.Random)
                    continue;
                if (!inside || !Plays(line, hhmm, weather, underwater))
                {
                    _nextRoll.Remove((r, l));
                    continue;
                }
                if (!_nextRoll.TryGetValue((r, l), out double next))
                {
                    _nextRoll[(r, l)] = _time + Period(line);
                    continue;
                }
                if (_time < next)
                    continue;
                _nextRoll[(r, l)] = _time + Period(line);
                if (_rng.NextDouble() * 100 >= line.Chance || _time - _lastRandom[r] < region.RandomSpacing)
                    continue;
                _lastRandom[r] = _time;
                shots.Add(new AmbientShot(r, l, line.Volume / 100f));
            }
        }
        return shots;
    }

    /// <summary>The loop lines that should sound now, with their level (the region's fade × the line's volume).</summary>
    public IEnumerable<AmbientShot> Loops(int hhmm, int weather, bool underwater)
    {
        for (int r = 0; r < regions.Count; r++)
        {
            if (_gain[r] <= 0)
                continue;
            for (int l = 0; l < regions[r].Sounds.Count; l++)
            {
                ZoneSoundLine line = regions[r].Sounds[l];
                if (line.Kind == ZoneSoundLine.Loop && Plays(line, hhmm, weather, underwater))
                    yield return new AmbientShot(r, l, _gain[r] * line.Volume / 100f);
            }
        }
    }

    private static bool Plays(ZoneSoundLine line, int hhmm, int weather, bool underwater) =>
        line.PlaysAt(hhmm) && line.PlaysIn(weather) && (line.Environment == 2) == underwater;

    private double Period(ZoneSoundLine line) => Math.Max(1, line.PeriodS.Pick(_rng));
}
