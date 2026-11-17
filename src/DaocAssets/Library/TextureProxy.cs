// SPDX-License-Identifier: GPL-3.0-or-later
//
// A dungeon's texture substitutions: datNNN.mpk : texproxy.csv, rows "old,new,0" (file names without extension). While
// the client loads that dungeon's fixtures, a texture named "old" is read as "new", keeping the name's folder and
// extension, before the usual lookup (the model's own folder, then zones/Nifs). Three dungeons have one (zones 060, 079,
// 423). (daoc-re, 2026-11-16; W63.)

using DaocAssets.Mpk;

namespace DaocAssets.Library;

/// <summary>A dungeon's texture renames (<c>texproxy.csv</c>), applied to a texture name before it is looked up.</summary>
public sealed class TextureProxy
{
    private readonly Dictionary<string, string> _renames;

    private TextureProxy(Dictionary<string, string> renames) => _renames = renames;

    public static TextureProxy Empty { get; } = new(new(StringComparer.OrdinalIgnoreCase));

    /// <summary>How many names are renamed.</summary>
    public int Count => _renames.Count;

    /// <summary>The renames, old stem to new stem.</summary>
    public IReadOnlyDictionary<string, string> Renames => _renames;

    /// <summary>
    /// Reads <c>old,new,flag</c> rows. The third column is 0 in every row of the install; what it means isn't known, so it
    /// is not read. A row without two names is skipped; a repeated old name keeps its first row (ours).
    /// </summary>
    public static TextureProxy Parse(string text)
    {
        var renames = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        foreach (string line in text.Split('\n'))
        {
            string[] cols = line.Trim().Split(',');
            if (cols.Length >= 2 && cols[0].Trim() is { Length: > 0 } from && cols[1].Trim() is { Length: > 0 } to)
                renames.TryAdd(from, to);
        }
        return new TextureProxy(renames);
    }

    /// <summary>The table in a zone's <c>dat&lt;NNN&gt;.mpk</c>, or <see cref="Empty"/> when it has none.</summary>
    public static TextureProxy FromZoneArchive(string datArchive)
    {
        using MpkArchive mpk = MpkArchive.Open(datArchive);
        return mpk.TryGetEntry("texproxy.csv", out MpkEntry e)
            ? Parse(System.Text.Encoding.Latin1.GetString(mpk.Extract(e)))
            : Empty;
    }

    /// <summary>
    /// The name the client reads for <paramref name="name"/>: its stem renamed, its folder and extension kept
    /// ("dstonewall04.dds" → "dstonewall03.dds"). One rename only: a new name that is itself an old one in the table
    /// (zone 060: RockCaveFloor01 → dstonerockfloor01 → wetstones) is not renamed again (inferred; not traced).
    /// </summary>
    public string Apply(string name)
    {
        int slash = name.LastIndexOfAny(['/', '\\']);
        string folder = name[..(slash + 1)], file = name[(slash + 1)..];
        int dot = file.LastIndexOf('.');
        string stem = dot < 0 ? file : file[..dot], ext = dot < 0 ? "" : file[dot..];
        return _renames.TryGetValue(stem, out string? to) ? folder + to + ext : name;
    }
}
