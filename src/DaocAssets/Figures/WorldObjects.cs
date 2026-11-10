// SPDX-License-Identifier: GPL-3.0-or-later
//
// The server's own objects (S→C 0xD9: carts, banners, items lying on the ground, doors that aren't part of a zone model)
// name their look by a model number: a row of gamedata.mpk's objects.csv, whose "NIF #" (column 2) is an items.csv row,
// whose column 2 is the model file. The same tables give worn and held gear its models (CataGear); here every row with
// a model counts, not only the held ones. Model 3405 is "Fest_cart1" (a cart, Black Mtns. South, 2026-11-10).
//
// Solid: objects.csv column 52 ("Colideable") is 1 on 206 rows: props such as carts, tents, fences, statues, crates,
// crypts and an "Invisible Blocker"; chests, banners and loose items are left out. (Column 43, "House Collide", is the
// housing items'.) That the client makes exactly these solid is read from the column's name and contents, unconfirmed.

namespace DaocAssets.Figures;

/// <summary>An object's model file and whether it's solid.</summary>
public readonly record struct ObjectModel(string Model, bool Solid);

public static class WorldObjects
{
    private const int CollideableColumn = 52;

    /// <summary>Each objects.csv model number → its model file ("Fest_cart1.nif") and solidity; rows without a model
    /// are left out.</summary>
    public static Dictionary<int, ObjectModel> Parse(string objectsCsv, string itemsCsv)
    {
        var items = new Dictionary<int, string>();
        foreach (string[] c in Rows(itemsCsv))
            if (c.Length > 2 && int.TryParse(c[0], out int id) && c[2].Trim().Length > 0)
                items.TryAdd(id, c[2].Trim() + ".nif");
        var models = new Dictionary<int, ObjectModel>();
        foreach (string[] r in Rows(objectsCsv))
            if (r.Length > 2 && int.TryParse(r[0], out int model) && int.TryParse(r[2], out int item) && items.TryGetValue(item, out string? nif))
                models.TryAdd(model, new ObjectModel(nif, r.Length > CollideableColumn && r[CollideableColumn].Trim() == "1"));
        return models;
    }

    /// <summary>models/objects.csv, written by the converter: "model,glb,solid" (the glb's path under models/, no
    /// extension; solid 1 or 0).</summary>
    public static Dictionary<int, ObjectModel> ReadTable(string csv)
    {
        var table = new Dictionary<int, ObjectModel>();
        foreach (string[] c in Rows(csv))
            if (c.Length >= 2 && int.TryParse(c[0], out int model) && c[1].Trim().Length > 0)
                table.TryAdd(model, new ObjectModel(c[1].Trim(), c.Length > 2 && c[2].Trim() == "1"));
        return table;
    }

    /// <summary>A line of models/objects.csv (<see cref="ReadTable"/>).</summary>
    public static string Row(int model, string glb, bool solid) => $"{model},{glb},{(solid ? 1 : 0)}";

    private static IEnumerable<string[]> Rows(string csv) => csv.Split('\n').Select(l => l.TrimEnd('\r').Split(','));
}
