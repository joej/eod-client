// SPDX-License-Identifier: GPL-3.0-or-later
//
// The client's sky: zones/sky/sky.mpk holds one sky_<name>.dat per realm or region (albion, midgard,
// hibernia, frontiers, aerus, ...). Each defines cloud layers, the sun, and, per weather condition
// (clear, stormy) and time of day (dawn, day, dusk, night): the sky-dome ("canopy") colours, the cloud
// colours, ambient and sun ("dynamic") light, and the distance-fog colour. Colours are 0..255 "r,g,b" or
// "r,g,b,a". The dome is described by a zenith colour, four bands from overhead to the horizon on the
// east and on the west side (east1..4, west1..4), and a nadir colour; cloud colours use the same bands,
// with alpha fading to 0 at the horizon. The band elevations, how east/west are chosen, and which file
// a zone uses are not confirmed (asked; see docs/world-coverage.md). A comment in the files says cloud
// scroll speeds are multiplied by 0.001 in the client.

using System.Numerics;

namespace DaocAssets.Zone;

/// <summary>One cloud layer: texture stem, how many times it tiles across the dome, scroll speed
/// (as written in the file; the client multiplies by 0.001).</summary>
public sealed record CloudLayer(string Texture, float Tile, float Speed);

/// <summary>A sky-dome or cloud gradient: zenith, bands 1..4 toward the horizon (east and west), nadir.</summary>
public sealed record SkyGradient(Vector4 Zenith, Vector4 Nadir, Vector4[] East, Vector4[] West);

/// <summary>The sky for one weather condition and time of day.</summary>
public sealed record SkyState
{
    public required SkyGradient Canopy { get; init; }
    public required SkyGradient Clouds { get; init; }
    public required Vector3 AmbientLight { get; init; }
    public required float AmbientAmount { get; init; }
    public required Vector3 SunLight { get; init; }
    public required float SunAmount { get; init; }
    public required Vector3 DistanceFog { get; init; }
}

public sealed class SkyDefinition
{
    public static readonly string[] Times = ["dawn", "day", "dusk", "night"];
    public static readonly string[] Conditions = ["clear", "stormy"];

    private readonly IniFile _ini;

    private SkyDefinition(IniFile ini) => _ini = ini;

    public static SkyDefinition Parse(string text) => new(IniFile.Parse(text));

    /// <summary>The four cloud layers that are defined: top, bottom, top2, bot2.</summary>
    public IReadOnlyList<CloudLayer> CloudLayers =>
        new[] { ("cloud_top", ""), ("cloud_bot", ""), ("cloud_top", "2"), ("cloud_bot", "2") }
            .Select(l => (Tex: _ini.Get("main", $"{l.Item1}_texture{l.Item2}"),
                          Tile: _ini.Float("main", $"{l.Item1}_tile{l.Item2}") ?? 1,
                          Speed: _ini.Float("main", $"{l.Item1}_speed{l.Item2}") ?? 0))
            .Where(l => !string.IsNullOrEmpty(l.Tex))
            .Select(l => new CloudLayer(Path.GetFileNameWithoutExtension(l.Tex!), l.Tile, l.Speed))
            .ToList();

    public string SunTexture => Path.GetFileNameWithoutExtension(_ini.Get("main", "sun_texture") ?? "");
    public float SunScale => _ini.Float("main", "sun_scale") ?? 15;
    /// <summary>Tilt of the sun's path, degrees.</summary>
    public float EclipticAngle => _ini.Float("main", "ecliptic_angle") ?? 0;
    public Vector4 SunDiskNoon => _ini.Color("main", "sundisk_color_noon") ?? Vector4.One;

    /// <summary>The sky for a weather condition ("clear", "stormy") and time ("dawn".."night").
    /// Missing values fall back to the clear/day ones, then to neutral defaults.</summary>
    public SkyState State(string condition, string time) => new()
    {
        Canopy = Gradient($"canopy_color_{condition}", time),
        Clouds = Gradient($"cloud_color_{condition}", time),
        AmbientLight = Rgb(Pick($"lights_and_fog_{condition}", $"{time}_ambient_light")),
        AmbientAmount = PickFloat($"lights_and_fog_{condition}", $"{time}_ambient_light_amount", 0.5f),
        SunLight = Rgb(Pick($"lights_and_fog_{condition}", $"{time}_dynamic_light")),
        SunAmount = PickFloat($"lights_and_fog_{condition}", $"{time}_dynamic_light_amount", 0.6f),
        DistanceFog = Rgb(Pick($"lights_and_fog_{condition}", $"{time}_distance_fog")),
    };

    private SkyGradient Gradient(string section, string time) => new(
        Pick(section, $"{time}_zenith"),
        Pick(section, $"{time}_nadir", Vector4.Zero),
        [.. Enumerable.Range(1, 4).Select(i => Pick(section, $"{time}_east{i}"))],
        [.. Enumerable.Range(1, 4).Select(i => Pick(section, $"{time}_west{i}"))]);

    private Vector4 Pick(string section, string key, Vector4? fallback = null) =>
        _ini.Color(section, key)
        ?? _ini.Color(section.Replace("stormy", "clear"), key.Replace(key.Split('_')[0], "day"))
        ?? fallback ?? Vector4.One;

    private float PickFloat(string section, string key, float fallback) =>
        _ini.Float(section, key) ?? _ini.Float(section.Replace("stormy", "clear"), key) ?? fallback;

    private static Vector3 Rgb(Vector4 c) => new(c.X, c.Y, c.Z);
}

public static class ZoneSky
{
    /// <summary>
    /// Which sky file a zone uses, as the client chooses it from zones/zones.mpk → zones.dat:
    /// [zoneNNN] skydome, else [regionNNN] skydome for the zone's region=, else sky_default.dat.
    /// Values are trimmed (some carry trailing spaces). Returns the file name, e.g. "sky_albion.dat".
    /// </summary>
    public static string SkyFile(IniFile zonesDat, int zone)
    {
        string? own = zonesDat.Get($"zone{zone:000}", "skydome")?.Trim();
        if (!string.IsNullOrEmpty(own))
            return own;
        if (int.TryParse(zonesDat.Get($"zone{zone:000}", "region"), out int region))
        {
            string? regional = zonesDat.Get($"region{region:000}", "skydome")?.Trim();
            if (!string.IsNullOrEmpty(regional))
                return regional;
        }
        return "sky_default.dat";
    }
}
