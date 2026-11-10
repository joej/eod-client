// SPDX-License-Identifier: GPL-3.0-or-later
//
// The server's own objects (S→C 0xD9: carts, banners, items lying on the ground, doors that aren't part of a zone model)
// name their look by a model number: a row of gamedata.mpk's objects.csv, whose "NIF #" (column 2) is an items.csv row,
// whose column 2 is the model file. The same tables give worn and held gear its models (CataGear); here every row with
// a model counts, not only the held ones. Model 3405 is "Fest_cart1" (a cart, Black Mtns. South, 2026-11-10).

namespace DaocAssets.Figures;

public static class WorldObjects
{
    /// <summary>Each objects.csv model number → its model file ("Fest_cart1.nif"); rows without one are left out.</summary>
    public static Dictionary<int, string> Parse(string objectsCsv, string itemsCsv)
    {
        var items = new Dictionary<int, string>();
        foreach (string[] c in Rows(itemsCsv))
            if (c.Length > 2 && int.TryParse(c[0], out int id) && c[2].Trim().Length > 0)
                items.TryAdd(id, c[2].Trim() + ".nif");
        var models = new Dictionary<int, string>();
        foreach (string[] r in Rows(objectsCsv))
            if (r.Length > 2 && int.TryParse(r[0], out int model) && int.TryParse(r[2], out int item) && items.TryGetValue(item, out string? nif))
                models.TryAdd(model, nif);
        return models;
    }

    /// <summary>models/objects.csv, written by the converter: "model,glb" (the glb's path under models/, no extension).</summary>
    public static Dictionary<int, string> ReadTable(string csv)
    {
        var table = new Dictionary<int, string>();
        foreach (string[] c in Rows(csv))
            if (c.Length >= 2 && int.TryParse(c[0], out int model) && c[1].Trim().Length > 0)
                table.TryAdd(model, c[1].Trim());
        return table;
    }

    private static IEnumerable<string[]> Rows(string csv) => csv.Split('\n').Select(l => l.TrimEnd('\r').Split(','));
}
