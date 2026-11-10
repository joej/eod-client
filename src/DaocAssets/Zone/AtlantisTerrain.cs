// SPDX-License-Identifier: GPL-3.0-or-later
//
// "Atlantis Terrain", retail's default ground (W8; daoc-re RE/70, 2026-11-09): each zone's ter<NNN>.mpk splits the
// zone into 8 x 8 patches (the same 8192-unit sectors as the heightmap and the classic texRR-CC tiles) and paints
// each with a stack of tiled detail textures, blended by masks.
//
//   textures.csv   one row per layer, in draw order, grouped by patch: patch x, patch y, base texture filename,
//                  rotate, u translate, v translate, u scale, v scale, visible, num tiles, tileable (and, in some
//                  files, normal map, specular tint and power, mask type and window size, all unused). The owner's
//                  install: 1–12 layers per patch, rotate and translate always 0, scale 1 (50 layers 0.75); num tiles
//                  256 mostly (the texture repeats 256 times across the patch, every 32 units), 1 for a whole-patch
//                  map, also 64, 128, 32 and others; tileable 0 on a whole-patch map.
//   patchXXYY-NN   the masks, 128 x 128 DXT3: layer i takes its weight from mask i / 3, channel i mod 3 (R, G, B).
//   textures       <name>.dds from the zone tree's own TerrainTex folder (frontiers/zones, Tutorial/zones), else
//                  zones/TerrainTex. The folders share names with different contents, so they're kept apart.
//
// Drawn: colour = layer 0; for each further layer, colour = lerp(colour, layer, weight); the ground shading on top.
// A layer with num tiles 1 is a map of the whole zone (AtlantisLayer.ZoneWide), sampled at the zone's UV. For the
// others, the UV rule (patch-local × num tiles ÷ scale, clamped when not tileable) and whether an invisible layer keeps its
// mask slot are unconfirmed (we assume it does: the slot is the row's place in the patch).

using System.Globalization;

namespace DaocAssets.Zone;

/// <summary>One layer of a patch: its place in the patch's stack, texture, repeats and flags.</summary>
public sealed record AtlantisLayer(int PatchX, int PatchY, int Index, string Texture, float Tiles, float UScale, float VScale,
    bool Visible, bool Tileable)
{
    /// <summary>The mask file holding this layer's weight, and its channel (0 R, 1 G, 2 B).</summary>
    public (string Mask, int Channel) Weight => ($"patch{PatchX:00}{PatchY:00}-{Index / 3:00}.dds", Index % 3);

    /// <summary>
    /// A layer drawn once (num tiles 1) is a painted map of the whole zone (named after it: bmsouth.dds,
    /// salisburybasemap.dds), not of one patch: each patch takes its 1/64 of it. Measured 2026-11-10: in 68 of 71 zones
    /// each classic tile matches its own 1/64 of the map (median correlation 0.83) and not the whole map squeezed into a
    /// patch (about 0); stretched per patch, neighbouring patches showed different parts and met at visible seams.
    /// The other 3 are the Atlantis zones 70-72, whose maps cover only some patches.
    /// </summary>
    public bool ZoneWide => Tiles == 1;
}

public static class AtlantisTerrain
{
    /// <summary>The layers of a ter archive's textures.csv (the header and short or blank rows skipped).</summary>
    public static List<AtlantisLayer> Parse(string texturesCsv)
    {
        var layers = new List<AtlantisLayer>();
        var counts = new Dictionary<(int, int), int>();
        foreach (string raw in texturesCsv.Split('\n'))
        {
            string[] c = raw.Trim().Split(',').Select(s => s.Trim()).ToArray();
            if (c.Length < 11 || Int(c[0]) is not int px || Int(c[1]) is not int py || c[2].Length == 0)
                continue;
            int index = counts.GetValueOrDefault((px, py));
            counts[(px, py)] = index + 1;
            layers.Add(new AtlantisLayer(px, py, index, c[2], Num(c[9], 256), Num(c[6], 1), Num(c[7], 1), c[8] != "0", c[10] != "0"));
        }
        return layers;
    }

    /// <summary>atlantis.csv beside zone.csv: "px,py,index,texture,tiles,uscale,vscale,visible,tileable", texture as the
    /// library path under textures/terraintex ("zones/darkgrass2.dds").</summary>
    public static string ToCsv(IEnumerable<AtlantisLayer> layers)
    {
        var sb = new System.Text.StringBuilder("px,py,index,texture,tiles,uscale,vscale,visible,tileable\n");
        foreach (AtlantisLayer l in layers)
            sb.Append(string.Create(CultureInfo.InvariantCulture,
                $"{l.PatchX},{l.PatchY},{l.Index},{l.Texture},{l.Tiles},{l.UScale},{l.VScale},{(l.Visible ? 1 : 0)},{(l.Tileable ? 1 : 0)}\n"));
        return sb.ToString();
    }

    /// <summary>Reads an atlantis.csv written by <see cref="ToCsv"/>.</summary>
    public static List<AtlantisLayer> ReadCsv(string csv)
    {
        var layers = new List<AtlantisLayer>();
        foreach (string raw in csv.Split('\n').Skip(1))
        {
            string[] c = raw.Trim().Split(',');
            if (c.Length < 9 || Int(c[0]) is not int px || Int(c[1]) is not int py || Int(c[2]) is not int i)
                continue;
            layers.Add(new AtlantisLayer(px, py, i, c[3], Num(c[4], 256), Num(c[5], 1), Num(c[6], 1), c[7] == "1", c[8] == "1"));
        }
        return layers;
    }

    /// <summary>
    /// The TerrainTex folders a zone's layer textures come from, in order, with a short name for each: the zone tree's
    /// own (the "zones" folder holding the zone's folder: frontiers/zones, Tutorial/zones), then the install's
    /// zones/TerrainTex.
    /// </summary>
    public static List<(string Scope, string Folder)> TextureFolders(string zoneDatMpk)
    {
        var folders = new List<(string Scope, string Folder)>();
        string zonesDir = Path.GetDirectoryName(Path.GetDirectoryName(Path.GetFullPath(zoneDatMpk))!)!;   // …/zones
        string? install = Path.GetDirectoryName(zonesDir);
        // The install's own zones/TerrainTex: the "zones" scope (the install folder is the one holding zones/zone000…).
        string? main = null;
        for (string? d = install; d is not null && main is null; d = Path.GetDirectoryName(d))
            if (Child(d, "zones") is string z && Child(z, "terraintex") is string t && Child(z, "zone000") is not null)
                main = t;
        if (Child(zonesDir, "terraintex") is string own && own != main)
            folders.Add((Path.GetFileName(install ?? "").ToLowerInvariant(), own));   // "frontiers", "tutorial"
        if (main is not null)
            folders.Add(("zones", main));
        return folders;
    }

    private static string? Child(string dir, string name) => Directory.Exists(dir)
        ? Directory.EnumerateDirectories(dir).FirstOrDefault(d => Path.GetFileName(d).Equals(name, StringComparison.OrdinalIgnoreCase))
        : null;

    private static int? Int(string s) => int.TryParse(s, NumberStyles.Integer, CultureInfo.InvariantCulture, out int v) ? v : null;

    private static float Num(string s, float fallback) =>
        float.TryParse(s, NumberStyles.Float, CultureInfo.InvariantCulture, out float v) && v > 0 ? v : fallback;
}
