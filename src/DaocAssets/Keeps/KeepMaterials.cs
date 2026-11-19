// SPDX-License-Identifier: GPL-3.0-or-later
//
// Keep textures (daoc-re RE/92 §4, read from the 1.130 client): a keep piece's surfaces name a material ("!nf_main_wall",
// "!stone_trim", …: the NiMaterialProperty's name), and the client takes that surface's textures from fr_materials.csv
// by the keep's realm and level tier, each a row of fr_textures.csv (a file frontiers/nifs/<Filename>.dds).
//   fr_materials.csv  id, name, then groups of W texture ids: Albion, Midgard, Hibernia (the plain groups), then Albion
//                     Tier 1–4, Midgard Tier 1–4, Hibernia Tier 1–4; then Damage Texture (1: the damage overlay
//                     applies) and Emmitence (emissive = value / 255, grey). W is 4 in retail's table (Base, Dark,
//                     Detail, Glow) and 3 in the OpenDAoC install's (Base, Dark, Detail): read from the column count.
//                     Both 1.127 and 1.130 read 4, so they misread a 3-wide table; we read it as it is (daoc-re).
//   fr_textures.csv   id, description, filename, Damage (1–4: the overlay for that damage level), Clamp (unused).
// Which group: levels 0–2 → the plain group, 3–5 → Tier 1, 6–8 → Tier 2, 9 and up → Tier 3 (KeepLayout.LevelTier); a
// "!door…" material by the piece's own TIER column instead (1–5 → Tier 1–4; 5 clamped to 4: ours).

namespace DaocAssets.Keeps;

/// <summary>A group's textures (file stems, no extension; null when the cell is empty).</summary>
public sealed record KeepTextureSet(string? Base, string? Dark, string? Detail, string? Glow);

/// <summary>A material: its texture sets by realm (0 Albion, 1 Midgard, 2 Hibernia) and group (0 plain, 1–4 Tier 1–4).</summary>
public sealed record KeepMaterial(string Name, KeepTextureSet?[,] Sets, bool Damage, int Emittance);

public sealed class KeepMaterials
{
    /// <summary>Materials by name, any case.</summary>
    public Dictionary<string, KeepMaterial> Materials { get; } = new(StringComparer.OrdinalIgnoreCase);

    /// <summary>Every texture file the tables name (stems), and the damage overlays by damage level (1–4).</summary>
    public HashSet<string> Files { get; } = new(StringComparer.OrdinalIgnoreCase);
    public Dictionary<int, string> DamageOverlays { get; } = [];

    /// <summary>Textures per group in the materials table (4 retail, 3 OpenDAoC's install).</summary>
    public int GroupWidth { get; private set; }

    public static KeepMaterials Parse(string materialsCsv, string texturesCsv)
    {
        var result = new KeepMaterials();
        var textures = new Dictionary<int, string>();
        foreach (string[] r in Rows(texturesCsv))
            if (r.Length >= 3 && int.TryParse(r[0], out int id) && r[2].Trim().Length > 0)
            {
                textures[id] = r[2].Trim();
                result.Files.Add(r[2].Trim());
                if (r.Length >= 4 && int.TryParse(r[3], out int damage) && damage is >= 1 and <= 4)
                    result.DamageOverlays[damage] = r[2].Trim();
            }

        string[] lines = materialsCsv.Split('\n');
        int columns = lines.Length > 1 ? lines[1].TrimEnd('\r').Split(',').Length : 0;
        // id, name, 15 groups of W, damage, emittance.
        result.GroupWidth = (columns - 4) / 15 is var w && w is 3 or 4 ? w : throw new FormatException($"fr_materials.csv: {columns} columns");
        int W = result.GroupWidth;
        string? Tex(string[] r, int c) => c < r.Length && int.TryParse(r[c], out int id) && textures.TryGetValue(id, out string? f) ? f : null;
        foreach (string[] r in Rows(materialsCsv))
        {
            if (r.Length < 2 || !int.TryParse(r[0], out _) || !r[1].Trim().StartsWith('!'))
                continue;
            var sets = new KeepTextureSet?[3, 5];
            for (int realm = 0; realm < 3; realm++)
                for (int group = 0; group < 5; group++)
                {
                    int c = group == 0 ? 2 + realm * W : 2 + 3 * W + (realm * 4 + group - 1) * W;
                    KeepTextureSet set = new(Tex(r, c), Tex(r, c + 1), Tex(r, c + 2), W == 4 ? Tex(r, c + 3) : null);
                    sets[realm, group] = set.Base is null && set.Dark is null && set.Detail is null && set.Glow is null ? null : set;
                }
            int tail = 2 + 15 * W;
            bool damage = tail < r.Length && r[tail].Trim() == "1";
            int emit = tail + 1 < r.Length && int.TryParse(r[tail + 1], out int e) ? e : 0;
            result.Materials[r[1].Trim()] = new KeepMaterial(r[1].Trim(), sets, damage, emit);
        }
        return result;
    }

