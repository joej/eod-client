// SPDX-License-Identifier: GPL-3.0-or-later
//
// DAoC's INI-style data files (SECTOR.DAT, zones/sky/sky_*.dat, the .dat settings files): "[section]"
// headers, "key = value" lines, ';' comments. Section and key names are case-insensitive; a repeated
// section merges into the first; values are trimmed. A missing key reads as absent, not as an error.

using System.Globalization;
using System.Numerics;

namespace DaocAssets.Zone;

public sealed class IniFile
{
    private readonly Dictionary<string, Dictionary<string, string>> _sections =
        new(StringComparer.OrdinalIgnoreCase);

    public IReadOnlyDictionary<string, Dictionary<string, string>> Sections => _sections;

    public static IniFile Parse(string text)
    {
        var ini = new IniFile();
        Dictionary<string, string>? current = null;
        foreach (string raw in text.Split('\n'))
        {
            string line = raw.Trim();
            if (line.Length == 0 || line.StartsWith(';'))
                continue;
            if (line.StartsWith('[') && line.Contains(']'))
            {
                string name = line[1..line.IndexOf(']')].Trim();
                if (!ini._sections.TryGetValue(name, out current))
                    ini._sections[name] = current = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
                continue;
            }
            int eq = line.IndexOf('=');
            if (current is not null && eq > 0)
            {
                string value = line[(eq + 1)..];
                int comment = value.IndexOf(';');
                if (comment >= 0)
                    value = value[..comment];
                current[line[..eq].Trim()] = value.Trim();
            }
        }
        return ini;
    }

    public string? Get(string section, string key) =>
        _sections.TryGetValue(section, out var s) && s.TryGetValue(key, out string? v) ? v : null;

    public float? Float(string section, string key) =>
        float.TryParse(Get(section, key), NumberStyles.Float, CultureInfo.InvariantCulture, out float f) ? f : null;

    /// <summary>"r,g,b" or "r,g,b,a" in 0..255 as a 0..1 colour (alpha 1 when absent), or null.</summary>
    public Vector4? Color(string section, string key)
    {
        string[]? p = Get(section, key)?.Split(',');
        if (p is null || p.Length < 3)
            return null;
        var c = new float[4] { 0, 0, 0, 255 };
        for (int i = 0; i < Math.Min(4, p.Length); i++)
            if (!float.TryParse(p[i].Trim(), NumberStyles.Float, CultureInfo.InvariantCulture, out c[i]))
                return null;
        return new Vector4(c[0], c[1], c[2], c[3]) / 255f;
    }
}
