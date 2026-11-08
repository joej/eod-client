// SPDX-License-Identifier: GPL-3.0-or-later
//
// The pictures the client shows while it loads a region (gamedata.mpk loading.dat). One INI section per
// region, [regionNNN]: name (shown with the picture), screens (count), screen01..screenNN as
// "archive://<archive path>:<member without extension>" (e.g. data/loading/camcity.mpk:camelot1),
// instance (1 for instanced regions) and widestyle. The client picks one screen at random; each is a
// 1024 x 256 banner drawn the full screen width, centred vertically, with a tip from data/loading/tips.dat
// under it. (Layout and choice per the daoc-re notes, 2026-11-07.)
//
// `mpk loading` turns this into a small table in the library (loading.csv) next to the pictures it names,
// so the Godot client never reads archives.

namespace DaocAssets.Zone;

/// <summary>One screen's picture: the archive (install-relative path) and the member's name, without extension.</summary>
public sealed record LoadingPicture(string Archive, string Member)
{
    /// <summary>The library file name: the archive's stem and the member, so equal member names in two archives
    /// don't collide (e.g. "camcity_camelot1").</summary>
    public string Stem => $"{Path.GetFileNameWithoutExtension(Archive)}_{Member}".ToLowerInvariant();
}

/// <summary>A region's loading screens.</summary>
public sealed record LoadingRegion(int Region, string Name, bool Instance, bool WideStyle, IReadOnlyList<LoadingPicture> Pictures);

public static class LoadingScreens
{
    /// <summary>Reads loading.dat. Regions without a usable screen still give their name.</summary>
    public static IReadOnlyList<LoadingRegion> Parse(string loadingDat)
    {
        var result = new List<LoadingRegion>();
        foreach ((string section, Dictionary<string, string> keys) in IniFile.Parse(loadingDat).Sections)
        {
            if (!section.StartsWith("region", StringComparison.OrdinalIgnoreCase)
                || !int.TryParse(section.AsSpan(6), out int region))
                continue;
            string Get(string key) => keys.TryGetValue(key, out string? v) ? v.Trim() : "";
            int count = int.TryParse(Get("screens"), out int n) ? n : 0;
            var pictures = new List<LoadingPicture>();
            for (int i = 1; i <= count; i++)
                if (ParsePicture(Get($"screen{i:D2}")) is { } p)
                    pictures.Add(p);
            result.Add(new LoadingRegion(region, Get("name"), Get("instance") == "1", Get("widestyle") == "1", pictures));
        }
        return result.OrderBy(r => r.Region).ToList();
    }

    /// <summary>"archive://data/loading/alb1.mpk:alb0" → (data/loading/alb1.mpk, alb0); null for anything else.</summary>
    public static LoadingPicture? ParsePicture(string reference)
    {
        const string scheme = "archive://";
        if (!reference.StartsWith(scheme, StringComparison.OrdinalIgnoreCase))
            return null;
        string rest = reference[scheme.Length..];
        int colon = rest.LastIndexOf(':');
        if (colon <= 0 || colon == rest.Length - 1)
            return null;
        return new LoadingPicture(rest[..colon].Replace('\\', '/'), rest[(colon + 1)..]);
    }

    /// <summary>The library table: "region,instance,widestyle,name,stem;stem;…", one line per region (a name has no commas
    /// in the file; any are dropped). Only stems in <paramref name="present"/> are listed.</summary>
    public static string ToCsv(IEnumerable<LoadingRegion> regions, ISet<string> present) =>
        string.Concat(regions.Select(r =>
            $"{r.Region},{(r.Instance ? 1 : 0)},{(r.WideStyle ? 1 : 0)},{r.Name.Replace(",", "")}," +
            string.Join(';', r.Pictures.Select(p => p.Stem).Where(present.Contains)) + "\n"));

    /// <summary>Reads <see cref="ToCsv"/>'s table: region → (name, instance, picture stems).</summary>
    public static Dictionary<int, (string Name, bool Instance, string[] Stems)> ReadCsv(string csv)
    {
        var result = new Dictionary<int, (string, bool, string[])>();
        foreach (string line in csv.Split('\n'))
        {
            string[] f = line.TrimEnd('\r').Split(',');
            if (f.Length < 5 || !int.TryParse(f[0], out int region))
                continue;
            result[region] = (f[3], f[1] == "1", f[4].Split(';', StringSplitOptions.RemoveEmptyEntries));
        }
        return result;
    }
}
