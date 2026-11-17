// SPDX-License-Identifier: GPL-3.0-or-later
//
// A dungeon's piece substitutions: datNNN.mpk : nifproxy.csv, rows "old,new,0" with whole file names
// ("STONEHall01.NIF,ALTSTONEHall01.NIF,0"). Three dungeons have one (zones 060 and 423, the same 26 rows; 079, 35),
// the same three that have texproxy.csv. Every old name is one of the zone's dungeon.chunk pieces, every new one a
// zones/Dnifs/<name>.npk; zone 079's old pieces aren't in the install at all, so it is built only from the new ones.
//
// Read as a substitution made before the piece is loaded (inferred from the data, 2026-11-17; asked of daoc-re):
// patch files exist under the new names (altstonehall14_p00, alt_oceanusroom01_p00) and none under the old ones, and
// 060's ALTSTONEHall pieces with its texproxy renames applied once carry exactly the texture names of the STONEHall
// pieces they replace.

using DaocAssets.Mpk;

namespace DaocAssets.Library;

/// <summary>A dungeon's piece substitutions (<c>nifproxy.csv</c>): the model file loaded in place of another.</summary>
public sealed class ModelProxy
{
    private readonly Dictionary<string, string> _swaps;

    private ModelProxy(Dictionary<string, string> swaps) => _swaps = swaps;

    public static ModelProxy Empty { get; } = new(new(StringComparer.OrdinalIgnoreCase));

    /// <summary>How many pieces are substituted.</summary>
    public int Count => _swaps.Count;

    /// <summary>The substitutions, old file name to new.</summary>
    public IReadOnlyDictionary<string, string> Swaps => _swaps;

    /// <summary>
    /// Reads <c>old,new,flag</c> rows of file names. The third column is 0 in every row of the install and is not read.
    /// A row without two names is skipped; a repeated old name keeps its first row (ours).
    /// </summary>
    public static ModelProxy Parse(string text)
    {
        var swaps = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        foreach (string line in text.Split('\n'))
        {
            string[] cols = line.Trim().Split(',');
            if (cols.Length >= 2 && cols[0].Trim() is { Length: > 0 } from && cols[1].Trim() is { Length: > 0 } to)
                swaps.TryAdd(from, to);
        }
        return new ModelProxy(swaps);
    }

    /// <summary>The table in a zone's <c>dat&lt;NNN&gt;.mpk</c>, or <see cref="Empty"/> when it has none.</summary>
    public static ModelProxy FromZoneArchive(string datArchive)
    {
        using MpkArchive mpk = MpkArchive.Open(datArchive);
        return mpk.TryGetEntry("nifproxy.csv", out MpkEntry e)
            ? Parse(System.Text.Encoding.Latin1.GetString(mpk.Extract(e)))
            : Empty;
    }

    /// <summary>The model file loaded for <paramref name="name"/>: the new file name in its folder, or the name unchanged.
    /// Matched by the whole file name, any case (inferred).</summary>
    public string Apply(string name)
    {
        int slash = name.LastIndexOfAny(['/', '\\']);
        return _swaps.TryGetValue(name[(slash + 1)..], out string? to) ? name[..(slash + 1)] + to : name;
    }
}