    /// <summary>The group a surface takes: the keep's level tier (0 plain, 1–3 Tier 1–3), or for a "!door" material the
    /// piece's TIER column (1–5 → Tier 1–4).</summary>
    public static int Group(string material, int levelTier, int tierColumn) =>
        material.Contains("!door", StringComparison.OrdinalIgnoreCase) ? Math.Clamp(tierColumn, 1, 4) : Math.Clamp(levelTier, 0, 4);

    /// <summary>The table the library keeps: name,realm,group,base,dark,detail,glow,damage,emittance (realm 1–3).</summary>
    public string ToCsv()
    {
        var sb = new System.Text.StringBuilder("name,realm,group,base,dark,detail,glow,damage,emittance\n");
        foreach (KeepMaterial m in Materials.Values.OrderBy(m => m.Name, StringComparer.OrdinalIgnoreCase))
            for (int realm = 0; realm < 3; realm++)
                for (int group = 0; group < 5; group++)
                    if (m.Sets[realm, group] is { } s)
                        sb.Append($"{m.Name},{realm + 1},{group},{s.Base},{s.Dark},{s.Detail},{s.Glow},{(m.Damage ? 1 : 0)},{m.Emittance}\n");
        return sb.ToString();
    }

    /// <summary>Reads keeps/damage.csv (state,texture): the overlay texture stem by damage state 1–4.</summary>
    public static Dictionary<int, string> ReadDamageCsv(string csv) =>
        csv.Split('\n').Skip(1).Select(l => l.TrimEnd('\r').Split(','))
            .Where(f => f.Length >= 2 && int.TryParse(f[0], out _) && f[1].Length > 0)
            .ToDictionary(f => int.Parse(f[0]), f => f[1]);

    /// <summary>Reads <see cref="ToCsv"/>'s table: (material name, realm 1–3, group) → its row.</summary>
    public static Dictionary<(string Name, int Realm, int Group), (KeepTextureSet Set, bool Damage, int Emittance)> ReadCsv(string csv)
    {
        var result = new Dictionary<(string, int, int), (KeepTextureSet, bool, int)>(new NameKeyComparer());
        foreach (string line in csv.Split('\n').Skip(1))
        {
            string[] f = line.TrimEnd('\r').Split(',');
            if (f.Length < 9 || !int.TryParse(f[1], out int realm) || !int.TryParse(f[2], out int group))
                continue;
            static string? N(string s) => s.Length == 0 ? null : s;
            result[(f[0], realm, group)] = (new KeepTextureSet(N(f[3]), N(f[4]), N(f[5]), N(f[6])), f[7] == "1",
                int.TryParse(f[8], out int e) ? e : 0);
        }
        return result;
    }

    private sealed class NameKeyComparer : IEqualityComparer<(string, int, int)>
    {
        public bool Equals((string, int, int) a, (string, int, int) b) =>
            string.Equals(a.Item1, b.Item1, StringComparison.OrdinalIgnoreCase) && a.Item2 == b.Item2 && a.Item3 == b.Item3;

        public int GetHashCode((string, int, int) k) =>
            HashCode.Combine(StringComparer.OrdinalIgnoreCase.GetHashCode(k.Item1), k.Item2, k.Item3);
    }

    private static IEnumerable<string[]> Rows(string csv) => csv.Split('\n').Skip(2).Select(l => l.TrimEnd('\r').Split(','));
}
