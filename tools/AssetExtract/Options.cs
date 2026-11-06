// SPDX-License-Identifier: GPL-3.0-or-later
//
// The options after a command's own arguments: `--name value`, or `--name X Y` for the integer
// pairs (--offset, --size). The last of a repeated option wins.

internal sealed class Options
{
    private static readonly HashSet<string> PairNames = ["--offset", "--size"];

    private readonly Dictionary<string, string> _values = [];
    private readonly Dictionary<string, (int, int)> _pairs = [];

    /// <summary>Arguments that aren't options, in order (only when <see cref="Parse"/> allows them).</summary>
    public List<string> Positional { get; } = [];

    /// <summary>Reads <paramref name="args"/> from <paramref name="start"/>. Null when an option isn't one of
    /// <paramref name="names"/>, lacks its value, or a pair isn't two integers; a bare argument is null too
    /// unless <paramref name="positional"/>. The caller then prints the usage.</summary>
    public static Options? Parse(string[] args, int start, string[] names, bool positional = false)
    {
        var o = new Options();
        for (int i = start; i < args.Length; i++)
        {
            string a = args[i];
            if (!a.StartsWith('-'))
            {
                if (!positional) return null;
                o.Positional.Add(a);
            }
            else if (!names.Contains(a))
                return null;
            else if (PairNames.Contains(a))
            {
                if (i + 2 >= args.Length || !int.TryParse(args[i + 1], out int x) || !int.TryParse(args[i + 2], out int y))
                    return null;
                o._pairs[a] = (x, y);
                i += 2;
            }
            else
            {
                if (i + 1 >= args.Length) return null;
                o._values[a] = args[++i];
            }
        }
        return o;
    }

    public string? this[string name] => _values.GetValueOrDefault(name);

    /// <summary>--offset X Y: the zone's region offset (zones.tsv x 8192).</summary>
    public (int X, int Y)? Offset => _pairs.TryGetValue("--offset", out var p) ? p : null;

    /// <summary>--size W H: the zone's size in 8192-unit sectors from the zone table (default 8 x 8, which every
    /// zone is). Terrain and water span this, not SECTOR.DAT's [sectorsize] (see ZoneTerrain.Read).</summary>
    public (int X, int Y) ZoneSize => _pairs.TryGetValue("--size", out var p) ? p : (8, 8);
}
